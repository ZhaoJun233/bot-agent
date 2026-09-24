using System.Collections.Concurrent;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Music;

/// <summary>
/// 听歌 / 分享歌的用例（批次 4 从 <c>BotAgentHost</c> 抽出）：持有音乐服务与两份状态
/// （每会话的"刚听过"冷却、每会话"攒下的听歌笔记"），把"去听一遍"这件事包成一个返回**笔记文本**的调用。
///
/// 边界：**编排仍留给回复流程** —— 谁来触发（模型填了 listen / shareSong）、要不要再过能力闸门、
/// 听完要不要再给模型一次开口机会（<c>RequestReply</c>）、失败要不要记一次参与状态，
/// 这些都属于"一次回复该怎么走"，留在了 BotAgentHost；这里只做"音乐这一件事"。
/// </summary>
public sealed class MusicUseCase
{
    private readonly MusicService? _music;
    private readonly IQqChatSource _source;
    private readonly Action<string> _log;

    /// <summary>每个会话最近一次"去听歌"的时间（冷却：别一句接一句点歌）。</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastListen = new();

    /// <summary>每会话攒下的"听完的感觉"：下一轮生成时注入，然后取走（一次性）。</summary>
    private readonly ConcurrentDictionary<string, string> _notes = new();

    public MusicUseCase(MusicService? music, IQqChatSource source, Action<string> log)
    {
        _music = music;
        _source = source;
        _log = log;
    }

    /// <summary>音乐服务就绪（面板自测 / 模型动作都要先看它）。</summary>
    public bool IsReady => _music is not null;

    /// <summary>
    /// 最近一次「面板自测听歌」测的是哪首（面板要拿它回报；没测过 / 这次没搜到 = null）。
    /// 这份状态以前长在 BotAgentHost 上（面板 façade 的一部分），批次 5 跟着自测入口一起搬进来。
    /// </summary>
    public string? LastTestHeader { get; private set; }

    /// <summary>
    /// 面板自测：按歌名走一遍完整链路（搜歌 → 歌词 → 音源 → 波形 → 交给模型）。
    /// 顺手记下"这次测的是哪首"并写一条日志 —— 面板要拿它回报，日志要能解释"点了按钮之后到底做了什么"。
    /// </summary>
    public async Task<string?> TestByNameAsync(string song, CancellationToken ct)
    {
        LastTestHeader = null;
        if (_music is null)
        {
            return null;
        }

        var note = await _music.DescribeByNameAsync(song, "面板自测", ct);
        LastTestHeader = song;
        _log(note is null
            ? $"[Music] 面板自测「{song}」：没搜到或没听到"
            : $"[Music] 面板自测「{song}」完成");
        return note;
    }

    /// <summary>群里有人分享了歌：按分享内容去听一遍（识别平台/歌名那步在 MusicService 里）。</summary>
    public Task<string?> DescribeShareAsync(MusicShare share, string sender)
        => _music is null ? Task.FromResult<string?>(null) : _music.DescribeAsync(share, sender, CancellationToken.None);

    /// <summary>
    /// 冷却门（"去听歌"这一侧的频率限制）。返回 true 表示这次可以听（并**已经记上时间**）。
    /// </summary>
    public bool TryBeginListen(string sourceKey, int cooldownSeconds, out string reason)
    {
        reason = string.Empty;
        var now = Clock.Now;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, cooldownSeconds));
        if (_lastListen.TryGetValue(sourceKey, out var lastAt) && now - lastAt < cooldown)
        {
            reason = $"「{Math.Round((now - lastAt).TotalSeconds)}s」前刚听过";
            return false;
        }

        _lastListen[sourceKey] = now;
        return true;
    }

    /// <summary>
    /// 去听一首歌（真实出网、可能几秒），听完把"听感"攒进这个会话的笔记里（下一轮注入）。
    /// 返回听感文本；没搜到 / 没听到 / 出错 → null（调用方据此记一次工具失败）。
    /// </summary>
    public async Task<string?> ListenAsync(string sourceKey, string song, string requester)
    {
        if (_music is null)
        {
            return null;
        }

        try
        {
            var note = await _music.DescribeByNameAsync(song, requester, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(note))
            {
                _log($"[Music] 没搜到/没听到「{song}」");
                return null;
            }

            AddNote(sourceKey, note!);
            return note;
        }
        catch (Exception ex)
        {
            _log($"[Music] 听「{song}」失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>把一条听感追加到某个会话的笔记里（下一轮注入）。</summary>
    public void AddNote(string sourceKey, string note)
        => _notes.AddOrUpdate(sourceKey, note, (_, old) => old + "\n\n" + note);

    /// <summary>这个会话有没有攒着笔记（分享的那条路用它决定要不要写）。</summary>
    public bool HasNote(string sourceKey) => _notes.ContainsKey(sourceKey);

    /// <summary>取走这个会话的笔记（一次性：注入过就不再重复注入）。</summary>
    public string? TakeNote(string sourceKey)
        => _notes.TryRemove(sourceKey, out var note) ? note : null;

    /// <summary>
    /// 搜歌 → 发一张网易云音乐卡片；协议端不认卡片就退化成发链接（绝不能什么都不发）。
    /// 返回是否发出去了（卡片或链接）。
    /// </summary>
    public async Task<bool> ShareAsync(string song, bool isGroup, long targetId)
    {
        if (_music is null || targetId == 0 || !_source.IsConnected)
        {
            return false;
        }

        var songId = await _music.ResolveSongIdByNameAsync(song, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(songId))
        {
            _log($"[Music] 想分享「{song}」但没搜到，不发卡片");
            return false;
        }

        var ok = await _source.SendMusicAsync(isGroup, targetId, "163", songId, title: song, ct: CancellationToken.None);
        _log(ok ? $"[Music] 已分享卡片「{song}」(# {songId})" : $"[Music] 卡片发送失败，改用链接分享: {song}");

        // 协议端不接卡片（NapCat 各版本对 music 段的接受程度不一样）时退化成发链接：
        // QQ 客户端会把网易云链接自己渲染成卡片，效果差不多，但绝不能什么都不发
        if (!ok)
        {
            var link = $"https://music.163.com/song?id={songId}";
            var sentLink = await _source.SendTextAsync(isGroup, targetId, link, CancellationToken.None);
            _log(sentLink.Ok ? $"[Music] 已用链接分享：{link}" : $"[Music] 链接也发送失败：{link}");
            return sentLink.Ok;
        }

        return true;
    }
}

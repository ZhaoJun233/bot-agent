using System.Collections.Concurrent;
using BotAgent.Services.OneBot;
using BotAgent.Domain.Qq;

namespace BotAgent.Services.Qq;

/// <summary>
/// 多通道聚合：把「私域（NapCat）」与「官方（QQ 开放平台）」两个上行合成**一个**
/// <see cref="IQqChatSource"/> 交给上层，上层照旧只认 (isGroup, targetId)。
///
/// 为什么要这层，而不是让 BotAgentHost 到处判断通道：
///   机器人的发送点有十几处（回复、分句、语音、表情包、点歌、戳一戳、面板代发…），
///   每处都加一个 channel 参数 = 十几处都要改对，改错一处就是“串台”。
///   这里改成**按目标号路由**：官方通道的目标号是它自己申请的**别名号**（<see cref="Channels.AliasBase"/> 起步，
///   见官方网关的 OfficialIdMap），与真实 QQ 号天然不重叠 —— 于是“这个号属于哪个通道”
///   从一个约定变成可判定的规则，发送点一行都不用改。
///
/// 事件方向则相反：入站消息会在 <see cref="QqChatMessage.Channel"/> 上打标，
/// 上层用 <see cref="Channels.Key"/> 拼会话 key —— 会话隔离就是这么落地的。
/// </summary>
public sealed class ChannelRouter : IQqChatSource, IChannelRegistry, IDisposable
{
    private readonly List<IQqChatSource> _sources;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, IQqChatSource> _byChannel = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>学到的「目标 → 通道」（入站时记，重启后靠别名区间兜底）。</summary>
    private readonly ConcurrentDictionary<(bool IsGroup, long Id), string> _learned = new();

    /// <summary>会话 key → 通道：面板/命令侧拿到的 key 也能反查（含别名区间之外的异常情况）。</summary>
    private readonly ConcurrentDictionary<string, string> _byKey = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private bool _disposed;

    public ChannelRouter(IEnumerable<IQqChatSource> sources, Action<string>? log = null)
    {
        _sources = [.. sources.Where(s => s is not null)];
        _log = log;

        foreach (var src in _sources)
        {
            _byChannel[src.Channel] = src;
            src.MessageReceived += OnMessage;
            src.Poked += OnPoked;
            src.MessageRecalled += OnRecall;
            src.ConnectionChanged += OnConnection;
        }

        if (_sources.Count == 0)
        {
            throw new ArgumentException("ChannelRouter 至少要有一个上行通道", nameof(sources));
        }
    }

    /// <summary>路由器本身不是一个「通道」—— 判渠道请用 <see cref="Channels.ChannelOf(string?)"/> 或消息上的 Channel。</summary>
    public string Channel => Channels.Private;

    public event Action<QqChatMessage>? MessageReceived;

    public event Action<QqPokeEvent>? Poked;

    public event Action<QqRecallEvent>? MessageRecalled;

    public event Action<bool>? ConnectionChanged;

    /// <summary>至少一路在线（面板/健康检查用）。</summary>
    public bool IsConnected => _sources.Any(s => s.IsConnected);

    // ---------- 通道台账（面板分块、命令过滤都靠它）----------

    public IReadOnlyList<IQqChatSource> Sources => _sources;

    public IQqChatSource? Get(string channel)
        => _byChannel.TryGetValue(channel, out var src) ? src : null;

    public bool IsEnabled(string channel) => _byChannel.ContainsKey(channel);

    /// <summary>对外的一句话状态：私域 在线 / 官方 未启用 ……</summary>
    public string DescribeChannels()
    {
        var parts = new List<string>();
        foreach (var channel in new[] { Channels.Private, Channels.Official })
        {
            if (_byChannel.TryGetValue(channel, out var src))
            {
                parts.Add($"{Channels.Display(channel)} {(src.IsConnected ? "在线" : "离线")}");
            }
            else if (Channels.IsOfficial(channel))
            {
                parts.Add($"{Channels.Display(channel)} 未启用");
            }
        }

        return string.Join("，", parts);
    }

    // ---------- 入站：打上通道标签再往上走 ----------

    private void OnMessage(QqChatMessage msg)
    {
        var channel = Channels.IsOfficial(msg.Channel) ? Channels.Official : Channels.Private;
        Learn(channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);
        MessageReceived?.Invoke(Channels.IsOfficial(msg.Channel) ? msg : msg with { Channel = channel });
    }

    private void OnPoked(QqPokeEvent evt)
    {
        var channel = ResolveChannel(evt.IsGroup, evt.GroupId);
        Poked?.Invoke(evt with { Channel = channel });
    }

    private void OnRecall(QqRecallEvent evt)
    {
        var channel = ResolveChannel(evt.IsGroup, evt.GroupId);
        MessageRecalled?.Invoke(evt with { Channel = channel });
    }

    private void OnConnection(bool up)
    {
        // 单通道的在线/离线由各自网关自己打日志（那边知道是谁断了）；这里只上报聚合状态，
        // 免得“官方掉线”被上层当成“QQ 掉线”去走重连/告警那套。
        // 注：日志前缀由调用方（FileLog.Write("Channel", …)）给，这里不要再写一遍 [Channel]。
        _log?.Invoke($"上行状态变化：{DescribeChannels()}");
        ConnectionChanged?.Invoke(IsConnected);
    }

    private void Learn(string channel, bool isGroup, long id)
    {
        if (id > 0)
        {
            _learned[(isGroup, id)] = channel;
            _byKey[Channels.Key(channel, isGroup, id)] = channel;
        }
    }

    /// <summary>把会话 key 登记进来（上层从库里恢复会话时调）—— 面板代发这类“没有入站消息”的场景靠它定通道。</summary>
    public void RegisterKey(string sourceKey)
    {
        var channel = Channels.ChannelOf(sourceKey);
        var (isGroup, id) = Channels.Parse(sourceKey);
        if (id > 0)
        {
            _byKey[sourceKey] = channel;
            _learned[(isGroup, id)] = channel;
        }
    }

    /// <summary>通道 → 归属判定：先看学到的，再看别名区间（别名是官方通道自己发的号段）。</summary>
    private string ResolveChannel(bool isGroup, long id)
    {
        if (_learned.TryGetValue((isGroup, id), out var learned))
        {
            return learned;
        }

        return Channels.IsAliasId(id) ? Channels.Official : Channels.Private;
    }

    private IQqChatSource Resolve(bool isGroup, long id)
    {
        var channel = ResolveChannel(isGroup, id);
        if (_byChannel.TryGetValue(channel, out var src))
        {
            return src;
        }

        // 该通道没启用（比如官方没配凭据）：退回默认通道，至少别把消息丢在路由器里。
        return _sources[0];
    }

    // ---------- 出站：按目标号路由 ----------

    public Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
        => Resolve(isGroup, targetId).SendMusicAsync(isGroup, targetId, platform, songId, title, ct);

    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null)
        => Resolve(isGroup, targetId).SendTextAsync(isGroup, targetId, text, ct, replyToMessageId);

    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => Resolve(false, messageId).GetMessageInfoAsync(messageId, ct);

    public Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
        => Resolve(isGroup, targetId).SendVoiceAsync(isGroup, targetId, audioUrl, ct);

    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => Resolve(isGroup, targetId).SendImageAsync(isGroup, targetId, data, ct, replyToMessageId);

    public Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
        => Resolve(isGroup, targetId).SendPokeAsync(isGroup, targetId, userId, ct);

    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => Resolve(true, groupId).GetGroupNameAsync(groupId, ct);

    public Task<GroupMemberInfo?> GetGroupMemberInfoAsync(long groupId, long userId, CancellationToken ct = default)
        => Resolve(true, groupId).GetGroupMemberInfoAsync(groupId, userId, ct);

    public Task<List<string>> FetchCustomFacesAsync(int count = 48, CancellationToken ct = default)
        => _byChannel.TryGetValue(Channels.Private, out var priv)
            ? priv.FetchCustomFacesAsync(count, ct)
            : _sources[0].FetchCustomFacesAsync(count, ct);

    public Task<IReadOnlyList<string>> RefreshImageUrlsAsync(long messageId, CancellationToken ct = default)
        => Resolve(false, messageId).RefreshImageUrlsAsync(messageId, ct);

    /// <summary>上层登记会话用（见 <see cref="RegisterKey"/>）。</summary>
    public void RegisterTarget(string channel, bool isGroup, long id)
    {
        if (id > 0)
        {
            var ch = Channels.IsOfficial(channel) ? Channels.Official : Channels.Private;
            _learned[(isGroup, id)] = ch;
            _byKey[Channels.Key(ch, isGroup, id)] = ch;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (var src in _sources)
        {
            src.MessageReceived -= OnMessage;
            src.Poked -= OnPoked;
            src.MessageRecalled -= OnRecall;
            src.ConnectionChanged -= OnConnection;
            (src as IDisposable)?.Dispose();
        }
    }
}

/// <summary>
/// 「这台机器人有哪些上行通道」的只读台账：面板分块、状态条、`//status` 都从这里问。
/// 单通道部署时由网关自己实现（<see cref="IQqChatSource"/> 的默认实现就是“只有我自己”）。
/// </summary>
public interface IChannelRegistry
{
    IReadOnlyList<IQqChatSource> Sources { get; }

    IQqChatSource? Get(string channel);

    bool IsEnabled(string channel);
}

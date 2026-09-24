using System.Collections.Concurrent;
using BotAgent.Services.Agent;

namespace BotAgent.Services.Voice;

/// <summary>
/// 语音（TTS）用例（批次 4 从 <c>BotAgentHost</c> 抽出的技术侧）：持有客户端、拼分段 URL、
/// 守住"同一会话几秒内不连发"这道**物理性**闸门、给面板提供试听与建议节奏。
///
/// 边界（刻意划清）：**"该不该发语音"是模型的事** —— 2026-09-21 号主明确"判别逻辑要自然"，
/// 所以这里不再按气氛/积极性去拦人，只挡"连发"这种会卡住合成队列的情况。
/// 真正的发送编排（发哪几段、失败改文字）留在回复流程里，因为它要和分句/记账一起看。
/// </summary>
public sealed class VoiceUseCase
{
    /// <summary>
    /// 代码侧唯一的语音硬护栏（秒）——**只是防炸**，不是"该不该发语音"的判断。
    /// 2026-09-21 号主说"判别逻辑不自然"✗：以前这里按「语音积极性」算 15~180 秒的硬门，
    /// 模型兴致上来想用语音，却被代码按回去 ✗，而且它自己看不见这个拦截（只被告知"有硬约束"）。
    /// 现在：**节奏归模型**（提示词把积极性、上次语音的事实都给它），代码只挡同一会话几秒内连发两条。
    /// </summary>
    public const int BreakerSeconds = 8;

    private readonly VoiceService? _voice;
    private readonly SettingsBox _box;
    private readonly Action<string> _log;

    /// <summary>每个会话最近一次发语音的时间（频率门：语音是“稀罕事”，不能每句都发）。</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    public VoiceUseCase(VoiceService? voice, SettingsBox box, Action<string> log)
    {
        _voice = voice;
        _box = box;
        _log = log;
    }

    private AppSettings _settings => _box.Current;

    /// <summary>语音客户端（面板试听 / 音色管理用；TTS 地址没配好时为 null）。</summary>
    public VoiceService? Client => _voice;

    /// <summary>面板上「语音积极性」对应给模型的**建议节奏**（秒）——只进提示词，不拦人。</summary>
    public int SuggestIntervalSeconds => OpenAiClient.VoiceIntervalSeconds(_settings.VoiceEagerness);

    /// <summary>
    /// 语音频率门。为什么要它：
    ///   • 语音在群里是“稀罕事”，几秒内连发就是刷屏（和表情包同一个道理）；
    ///   • 合成+转码是串行的，连发会排队卡住后面的消息。
    /// 只挡“几秒内连发”这种物理性的问题；频率是否得体由模型自己判断（提示词给它积极性与建议节奏）。
    /// </summary>
    public bool Allow(string sourceKey, out string reason)
    {
        reason = string.Empty;
        if (!_lastSent.TryGetValue(sourceKey, out var last))
        {
            return true;
        }

        var since = Clock.Now - last;
        if (since < TimeSpan.FromSeconds(BreakerSeconds))
        {
            reason = $"{since.TotalSeconds:F0} 秒前刚发过语音（同一会话 {BreakerSeconds} 秒内不连发）";
            return false;
        }

        return true;
    }

    /// <summary>记一条"这个会话刚发过语音"（发送成功后调用）。</summary>
    public void MarkSent(string sourceKey) => _lastSent[sourceKey] = Clock.Now;

    /// <summary>
    /// 把模型给的分段文本拼成 /speak URL 列表（最多 3 段）。
    /// 语速/情绪/音调**由模型按语境自己定**（号主 2026-09-21）：它给了就用它的，没给就退回面板默认值。
    /// 返回空列表表示"拼不出来"（TTS 地址没配 / 客户端没起来）。
    /// </summary>
    public List<string> BuildSpeakUrls(IReadOnlyList<string> parts, string? emotion, double? speed, int? pitch)
    {
        var urls = new List<string>();
        if (_voice is null || parts.Count == 0)
        {
            return urls;
        }

        var speedPercent = speed is double s ? (int)Math.Round(s * 100) : (int?)null;
        var first = _voice.BuildSpeakUrl(parts[0], emotionOverride: emotion, speedOverride: speedPercent, pitchOverride: pitch);
        if (first is null)
        {
            return urls;
        }

        urls.Add(first);

        // 第 2、3 段：同一套语气参数（模型只给一份），只是各自合成一次
        for (var i = 1; i < parts.Count; i++)
        {
            var extra = _voice.BuildSpeakUrl(parts[i], emotionOverride: emotion, speedOverride: speedPercent, pitchOverride: pitch);
            if (extra is null)
            {
                break;
            }

            urls.Add(extra);
        }

        return urls;
    }

    /// <summary>
    /// 面板自测：真合成一句语音（走 /speak），把 wav 字节还给面板自己播。
    /// 只合成、不发群 —— 面板里重点验证的是“TTS 服务通不通、音色/语速对不对”。
    /// </summary>
    public async Task<(byte[]? Data, string? Error)> TestAsync(
        string text,
        string? voiceOverride,
        int? speedOverride,
        CancellationToken ct)
    {
        if (_voice is null)
        {
            return (null, "语音服务还没初始化");
        }

        var (data, error) = await _voice.SynthesizeAsync(text, voiceOverride, speedOverride, ct);
        _log(data is null
            ? $"[Voice] 面板试听失败：{error}"
            : $"[Voice] 面板试听成功（{text.Length} 字 → {data.Length / 1024} KB wav）");
        return (data, error);
    }
}

using System.Collections.Concurrent;

namespace BotAgent.Services.Conversations;

/// <summary>
/// 每个会话最近一次"读到的气氛"：模型自己写的 vibe + 一句人话，下一轮当底色用（有 TTL）。
///
/// 为什么存内存不落库：气氛是分钟级的东西，重启后重读一遍上下文就行，不值得占库。
/// 口诀：**读气氛是回复链的事，记不记得住是这里的事**（判断"沉不沉"的纯规则在
/// <see cref="BotAgent.Domain.Reply.VibeRules" />）。
/// </summary>
public sealed class VibeTracker
{
    private readonly ConcurrentDictionary<string, (string Vibe, string Note, DateTimeOffset At)> _vibes = new();

    /// <summary>气氛的保留时长：超过就当"上一轮"已经过去，不再影响提示词。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(45);

    /// <summary>记下这一轮读到的气氛（下一轮当底色用；没有 vibe / 中性 就不记）。</summary>
    public void Remember(string sourceKey, string? vibe, string? note)
    {
        if (string.IsNullOrWhiteSpace(vibe) || vibe == "中性")
        {
            _vibes.TryRemove(sourceKey, out _);
            return;
        }

        _vibes[sourceKey] = (vibe, (note ?? string.Empty).Trim(), Clock.Now);
    }

    /// <summary>当前还记得的气氛（过期/没记返回空串）。</summary>
    public string Current(string sourceKey)
        => _vibes.TryGetValue(sourceKey, out var v) && Clock.Now - v.At <= Ttl ? v.Vibe : string.Empty;

    /// <summary>给模型的"上一轮感觉"（带一句人话），没有就返回 null。</summary>
    public string? Hint(string sourceKey)
    {
        if (!_vibes.TryGetValue(sourceKey, out var v) || Clock.Now - v.At > Ttl)
        {
            return null;
        }

        return v.Note.Length > 0 ? $"{v.Vibe}：{v.Note}" : v.Vibe;
    }

    /// <summary>
    /// 气氛"沉"的时候不发图/不发表情/不戳人：人家在难过或者在对线，
    /// 机器人丢个表情包过去看着就像在笑。（语音也不算合适，一句就够，别弄得很热闹）
    /// </summary>
    public bool IsSober(string sourceKey)
        => Current(sourceKey) is "低落" or "求助" or "吵架" or "生气" or "吐槽";
}

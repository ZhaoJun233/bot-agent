using System.Collections.Concurrent;
using BotAgent.Domain.Qq;

namespace BotAgent.Services.Resilience;

/// <summary>
/// 协议端风控退避台账。
///
/// 退避 key 是完整会话 key，不使用全局开关：一个会话触发风控时，其他租户仍可正常发送。
/// 这里只保存时间、动作和原因码，不保存消息正文或协议端原始响应。
/// </summary>
public sealed class ProtocolRiskBackoff
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(30);
    public const int MaxShortTextChars = 240;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;
    private readonly TimeSpan _duration;

    public ProtocolRiskBackoff(
        Action<string>? log = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? duration = null)
    {
        _log = log;
        _now = now ?? (() => Clock.UtcNow);
        _duration = duration ?? DefaultDuration;
    }

    /// <summary>
    /// 记录一次协议端发送风险。只在明确风险措辞出现时打开退避，
    /// 普通参数错误不会把整个会话摘除。
    /// </summary>
    public void ObserveFailure(string sourceKey, string action, int retcode, string? wording = null)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || !IsRiskSignal(wording))
        {
            return;
        }

        var until = _now().Add(_duration);
        const string reason = "protocol_risk_wording";
        _entries[sourceKey] = new Entry(until, reason, retcode, action);
        _log?.Invoke($"协议端风控退避已开启（会话={Describe(sourceKey)}，动作={action}，原因={reason}，持续 {_duration.TotalMinutes:0} 分钟）");
    }

    /// <summary>读取当前会话是否处于协议端退避中。</summary>
    public bool IsActive(string sourceKey)
    {
        if (!_entries.TryGetValue(sourceKey, out var entry))
        {
            return false;
        }

        if (_now() < entry.Until)
        {
            return true;
        }

        _entries.TryRemove(new KeyValuePair<string, Entry>(sourceKey, entry));
        return false;
    }

    /// <summary>读取当前是否有任何会话处于退避中。</summary>
    public bool HasAnyActive()
    {
        var now = _now();
        foreach (var entry in _entries.Values)
        {
            if (now < entry.Until)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 决定文本是否可以发出。退避期间仅允许直接点名的消息，且强制单条短文本。
    /// </summary>
    public ProtocolBackoffDecision EvaluateText(string sourceKey, bool directAddress, string text)
    {
        if (!IsActive(sourceKey))
        {
            return new ProtocolBackoffDecision(true, text, false, string.Empty);
        }

        if (!directAddress)
        {
            return new ProtocolBackoffDecision(false, string.Empty, true, "protocol_backoff");
        }

        var shortText = Shorten(text, MaxShortTextChars);
        return new ProtocolBackoffDecision(true, shortText, true, "protocol_backoff_direct_text");
    }

    public ProtocolBackoffSnapshot? Snapshot(string sourceKey)
    {
        return IsActive(sourceKey) && _entries.TryGetValue(sourceKey, out var entry)
            ? new ProtocolBackoffSnapshot(sourceKey, entry.Until, entry.ReasonCode, entry.Retcode, entry.Action)
            : null;
    }

    private static bool IsRiskSignal(string? wording)
    {
        if (string.IsNullOrWhiteSpace(wording))
        {
            return false;
        }

        var normalized = wording.Trim();
        return normalized.Contains("风控", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("频繁", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("限流", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("限制", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("封禁", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("risk", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("flood", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("blocked", StringComparison.OrdinalIgnoreCase);
    }

    private static string Shorten(string text, int maxChars)
    {
        var normalized = (text ?? string.Empty).Trim();
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return normalized[..Math.Max(0, maxChars - 1)].TrimEnd() + "…";
    }

    private static string Describe(string sourceKey)
        => Channels.Tag(Channels.ChannelOf(sourceKey));

    private readonly record struct Entry(
        DateTimeOffset Until,
        string ReasonCode,
        int Retcode,
        string Action);
}

public readonly record struct ProtocolBackoffDecision(
    bool Allowed,
    string Text,
    bool Restricted,
    string ReasonCode);

public sealed record ProtocolBackoffSnapshot(
    string SourceKey,
    DateTimeOffset Until,
    string ReasonCode,
    int Retcode,
    string Action);
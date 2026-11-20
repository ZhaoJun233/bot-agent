using System.Collections.Concurrent;
using BotAgent.Services.Voice;

namespace BotAgent.Packages.Voice;

/// <summary>Owns per-conversation voice burst-breaker state and eagerness-to-interval cadence policy.</summary>
public sealed class VoicePolicyEngine : IVoicePolicyContribution
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);

    public bool Allow(string sourceKey, int breakerSeconds, DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        if (!_lastSent.TryGetValue(sourceKey, out var last))
        {
            return true;
        }

        var since = now - last;
        var limit = Math.Max(0, breakerSeconds);
        if (limit > 0 && since < TimeSpan.FromSeconds(limit))
        {
            reason = $"{since.TotalSeconds:F0} 秒前刚发过语音（同一会话 {limit} 秒内不连发）";
            return false;
        }

        return true;
    }

    public void MarkSent(string sourceKey, DateTimeOffset now)
        => _lastSent[sourceKey] = now;

    public int SuggestIntervalSeconds(int eagerness)
        => VoiceCadence.IntervalSeconds(eagerness);

    public void ClearConversation(string sourceKey)
        => _lastSent.TryRemove(sourceKey, out _);
}

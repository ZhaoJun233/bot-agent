using System.Collections.Concurrent;
using BotAgent.Services.Conversations;

namespace BotAgent.Packages.Vibes;

/// <summary>
/// Tracks per-conversation atmosphere snapshots with a 45-minute sliding TTL and classifies sober moods
/// where media/poke/voice responses should stay restrained.
/// </summary>
public sealed class VibesEngine : IVibesContribution
{
    private readonly ConcurrentDictionary<string, (string Vibe, string Note, DateTimeOffset At)> _vibes = new(StringComparer.Ordinal);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(45);

    public void Remember(string sourceKey, string? vibe, string? note, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(vibe) || vibe == "中性")
        {
            _vibes.TryRemove(sourceKey, out _);
            return;
        }

        _vibes[sourceKey] = (vibe, (note ?? string.Empty).Trim(), now);
    }

    public string Current(string sourceKey, DateTimeOffset now)
        => _vibes.TryGetValue(sourceKey, out var v) && now - v.At <= Ttl ? v.Vibe : string.Empty;

    public string? Hint(string sourceKey, DateTimeOffset now)
    {
        if (!_vibes.TryGetValue(sourceKey, out var v) || now - v.At > Ttl)
        {
            return null;
        }

        return v.Note.Length > 0 ? $"{v.Vibe}：{v.Note}" : v.Vibe;
    }

    public bool IsSober(string sourceKey, DateTimeOffset now)
        => Current(sourceKey, now) is "低落" or "求助" or "吵架" or "生气" or "吐槽";

    public void ClearConversation(string sourceKey)
        => _vibes.TryRemove(sourceKey, out _);
}

using System.Collections.Concurrent;
using BotAgent.Services.Music;

namespace BotAgent.Packages.Music;

/// <summary>Owns per-conversation music listen cooldowns, one-shot listening notes, and share deduplication.</summary>
public sealed class MusicPolicyEngine : IMusicPolicyContribution
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastListen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string SongId, DateTimeOffset At)> _recentShares = new(StringComparer.Ordinal);

    public bool TryBeginListen(string sourceKey, int cooldownSeconds, DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, cooldownSeconds));
        lock (_sync)
        {
            if (_lastListen.TryGetValue(sourceKey, out var lastAt) && now - lastAt < cooldown)
            {
                reason = $"「{Math.Round((now - lastAt).TotalSeconds)}s」前刚听过";
                return false;
            }

            _lastListen[sourceKey] = now;
            return true;
        }
    }

    public void AddNote(string sourceKey, string note)
        => _notes.AddOrUpdate(sourceKey, note, (_, old) => old + "\n\n" + note);

    public bool HasNote(string sourceKey) => _notes.ContainsKey(sourceKey);

    public string? TakeNote(string sourceKey)
        => _notes.TryRemove(sourceKey, out var note) ? note : null;

    public bool TryRecordShare(string sourceKey, string songId, TimeSpan dedupWindow, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (dedupWindow > TimeSpan.Zero &&
                _recentShares.TryGetValue(sourceKey, out var prev) &&
                string.Equals(prev.SongId, songId, StringComparison.OrdinalIgnoreCase) &&
                now - prev.At < dedupWindow)
            {
                return false;
            }

            _recentShares[sourceKey] = (songId, now);
            return true;
        }
    }

    public void ClearConversation(string sourceKey)
    {
        lock (_sync)
        {
            _lastListen.Remove(sourceKey);
            _recentShares.Remove(sourceKey);
        }
        _notes.TryRemove(sourceKey, out _);
    }
}

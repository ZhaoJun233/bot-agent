using System.Collections.Concurrent;
using BotAgent.Services.Poke;

namespace BotAgent.Packages.Poke;

/// <summary>
/// Owns per-conversation poke cooldowns, recent-poker validation, and anti-spam poke-back gates.
/// Operates independently even when mood/vibes sinks are absent.
/// </summary>
public sealed class PokePolicyEngine(IPokeInteractionFactSink? factSink = null) : IPokePolicy
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (long PokerId, DateTimeOffset At)> _lastPoke = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long TargetId, DateTimeOffset At)> _lastPokeSent = new(StringComparer.Ordinal);

    public PokeInboundDecision EvaluateInbound(string sourceKey, long userId, int cooldownSeconds, DateTimeOffset now)
    {
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, cooldownSeconds));
        lock (_sync)
        {
            if (cooldown > TimeSpan.Zero &&
                _lastPoke.TryGetValue(sourceKey, out var last) &&
                last.PokerId == userId &&
                now - last.At < cooldown)
            {
                var retryAfter = Math.Max(0, (cooldown - (now - last.At)).TotalSeconds);
                return new PokeInboundDecision(false, retryAfter);
            }

            _lastPoke[sourceKey] = (userId, now);
        }

        factSink?.RecordPokeInteraction(now);
        return new PokeInboundDecision(true, 0);
    }

    public bool RecentlyPoked(string sourceKey, TimeSpan within, DateTimeOffset now)
    {
        lock (_sync) return _lastPoke.TryGetValue(sourceKey, out var last) && now - last.At < within;
    }

    public bool IsRecentPoker(string sourceKey, long userId)
    {
        lock (_sync) return _lastPoke.TryGetValue(sourceKey, out var last) && last.PokerId == userId;
    }

    public bool AllowPokeBack(string sourceKey, long targetId, int cooldownSeconds, DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        (long TargetId, DateTimeOffset At) last;
        lock (_sync)
        {
            if (!_lastPokeSent.TryGetValue(sourceKey, out last))
            {
                return true;
            }
        }

        var since = now - last.At;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, cooldownSeconds));
        if (cooldown > TimeSpan.Zero && since < cooldown)
        {
            reason = $"距上次戳人才 {since.TotalSeconds:F0}s（下限 {cooldown.TotalSeconds:F0}s）";
            return false;
        }

        if (last.TargetId == targetId && since < TimeSpan.FromMinutes(5))
        {
            reason = $"{since.TotalSeconds:F0}s 前刚戳过这个人";
            return false;
        }

        return true;
    }

    public void NotePokedBack(string sourceKey, long targetId, DateTimeOffset now)
    {
        lock (_sync) _lastPokeSent[sourceKey] = (targetId, now);
    }

    public void ClearConversation(string sourceKey)
    {
        lock (_sync)
        {
            _lastPoke.Remove(sourceKey);
            _lastPokeSent.Remove(sourceKey);
        }
    }
}

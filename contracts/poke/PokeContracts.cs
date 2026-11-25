using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Poke;

/// <summary>
/// Narrow interaction fact sink allowing the poke package to report accepted poke interactions
/// without depending on concrete mood/vibes storage types. Optional: poke works normally when absent.
/// </summary>
public interface IPokeInteractionFactSink
{
    void RecordPokeInteraction(DateTimeOffset at);
}

/// <summary>Result of evaluating an inbound self-poke against cooldown policy.</summary>
public readonly record struct PokeInboundDecision(bool ShouldReply, double RetryAfterSeconds);

/// <summary>
/// Generation-bound poke interaction state and rate-limiting policy owned by <c>preset.feature.poke</c>.
/// </summary>
public interface IPokePolicy : IPluginContribution
{
    PokeInboundDecision EvaluateInbound(string sourceKey, long userId, int cooldownSeconds, DateTimeOffset now);

    bool RecentlyPoked(string sourceKey, TimeSpan within, DateTimeOffset now);

    bool IsRecentPoker(string sourceKey, long userId);

    bool AllowPokeBack(string sourceKey, long targetId, int cooldownSeconds, DateTimeOffset now, out string reason);

    void NotePokedBack(string sourceKey, long targetId, DateTimeOffset now);

    void ClearConversation(string sourceKey);
}

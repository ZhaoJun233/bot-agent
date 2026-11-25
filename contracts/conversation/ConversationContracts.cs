using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Reply;

/// <summary>Generic prompt-context section contributed by any installed package without concrete preset coupling.</summary>
public sealed record TurnContextFragment(string SectionTitle, string Content, int Priority = 0);

/// <summary>Generic port allowing any package to contribute per-turn prompt context fragments.</summary>
public interface ITurnContextContributor : IPluginContribution
{
    TurnContextFragment? Contribute(string sourceKey, DateTimeOffset now);
}

/// <summary>Generic port allowing any package to gate per-turn media/action delivery without preset-specific branching.</summary>
public interface ITurnDeliveryContributor : IPluginContribution
{
    bool AllowsDelivery(string sourceKey, string mediaKind, DateTimeOffset now, out string reason);
}

/// <summary>
/// Generation-bound conversation turn budgeting, generic contributor aggregation, and session-deletion fan-out
/// owned by <c>system.conversation</c>. Zero references to concrete preset plugin types.
/// </summary>
public interface IConversationContribution : IPluginContribution
{
    int ComputeContextWindow(int configuredMaxMessages, bool energySaving);

    TurnContextFragment[] CollectContextFragments(
        string sourceKey,
        IEnumerable<ITurnContextContributor> contributors,
        DateTimeOffset now);

    void NotifySessionDeleted(string sourceKey, IEnumerable<Action<string>> cleanupCallbacks);
}

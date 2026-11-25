using BotAgent.Services.Reply;

namespace BotAgent.Packages.Conversation;

/// <summary>
/// Aggregates generic turn context contributors, enforces energy-saving context budgets,
/// and fans out conversation deletion cleanly across independent packages.
/// </summary>
public sealed class ConversationOrchestratorEngine : IConversationContribution
{
    public int ComputeContextWindow(int configuredMaxMessages, bool energySaving)
    {
        var bounded = Math.Clamp(configuredMaxMessages <= 0 ? 30 : configuredMaxMessages, 4, 1000);
        return energySaving ? Math.Min(bounded, 12) : bounded;
    }

    public TurnContextFragment[] CollectContextFragments(
        string sourceKey,
        IEnumerable<ITurnContextContributor> contributors,
        DateTimeOffset now)
    {
        var list = new List<TurnContextFragment>();
        foreach (var contributor in contributors ?? Array.Empty<ITurnContextContributor>())
        {
            if (contributor is null) continue;
            var fragment = contributor.Contribute(sourceKey, now);
            if (fragment is not null && !string.IsNullOrWhiteSpace(fragment.Content))
            {
                list.Add(fragment);
            }
        }

        return list.OrderByDescending(f => f.Priority)
            .ThenBy(f => f.SectionTitle, StringComparer.Ordinal)
            .ToArray();
    }

    public void NotifySessionDeleted(string sourceKey, IEnumerable<Action<string>> cleanupCallbacks)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return;
        foreach (var callback in cleanupCallbacks ?? Array.Empty<Action<string>>())
        {
            try { callback?.Invoke(sourceKey); }
            catch { /* Individual package cleanup failure must not block other packages' cleanup. */ }
        }
    }
}

using System.Collections.Concurrent;
using BotAgent.Services.Profiles;

namespace BotAgent.Packages.Profiles;

/// <summary>
/// Enforces scoped profile presentation rules (manual override takes precedence over auto summary,
/// never resurrects disabled legacy summary paths) and tracks deleted conversations for revocation.
/// </summary>
public sealed class ProfilesPolicyEngine : IProfilesPolicyContribution
{
    private const int MaxTrackedDeletions = 4096;
    private readonly object _deletedSync = new();
    private readonly HashSet<string> _deletedSessions = new(StringComparer.Ordinal);
    private readonly Queue<string> _deletedOrder = new();

    public bool AllowsProfileInjection(bool featureEnabled, bool hasAuthorizedScope)
        => featureEnabled && hasAuthorizedScope;

    public bool AllowsMemoryMutation(bool featureEnabled, bool hasAuthorizedScope, bool legacySummaryDisabled)
        => featureEnabled && hasAuthorizedScope && !legacySummaryDisabled;

    public string FormatProfileSummary(string displayName, string? manualOverride, string? autoSummary, int maxChars)
    {
        var chosen = !string.IsNullOrWhiteSpace(manualOverride) ? manualOverride!.Trim()
            : !string.IsNullOrWhiteSpace(autoSummary) ? autoSummary!.Trim()
            : string.Empty;
        if (chosen.Length == 0)
        {
            return string.Empty;
        }

        var budget = Math.Max(32, maxChars);
        if (chosen.Length > budget)
        {
            chosen = chosen[..budget] + "…";
        }

        return string.IsNullOrWhiteSpace(displayName) ? chosen : $"{displayName.Trim()}：{chosen}";
    }

    public void NotifyConversationDeleted(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return;
        lock (_deletedSync)
        {
            if (_deletedSessions.Add(sourceKey))
            {
                _deletedOrder.Enqueue(sourceKey);
                while (_deletedOrder.Count > MaxTrackedDeletions)
                {
                    _deletedSessions.Remove(_deletedOrder.Dequeue());
                }
            }
        }
    }

    public bool WasConversationDeleted(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return false;
        lock (_deletedSync)
        {
            return _deletedSessions.Contains(sourceKey);
        }
    }
}

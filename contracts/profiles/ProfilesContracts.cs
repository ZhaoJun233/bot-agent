using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Profiles;

/// <summary>Credential-free candidate snapshot for background persona summarization.</summary>
public sealed record ProfileSummaryCandidateDto(
    string Uid,
    string Name,
    string Scope,
    string ExistingSummary,
    string[] NewMessages,
    long ThroughSeq,
    int FoldedCount);

/// <summary>Read-only settings and safety flags governing persona summarization.</summary>
public sealed record ProfileSummarySettingsSnapshot(
    bool EnableProfileSummary,
    int IntervalSeconds,
    int Threshold,
    int MaxChars,
    bool LegacySummaryDisabled,
    bool HostDisposed);

/// <summary>
/// Narrow, credential-free host ports provided to <c>preset.feature.profiles</c> for
/// persona summarization storage and model execution.
/// </summary>
public interface IProfilesHostPorts
{
    ProfileSummarySettingsSnapshot ReadSummarySettings();

    bool IsProfilesFeatureAllowed();

    ProfileSummaryCandidateDto[] FindSummarizable(int threshold, int maxCandidates);

    Task<string?> SummarizePersonaAsync(string displayName, string existingSummary, string[] newMessages, int maxChars);

    Task<string?> SummarizeCandidateAsync(ProfileSummaryCandidateDto candidate, int maxChars)
        => SummarizePersonaAsync(string.IsNullOrWhiteSpace(candidate.Name) ? $"QQ{candidate.Uid}" : candidate.Name,
            candidate.ExistingSummary, candidate.NewMessages, maxChars);

    void ApplySummary(string uid, string scope, string summary, long throughSeq, int foldedCount);
    bool TryApplySummary(string uid, string scope, string summary, long throughSeq, int foldedCount)
    {
        ApplySummary(uid, scope, summary, throughSeq, foldedCount);
        return true;
    }

    void Log(string message);

    void LogInternal(string message);
}

/// <summary>
/// Generation-bound member profile &amp; scoped memory presentation/revocation policy
/// owned by <c>preset.feature.profiles</c>. Preserves unified memory authorization boundaries.
/// </summary>
public interface IProfilesPolicyContribution : IPluginContribution
{
    bool AllowsProfileInjection(bool featureEnabled, bool hasAuthorizedScope);

    bool AllowsMemoryMutation(bool featureEnabled, bool hasAuthorizedScope, bool legacySummaryDisabled);

    string FormatProfileSummary(string displayName, string? manualOverride, string? autoSummary, int maxChars);

    void NotifyConversationDeleted(string sourceKey);

    bool WasConversationDeleted(string sourceKey);
}

/// <summary>
/// Full profiles runtime coordinator owned by <c>preset.feature.profiles</c>:
/// background persona summarization timer/runner (strictly respecting unified-memory revocation),
/// plus scoped injection and conversation-deletion revocation policy.
/// </summary>
public interface IProfilesRuntimeContribution : IProfilesPolicyContribution
{
    long SummaryDoneCount { get; }

    long SummaryFailCount { get; }

    bool HasActiveSummaryTimer { get; }

    void EnsureSummaryTimer(bool enabled, int intervalSeconds);

    void StopSummaryTimer();

    Task RunSummarySweepAsync();
}

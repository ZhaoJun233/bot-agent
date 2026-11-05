using System.Collections.Immutable;

namespace BotAgent.Domain.Configuration;

/// <summary>
/// Process-local apply evidence. A receipt describes one request; this ledger describes
/// confirmation of the currently published snapshot. It never stores values or secrets.
/// </summary>
public sealed class ConfigurationRuntimeTracker
{
    private readonly object _gate = new();
    private ImmutableDictionary<ApplyStep, ApplyState> _steps = ImmutableDictionary<ApplyStep, ApplyState>.Empty;

    public void BeginApply(bool projectTts)
    {
        lock (_gate)
        {
            foreach (var step in new[] { ApplyStep.PublishSnapshot, ApplyStep.RuntimePolicy,
                         ApplyStep.ModelProjection, ApplyStep.ConcurrencyGate, ApplyStep.ContextWindow, ApplyStep.Schedulers })
                _steps = _steps.SetItem(step, ApplyState.Unknown);
            // Unrelated saves do not run the external projection and cannot confirm it.
            if (projectTts) _steps = _steps.SetItem(ApplyStep.TtsFileProjection, ApplyState.Unknown);
        }
    }

    public void Record(ApplyStepReport report)
    {
        lock (_gate) _steps = _steps.SetItem(report.Step, report.State);
    }

    public ImmutableDictionary<ApplyStep, ApplyState> Snapshot()
    {
        lock (_gate) return _steps;
    }

    public static ConfigurationRuntimeState Resolve(ConfigurationFieldId field, FieldEffect effect,
        bool desiredMatchesActive, ImmutableDictionary<ApplyStep, ApplyState> steps)
    {
        if (effect == FieldEffect.Restart)
            return desiredMatchesActive ? ConfigurationRuntimeState.Current : ConfigurationRuntimeState.RestartRequired;
        var states = Dependencies(field, effect).Select(step => steps.GetValueOrDefault(step,
            step == ApplyStep.TtsFileProjection ? ApplyState.Unknown : ApplyState.Applied)).ToArray();
        if (states.Contains(ApplyState.Failed)) return ConfigurationRuntimeState.ApplyFailed;
        if (states.Any(state => state is not (ApplyState.Applied or ApplyState.RestartRequired)))
            return ConfigurationRuntimeState.Unknown;
        if (states.Contains(ApplyState.RestartRequired))
            return desiredMatchesActive ? ConfigurationRuntimeState.Current : ConfigurationRuntimeState.RestartRequired;
        if (!desiredMatchesActive) return ConfigurationRuntimeState.Unknown;
        return ConfigurationRuntimeState.Current;
    }

    private static ApplyStep[] Dependencies(ConfigurationFieldId field, FieldEffect effect)
    {
        if (effect == FieldEffect.ExternalProjection) return [ApplyStep.TtsFileProjection];
        return field switch
        {
            // SyncBrainFields and the provider registry are separate from snapshot publication.
            ConfigurationFieldId.Model or ConfigurationFieldId.ModelBaseUrl
                or ConfigurationFieldId.RegisteredProviders or ConfigurationFieldId.BotPersona
                or ConfigurationFieldId.AiDesire or ConfigurationFieldId.SuitabilityThreshold
                or ConfigurationFieldId.MaxContextMessages => [ApplyStep.ModelProjection],
            ConfigurationFieldId.EnableStickers => [ApplyStep.RuntimePolicy, ApplyStep.Schedulers],
            ConfigurationFieldId.WhitelistGroups or ConfigurationFieldId.WhitelistPrivates
                or ConfigurationFieldId.OfficialWhitelistGroups or ConfigurationFieldId.OfficialWhitelistPrivates
                or ConfigurationFieldId.FeishuWhitelist or ConfigurationFieldId.FeishuWhitelistPrivates
                or ConfigurationFieldId.PlatformPolicies or ConfigurationFieldId.PrivateChatEnabled
                or ConfigurationFieldId.OfficialChatEnabled or ConfigurationFieldId.OfficialEnabled
                or ConfigurationFieldId.FeishuEnabled or ConfigurationFieldId.AgentAllowedUsers
                or ConfigurationFieldId.EnableWebSearch or ConfigurationFieldId.EnableMusic
                or ConfigurationFieldId.EnableVoice or ConfigurationFieldId.EnablePoke
                or ConfigurationFieldId.ScenarioPreset or ConfigurationFieldId.EnableApprovals
                or ConfigurationFieldId.EnableQuestions or ConfigurationFieldId.ParticipationMaxConsecutiveReplies
                or ConfigurationFieldId.ParticipationCooldownSeconds or ConfigurationFieldId.ParticipationProbingMaxReplies
                or ConfigurationFieldId.ParticipationMaxActiveLifetimeSeconds
                or ConfigurationFieldId.ParticipationMaxExitingLifetimeSeconds => [ApplyStep.RuntimePolicy],
            ConfigurationFieldId.MaxConcurrentReplies => [ApplyStep.ConcurrencyGate],
            ConfigurationFieldId.MaxMessagesPerConversation => [ApplyStep.ContextWindow],
            ConfigurationFieldId.HealthReportEnabled or ConfigurationFieldId.HealthReportTargets
                or ConfigurationFieldId.HealthReportTime or ConfigurationFieldId.IdleFallbackSeconds
                or ConfigurationFieldId.EnableProfileSummary or ConfigurationFieldId.ProfileSummaryIntervalSeconds
                or ConfigurationFieldId.StickerLibraryMax => [ApplyStep.Schedulers],
            _ => [ApplyStep.PublishSnapshot],
        };
    }
}

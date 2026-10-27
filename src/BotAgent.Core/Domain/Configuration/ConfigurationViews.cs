using System.Collections.Immutable;

namespace BotAgent.Domain.Configuration;

public sealed class AgentView
{
    public required ReadField<string> AgentAllowedUsers { get; init; }
    public required ReadField<string> AgentPrefix { get; init; }
    public required ReadField<int> AgentProgressSeconds { get; init; }
    public required ReadField<string> AgentPrompt { get; init; }
    public required ReadField<string> AgentReasoningEffort { get; init; }
    public required ReadField<string> AgentReasoningLevels { get; init; }
    public required ReadField<int> AgentReplyMaxChars { get; init; }
    public required ReadField<string> AgentServerBaseUrl { get; init; }
    public required ReadField<int> AgentServerCommandTimeoutSeconds { get; init; }
    public required ReadField<bool> AgentServerDocker { get; init; }
    public required ReadField<bool> AgentServerKeepContext { get; init; }
    public required ReadField<bool> EnableNaturalAgent { get; init; }
    public required ReadField<int> AgentServerMaxSteps { get; init; }
    public required ReadField<string> AgentServerModel { get; init; }
    public required ReadField<string> AgentServerQqActions { get; init; }
    public required ReadField<string> AgentServerTools { get; init; }
    public required ReadField<bool> AgentServerUseGate { get; init; }
    public required ReadField<string> AgentServerWorkDir { get; init; }
    public required ReadField<bool> EnableAgentBridge { get; init; }
    public required ReadField<bool> EnableAgentMask { get; init; }
    public required ReadField<bool> EnableServerAgent { get; init; }
    public required ReadField<bool> PanelDeployEnabled { get; init; }
    public required ReadField<string> PanelDeployUrl { get; init; }
}

public sealed class BehaviorView
{
    public required ReadField<int> AiDesire { get; init; }
    public required ReadField<bool> AiModeEnabled { get; init; }
    public required ReadField<string> ApprovalApprovers { get; init; }
    public required ReadField<string> BotPersona { get; init; }
    public required ReadField<bool> EnableApprovals { get; init; }
    public required ReadField<bool> EnableAtmosphereDamping { get; init; }
    public required ReadField<bool> EnableLinkPreview { get; init; }
    public required ReadField<bool> EnableParticipationGating { get; init; }
    public required ReadField<bool> EnablePoke { get; init; }
    public required ReadField<bool> EnableProactive { get; init; }
    public required ReadField<bool> EnableProfileSummary { get; init; }
    public required ReadField<bool> EnableQuestions { get; init; }
    public required ReadField<bool> EnableStickers { get; init; }
    public required ReadField<bool> EnableWebSearch { get; init; }
    public required ReadField<bool> FilterActionNarration { get; init; }
    public required ReadField<int> GroupCooldownSeconds { get; init; }
    public required ReadField<int> IdleFallbackSeconds { get; init; }
    public required ReadField<bool> IgnoreBracketMessages { get; init; }
    public required ReadField<int> LinkPreviewMax { get; init; }
    public required ReadField<int> LinkPreviewTimeoutSeconds { get; init; }
    public required ReadField<int> MaxAgentSteps { get; init; }
    public required ReadField<int> MaxConcurrentReplies { get; init; }
    public required ReadField<int> MaxContextMessages { get; init; }
    public required ReadField<int> MaxMessagesPerConversation { get; init; }
    public required ReadField<int> MaxProfileChars { get; init; }
    public required ReadField<int> MoodTtlSeconds { get; init; }
    public required ReadField<int> ParticipationCooldownSeconds { get; init; }
    public required ReadField<int> ParticipationMaxActiveLifetimeSeconds { get; init; }
    public required ReadField<int> ParticipationMaxConsecutiveReplies { get; init; }
    public required ReadField<int> ParticipationMaxExitingLifetimeSeconds { get; init; }
    public required ReadField<int> ParticipationProbingMaxReplies { get; init; }
    public required ReadField<int> PokeCooldownSeconds { get; init; }
    public required ReadField<int> PrivateCooldownSeconds { get; init; }
    public required ReadField<int> ProactiveCooldownSeconds { get; init; }
    public required ReadField<int> ProactiveQuietSeconds { get; init; }
    public required ReadField<int> ProfileLookupCount { get; init; }
    public required ReadField<int> ProfileSummaryIntervalSeconds { get; init; }
    public required ReadField<int> ProfileSummaryLines { get; init; }
    public required ReadField<int> ProfileSummaryMaxChars { get; init; }
    public required ReadField<int> ProfileSummaryThreshold { get; init; }
    public required ReadField<string> ScenarioPreset { get; init; }
    public required ReadField<int> SegmentDelayMs { get; init; }
    public required ReadField<bool> SplitReplies { get; init; }
    public required ReadField<int> StickerCandidates { get; init; }
    public required ReadField<int> StickerCooldownSeconds { get; init; }
    public required ReadField<int> StickerCurateIntervalSeconds { get; init; }
    public required ReadField<int> StickerLibraryMax { get; init; }
    public required ReadField<int> SuitabilityThreshold { get; init; }
    public required ReadField<int> WebSearchCooldownSeconds { get; init; }
    public required ReadField<int> WebSearchMaxResults { get; init; }
    public required ReadField<string> WebSearchSources { get; init; }
    public required ReadField<int> WebSearchTimeoutSeconds { get; init; }
    public required ReadField<bool> WebSearchUseModelSearch { get; init; }
}

public sealed class ModelView
{
    public required ReadField<bool> AdaptiveSamplingEnabled { get; init; }
    public required ReadField<double> DefaultTemperature { get; init; }
    public required ReadField<double> DefaultTopP { get; init; }
    public required ReadField<double> EmotionalTemperature { get; init; }
    public required ReadField<double> EmotionalTopP { get; init; }
    public required ReadField<string> FastModel { get; init; }
    public required ReadField<bool> FastReply { get; init; }
    public required ReadField<string> FastThinkingBudget { get; init; }
    public required ReadField<string> FastThinkingCustomBudget { get; init; }
    public required ReadField<int> MaxTokens { get; init; }
    public required ReadField<string> Model { get; init; }
    public required ReadField<string> ModelBaseUrl { get; init; }
    public required ReadField<double> RationalTemperature { get; init; }
    public required ReadField<double> RationalTopP { get; init; }
    public required ReadField<string> ThinkingBudget { get; init; }
    public required ReadField<string> ThinkingCustomBudget { get; init; }
}

public sealed class MusicView
{
    public required ReadField<bool> EnableMusic { get; init; }
    public required ReadField<int> MusicBitrate { get; init; }
    public required ReadField<bool> MusicKeepAudio { get; init; }
    public required ReadField<int> MusicLibraryMax { get; init; }
    public required ReadField<int> MusicListenCooldownSeconds { get; init; }
    public required ReadField<int> MusicMaxAnalysisSeconds { get; init; }
    public required ReadField<int> MusicMaxDownloadMb { get; init; }
    public required ReadField<int> MusicNoteTtlDays { get; init; }
    public required ReadField<bool> MusicSendAudioToModel { get; init; }
    public required ReadField<string> MusicSources { get; init; }
    public required ReadField<string> MusicUnderstandModel { get; init; }
    public required ReadField<string> NeteaseBaseUrl { get; init; }
}

public sealed class PlatformView
{
    public required ReadField<string> FeishuApiBase { get; init; }
    public required ReadField<string> FeishuAppId { get; init; }
    public required ReadField<bool> FeishuEnabled { get; init; }
    public required ReadField<string> FeishuWhitelist { get; init; }
    public required ReadField<string> LocalChannelIds { get; init; }
    public required ReadField<string> MessageWhitelist { get; init; }
    public required ReadField<string> OfficialAppId { get; init; }
    public required ReadField<bool> OfficialChatEnabled { get; init; }
    public required ReadField<bool> OfficialEnabled { get; init; }
    public required ReadField<bool> OfficialSandbox { get; init; }
    public required ReadField<string> OfficialWhitelistGroups { get; init; }
    public required ReadField<string> OfficialWhitelistPrivates { get; init; }
    public required ReadField<ImmutableArray<PlatformPolicyEntryView>> PlatformPolicies { get; init; }
    public required ReadField<bool> PrivateChatEnabled { get; init; }
    public required ReadField<string> WhitelistGroups { get; init; }
    public required ReadField<string> WhitelistPrivates { get; init; }
}

public sealed class ReportingView
{
    public required ReadField<bool> HealthReportEnabled { get; init; }
    public required ReadField<string> HealthReportTargets { get; init; }
    public required ReadField<string> HealthReportTime { get; init; }
}

public sealed class VoiceView
{
    public required ReadField<bool> EnableVoice { get; init; }
    public required ReadField<string> TtsApiBase { get; init; }
    public required ReadField<string> TtsModel { get; init; }
    public required ReadField<string> TtsProvider { get; init; }
    public required ReadField<string> TtsServiceUrl { get; init; }
    public required ReadField<int> VoiceEagerness { get; init; }
    public required ReadField<string> VoiceEmotion { get; init; }
    public required ReadField<int> VoiceMaxChars { get; init; }
    public required ReadField<string> VoiceName { get; init; }
    public required ReadField<int> VoicePitch { get; init; }
    public required ReadField<int> VoiceSpeed { get; init; }
    public required ReadField<int> VoiceVol { get; init; }
}

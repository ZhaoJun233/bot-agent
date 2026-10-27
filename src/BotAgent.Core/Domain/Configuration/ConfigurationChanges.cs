using System.Collections.Immutable;

namespace BotAgent.Domain.Configuration;

public sealed class AgentChanges
{
    public FieldEdit<string> AgentAllowedUsers { get; init; }
    public FieldEdit<string> AgentPrefix { get; init; }
    public FieldEdit<int> AgentProgressSeconds { get; init; }
    public FieldEdit<string> AgentPrompt { get; init; }
    public FieldEdit<string> AgentReasoningEffort { get; init; }
    public FieldEdit<string> AgentReasoningLevels { get; init; }
    public FieldEdit<int> AgentReplyMaxChars { get; init; }
    public FieldEdit<string> AgentServerBaseUrl { get; init; }
    public FieldEdit<int> AgentServerCommandTimeoutSeconds { get; init; }
    public FieldEdit<bool> AgentServerDocker { get; init; }
    public FieldEdit<bool> AgentServerKeepContext { get; init; }
    public FieldEdit<bool> EnableNaturalAgent { get; init; }
    public FieldEdit<int> AgentServerMaxSteps { get; init; }
    public FieldEdit<string> AgentServerModel { get; init; }
    public FieldEdit<string> AgentServerQqActions { get; init; }
    public FieldEdit<string> AgentServerTools { get; init; }
    public FieldEdit<bool> AgentServerUseGate { get; init; }
    public FieldEdit<string> AgentServerWorkDir { get; init; }
    public FieldEdit<bool> EnableAgentBridge { get; init; }
    public FieldEdit<bool> EnableAgentMask { get; init; }
    public FieldEdit<bool> EnableServerAgent { get; init; }
    public FieldEdit<bool> PanelDeployEnabled { get; init; }
    public FieldEdit<string> PanelDeployUrl { get; init; }
}

public sealed class BehaviorChanges
{
    public FieldEdit<int> AiDesire { get; init; }
    public FieldEdit<bool> AiModeEnabled { get; init; }
    public FieldEdit<string> ApprovalApprovers { get; init; }
    public FieldEdit<string> BotPersona { get; init; }
    public FieldEdit<bool> EnableApprovals { get; init; }
    public FieldEdit<bool> EnableAtmosphereDamping { get; init; }
    public FieldEdit<bool> EnableLinkPreview { get; init; }
    public FieldEdit<bool> EnableParticipationGating { get; init; }
    public FieldEdit<bool> EnablePoke { get; init; }
    public FieldEdit<bool> EnableProactive { get; init; }
    public FieldEdit<bool> EnableProfileSummary { get; init; }
    public FieldEdit<bool> EnableQuestions { get; init; }
    public FieldEdit<bool> EnableStickers { get; init; }
    public FieldEdit<bool> EnableWebSearch { get; init; }
    public FieldEdit<bool> FilterActionNarration { get; init; }
    public FieldEdit<int> GroupCooldownSeconds { get; init; }
    public FieldEdit<int> IdleFallbackSeconds { get; init; }
    public FieldEdit<bool> IgnoreBracketMessages { get; init; }
    public FieldEdit<int> LinkPreviewMax { get; init; }
    public FieldEdit<int> LinkPreviewTimeoutSeconds { get; init; }
    public FieldEdit<int> MaxAgentSteps { get; init; }
    public FieldEdit<int> MaxConcurrentReplies { get; init; }
    public FieldEdit<int> MaxContextMessages { get; init; }
    public FieldEdit<int> MaxMessagesPerConversation { get; init; }
    public FieldEdit<int> MaxProfileChars { get; init; }
    public FieldEdit<int> MoodTtlSeconds { get; init; }
    public FieldEdit<int> ParticipationCooldownSeconds { get; init; }
    public FieldEdit<int> ParticipationMaxActiveLifetimeSeconds { get; init; }
    public FieldEdit<int> ParticipationMaxConsecutiveReplies { get; init; }
    public FieldEdit<int> ParticipationMaxExitingLifetimeSeconds { get; init; }
    public FieldEdit<int> ParticipationProbingMaxReplies { get; init; }
    public FieldEdit<int> PokeCooldownSeconds { get; init; }
    public FieldEdit<int> PrivateCooldownSeconds { get; init; }
    public FieldEdit<int> ProactiveCooldownSeconds { get; init; }
    public FieldEdit<int> ProactiveQuietSeconds { get; init; }
    public FieldEdit<int> ProfileLookupCount { get; init; }
    public FieldEdit<int> ProfileSummaryIntervalSeconds { get; init; }
    public FieldEdit<int> ProfileSummaryLines { get; init; }
    public FieldEdit<int> ProfileSummaryMaxChars { get; init; }
    public FieldEdit<int> ProfileSummaryThreshold { get; init; }
    public FieldEdit<string> ScenarioPreset { get; init; }
    public FieldEdit<int> SegmentDelayMs { get; init; }
    public FieldEdit<bool> SplitReplies { get; init; }
    public FieldEdit<int> StickerCandidates { get; init; }
    public FieldEdit<int> StickerCooldownSeconds { get; init; }
    public FieldEdit<int> StickerCurateIntervalSeconds { get; init; }
    public FieldEdit<int> StickerLibraryMax { get; init; }
    public FieldEdit<int> SuitabilityThreshold { get; init; }
    public FieldEdit<int> WebSearchCooldownSeconds { get; init; }
    public FieldEdit<int> WebSearchMaxResults { get; init; }
    public FieldEdit<string> WebSearchSources { get; init; }
    public FieldEdit<int> WebSearchTimeoutSeconds { get; init; }
    public FieldEdit<bool> WebSearchUseModelSearch { get; init; }
}

public sealed class ModelChanges
{
    public FieldEdit<bool> AdaptiveSamplingEnabled { get; init; }
    public FieldEdit<double> DefaultTemperature { get; init; }
    public FieldEdit<double> DefaultTopP { get; init; }
    public FieldEdit<double> EmotionalTemperature { get; init; }
    public FieldEdit<double> EmotionalTopP { get; init; }
    public FieldEdit<string> FastModel { get; init; }
    public FieldEdit<bool> FastReply { get; init; }
    public FieldEdit<string> FastThinkingBudget { get; init; }
    public FieldEdit<string> FastThinkingCustomBudget { get; init; }
    public FieldEdit<int> MaxTokens { get; init; }
    public FieldEdit<string> Model { get; init; }
    public FieldEdit<string> ModelBaseUrl { get; init; }
    public FieldEdit<double> RationalTemperature { get; init; }
    public FieldEdit<double> RationalTopP { get; init; }
    public FieldEdit<string> ThinkingBudget { get; init; }
    public FieldEdit<string> ThinkingCustomBudget { get; init; }
}

public sealed class MusicChanges
{
    public FieldEdit<bool> EnableMusic { get; init; }
    public FieldEdit<int> MusicBitrate { get; init; }
    public FieldEdit<bool> MusicKeepAudio { get; init; }
    public FieldEdit<int> MusicLibraryMax { get; init; }
    public FieldEdit<int> MusicListenCooldownSeconds { get; init; }
    public FieldEdit<int> MusicMaxAnalysisSeconds { get; init; }
    public FieldEdit<int> MusicMaxDownloadMb { get; init; }
    public FieldEdit<int> MusicNoteTtlDays { get; init; }
    public FieldEdit<bool> MusicSendAudioToModel { get; init; }
    public FieldEdit<string> MusicSources { get; init; }
    public FieldEdit<string> MusicUnderstandModel { get; init; }
    public FieldEdit<string> NeteaseBaseUrl { get; init; }
}

public sealed class PlatformChanges
{
    public FieldEdit<string> FeishuApiBase { get; init; }
    public FieldEdit<string> FeishuAppId { get; init; }
    public FieldEdit<bool> FeishuEnabled { get; init; }
    public FieldEdit<string> FeishuWhitelist { get; init; }
    public FieldEdit<string> LocalChannelIds { get; init; }
    public FieldEdit<string> MessageWhitelist { get; init; }
    public FieldEdit<string> OfficialAppId { get; init; }
    public FieldEdit<bool> OfficialChatEnabled { get; init; }
    public FieldEdit<bool> OfficialEnabled { get; init; }
    public FieldEdit<bool> OfficialSandbox { get; init; }
    public FieldEdit<string> OfficialWhitelistGroups { get; init; }
    public FieldEdit<string> OfficialWhitelistPrivates { get; init; }
    public FieldEdit<ImmutableArray<PlatformPolicyEntry>> PlatformPolicies { get; init; }
    public FieldEdit<bool> PrivateChatEnabled { get; init; }
    public FieldEdit<string> WhitelistGroups { get; init; }
    public FieldEdit<string> WhitelistPrivates { get; init; }
}

public sealed class ReportingChanges
{
    public FieldEdit<bool> HealthReportEnabled { get; init; }
    public FieldEdit<string> HealthReportTargets { get; init; }
    public FieldEdit<string> HealthReportTime { get; init; }
}

public sealed class VoiceChanges
{
    public FieldEdit<bool> EnableVoice { get; init; }
    public FieldEdit<string> TtsApiBase { get; init; }
    public FieldEdit<string> TtsModel { get; init; }
    public FieldEdit<string> TtsProvider { get; init; }
    public FieldEdit<string> TtsServiceUrl { get; init; }
    public FieldEdit<int> VoiceEagerness { get; init; }
    public FieldEdit<string> VoiceEmotion { get; init; }
    public FieldEdit<int> VoiceMaxChars { get; init; }
    public FieldEdit<string> VoiceName { get; init; }
    public FieldEdit<int> VoicePitch { get; init; }
    public FieldEdit<int> VoiceSpeed { get; init; }
    public FieldEdit<int> VoiceVol { get; init; }
}

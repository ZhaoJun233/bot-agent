using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Settings;
using BotAgent.Adapters.Persistence;
using System.Collections.Immutable;
using BotAgent.Domain.Configuration;

namespace BotAgent.Adapters.Panel;

internal static class PanelLegacyScalarFields
{
    /// <summary>Only the 133 scalar inputs; composite policy plans are assembled separately.</summary>
    internal static ConfigurationPatch ParseLegacyScalarFields(JsonObject body)
        => ParseLegacyFields(body, default);

    internal static ConfigurationPatch ParseLegacyFields(JsonObject body,
        FieldEdit<ImmutableArray<PlatformPolicyEntry>> policies)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new(ParseLegacyBehaviorFields(body), ParseLegacyModelFields(body), ParseLegacyAgentFields(body),
            ParseLegacyPlatformFieldsWithPolicies(body, policies), ParseLegacyVoiceFields(body), ParseLegacyMusicFields(body),
            ParseLegacyReportingFields(body), null);
    }

    internal static BehaviorChanges ParseLegacyBehaviorFields(JsonObject body)
        => new()
        {
            AiDesire = PanelLegacyFieldEdits.LegacyField(body, "aiDesire", node => Math.Clamp(node.GetValue<int>(), 0, 100)),
            ReplyPacingEnabled = PanelLegacyFieldEdits.LegacyField(body, "replyPacingEnabled", node => node.GetValue<bool>()),
            GroupInterjectionProbability = PanelLegacyFieldEdits.LegacyField(body, "groupInterjectionProbability", node => node.GetValue<int>()),
            GroupInterjectionCooldownSeconds = PanelLegacyFieldEdits.LegacyField(body, "groupInterjectionCooldownSeconds", node => node.GetValue<int>()),
            GroupContinuationWaitSeconds = PanelLegacyFieldEdits.LegacyField(body, "groupContinuationWaitSeconds", node => node.GetValue<int>()),
            PrivateContinuationWaitSeconds = PanelLegacyFieldEdits.LegacyField(body, "privateContinuationWaitSeconds", node => node.GetValue<double>()),
            AiModeEnabled = PanelLegacyFieldEdits.LegacyField(body, "aiModeEnabled", node => node.GetValue<bool>()),
            ApprovalApprovers = PanelLegacyFieldEdits.LegacyField(body, "approvalApprovers", node => PanelLegacyFieldEdits.LegacyTruncate((node.GetValue<string>() ?? string.Empty).Trim(), 300)),
            BotPersona = PanelLegacyFieldEdits.LegacyField(body, "botPersona", node => node.GetValue<string>() ?? string.Empty),
            EnableApprovals = PanelLegacyFieldEdits.LegacyField(body, "enableApprovals", node => node.GetValue<bool>()),
            EnableAtmosphereDamping = PanelLegacyFieldEdits.LegacyField(body, "enableAtmosphereDamping", node => node.GetValue<bool>()),
            EnableLinkPreview = PanelLegacyFieldEdits.LegacyField(body, "enableLinkPreview", node => node.GetValue<bool>()),
            EnableParticipationGating = PanelLegacyFieldEdits.LegacyField(body, "enableParticipationGating", node => node.GetValue<bool>()),
            EnablePoke = PanelLegacyFieldEdits.LegacyField(body, "enablePoke", node => node.GetValue<bool>()),
            EnableProactive = PanelLegacyFieldEdits.LegacyField(body, "enableProactive", node => node.GetValue<bool>()),
            EnableProfileSummary = PanelLegacyFieldEdits.LegacyField(body, "enableProfileSummary", node => node.GetValue<bool>()),
            EnableQuestions = PanelLegacyFieldEdits.LegacyField(body, "enableQuestions", node => node.GetValue<bool>()),
            EnableStickers = PanelLegacyFieldEdits.LegacyField(body, "enableStickers", node => node.GetValue<bool>()),
            EnableWebSearch = PanelLegacyFieldEdits.LegacyField(body, "enableWebSearch", node => node.GetValue<bool>()),
            FilterActionNarration = PanelLegacyFieldEdits.LegacyField(body, "filterActionNarration", node => node.GetValue<bool>()),
            GroupCooldownSeconds = PanelLegacyFieldEdits.LegacyField(body, "groupCooldownSeconds", node => Math.Max(0, node.GetValue<int>())),
            IdleFallbackSeconds = PanelLegacyFieldEdits.LegacyField(body, "idleFallbackSeconds", node => Math.Max(0, node.GetValue<int>())),
            IgnoreBracketMessages = PanelLegacyFieldEdits.LegacyField(body, "ignoreBracketMessages", node => node.GetValue<bool>()),
            LinkPreviewMax = PanelLegacyFieldEdits.LegacyField(body, "linkPreviewMax", node => Math.Clamp(node.GetValue<int>(), 0, 5)),
            LinkPreviewTimeoutSeconds = PanelLegacyFieldEdits.LegacyField(body, "linkPreviewTimeoutSeconds", node => Math.Clamp(node.GetValue<int>(), 2, 30)),
            MaxAgentSteps = PanelLegacyFieldEdits.LegacyField(body, "maxAgentSteps", node => Math.Clamp(node.GetValue<int>(), 1, 3)),
            MaxConcurrentReplies = PanelLegacyFieldEdits.LegacyField(body, "maxConcurrentReplies", node => Math.Clamp(node.GetValue<int>(), 1, 16)),
            MaxContextMessages = PanelLegacyFieldEdits.LegacyField(body, "maxContextMessages", node => Math.Clamp(node.GetValue<int>(), 10, 1000)),
            MaxMessagesPerConversation = PanelLegacyFieldEdits.LegacyField(body, "maxMessagesPerConversation", node => Math.Clamp(node.GetValue<int>(), 20, 100000)),
            MaxProfileChars = PanelLegacyFieldEdits.LegacyField(body, "maxProfileChars", node => Math.Clamp(node.GetValue<int>(), 0, 20000)),
            MoodTtlSeconds = PanelLegacyFieldEdits.LegacyField(body, "moodTtlSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400 * 7)),
            ParticipationCooldownSeconds = PanelLegacyFieldEdits.LegacyField(body, "participationCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 600)),
            ParticipationMaxActiveLifetimeSeconds = PanelLegacyFieldEdits.LegacyField(body, "participationMaxActiveLifetimeSeconds", node => Math.Clamp(node.GetValue<int>(), 30, 3600)),
            ParticipationMaxConsecutiveReplies = PanelLegacyFieldEdits.LegacyField(body, "participationMaxConsecutiveReplies", node => Math.Clamp(node.GetValue<int>(), 1, 10)),
            ParticipationMaxExitingLifetimeSeconds = PanelLegacyFieldEdits.LegacyField(body, "participationMaxExitingLifetimeSeconds", node => Math.Clamp(node.GetValue<int>(), 10, 3600)),
            ParticipationProbingMaxReplies = PanelLegacyFieldEdits.LegacyField(body, "participationProbingMaxReplies", node => Math.Clamp(node.GetValue<int>(), 1, 3)),
            PokeCooldownSeconds = PanelLegacyFieldEdits.LegacyField(body, "pokeCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            PrivateCooldownSeconds = PanelLegacyFieldEdits.LegacyField(body, "privateCooldownSeconds", node => Math.Max(0, node.GetValue<int>())),
            ProactiveCooldownSeconds = PanelLegacyFieldEdits.LegacyField(body, "proactiveCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 60, 86400)),
            ProactiveQuietSeconds = PanelLegacyFieldEdits.LegacyField(body, "proactiveQuietSeconds", node => Math.Clamp(node.GetValue<int>(), 1, 3600)),
            ProfileLookupCount = PanelLegacyFieldEdits.LegacyField(body, "profileLookupCount", node => Math.Clamp(node.GetValue<int>(), 0, 50)),
            ProfileSummaryIntervalSeconds = PanelLegacyFieldEdits.LegacyField(body, "profileSummaryIntervalSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            ProfileSummaryLines = PanelLegacyFieldEdits.LegacyField(body, "profileSummaryLines", node => Math.Clamp(node.GetValue<int>(), 0, 50)),
            ProfileSummaryMaxChars = PanelLegacyFieldEdits.LegacyField(body, "profileSummaryMaxChars", node => Math.Clamp(node.GetValue<int>(), 40, 2000)),
            ProfileSummaryThreshold = PanelLegacyFieldEdits.LegacyField(body, "profileSummaryThreshold", node => Math.Clamp(node.GetValue<int>(), 5, 500)),
            ScenarioPreset = PanelLegacyFieldEdits.LegacyField(body, "scenarioPreset", node => (node.GetValue<string>() ?? string.Empty).Trim()),
            SegmentDelayMs = PanelLegacyFieldEdits.LegacyField(body, "segmentDelayMs", node => Math.Max(0, node.GetValue<int>())),
            SplitReplies = PanelLegacyFieldEdits.LegacyField(body, "splitReplies", node => node.GetValue<bool>()),
            StickerCandidates = PanelLegacyFieldEdits.LegacyField(body, "stickerCandidates", node => Math.Clamp(node.GetValue<int>(), 0, 20)),
            StickerCooldownSeconds = PanelLegacyFieldEdits.LegacyField(body, "stickerCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            StickerCurateIntervalSeconds = PanelLegacyFieldEdits.LegacyField(body, "stickerCurateIntervalSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            StickerLibraryMax = PanelLegacyFieldEdits.LegacyField(body, "stickerLibraryMax", node => Math.Clamp(node.GetValue<int>(), 0, 2000)),
            SuitabilityThreshold = PanelLegacyFieldEdits.LegacyField(body, "suitabilityThreshold", node => Math.Clamp(node.GetValue<int>(), 0, 100)),
            WebSearchCooldownSeconds = PanelLegacyFieldEdits.LegacyField(body, "webSearchCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            WebSearchMaxResults = PanelLegacyFieldEdits.LegacyField(body, "webSearchMaxResults", node => Math.Clamp(node.GetValue<int>(), 1, 10)),
            WebSearchSources = PanelLegacyFieldEdits.LegacyField(body, "webSearchSources", node => node.GetValue<string>().Trim()),
            WebSearchTimeoutSeconds = PanelLegacyFieldEdits.LegacyField(body, "webSearchTimeoutSeconds", node => Math.Clamp(node.GetValue<int>(), 5, 60)),
            WebSearchUseModelSearch = PanelLegacyFieldEdits.LegacyField(body, "webSearchUseModelSearch", node => node.GetValue<bool>()),
        };

    internal static ModelChanges ParseLegacyModelFields(JsonObject body)
        => new()
        {
            AdaptiveSamplingEnabled = PanelLegacyFieldEdits.LegacyField(body, "adaptiveSamplingEnabled", node => node.GetValue<bool>()),
            DefaultTemperature = PanelLegacyFieldEdits.LegacyField(body, "defaultTemperature", node => Math.Clamp(node.GetValue<double>(), 0.0, 2.0)),
            DefaultTopP = PanelLegacyFieldEdits.LegacyField(body, "defaultTopP", node => Math.Clamp(node.GetValue<double>(), 0.0, 1.0)),
            EmotionalTemperature = PanelLegacyFieldEdits.LegacyField(body, "emotionalTemperature", node => Math.Clamp(node.GetValue<double>(), 0.0, 2.0)),
            EmotionalTopP = PanelLegacyFieldEdits.LegacyField(body, "emotionalTopP", node => Math.Clamp(node.GetValue<double>(), 0.0, 1.0)),
            FastModel = PanelLegacyFieldEdits.ParseLegacyFastModel(body),
            FastReply = PanelLegacyFieldEdits.LegacyField(body, "fastReply", node => node.GetValue<bool>()),
            FastThinkingBudget = PanelLegacyFieldEdits.ParseLegacyThinkingText(body, "fastThinkingBudget", true),
            FastThinkingCustomBudget = PanelLegacyFieldEdits.ParseLegacyThinkingText(body, "fastThinkingCustomBudget", false),
            MaxTokens = PanelLegacyFieldEdits.LegacyField(body, "maxTokens", node => Math.Clamp(node.GetValue<int>(), 64, 32000)),
            Model = PanelLegacyFieldEdits.ParseLegacyOverride(body, "model", false),
            ModelBaseUrl = PanelLegacyFieldEdits.ParseLegacyOverride(body, "modelBaseUrl", true),
            RationalTemperature = PanelLegacyFieldEdits.LegacyField(body, "rationalTemperature", node => Math.Clamp(node.GetValue<double>(), 0.0, 2.0)),
            RationalTopP = PanelLegacyFieldEdits.LegacyField(body, "rationalTopP", node => Math.Clamp(node.GetValue<double>(), 0.0, 1.0)),
            ThinkingBudget = PanelLegacyFieldEdits.ParseLegacyThinkingText(body, "thinkingBudget", true),
            ThinkingCustomBudget = PanelLegacyFieldEdits.ParseLegacyThinkingText(body, "thinkingCustomBudget", false),
        };

    internal static AgentChanges ParseLegacyAgentFields(JsonObject body)
    {
        PanelLegacyRemovedFields.RejectRemovedAgentFields(body);
        return new()
        {
            AgentAllowedUsers = PanelLegacyFieldEdits.LegacyField(body, "agentAllowedUsers", node => node.GetValue<string>() ?? string.Empty),
            AgentPrefix = PanelLegacyFieldEdits.LegacyField(body, "agentPrefix", node => (node.GetValue<string>() ?? "//").Trim()),
            AgentProgressSeconds = PanelLegacyFieldEdits.LegacyField(body, "agentProgressSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 3600)),
            AgentPrompt = PanelLegacyFieldEdits.LegacyField(body, "agentPrompt", node => PanelLegacyFieldEdits.LegacyTruncate((node.ToString() ?? string.Empty).Trim(), 8000)),
            AgentReasoningEffort = PanelLegacyFieldEdits.LegacyField(body, "agentReasoningEffort", node => PanelLegacyFieldEdits.LegacyReasoningEffort(node.GetValue<string>())),
            AgentReasoningLevels = PanelLegacyFieldEdits.LegacyField(body, "agentReasoningLevels", node => PanelLegacyFieldEdits.LegacyTruncate(node.GetValue<string>() ?? string.Empty, 1000)),
            AgentReplyMaxChars = PanelLegacyFieldEdits.LegacyField(body, "agentReplyMaxChars", node => Math.Clamp(node.GetValue<int>(), 200, 3000)),
            AgentServerBaseUrl = PanelLegacyFieldEdits.ParseLegacyAgentBaseUrl(body),
            AgentServerCommandTimeoutSeconds = PanelLegacyFieldEdits.LegacyField(body, "agentServerCommandTimeoutSeconds", node => Math.Clamp(node.GetValue<int>(), 5, 300)),
            AgentServerDocker = PanelLegacyFieldEdits.LegacyField(body, "agentServerDocker", node => node.GetValue<bool>()),
            AgentServerKeepContext = PanelLegacyFieldEdits.LegacyField(body, "agentServerKeepContext", node => node.GetValue<bool>()),
            EnableNaturalAgent = PanelLegacyFieldEdits.LegacyField(body, "enableNaturalAgent", node => node.GetValue<bool>()),
            AgentServerMaxSteps = PanelLegacyFieldEdits.LegacyField(body, "agentServerMaxSteps", node => Math.Clamp(node.GetValue<int>(), 1, 30)),
            AgentServerModel = PanelLegacyFieldEdits.LegacyField(body, "agentServerModel", node => (node.GetValue<string>() ?? string.Empty).Trim()),
            AgentServerQqActions = PanelLegacyFieldEdits.ParseLegacyQqActions(body),
            AgentServerTools = PanelLegacyFieldEdits.LegacyField(body, "agentServerTools", node => (node.GetValue<string>() ?? string.Empty).Trim()),
            AgentServerUseGate = PanelLegacyFieldEdits.LegacyField(body, "agentServerUseGate", node => node.GetValue<bool>()),
            AgentServerWorkDir = PanelLegacyFieldEdits.LegacyField(body, "agentServerWorkDir", node => (node.GetValue<string>() ?? "/data").Trim()),
            EnableAgentBridge = PanelLegacyFieldEdits.LegacyField(body, "enableAgentBridge", node => node.GetValue<bool>()),
            EnableAgentMask = PanelLegacyFieldEdits.LegacyField(body, "enableAgentMask", node => node.GetValue<bool>()),
            EnableServerAgent = PanelLegacyFieldEdits.LegacyField(body, "enableServerAgent", node => node.GetValue<bool>()),
            PanelDeployEnabled = PanelLegacyFieldEdits.LegacyField(body, "panelDeployEnabled", node => node.GetValue<bool>()),
            PanelDeployUrl = PanelLegacyFieldEdits.LegacyField(body, "panelDeployUrl", node => (node.GetValue<string>() ?? string.Empty).Trim()),
        };
    }

    internal static PlatformChanges ParseLegacyPlatformFields(JsonObject body)
        => ParseLegacyPlatformFieldsWithPolicies(body, default);

    internal static PlatformChanges ParseLegacyPlatformFieldsWithPolicies(JsonObject body,
        FieldEdit<ImmutableArray<PlatformPolicyEntry>> policies)
        => new()
        {
            PlatformPolicies = policies,
            FeishuApiBase = PanelLegacyFieldEdits.LegacyField(body, "feishuApiBase", node => node.GetValue<string>().Trim()),
            FeishuAppId = PanelLegacyFieldEdits.LegacyField(body, "feishuAppId", node => node.GetValue<string>().Trim()),
            FeishuEnabled = PanelLegacyFieldEdits.LegacyField(body, "feishuEnabled", node => node.GetValue<bool>()),
            FeishuWhitelist = PanelLegacyFieldEdits.LegacyField(body, "feishuWhitelist", node => node.GetValue<string>().Trim()),
            FeishuWhitelistPrivates = PanelLegacyFieldEdits.LegacyField(body, "feishuWhitelistPrivates", node => node.GetValue<string>().Trim()),
            LocalChannelIds = PanelLegacyFieldEdits.LegacyField(body, "localChannelIds", node => node.GetValue<string>() ?? string.Empty),
            MessageWhitelist = PanelLegacyFieldEdits.LegacyField(body, "messageWhitelist", node => node.GetValue<string>() ?? string.Empty),
            OfficialAppId = PanelLegacyFieldEdits.LegacyField(body, "officialAppId", node => node.GetValue<string>().Trim()),
            OfficialChatEnabled = PanelLegacyFieldEdits.LegacyField(body, "officialChatEnabled", node => node.GetValue<bool>()),
            OfficialEnabled = PanelLegacyFieldEdits.LegacyField(body, "officialEnabled", node => node.GetValue<bool>()),
            OfficialSandbox = PanelLegacyFieldEdits.LegacyField(body, "officialSandbox", node => node.GetValue<bool>()),
            OfficialWhitelistGroups = PanelLegacyFieldEdits.LegacyField(body, "officialWhitelistGroups", node => node.GetValue<string>().Trim()),
            OfficialWhitelistPrivates = PanelLegacyFieldEdits.LegacyField(body, "officialWhitelistPrivates", node => node.GetValue<string>().Trim()),
            PrivateChatEnabled = PanelLegacyFieldEdits.LegacyField(body, "privateChatEnabled", node => node.GetValue<bool>()),
            WhitelistGroups = PanelLegacyFieldEdits.LegacyField(body, "whitelistGroups", node => node.GetValue<string>() ?? string.Empty),
            WhitelistPrivates = PanelLegacyFieldEdits.LegacyField(body, "whitelistPrivates", node => node.GetValue<string>() ?? string.Empty),
        };

    internal static VoiceChanges ParseLegacyVoiceFields(JsonObject body)
        => new()
        {
            EnableVoice = PanelLegacyFieldEdits.LegacyField(body, "enableVoice", node => node.GetValue<bool>()),
            TtsApiBase = PanelLegacyFieldEdits.LegacyField(body, "ttsApiBase", node => node.GetValue<string>().Trim()),
            TtsModel = PanelLegacyFieldEdits.LegacyField(body, "ttsModel", node => node.GetValue<string>().Trim()),
            TtsProvider = PanelLegacyFieldEdits.LegacyField(body, "ttsProvider", node => node.GetValue<string>().Trim()),
            TtsServiceUrl = PanelLegacyFieldEdits.LegacyField(body, "ttsServiceUrl", node => node.GetValue<string>().Trim()),
            VoiceEagerness = PanelLegacyFieldEdits.LegacyField(body, "voiceEagerness", node => Math.Clamp(node.GetValue<int>(), 0, 100)),
            VoiceEmotion = PanelLegacyFieldEdits.LegacyField(body, "voiceEmotion", node => (node.GetValue<string>() ?? string.Empty).Trim()),
            VoiceMaxChars = PanelLegacyFieldEdits.LegacyField(body, "voiceMaxChars", node => Math.Clamp(node.GetValue<int>(), 10, 300)),
            VoiceName = PanelLegacyFieldEdits.LegacyField(body, "voiceName", node => node.GetValue<string>().Trim()),
            VoicePitch = PanelLegacyFieldEdits.LegacyField(body, "voicePitch", node => Math.Clamp(node.GetValue<int>(), -12, 12)),
            VoiceSpeed = PanelLegacyFieldEdits.LegacyField(body, "voiceSpeed", node => Math.Clamp(node.GetValue<int>(), 50, 200)),
            VoiceVol = PanelLegacyFieldEdits.LegacyField(body, "voiceVol", node => Math.Clamp(node.GetValue<int>(), 0, 1000)),
        };

    internal static MusicChanges ParseLegacyMusicFields(JsonObject body)
        => new()
        {
            EnableMusic = PanelLegacyFieldEdits.LegacyField(body, "enableMusic", node => node.GetValue<bool>()),
            MusicBitrate = PanelLegacyFieldEdits.LegacyField(body, "musicBitrate", node => Math.Clamp(node.GetValue<int>(), 32, 320)),
            MusicKeepAudio = PanelLegacyFieldEdits.LegacyField(body, "musicKeepAudio", node => node.GetValue<bool>()),
            MusicLibraryMax = PanelLegacyFieldEdits.LegacyField(body, "musicLibraryMax", node => Math.Clamp(node.GetValue<int>(), 10, 5000)),
            MusicListenCooldownSeconds = PanelLegacyFieldEdits.LegacyField(body, "musicListenCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            MusicMaxAnalysisSeconds = PanelLegacyFieldEdits.LegacyField(body, "musicMaxAnalysisSeconds", node => Math.Clamp(node.GetValue<int>(), 20, 600)),
            MusicMaxDownloadMb = PanelLegacyFieldEdits.LegacyField(body, "musicMaxDownloadMb", node => Math.Clamp(node.GetValue<int>(), 1, 64)),
            MusicNoteTtlDays = PanelLegacyFieldEdits.LegacyField(body, "musicNoteTtlDays", node => Math.Clamp(node.GetValue<int>(), 1, 365)),
            MusicSendAudioToModel = PanelLegacyFieldEdits.LegacyField(body, "musicSendAudioToModel", node => node.GetValue<bool>()),
            MusicSources = PanelLegacyFieldEdits.LegacyField(body, "musicSources", node => node.GetValue<string>()),
            MusicUnderstandModel = PanelLegacyFieldEdits.LegacyField(body, "musicUnderstandModel", node => node.GetValue<string>().Trim()),
            NeteaseBaseUrl = PanelLegacyFieldEdits.LegacyField(body, "neteaseBaseUrl", node => node.GetValue<string>().Trim()),
        };

    internal static ReportingChanges ParseLegacyReportingFields(JsonObject body)
        => new()
        {
            HealthReportEnabled = PanelLegacyFieldEdits.LegacyField(body, "healthReportEnabled", node => node.GetValue<bool>()),
            HealthReportTargets = PanelLegacyFieldEdits.LegacyField(body, "healthReportTargets", node => (node.ToString() ?? string.Empty).Trim()),
            HealthReportTime = PanelLegacyFieldEdits.ParseLegacyHealthClock(body),
        };
}

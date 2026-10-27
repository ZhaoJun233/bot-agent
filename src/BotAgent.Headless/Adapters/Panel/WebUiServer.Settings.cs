extern alias StorageModule;

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

public sealed partial class WebUiServer
{
    /// <summary>Only the 133 scalar inputs; composite policy plans are assembled separately.</summary>
    private static ConfigurationPatch ParseLegacyScalarFields(JsonObject body)
        => ParseLegacyFields(body, default);

    private static ConfigurationPatch ParseLegacyFields(JsonObject body,
        FieldEdit<ImmutableArray<PlatformPolicyEntry>> policies)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new(ParseLegacyBehaviorFields(body), ParseLegacyModelFields(body), ParseLegacyAgentFields(body),
            ParseLegacyPlatformFieldsWithPolicies(body, policies), ParseLegacyVoiceFields(body), ParseLegacyMusicFields(body),
            ParseLegacyReportingFields(body), null);
    }

    private static BehaviorChanges ParseLegacyBehaviorFields(JsonObject body)
        => new()
        {
            AiDesire = LegacyField(body, "aiDesire", node => Math.Clamp(node.GetValue<int>(), 0, 100)),
            AiModeEnabled = LegacyField(body, "aiModeEnabled", node => node.GetValue<bool>()),
            ApprovalApprovers = LegacyField(body, "approvalApprovers", node => LegacyTruncate((node.GetValue<string>() ?? string.Empty).Trim(), 300)),
            BotPersona = LegacyField(body, "botPersona", node => node.GetValue<string>() ?? string.Empty),
            EnableApprovals = LegacyField(body, "enableApprovals", node => node.GetValue<bool>()),
            EnableAtmosphereDamping = LegacyField(body, "enableAtmosphereDamping", node => node.GetValue<bool>()),
            EnableLinkPreview = LegacyField(body, "enableLinkPreview", node => node.GetValue<bool>()),
            EnableParticipationGating = LegacyField(body, "enableParticipationGating", node => node.GetValue<bool>()),
            EnablePoke = LegacyField(body, "enablePoke", node => node.GetValue<bool>()),
            EnableProactive = LegacyField(body, "enableProactive", node => node.GetValue<bool>()),
            EnableProfileSummary = LegacyField(body, "enableProfileSummary", node => node.GetValue<bool>()),
            EnableQuestions = LegacyField(body, "enableQuestions", node => node.GetValue<bool>()),
            EnableStickers = LegacyField(body, "enableStickers", node => node.GetValue<bool>()),
            EnableWebSearch = LegacyField(body, "enableWebSearch", node => node.GetValue<bool>()),
            FilterActionNarration = LegacyField(body, "filterActionNarration", node => node.GetValue<bool>()),
            GroupCooldownSeconds = LegacyField(body, "groupCooldownSeconds", node => Math.Max(0, node.GetValue<int>())),
            IdleFallbackSeconds = LegacyField(body, "idleFallbackSeconds", node => Math.Max(0, node.GetValue<int>())),
            IgnoreBracketMessages = LegacyField(body, "ignoreBracketMessages", node => node.GetValue<bool>()),
            LinkPreviewMax = LegacyField(body, "linkPreviewMax", node => Math.Clamp(node.GetValue<int>(), 0, 5)),
            LinkPreviewTimeoutSeconds = LegacyField(body, "linkPreviewTimeoutSeconds", node => Math.Clamp(node.GetValue<int>(), 2, 30)),
            MaxAgentSteps = LegacyField(body, "maxAgentSteps", node => Math.Clamp(node.GetValue<int>(), 1, 3)),
            MaxConcurrentReplies = LegacyField(body, "maxConcurrentReplies", node => Math.Clamp(node.GetValue<int>(), 1, 16)),
            MaxContextMessages = LegacyField(body, "maxContextMessages", node => Math.Clamp(node.GetValue<int>(), 10, 1000)),
            MaxMessagesPerConversation = LegacyField(body, "maxMessagesPerConversation", node => Math.Clamp(node.GetValue<int>(), 20, 100000)),
            MaxProfileChars = LegacyField(body, "maxProfileChars", node => Math.Clamp(node.GetValue<int>(), 0, 20000)),
            MoodTtlSeconds = LegacyField(body, "moodTtlSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400 * 7)),
            ParticipationCooldownSeconds = LegacyField(body, "participationCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 600)),
            ParticipationMaxActiveLifetimeSeconds = LegacyField(body, "participationMaxActiveLifetimeSeconds", node => Math.Clamp(node.GetValue<int>(), 30, 3600)),
            ParticipationMaxConsecutiveReplies = LegacyField(body, "participationMaxConsecutiveReplies", node => Math.Clamp(node.GetValue<int>(), 1, 10)),
            ParticipationMaxExitingLifetimeSeconds = LegacyField(body, "participationMaxExitingLifetimeSeconds", node => Math.Clamp(node.GetValue<int>(), 10, 3600)),
            ParticipationProbingMaxReplies = LegacyField(body, "participationProbingMaxReplies", node => Math.Clamp(node.GetValue<int>(), 1, 3)),
            PokeCooldownSeconds = LegacyField(body, "pokeCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            PrivateCooldownSeconds = LegacyField(body, "privateCooldownSeconds", node => Math.Max(0, node.GetValue<int>())),
            ProactiveCooldownSeconds = LegacyField(body, "proactiveCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 60, 86400)),
            ProactiveQuietSeconds = LegacyField(body, "proactiveQuietSeconds", node => Math.Clamp(node.GetValue<int>(), 1, 3600)),
            ProfileLookupCount = LegacyField(body, "profileLookupCount", node => Math.Clamp(node.GetValue<int>(), 0, 50)),
            ProfileSummaryIntervalSeconds = LegacyField(body, "profileSummaryIntervalSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            ProfileSummaryLines = LegacyField(body, "profileSummaryLines", node => Math.Clamp(node.GetValue<int>(), 0, 50)),
            ProfileSummaryMaxChars = LegacyField(body, "profileSummaryMaxChars", node => Math.Clamp(node.GetValue<int>(), 40, 2000)),
            ProfileSummaryThreshold = LegacyField(body, "profileSummaryThreshold", node => Math.Clamp(node.GetValue<int>(), 5, 500)),
            ScenarioPreset = LegacyField(body, "scenarioPreset", node => (node.GetValue<string>() ?? string.Empty).Trim()),
            SegmentDelayMs = LegacyField(body, "segmentDelayMs", node => Math.Max(0, node.GetValue<int>())),
            SplitReplies = LegacyField(body, "splitReplies", node => node.GetValue<bool>()),
            StickerCandidates = LegacyField(body, "stickerCandidates", node => Math.Clamp(node.GetValue<int>(), 0, 20)),
            StickerCooldownSeconds = LegacyField(body, "stickerCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            StickerCurateIntervalSeconds = LegacyField(body, "stickerCurateIntervalSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            StickerLibraryMax = LegacyField(body, "stickerLibraryMax", node => Math.Clamp(node.GetValue<int>(), 0, 2000)),
            SuitabilityThreshold = LegacyField(body, "suitabilityThreshold", node => Math.Clamp(node.GetValue<int>(), 0, 100)),
            WebSearchCooldownSeconds = LegacyField(body, "webSearchCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            WebSearchMaxResults = LegacyField(body, "webSearchMaxResults", node => Math.Clamp(node.GetValue<int>(), 1, 10)),
            WebSearchSources = LegacyField(body, "webSearchSources", node => node.GetValue<string>().Trim()),
            WebSearchTimeoutSeconds = LegacyField(body, "webSearchTimeoutSeconds", node => Math.Clamp(node.GetValue<int>(), 5, 60)),
            WebSearchUseModelSearch = LegacyField(body, "webSearchUseModelSearch", node => node.GetValue<bool>()),
        };

    private static ModelChanges ParseLegacyModelFields(JsonObject body)
        => new()
        {
            AdaptiveSamplingEnabled = LegacyField(body, "adaptiveSamplingEnabled", node => node.GetValue<bool>()),
            DefaultTemperature = LegacyField(body, "defaultTemperature", node => Math.Clamp(node.GetValue<double>(), 0.0, 2.0)),
            DefaultTopP = LegacyField(body, "defaultTopP", node => Math.Clamp(node.GetValue<double>(), 0.0, 1.0)),
            EmotionalTemperature = LegacyField(body, "emotionalTemperature", node => Math.Clamp(node.GetValue<double>(), 0.0, 2.0)),
            EmotionalTopP = LegacyField(body, "emotionalTopP", node => Math.Clamp(node.GetValue<double>(), 0.0, 1.0)),
            FastModel = ParseLegacyFastModel(body),
            FastReply = LegacyField(body, "fastReply", node => node.GetValue<bool>()),
            FastThinkingBudget = ParseLegacyThinkingText(body, "fastThinkingBudget", true),
            FastThinkingCustomBudget = ParseLegacyThinkingText(body, "fastThinkingCustomBudget", false),
            MaxTokens = LegacyField(body, "maxTokens", node => Math.Clamp(node.GetValue<int>(), 64, 32000)),
            Model = ParseLegacyOverride(body, "model", false),
            ModelBaseUrl = ParseLegacyOverride(body, "modelBaseUrl", true),
            RationalTemperature = LegacyField(body, "rationalTemperature", node => Math.Clamp(node.GetValue<double>(), 0.0, 2.0)),
            RationalTopP = LegacyField(body, "rationalTopP", node => Math.Clamp(node.GetValue<double>(), 0.0, 1.0)),
            ThinkingBudget = ParseLegacyThinkingText(body, "thinkingBudget", true),
            ThinkingCustomBudget = ParseLegacyThinkingText(body, "thinkingCustomBudget", false),
        };

    private static AgentChanges ParseLegacyAgentFields(JsonObject body)
    {
        RejectRemovedAgentFields(body);
        return new()
        {
            AgentAllowedUsers = LegacyField(body, "agentAllowedUsers", node => node.GetValue<string>() ?? string.Empty),
            AgentPrefix = LegacyField(body, "agentPrefix", node => (node.GetValue<string>() ?? "//").Trim()),
            AgentProgressSeconds = LegacyField(body, "agentProgressSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 3600)),
            AgentPrompt = LegacyField(body, "agentPrompt", node => LegacyTruncate((node.ToString() ?? string.Empty).Trim(), 8000)),
            AgentReasoningEffort = LegacyField(body, "agentReasoningEffort", node => LegacyReasoningEffort(node.GetValue<string>())),
            AgentReasoningLevels = LegacyField(body, "agentReasoningLevels", node => LegacyTruncate(node.GetValue<string>() ?? string.Empty, 1000)),
            AgentReplyMaxChars = LegacyField(body, "agentReplyMaxChars", node => Math.Clamp(node.GetValue<int>(), 200, 3000)),
            AgentServerBaseUrl = ParseLegacyAgentBaseUrl(body),
            AgentServerCommandTimeoutSeconds = LegacyField(body, "agentServerCommandTimeoutSeconds", node => Math.Clamp(node.GetValue<int>(), 5, 300)),
            AgentServerDocker = LegacyField(body, "agentServerDocker", node => node.GetValue<bool>()),
            AgentServerKeepContext = LegacyField(body, "agentServerKeepContext", node => node.GetValue<bool>()),
            EnableNaturalAgent = LegacyField(body, "enableNaturalAgent", node => node.GetValue<bool>()),
            AgentServerMaxSteps = LegacyField(body, "agentServerMaxSteps", node => Math.Clamp(node.GetValue<int>(), 1, 30)),
            AgentServerModel = LegacyField(body, "agentServerModel", node => (node.GetValue<string>() ?? string.Empty).Trim()),
            AgentServerQqActions = ParseLegacyQqActions(body),
            AgentServerTools = LegacyField(body, "agentServerTools", node => (node.GetValue<string>() ?? string.Empty).Trim()),
            AgentServerUseGate = LegacyField(body, "agentServerUseGate", node => node.GetValue<bool>()),
            AgentServerWorkDir = LegacyField(body, "agentServerWorkDir", node => (node.GetValue<string>() ?? "/data").Trim()),
            EnableAgentBridge = LegacyField(body, "enableAgentBridge", node => node.GetValue<bool>()),
            EnableAgentMask = LegacyField(body, "enableAgentMask", node => node.GetValue<bool>()),
            EnableServerAgent = LegacyField(body, "enableServerAgent", node => node.GetValue<bool>()),
            PanelDeployEnabled = LegacyField(body, "panelDeployEnabled", node => node.GetValue<bool>()),
            PanelDeployUrl = LegacyField(body, "panelDeployUrl", node => (node.GetValue<string>() ?? string.Empty).Trim()),
        };
    }

    /// <summary>cfg-0.3.3 removed the host/device editor; any legacy key rejects the whole batch.</summary>
    private static void RejectRemovedAgentFields(JsonObject body)
    {
        foreach (var key in new[]
                 {
                     "agentDevices", "agentTarget", "agentTimeoutSeconds", "agentTools", "agentWorkDir",
                     "agentMaxQueued", "agentModel", "enableHostAgent"
                 })
        {
            if (body.ContainsKey(key)) throw new ArgumentException("host_backend_removed");
        }
    }

    private static PlatformChanges ParseLegacyPlatformFields(JsonObject body)
        => ParseLegacyPlatformFieldsWithPolicies(body, default);

    private static PlatformChanges ParseLegacyPlatformFieldsWithPolicies(JsonObject body,
        FieldEdit<ImmutableArray<PlatformPolicyEntry>> policies)
        => new()
        {
            PlatformPolicies = policies,
            FeishuApiBase = LegacyField(body, "feishuApiBase", node => node.GetValue<string>().Trim()),
            FeishuAppId = LegacyField(body, "feishuAppId", node => node.GetValue<string>().Trim()),
            FeishuEnabled = LegacyField(body, "feishuEnabled", node => node.GetValue<bool>()),
            FeishuWhitelist = LegacyField(body, "feishuWhitelist", node => node.GetValue<string>().Trim()),
            LocalChannelIds = LegacyField(body, "localChannelIds", node => node.GetValue<string>() ?? string.Empty),
            MessageWhitelist = LegacyField(body, "messageWhitelist", node => node.GetValue<string>() ?? string.Empty),
            OfficialAppId = LegacyField(body, "officialAppId", node => node.GetValue<string>().Trim()),
            OfficialChatEnabled = LegacyField(body, "officialChatEnabled", node => node.GetValue<bool>()),
            OfficialEnabled = LegacyField(body, "officialEnabled", node => node.GetValue<bool>()),
            OfficialSandbox = LegacyField(body, "officialSandbox", node => node.GetValue<bool>()),
            OfficialWhitelistGroups = LegacyField(body, "officialWhitelistGroups", node => node.GetValue<string>().Trim()),
            OfficialWhitelistPrivates = LegacyField(body, "officialWhitelistPrivates", node => node.GetValue<string>().Trim()),
            PrivateChatEnabled = LegacyField(body, "privateChatEnabled", node => node.GetValue<bool>()),
            WhitelistGroups = LegacyField(body, "whitelistGroups", node => node.GetValue<string>() ?? string.Empty),
            WhitelistPrivates = LegacyField(body, "whitelistPrivates", node => node.GetValue<string>() ?? string.Empty),
        };

    private static VoiceChanges ParseLegacyVoiceFields(JsonObject body)
        => new()
        {
            EnableVoice = LegacyField(body, "enableVoice", node => node.GetValue<bool>()),
            TtsApiBase = LegacyField(body, "ttsApiBase", node => node.GetValue<string>().Trim()),
            TtsModel = LegacyField(body, "ttsModel", node => node.GetValue<string>().Trim()),
            TtsProvider = LegacyField(body, "ttsProvider", node => node.GetValue<string>().Trim()),
            TtsServiceUrl = LegacyField(body, "ttsServiceUrl", node => node.GetValue<string>().Trim()),
            VoiceEagerness = LegacyField(body, "voiceEagerness", node => Math.Clamp(node.GetValue<int>(), 0, 100)),
            VoiceEmotion = LegacyField(body, "voiceEmotion", node => (node.GetValue<string>() ?? string.Empty).Trim()),
            VoiceMaxChars = LegacyField(body, "voiceMaxChars", node => Math.Clamp(node.GetValue<int>(), 10, 300)),
            VoiceName = LegacyField(body, "voiceName", node => node.GetValue<string>().Trim()),
            VoicePitch = LegacyField(body, "voicePitch", node => Math.Clamp(node.GetValue<int>(), -12, 12)),
            VoiceSpeed = LegacyField(body, "voiceSpeed", node => Math.Clamp(node.GetValue<int>(), 50, 200)),
            VoiceVol = LegacyField(body, "voiceVol", node => Math.Clamp(node.GetValue<int>(), 0, 1000)),
        };

    private static MusicChanges ParseLegacyMusicFields(JsonObject body)
        => new()
        {
            EnableMusic = LegacyField(body, "enableMusic", node => node.GetValue<bool>()),
            MusicBitrate = LegacyField(body, "musicBitrate", node => Math.Clamp(node.GetValue<int>(), 32, 320)),
            MusicKeepAudio = LegacyField(body, "musicKeepAudio", node => node.GetValue<bool>()),
            MusicLibraryMax = LegacyField(body, "musicLibraryMax", node => Math.Clamp(node.GetValue<int>(), 10, 5000)),
            MusicListenCooldownSeconds = LegacyField(body, "musicListenCooldownSeconds", node => Math.Clamp(node.GetValue<int>(), 0, 86400)),
            MusicMaxAnalysisSeconds = LegacyField(body, "musicMaxAnalysisSeconds", node => Math.Clamp(node.GetValue<int>(), 20, 600)),
            MusicMaxDownloadMb = LegacyField(body, "musicMaxDownloadMb", node => Math.Clamp(node.GetValue<int>(), 1, 64)),
            MusicNoteTtlDays = LegacyField(body, "musicNoteTtlDays", node => Math.Clamp(node.GetValue<int>(), 1, 365)),
            MusicSendAudioToModel = LegacyField(body, "musicSendAudioToModel", node => node.GetValue<bool>()),
            MusicSources = LegacyField(body, "musicSources", node => node.GetValue<string>()),
            MusicUnderstandModel = LegacyField(body, "musicUnderstandModel", node => node.GetValue<string>().Trim()),
            NeteaseBaseUrl = LegacyField(body, "neteaseBaseUrl", node => node.GetValue<string>().Trim()),
        };

    private static ReportingChanges ParseLegacyReportingFields(JsonObject body)
        => new()
        {
            HealthReportEnabled = LegacyField(body, "healthReportEnabled", node => node.GetValue<bool>()),
            HealthReportTargets = LegacyField(body, "healthReportTargets", node => (node.ToString() ?? string.Empty).Trim()),
            HealthReportTime = ParseLegacyHealthClock(body),
        };

    private static FieldEdit<T> LegacyField<T>(JsonObject body, string key, Func<JsonNode, T> parse)
        => body[key] is { } node ? FieldEdit<T>.Set(parse(node)) : FieldEdit<T>.Keep();

    private static string LegacyTruncate(string value, int maximum)
        => value.Length > maximum ? value[..maximum] : value;

    private static string LegacyReasoningEffort(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value is "" or "default" ? "auto" : value;
    }

    private static FieldEdit<string> ParseLegacyFastModel(JsonObject body)
        => body["fastModel"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? FieldEdit<string>.Set((text ?? string.Empty).Trim()) : FieldEdit<string>.Keep();

    private static FieldEdit<string> ParseLegacyThinkingText(JsonObject body, string key, bool lowercase)
    {
        if (body[key] is not { } node || node.GetValue<string>() is not { Length: > 0 } text)
            return FieldEdit<string>.Keep();
        var normalized = text.Trim();
        return FieldEdit<string>.Set(lowercase ? normalized.ToLowerInvariant() : normalized);
    }

    private static FieldEdit<string> ParseLegacyOverride(JsonObject body, string key, bool isUrl)
    {
        if (body[key] is not { } node) return FieldEdit<string>.Keep();
        var text = (node.GetValue<string>() ?? string.Empty).Trim();
        if (text.Length == 0) return FieldEdit<string>.Clear();
        if (isUrl && (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            throw new ArgumentException("invalid_legacy_model_endpoint");
        return FieldEdit<string>.Set(text);
    }

    private static FieldEdit<string> ParseLegacyAgentBaseUrl(JsonObject body)
    {
        if (body["agentServerBaseUrl"] is not { } node) return FieldEdit<string>.Keep();
        var text = (node.GetValue<string>() ?? string.Empty).Trim();
        if (text.Length != 0 && (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            return FieldEdit<string>.Keep();
        return FieldEdit<string>.Set(text);
    }

    private static FieldEdit<string> ParseLegacyHealthClock(JsonObject body)
    {
        if (body["healthReportTime"] is not { } node) return FieldEdit<string>.Keep();
        var text = (node.ToString() ?? string.Empty).Trim();
        if (text.Length == 0 || !(text.Replace('：', ':').Contains(':') || text.Length == 4))
            return FieldEdit<string>.Keep();
        var (hour, minute) = AppSettings.ParseHealthReportClock(text);
        return FieldEdit<string>.Set($"{hour:00}:{minute:00}");
    }

    private static FieldEdit<string> ParseLegacyQqActions(JsonObject body)
    {
        if (body["agentServerQqActions"] is not { } node) return FieldEdit<string>.Keep();
        var raw = (node.GetValue<string>() ?? string.Empty).Trim();
        if (raw.Length == 0 || raw is "all" or "*" or "全部" or "所有") return FieldEdit<string>.Set(raw);
        var names = new List<string>();
        foreach (var piece in raw.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            var canonical = QqActionCatalog.Canonical(piece.Trim());
            if (canonical is not null && !names.Contains(canonical)) names.Add(canonical);
        }
        return FieldEdit<string>.Set(string.Join(",", names));
    }
    /// <summary>Owned write-only legacy input, never a response DTO or an application result.</summary>
    private sealed class LegacySecretPlan : IDisposable
    {
        private readonly ImmutableArray<SensitiveInput> _owned;
        public ConfigurationResultCode Code { get; }
        public string? ErrorCode { get; }
        public ImmutableArray<SecretEdit> Secrets { get; }
        public FieldEdit<string> Mood { get; }
        public bool TtsProjectionRequested { get; }
        public ConfigurationPatch? Changes { get; private set; }

        internal void AttachChanges(ConfigurationPatch changes)
        {
            ArgumentNullException.ThrowIfNull(changes);
            if (Code != ConfigurationResultCode.Ok || Changes is not null)
                throw new InvalidOperationException("legacy_plan_already_assigned");
            Changes = changes;
        }

        internal LegacySecretPlan(ConfigurationResultCode code, string? errorCode,
            ImmutableArray<SecretEdit> secrets, ImmutableArray<SensitiveInput> owned,
            FieldEdit<string> mood = default, bool ttsProjectionRequested = false)
        {
            Code = code; ErrorCode = errorCode; Secrets = secrets; _owned = owned;
            Mood = mood; TtsProjectionRequested = ttsProjectionRequested;
        }
        public void Dispose()
        {
            foreach (var input in _owned) input.Dispose();
        }
        public override string ToString() => "[redacted]";
    }

    /// <summary>Pure fixed-six-slot mapping; no storage, environment, logging or runtime mutation.</summary>
    private static LegacySecretPlan ParseLegacySecrets(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (HasOfficialSecretInput(body))
            return RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "official_secret_environment_only");
        string? tokenText = null;
        if (body["feishuVerificationToken"] is { } tokenNode)
        {
            if (tokenNode is not JsonValue token || !token.TryGetValue<string>(out tokenText))
                return RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "invalid_legacy_secret");
            if (string.IsNullOrWhiteSpace(tokenText))
                return RejectedLegacySecrets(ConfigurationResultCode.LegacyNeedsReview, "verification_token_refresh_required");
        }
        var edits = ImmutableArray.CreateBuilder<SecretEdit>(6);
        var owned = ImmutableArray.CreateBuilder<SensitiveInput>();
        try
        {
            edits.Add(ParseLegacySecretSlot(body, "apiKey", null, WritableSecretSlot.ModelApiKey, true, owned));
            edits.Add(ParseLegacySecretSlot(body, "agentServerKey", null, WritableSecretSlot.AgentServerKey, true, owned));
            edits.Add(ParseLegacySecretSlot(body, "ttsApiKey", "clearTtsApiKey", WritableSecretSlot.TtsApiKey, false, owned));
            edits.Add(ParseLegacySecretSlot(body, "feishuAppSecret", "clearFeishuAppSecret",
                WritableSecretSlot.FeishuAppSecret, false, owned));
            edits.Add(ParseLegacySecretSlot(body, "feishuEncryptKey", "clearFeishuEncryptKey",
                WritableSecretSlot.FeishuEncryptKey, false, owned));
            edits.Add(tokenText is null ? SecretEdit.Keep(WritableSecretSlot.FeishuVerificationToken)
                : OwnedLegacySecret(WritableSecretSlot.FeishuVerificationToken, tokenText, owned));
            var mood = FieldEdit<string>.Keep();
            if (body["mood"] is JsonValue moodValue && moodValue.TryGetValue<string>(out var moodText))
                mood = string.IsNullOrWhiteSpace(moodText) ? FieldEdit<string>.Clear() : FieldEdit<string>.Set(moodText);
            var ttsRequested = edits.Any(edit => edit.Slot == WritableSecretSlot.TtsApiKey && edit.Kind != SecretEditKind.Keep)
                || body["ttsProvider"] is not null || body["ttsApiBase"] is not null || body["ttsModel"] is not null;
            return new(ConfigurationResultCode.Ok, null, edits.MoveToImmutable(), owned.ToImmutable(), mood, ttsRequested);
        }
        catch (ArgumentException)
        {
            foreach (var input in owned) input.Dispose();
            return RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "invalid_legacy_secret");
        }
        catch
        {
            foreach (var input in owned) input.Dispose();
            throw;
        }
    }

    private static bool HasOfficialSecretInput(JsonObject body)
        => body.Any(field => string.Equals(field.Key, "officialAppSecret", StringComparison.OrdinalIgnoreCase)
            || string.Equals(field.Key, "clearOfficialAppSecret", StringComparison.OrdinalIgnoreCase));

    /// <summary>Pure complete intent. Composite validation finishes before the one secret owner is allocated.</summary>
    private static LegacySecretPlan ParseLegacyConfiguration(JsonObject body, BotAgent.Platforms.PlatformOptions trustedOptions)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(trustedOptions);
        if (HasOfficialSecretInput(body))
            return RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "official_secret_environment_only");
        try
        {
            var policies = FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Keep();
            var scalarBody = body;
            if (body["platformPolicies"] is { } policyNode)
            {
                if (!TryParsePlatformPolicies(policyNode, out var parsed))
                    return RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "invalid_platform_policies");
                var legacy = new BotAgent.Platforms.PlatformOptions
                {
                    PlatformPolicies = parsed!, PlatformSwitchSchemaVersion = 1,
                    DeclaredPlatformDrivers = trustedOptions.DeclaredPlatformDrivers,
                    DeclaredPlatformActions = trustedOptions.DeclaredPlatformActions,
                };
                var imported = Services.Platforms.PlatformSwitchSettings.ImportLegacyPolicies(legacy,
                    legacy.DeclaredPlatformDrivers, ImmutableArray<ActionId>.Empty);
                if (imported.Code != ConfigurationResultCode.Ok)
                    return RejectedLegacySecrets(imported.Code, "legacy_policy_needs_review");
                policies = FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Set(imported.Entries);
                scalarBody = (JsonObject)body.DeepClone();
                foreach (var policy in parsed!)
                {
                    var platform = Domain.Platforms.PlatformId.Normalize(policy.PlatformId);
                    if (platform == Domain.Platforms.PlatformId.QqOfficial)
                    {
                        if (policy.Enabled is bool officialEnabled) scalarBody["officialEnabled"] = officialEnabled;
                        if (policy.ChatEnabled is bool officialChat) scalarBody["officialChatEnabled"] = officialChat;
                        if (policy.GroupWhitelist is not null) scalarBody["officialWhitelistGroups"] = policy.GroupWhitelist;
                        if (policy.PrivateWhitelist is not null) scalarBody["officialWhitelistPrivates"] = policy.PrivateWhitelist;
                    }
                    else if (platform == Domain.Platforms.PlatformId.QqPrivate)
                    {
                        if (policy.ChatEnabled is bool privateChat) scalarBody["privateChatEnabled"] = privateChat;
                        if (policy.GroupWhitelist is not null) scalarBody["whitelistGroups"] = policy.GroupWhitelist;
                        if (policy.PrivateWhitelist is not null) scalarBody["whitelistPrivates"] = policy.PrivateWhitelist;
                    }
                    else if (platform == Domain.Platforms.PlatformId.Feishu && policy.Enabled is bool feishuEnabled)
                        scalarBody["feishuEnabled"] = feishuEnabled;
                    if (platform is Domain.Platforms.PlatformId.Feishu or Domain.Platforms.PlatformId.Local
                        && (policy.GroupWhitelist is not null || policy.PrivateWhitelist is not null))
                        scalarBody[platform == Domain.Platforms.PlatformId.Local ? "localChannelIds" : "feishuWhitelist"] =
                            !string.IsNullOrWhiteSpace(policy.GroupWhitelist) ? policy.GroupWhitelist : policy.PrivateWhitelist ?? string.Empty;
                }
            }
            var changes = ParseLegacyFields(scalarBody, policies);
            var plan = ParseLegacySecrets(body);
            if (plan.Code == ConfigurationResultCode.Ok) plan.AttachChanges(changes);
            return plan;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "invalid_legacy_configuration");
        }
    }

    private static LegacySecretPlan RejectedLegacySecrets(ConfigurationResultCode code, string errorCode)
        => new(code, errorCode, ImmutableArray<SecretEdit>.Empty, ImmutableArray<SensitiveInput>.Empty);

    private static SecretEdit ParseLegacySecretSlot(JsonObject body, string key, string? clearKey,
        WritableSecretSlot slot, bool emptyDeletes, ImmutableArray<SensitiveInput>.Builder owned)
    {
        if (clearKey is not null && body[clearKey] is JsonValue flag && flag.TryGetValue<bool>(out var clear) && clear)
            return SecretEdit.Delete(slot);
        if (body[key] is not JsonValue value || !value.TryGetValue<string>(out var text)) return SecretEdit.Keep(slot);
        if (string.IsNullOrWhiteSpace(text)) return emptyDeletes ? SecretEdit.Delete(slot) : SecretEdit.Keep(slot);
        return OwnedLegacySecret(slot, text, owned);
    }

    private static SecretEdit OwnedLegacySecret(WritableSecretSlot slot, string value,
        ImmutableArray<SensitiveInput>.Builder owned)
    {
        var codec = new UTF8Encoding(false, true);
        if (codec.GetByteCount(value) > 16384 || value.Contains('\0')) throw new ArgumentException("invalid_legacy_secret");
        var bytes = codec.GetBytes(value.Trim());
        try
        {
            var input = SensitiveInput.FromUtf8(bytes);
            owned.Add(input);
            return SecretEdit.Replace(slot, input);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>
    /// 把云端 TTS 的密钥写给 tts 容器（它是**另一个进程**，读不到我们的 SQLite 密钥库）。
    ///
    /// 路径：机器人的 <c>/host/qqchat</c> 就是宿主机部署目录（compose 里常挂），
    /// 所以写 <c>/host/qqchat/tts-conf/tts.env</c>；tts 容器把同一目录挂在 <c>/conf</c>，
    /// 每次请求读一次（带 mtime 缓存）——于是**改完面板不用重启任何容器**。
    /// 本地开发（没有 /host/qqchat）就落在数据目录，拿不到就只记日志（不报错）。
    /// </summary>
    private void WriteTtsConfToHost()
    {
        try
        {
            if (!TtsConfFile.TryWrite(BuildTtsConfiguration(_settings, _secrets.LoadTtsKey() ?? string.Empty)))
                FileLog.Write("Web", "TTS projection unavailable");
        }
        catch (Exception) { FileLog.Write("Web", "TTS projection unavailable"); }
    }
    internal static ApplyStepReport ProjectTtsConfiguration(ConfigurationVersion expected, AppSettings settings)
    {
        string key;
        using (var connection = AppDatabase.Open())
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            if (StorageModule::BotAgent.Storage.ConfigurationStorageBridge.ReadConfigurationVersion(connection) != expected)
                return new(ApplyStep.TtsFileProjection, ApplyState.Failed, "configuration_superseded");
            key = StorageModule::BotAgent.Adapters.Persistence.SecretsStore.LoadInTransaction(connection, WritableSecretSlot.TtsApiKey) ?? string.Empty;
            transaction.Commit();
        }
        try
        {
            return TtsConfFile.TryWrite(BuildTtsConfiguration(settings, key))
                ? new(ApplyStep.TtsFileProjection, ApplyState.Applied, null)
                : new(ApplyStep.TtsFileProjection, ApplyState.Failed, "projection_unavailable");
        }
        catch (Exception) { return new(ApplyStep.TtsFileProjection, ApplyState.Unknown, "projection_unavailable"); }
    }
    private static string BuildTtsConfiguration(AppSettings settings, string key)
    {
        if (new[] { key, settings.TtsProvider, settings.TtsApiBase, settings.TtsModel }.Any(value => value.Contains('\r') || value.Contains('\n')))
            throw new InvalidOperationException("tts_projection_invalid");
        return "# Managed TTS configuration\n" + $"MINIMAX_API_KEY={key}\nOPENAI_TTS_API_KEY={key}\nTTS_PROVIDER={settings.TtsProvider}\n"
            + (string.Equals(settings.TtsProvider, "openai", StringComparison.OrdinalIgnoreCase)
                ? $"OPENAI_TTS_BASE_URL={settings.TtsApiBase}\nOPENAI_TTS_MODEL={settings.TtsModel}\n"
                : $"MINIMAX_API_BASE={settings.TtsApiBase}\nMINIMAX_MODEL={settings.TtsModel}\n");
    }

    /// <summary>
    /// 面板回显用的参与上限摘要（P1）。**走 Clamped()**：面板上看到的永远是“真正生效的那组数”，
    /// 不是你在输入框里填的 —— 填 999 时这里会显示被钳到的 10，免得“我改了怎么没变化”这类误会。
    /// </summary>
    private static string DescribeParticipationPolicy(AppSettings s)
    {
        var p = new Services.Participation.ParticipationPolicy(
            s.ParticipationMaxConsecutiveReplies,
            s.ParticipationCooldownSeconds,
            s.ParticipationProbingMaxReplies,
            s.ParticipationMaxActiveLifetimeSeconds,
            s.ParticipationMaxExitingLifetimeSeconds).Clamped();

        return $"连续 ≤{p.MaxConsecutiveReplies} / 冷却 {p.CooldownSeconds}s / 试探 ≤{p.ProbingMaxReplies}"
               + $" / 活性命 {p.MaxActiveLifetimeSeconds}s / 退场 {p.MaxExitingLifetimeSeconds}s";
    }

/// <summary>密钥掩码（只回显前 3 后 2，与其它密钥一致）。</summary>
    private static string MaskSecret(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return "***";
    }

    private static readonly SemaphoreSlim ConfigurationParseAdmission = new(4, 4);
    private static readonly JsonSerializerOptions ConfigurationJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
    private static int ConfigurationHttpStatus(ConfigurationResultCode code) => code switch
    {
        ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges => 200,
        ConfigurationResultCode.Forbidden => 403,
        ConfigurationResultCode.ValidationFailed or ConfigurationResultCode.UnsupportedContractRevision => 400,
        ConfigurationResultCode.CapacityExceeded => 429,
        ConfigurationResultCode.StorageUnavailable or ConfigurationResultCode.CommitFailed or ConfigurationResultCode.UnknownCommit => 503,
        ConfigurationResultCode.NotFound => 404,
        _ => 409,
    };
    private Task ConfigurationError(HttpListenerContext context, int status, string error,
        ConfigurationResultCode code = ConfigurationResultCode.ValidationFailed, bool refresh = false)
        => WriteJsonAsync(context, status, new JsonObject { ["error"] = error, ["code"] = (int)code, ["refreshRequired"] = refresh });
    private async Task HandleSettingsSaveAsync(HttpListenerContext context)
    {
        if (!IsAuthorized(context) || !IsSameOrigin(context))
        { await ConfigurationError(context, 403, "forbidden", ConfigurationResultCode.Forbidden); return; }
        var operations = context.Request.QueryString.GetValues("configuration");
        var caller = new ConfigurationCaller("headless-panel", "panel-administrator", "bot",
            ConfigurationAccess.ReadOverview | ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets);
        using var scope = _settingsHotReload.ConfigurationAuthorization.Enter(caller, () => IsAuthorized(context) && IsSameOrigin(context));
        if (operations is not null)
        {
            if (operations.Length != 1 || operations[0] is not ("read" or "save" or "find"))
            { await ConfigurationError(context, 400, "invalid_configuration_operation"); return; }
            await HandleConfigurationRequestAsync(context, operations[0]); return;
        }
        var body = await ReadJsonAsync(context);
        if (body is not JsonObject submitted)
        { await ConfigurationError(context, 400, "invalid_json"); return; }
        using var plan = ParseLegacyConfiguration(submitted, _settings);
        if (plan.Code != ConfigurationResultCode.Ok)
        {
            var refresh = plan.Code == ConfigurationResultCode.LegacyNeedsReview;
            await ConfigurationError(context, ConfigurationHttpStatus(plan.Code), refresh ? "refresh_required"
                : plan.ErrorCode ?? "invalid_configuration", plan.Code, refresh); return;
        }
        ConfigurationOperationKey? key = null;
        var supplied = context.Request.Headers.GetValues("X-BotAgent-Configuration-Key");
        if (supplied is not null)
        {
            try
            {
                if (supplied.Length != 1 || supplied[0].Contains(',')) throw new JsonException();
                key = JsonSerializer.Deserialize<ConfigurationOperationKey>(JsonSerializer.Serialize(supplied[0]), ConfigurationJson);
            }
            catch (JsonException) { await ConfigurationError(context, 400, "invalid_configuration_key"); return; }
        }
        var outcome = await _settingsHotReload.SaveLegacyConfigurationAsync(key, plan.Changes!, plan.Secrets, plan.Mood, plan.TtsProjectionRequested);
        var result = outcome.ConfigurationResult;
        JsonObject payload;
        if (result.Code is ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges) payload = BuildSettingsPayload();
        else payload = new JsonObject { ["error"] = "configuration_rejected", ["code"] = (int)result.Code,
            ["refreshRequired"] = result.Code == ConfigurationResultCode.LegacyNeedsReview };
        payload["configurationResult"] = JsonSerializer.SerializeToNode(result, ConfigurationJson);
        payload["legacyActionResults"] = JsonSerializer.SerializeToNode(outcome.LegacyActionResults, ConfigurationJson);
        await WriteJsonAsync(context, ConfigurationHttpStatus(result.Code), payload);
    }
    private async Task HandleConfigurationRequestAsync(HttpListenerContext context, string operation)
    {
        if (!await ConfigurationParseAdmission.WaitAsync(0))
        { await ConfigurationError(context, 429, "configuration_parse_busy", ConfigurationResultCode.CapacityExceeded); return; }
        byte[]? bytes = null;
        var storageStarted = false;
        try
        {
            const int maximum = 1048576;
            if (context.Request.ContentLength64 > maximum)
            { await ConfigurationError(context, 413, "configuration_body_too_large", ConfigurationResultCode.CapacityExceeded); return; }
            using var buffer = new MemoryStream(); var chunk = new byte[8192];
            try
            {
                int size;
                while ((size = await context.Request.InputStream.ReadAsync(chunk)) != 0)
                {
                    if (buffer.Length + size > maximum)
                    { await ConfigurationError(context, 413, "configuration_body_too_large", ConfigurationResultCode.CapacityExceeded); return; }
                    buffer.Write(chunk, 0, size);
                }
                bytes = buffer.ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(chunk); CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
            var raw = new UTF8Encoding(false, true).GetString(bytes);
            ValidateConfigurationUnicode(raw);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            ValidateConfigurationDuplicates(document.RootElement);
            var root = document.RootElement;
            if (operation == "read")
            {
                ConfigurationMembers(root, "kind");
                if (!root.GetProperty("kind").TryGetInt32(out var kind) || kind is < 0 or > 1) throw new JsonException();
                storageStarted = true;
                var read = await _settingsHotReload.ReadConfigurationAsync((ConfigurationReadKind)kind);
                if (read.Generation.Value == Guid.Empty)
                { await ConfigurationError(context, ConfigurationHttpStatus(read.Code), "configuration_unavailable", read.Code); return; }
                await WriteJsonAsync(context, ConfigurationHttpStatus(read.Code), JsonSerializer.SerializeToNode(read, ConfigurationJson)!); return;
            }
            if (operation == "find")
            {
                ConfigurationMembers(root, "key");
                var key = JsonSerializer.Deserialize<ConfigurationOperationKey>(root.GetProperty("key"), ConfigurationJson);
                storageStarted = true;
                var found = await _settingsHotReload.FindConfigurationAsync(key);
                await WriteJsonAsync(context, ConfigurationHttpStatus(found.ConfigurationResult.Code),
                    JsonSerializer.SerializeToNode(found, ConfigurationJson)!); return;
            }
            var (request, owner) = ParseConfigurationSave(root);
            using (owner)
            {
                storageStarted = true;
                var result = await _settingsHotReload.SaveConfigurationAsync(request);
                await WriteJsonAsync(context, ConfigurationHttpStatus(result.Code), JsonSerializer.SerializeToNode(result, ConfigurationJson)!);
            }
        }
        catch (Exception error) when (!storageStarted && (error is JsonException or ArgumentException or InvalidOperationException or DecoderFallbackException or OverflowException))
        { await ConfigurationError(context, 400, "invalid_configuration"); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); ConfigurationParseAdmission.Release(); }
    }
    private static (ConfigurationSaveRequest Request, LegacySecretPlan Owner) ParseConfigurationSave(JsonElement root)
    {
        ConfigurationMembers(root, "contractRevision", "expectedVersion", "key", "changes", "secrets");
        var revision = root.GetProperty("contractRevision").GetString() ?? throw new JsonException();
        var version = JsonSerializer.Deserialize<ConfigurationVersion>(root.GetProperty("expectedVersion"), ConfigurationJson);
        var key = JsonSerializer.Deserialize<ConfigurationOperationKey>(root.GetProperty("key"), ConfigurationJson);
        var patchNode = JsonNode.Parse(root.GetProperty("changes").GetRawText())?.AsObject() ?? throw new JsonException();
        if (patchNode["registered"] is JsonObject registered)
            foreach (var name in new[] { "plugins", "providers", "policies" })
                if (!registered.ContainsKey(name)) registered[name] = new JsonArray();
        using var normalized = JsonDocument.Parse(patchNode.ToJsonString());
        ValidateConfigurationShape(normalized.RootElement, typeof(ConfigurationPatch));
        var changes = JsonSerializer.Deserialize<ConfigurationPatch>(normalized.RootElement, ConfigurationJson) ?? throw new JsonException();
        var secretsNode = root.GetProperty("secrets");
        if (secretsNode.ValueKind != JsonValueKind.Array || secretsNode.GetArrayLength() > 6) throw new JsonException();
        var owned = ImmutableArray.CreateBuilder<SensitiveInput>(); var edits = ImmutableArray.CreateBuilder<SecretEdit>();
        var slots = new HashSet<WritableSecretSlot>();
        try
        {
            foreach (var edit in secretsNode.EnumerateArray())
            {
                if (edit.ValueKind != JsonValueKind.Object || !edit.TryGetProperty("kind", out var kindNode)
                    || !kindNode.TryGetInt32(out var kind) || kind is < 0 or > 2) throw new JsonException();
                ConfigurationMembers(edit, kind == 1 ? new[] { "slot", "kind", "value" } : new[] { "slot", "kind" });
                if (!edit.GetProperty("slot").TryGetInt32(out var slotNumber) || slotNumber is < 0 or > 5) throw new JsonException();
                var slot = (WritableSecretSlot)slotNumber;
                if (!slots.Add(slot)) throw new JsonException();
                edits.Add(kind switch { 0 => SecretEdit.Keep(slot), 2 => SecretEdit.Delete(slot),
                    _ => OwnedLegacySecret(slot, edit.GetProperty("value").GetString() ?? throw new JsonException(), owned) });
            }
            var owner = new LegacySecretPlan(ConfigurationResultCode.Ok, null, edits.ToImmutable(), owned.ToImmutable());
            return (new(revision, version, key, changes, owner.Secrets), owner);
        }
        catch { foreach (var input in owned) input.Dispose(); throw; }
    }
    private static void ConfigurationMembers(JsonElement node, params string[] expected)
    {
        if (node.ValueKind != JsonValueKind.Object) throw new JsonException();
        var names = node.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Length || names.Any(name => !expected.Contains(name, StringComparer.Ordinal))) throw new JsonException();
    }
    private static void ValidateConfigurationDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new JsonException(); ValidateConfigurationDuplicates(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) ValidateConfigurationDuplicates(item);
    }
    private static void ValidateConfigurationShape(JsonElement node, Type declared)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (node.ValueKind == JsonValueKind.Null) { if (Nullable.GetUnderlyingType(declared) is null) throw new JsonException(); return; }
        if (type == typeof(string)) { if (node.ValueKind != JsonValueKind.String || Encoding.UTF8.GetByteCount(node.GetString()!) > 32768) throw new JsonException(); return; }
        if (type == typeof(bool)) { if (node.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException(); return; }
        if (type == typeof(int) || type.IsEnum)
        { if (!node.TryGetInt32(out var value) || type.IsEnum && !Enum.IsDefined(type, value)) throw new JsonException(); return; }
        if (type == typeof(double)) { if (!node.TryGetDouble(out var value) || !double.IsFinite(value)) throw new JsonException(); return; }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        { if (node.ValueKind != JsonValueKind.Array) throw new JsonException(); foreach (var item in node.EnumerateArray()) ValidateConfigurationShape(item, type.GetGenericArguments()[0]); return; }
        if (type.GetCustomAttribute<System.Text.Json.Serialization.JsonConverterAttribute>() is not null)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FieldEdit<>))
            {
                if (!node.TryGetProperty("kind", out var k) || !k.TryGetInt32(out var kind) || kind is < 0 or > 2) throw new JsonException();
                ConfigurationMembers(node, kind == 1 ? new[] { "kind", "value" } : new[] { "kind" });
                if (kind == 1) ValidateConfigurationShape(node.GetProperty("value"), type.GetGenericArguments()[0]);
            }
            // The registered Core converter enforces the other closed scalar/rule encodings.
            JsonSerializer.Deserialize(node, type, ConfigurationJson); return;
        }
        if (type == typeof(PolicyTarget))
        {
            var kind = node.GetProperty("kind").GetString();
            if (kind == "accountDefault") { ConfigurationMembers(node, "kind", "platformId", "accountScope"); }
            else if (kind == "instance") { ConfigurationMembers(node, "kind", "key"); ValidateConfigurationShape(node.GetProperty("key"), typeof(DriverKey)); }
            else throw new JsonException();
            JsonSerializer.Deserialize<PolicyTarget>(node, ConfigurationJson); return;
        }
        if (node.ValueKind != JsonValueKind.Object) throw new JsonException();
        // Reflection is confined to these declared Core DTO types, never a body-provided type or a settings setter.
        if (type.Namespace != typeof(ConfigurationPatch).Namespace) throw new JsonException();
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).ToDictionary(
            property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), StringComparer.Ordinal);
        foreach (var property in node.EnumerateObject())
        {
            if (!properties.TryGetValue(property.Name, out var member)) throw new JsonException();
            if (property.Value.ValueKind == JsonValueKind.Null && (Nullable.GetUnderlyingType(member.PropertyType) is not null
                || new NullabilityInfoContext().Create(member).ReadState == NullabilityState.Nullable)) continue;
            ValidateConfigurationShape(property.Value, member.PropertyType);
        }
        var constructor = type.GetConstructors().OrderByDescending(value => value.GetParameters().Length).FirstOrDefault();
        foreach (var parameter in constructor?.GetParameters() ?? Array.Empty<ParameterInfo>())
        {
            if (parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition() == typeof(FieldEdit<>)) continue;
            if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null || new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable) continue;
            if (!node.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!), out _)) throw new JsonException();
        }
    }
    private static void ValidateConfigurationUnicode(string json)
    {
        var quoted = false;
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] == '"') { quoted = !quoted; continue; }
            if (!quoted || json[i] != '\\') continue;
            if (++i >= json.Length) throw new JsonException();
            if (json[i] != 'u') continue;
            if (i + 4 >= json.Length || !ushort.TryParse(json.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var unit)) throw new JsonException();
            i += 4;
            if (char.IsLowSurrogate((char)unit)) throw new JsonException();
            if (!char.IsHighSurrogate((char)unit)) continue;
            if (i + 6 >= json.Length || json[i + 1] != '\\' || json[i + 2] != 'u'
                || !ushort.TryParse(json.AsSpan(i + 3, 4), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var low) || !char.IsLowSurrogate((char)low)) throw new JsonException();
            i += 6;
        }
    }

    private static bool TryParsePlatformPolicies(
        JsonNode node,
        out List<Domain.Platforms.PlatformPolicySettings>? policies)
    {
        policies = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<Domain.Platforms.PlatformPolicySettings>>(node.ToJsonString(), Json);
            if (parsed is null || parsed.Count > 32) return false;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var policy in parsed)
            {
                if (policy is null) return false;
                var platform = Domain.Platforms.PlatformId.Normalize(policy.PlatformId);
                var account = (policy.AccountScope ?? string.Empty).Trim();
                if (platform.Length is 0 or > 80 || account.Length is 0 or > 80
                    || !platform.All(IsPolicyKeyChar) || !account.All(IsPolicyKeyChar)
                    || !seen.Add(platform + "|" + account)
                    || (policy.GroupWhitelist?.Length ?? 0) > 4096
                    || (policy.PrivateWhitelist?.Length ?? 0) > 4096)
                {
                    return false;
                }

                policy.PlatformId = platform;
                policy.AccountScope = account;
                policy.GroupWhitelist ??= string.Empty;
                policy.PrivateWhitelist ??= string.Empty;
                policy.FeatureOverrides ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                policy.AllowedActions ??= new List<string>();
                if (policy.FeatureOverrides.Count > 32 || policy.AllowedActions.Count > 64
                    || policy.FeatureOverrides.Keys.Any(k => k.Length > 64 || !k.All(IsPolicyKeyChar))
                    || policy.AllowedActions.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 80))
                {
                    return false;
                }
            }

            policies = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsPolicyKeyChar(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-';

    private JsonObject BuildSettingsPayload()
    {
        var s = _settings;

        return new JsonObject
        {
            ["runtime"] = BuildRuntimePayload(s),
            // 只读：容器环境变量职责，改这里无效（见 BuildEnvPayload）
            ["env"] = BuildEnvPayload(s),
            ["settingsFile"] = _settingsRepo.FilePath
        };
    }

    private JsonObject BuildRuntimePayload(AppSettings s)
    {
        var runtime = new JsonObject
        {
            ["botPersona"] = s.BotPersona,
                ["messageWhitelist"] = s.MessageWhitelist,
        ["whitelistGroups"] = s.WhitelistGroups,
        ["whitelistPrivates"] = s.WhitelistPrivates,
        // 留空的那一边会回落到旧的共用名单 —— 面板要如实告知，不然管理员会以为新框填了没生效
        ["whitelistGroupsFromLegacy"] = string.IsNullOrWhiteSpace(s.WhitelistGroups) && s.MessageWhitelist.Length > 0,
        ["whitelistPrivatesFromLegacy"] = string.IsNullOrWhiteSpace(s.WhitelistPrivates) && s.MessageWhitelist.Length > 0,
                ["aiDesire"] = s.AiDesire,
                ["fastReply"] = s.FastReply,
                ["fastModel"] = s.FastModel,
                ["replyModel"] = s.ReplyModel,
                ["thinkingBudget"] = s.ThinkingBudget,
                ["thinkingCustomBudget"] = s.ThinkingCustomBudget,
                ["fastThinkingBudget"] = s.FastThinkingBudget,
                ["fastThinkingCustomBudget"] = s.FastThinkingCustomBudget,
        ["suitabilityThreshold"] = s.SuitabilityThreshold,
        ["rationalTemperature"] = s.RationalTemperature,
        ["rationalTopP"] = s.RationalTopP,
        ["emotionalTemperature"] = s.EmotionalTemperature,
        ["emotionalTopP"] = s.EmotionalTopP,
        ["defaultTemperature"] = s.DefaultTemperature,
        ["defaultTopP"] = s.DefaultTopP,
        ["maxAgentSteps"] = s.MaxAgentSteps,
        ["localChannelIds"] = s.LocalChannelIds,
        ["agentServerUseGate"] = s.AgentServerUseGate,
                ["aiModeEnabled"] = s.AiModeEnabled,
                ["maxTokens"] = s.MaxTokens,
                ["groupCooldownSeconds"] = s.GroupCooldownSeconds,
                ["privateCooldownSeconds"] = s.PrivateCooldownSeconds,
                ["idleFallbackSeconds"] = s.IdleFallbackSeconds,
                ["splitReplies"] = s.SplitReplies,
                ["enableProactive"] = s.EnableProactive,
                ["proactiveCooldownSeconds"] = s.ProactiveCooldownSeconds,
                ["proactiveQuietSeconds"] = s.ProactiveQuietSeconds,
                ["ignoreBracketMessages"] = s.IgnoreBracketMessages,
                ["filterActionNarration"] = s.FilterActionNarration,
                ["enableAtmosphereDamping"] = s.EnableAtmosphereDamping,
                ["segmentDelayMs"] = s.SegmentDelayMs,
                ["maxContextMessages"] = s.MaxContextMessages,
                ["profileLookupCount"] = s.ProfileLookupCount,
                ["profileSummaryLines"] = s.ProfileSummaryLines,
                ["maxProfileChars"] = s.MaxProfileChars,
                ["maxMessagesPerConversation"] = s.MaxMessagesPerConversation,
                ["maxConcurrentReplies"] = s.MaxConcurrentReplies,
                ["enableProfileSummary"] = s.EnableProfileSummary,
                ["profileSummaryThreshold"] = s.ProfileSummaryThreshold,
                ["profileSummaryMaxChars"] = s.ProfileSummaryMaxChars,
                ["profileSummaryIntervalSeconds"] = s.ProfileSummaryIntervalSeconds,

        // 官方通道（QQ 开放平台）：与私域并存，两边会话/上下文/白名单互不串台。
        // secret 不在这里回（密钥只从环境变量读，面板不回显）。
        ["officialEnabled"] = s.OfficialEnabled,
        ["officialAppId"] = s.OfficialAppId,
        ["officialSecretConfigured"] = !string.IsNullOrWhiteSpace(s.OfficialAppSecret),
        ["officialSecretSource"] = !string.IsNullOrWhiteSpace(s.OfficialAppSecret) ? "env" : "none",
        ["officialSandbox"] = s.OfficialSandbox,
        ["officialWhitelistGroups"] = s.OfficialWhitelistGroups,
        ["officialWhitelistPrivates"] = s.OfficialWhitelistPrivates,
        ["channels"] = BuildChannelStatus(),
        // 对话总开关（分通道静音）：与顶部那个全局 AI 开关不同，这里能只关一条通道。
        ["privateChatEnabled"] = s.PrivateChatEnabled,
        ["officialChatEnabled"] = s.OfficialChatEnabled,
        // 官方通道**见过的会话**（别名号 + 名字）——面板上点一下就能填进官方白名单，
        // 不必再让人去猜“别名号长什么样”（填真实号 = 官方通道静默全拦，今天刚踩过）。
        ["officialConversations"] = BuildOfficialConversations(),
         ["enableWebSearch"] = s.EnableWebSearch,
         ["webSearchUseModelSearch"] = s.WebSearchUseModelSearch,
         ["webSearchSources"] = s.WebSearchSources,
         ["webSearchMaxResults"] = s.WebSearchMaxResults,
         ["webSearchCooldownSeconds"] = s.WebSearchCooldownSeconds,
         ["webSearchTimeoutSeconds"] = s.WebSearchTimeoutSeconds,
        ["enableLinkPreview"] = s.EnableLinkPreview,
        ["linkPreviewTimeoutSeconds"] = s.LinkPreviewTimeoutSeconds,
        ["linkPreviewMax"] = s.LinkPreviewMax,
        ["enableMusic"] = s.EnableMusic,
        ["neteaseBaseUrl"] = s.NeteaseBaseUrl,
        ["musicSources"] = s.MusicSources,
        ["musicBitrate"] = s.MusicBitrate,
        ["musicMaxDownloadMb"] = s.MusicMaxDownloadMb,
        ["musicMaxAnalysisSeconds"] = s.MusicMaxAnalysisSeconds,
        ["musicLibraryMax"] = s.MusicLibraryMax,
        ["musicNoteTtlDays"] = s.MusicNoteTtlDays,
        ["musicListenCooldownSeconds"] = s.MusicListenCooldownSeconds,
        ["musicUnderstandModel"] = s.MusicUnderstandModel,
        ["musicSendAudioToModel"] = s.MusicSendAudioToModel,
        ["musicAudioToModelMaxKb"] = s.MusicAudioToModelMaxKb,
        ["musicKeepAudio"] = s.MusicKeepAudio,
        ["enableAgentBridge"] = s.EnableAgentBridge,
        ["agentPrefix"] = s.AgentPrefix,
        ["agentAllowedUsers"] = s.AgentAllowedUsers,
        ["agentReplyMaxChars"] = s.AgentReplyMaxChars,
        ["agentProgressSeconds"] = s.AgentProgressSeconds,
        ["enableServerAgent"] = s.EnableServerAgent,
        ["agentServerTools"] = s.AgentServerTools,
        ["agentServerQqActions"] = s.AgentServerQqActions,
        ["agentServerQqActionsEffective"] = QqActionCatalog.Summarize(QqActionCatalog.ParseAllowed(s.AgentServerQqActions)),
        ["agentServerModel"] = s.AgentServerModel,
        ["agentReasoningEffort"] = s.AgentReasoningEffort,
        ["agentReasoningLevels"] = s.AgentReasoningLevels,
        ["agentServerBaseUrl"] = s.AgentServerBaseUrl,
        // 服务器 agent 的密钥：只给“设没设 / 掩码 / 来源”，不回显明文（与聊天那把 key 同样的规矩）
        ["agentServerKeySet"] = !string.IsNullOrWhiteSpace(s.AgentServerApiKey),
        ["agentServerKeyMasked"] = Mask(s.AgentServerApiKey),
        ["agentServerKeySource"] = !string.IsNullOrWhiteSpace(s.AgentServerApiKeyOverride)
            ? "panel"
            : (string.IsNullOrWhiteSpace(s.AgentServerApiKey) ? "none" : "env"),
        ["scenarioPreset"] = s.ScenarioPreset,
        ["scenarioPresets"] = new JsonArray(Domain.Permissions.ScenarioPresets.All
            .Select(name => (JsonNode)name!).ToArray()),
        ["scenarioCapabilities"] = s.ScenarioPreset.Length == 0
            ? "(跟随现有开关)"
            : Domain.Permissions.ChatCapabilitySet.FromSwitches(
                s.EnableWebSearch, s.EnableMusic, s.EnableVoice, s.EnableStickers, s.EnablePoke,
                s.ScenarioPreset).Describe(),
        ["enableApprovals"] = s.EnableApprovals,
        ["approvalApprovers"] = s.ApprovalApprovers,
        // 面板要能说清“开了审批会发生什么”：只覆盖这一个固定假工具，且它没有任何真实副作用
        ["approvalTool"] = Domain.Permissions.ApprovalFlow.FixedToolId,
        ["approvalTtlSeconds"] = Domain.Permissions.ApprovalFlow.TtlSeconds,
        ["approvalCapabilities"] = s.EnableApprovals
            ? Domain.Permissions.ChatCapabilitySet.FromSwitches(
                s.EnableWebSearch, s.EnableMusic, s.EnableVoice, s.EnableStickers, s.EnablePoke,
                s.ScenarioPreset, approvalsEnabled: true).Describe()
            : "(审批关闭：模型写的 action=tool 一律安全静默)",
        // 参与状态机（P1）：参数可改，但**只影响观测日志**（gating 未打开）
        ["participationMaxConsecutiveReplies"] = s.ParticipationMaxConsecutiveReplies,
        ["participationCooldownSeconds"] = s.ParticipationCooldownSeconds,
        ["participationProbingMaxReplies"] = s.ParticipationProbingMaxReplies,
        ["participationMaxActiveLifetimeSeconds"] = s.ParticipationMaxActiveLifetimeSeconds,
        ["participationMaxExitingLifetimeSeconds"] = s.ParticipationMaxExitingLifetimeSeconds,
        ["participationPolicy"] = DescribeParticipationPolicy(s),
        ["enableParticipationGating"] = s.EnableParticipationGating,
        ["participationGating"] = s.EnableParticipationGating
            ? "on（闸门开启：状态机说这一轮不参与就不叫模型 —— 只收不放）"
            : "off（只观测：不改“发不发”的判定）",
        ["enableQuestions"] = s.EnableQuestions,
        ["questionTool"] = Domain.Permissions.ApprovalFlow.QuestionToolId,
        ["questionCapabilities"] = s.EnableQuestions
            ? "允许提问（带一次性编号与有效期；不授予任何权限）"
            : "(提问关闭：模型写的 action=ask 一律安全静默)",
        ["enableAgentMask"] = s.AgentMaskSensitive,
        ["agentPrompt"] = s.AgentPrompt,
        // 面板「恢复默认」按钮用：默认那份写在 AppSettings.DefaultAgentPrompt（只有一处真源）
        ["agentPromptDefault"] = AppSettings.DefaultAgentPrompt,
        ["agentServerWorkDir"] = s.AgentServerWorkDir,
        ["agentServerKeepContext"] = s.AgentServerKeepContext,
        ["enableNaturalAgent"] = s.EnableNaturalAgent,
        ["agentServerDocker"] = s.AgentServerDocker,
        ["panelDeployEnabled"] = s.PanelDeployEnabled,
        ["panelDeployUrl"] = s.PanelDeployUrl,
        ["agentServerMaxSteps"] = s.AgentServerMaxSteps,
        ["agentServerCommandTimeoutSeconds"] = s.AgentServerCommandTimeoutSeconds,
        ["neteaseCookieSet"] = !string.IsNullOrWhiteSpace(s.NeteaseCookie),
        ["enableStickers"] = s.EnableStickers,
                ["stickerLibraryMax"] = s.StickerLibraryMax,
                ["stickerCandidates"] = s.StickerCandidates,
                ["stickerCurateIntervalSeconds"] = s.StickerCurateIntervalSeconds,
                ["stickerCooldownSeconds"] = s.StickerCooldownSeconds,
        ["enablePoke"] = s.EnablePoke,
        ["pokeCooldownSeconds"] = s.PokeCooldownSeconds,
        ["moodTtlSeconds"] = s.MoodTtlSeconds,
        // ---- 服务器健康日报（定时私聊推送）----
        ["healthReportEnabled"] = s.HealthReportEnabled,
        ["healthReportTime"] = s.HealthReportTime,
        ["healthReportTargets"] = s.HealthReportTargets,
        // 当前心情（可手改；空 = 由代码按被戳次数自动描述）
        ["mood"] = _mood.CurrentText(Clock.Now) ?? string.Empty,
        ["moodSummary"] = _mood.Describe(Clock.Now)
        };

        PopulateVoiceSettings(runtime, s);
        return runtime;
    }

    private void PopulateVoiceSettings(JsonObject runtime, AppSettings s)
    {
        runtime["enableVoice"] = s.EnableVoice;
        runtime["voiceName"] = s.VoiceName;
        runtime["voiceSpeed"] = s.VoiceSpeed;
        runtime["voicePitch"] = s.VoicePitch;
        runtime["voiceVol"] = s.VoiceVol;
        runtime["voiceEmotion"] = s.VoiceEmotion;
        runtime["voiceMaxChars"] = s.VoiceMaxChars;
        runtime["voiceEagerness"] = s.VoiceEagerness;
        runtime["ttsServiceUrl"] = s.TtsServiceUrl;
        runtime["ttsProvider"] = s.TtsProvider;
        runtime["ttsApiBase"] = s.TtsApiBase;
        runtime["ttsModel"] = s.TtsModel;
        runtime["ttsKeyConfigured"] = !string.IsNullOrWhiteSpace(_secrets.LoadTtsKey());
        runtime["ttsKeyMasked"] = MaskSecret(_secrets.LoadTtsKey());
        PopulatePlatformSettings(runtime, s);
    }

    private void PopulatePlatformSettings(JsonObject runtime, AppSettings s)
    {
        var display = s;
        if (s.RuntimePlatformPolicies.HasValue)
        {
            // JSON compatibility only: requested restart fields must not overwrite the live inheritance baseline.
            display = _settingsRepo.Load();
            display.DeclaredPlatformDrivers = s.DeclaredPlatformDrivers;
            display.DeclaredPlatformActions = s.DeclaredPlatformActions;
            if (!display.RuntimePlatformPolicies.HasValue) throw new InvalidOperationException("platform_policy_state_invalid");
            runtime["localChannelIds"] = display.LocalChannelIds;
        }
        var official = Services.Platforms.PlatformSwitchSettings.Read(display, Domain.Platforms.PlatformId.QqOfficial, Domain.Platforms.AccountScope.Legacy);
        runtime["officialEnabled"] = official.Enabled;
        runtime["officialChatEnabled"] = official.ChatEnabled;
        runtime["privateChatEnabled"] = Services.Platforms.PlatformSwitchSettings.Read(display, Domain.Platforms.PlatformId.QqPrivate, Domain.Platforms.AccountScope.Legacy).ChatEnabled;
        runtime["feishuEnabled"] = Services.Platforms.PlatformSwitchSettings.Read(display, Domain.Platforms.PlatformId.Feishu, Domain.Platforms.AccountScope.Default).Enabled;
        runtime["platformPolicies"] = JsonSerializer.SerializeToNode(Services.Platforms.PlatformSwitchSettings.EditablePolicies(display), Json) ?? new JsonArray();
        runtime["feishuAppId"] = s.FeishuAppId;
        runtime["feishuSecretConfigured"] = !string.IsNullOrWhiteSpace(s.FeishuAppSecret);
        runtime["feishuSecretMasked"] = MaskSecret(s.FeishuAppSecret);
        runtime["feishuSecretSource"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BOTAGENT_FEISHU_APP_SECRET"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QQCHAT_FEISHU_APP_SECRET"))
            ? "env"
            : (string.IsNullOrWhiteSpace(_secrets.LoadFeishuSecret()) ? "none" : "panel");
        using var secretConnection = AppDatabase.Open();
        var storedVerificationToken = StorageModule::BotAgent.Adapters.Persistence.SecretsStore.LoadInTransaction(
            secretConnection, WritableSecretSlot.FeishuVerificationToken);
        runtime["feishuVerificationTokenConfigured"] = !string.IsNullOrWhiteSpace(storedVerificationToken);
        runtime["feishuVerificationTokenSource"] = string.IsNullOrWhiteSpace(storedVerificationToken) ? "none" : "database";
        runtime["feishuEncryptKeyConfigured"] = !string.IsNullOrWhiteSpace(s.FeishuEncryptKey);
        runtime["feishuEncryptKeyMasked"] = MaskSecret(s.FeishuEncryptKey);
        runtime["feishuEncryptKeySource"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BOTAGENT_FEISHU_ENCRYPT_KEY"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QQCHAT_FEISHU_ENCRYPT_KEY"))
            ? "env"
            : (string.IsNullOrWhiteSpace(_secrets.LoadFeishuEncryptKey()) ? "none" : "panel");
        runtime["feishuWhitelist"] = s.FeishuWhitelist;
        runtime["feishuApiBase"] = s.FeishuApiBase;
    }

    /// <summary>
    /// 只读的「容器环境变量职责」那一段（模型端点 / 协议端地址 / 数据目录 / 端口 / 时区）：
    /// 面板只能看不能改 —— 改这些要动 compose 的环境变量。拆出来是因为主 payload 原本三百多行。
    /// </summary>
    private JsonObject BuildEnvPayload(AppSettings s) => new()
    {
        ["modelBaseUrl"] = s.ModelBaseUrl,
        ["modelBaseUrlSource"] = string.IsNullOrWhiteSpace(s.ModelBaseUrlOverride) ? "env" : "panel",
        ["model"] = s.Model,
        ["modelSource"] = string.IsNullOrWhiteSpace(s.ModelOverride) ? "env" : "panel",
        ["fastReply"] = s.FastReply,
        ["fastModel"] = s.FastModel,
        ["replyModel"] = s.ReplyModel,
        ["thinkingBudget"] = s.ThinkingBudget,
        ["thinkingCustomBudget"] = s.ThinkingCustomBudget,
        ["fastThinkingBudget"] = s.FastThinkingBudget,
        ["fastThinkingCustomBudget"] = s.FastThinkingCustomBudget,
        ["maxTokens"] = s.MaxTokens,
        ["apiKeyMasked"] = Mask(s.ApiKey),
        ["apiKeySet"] = !string.IsNullOrWhiteSpace(s.ApiKey),
        ["apiKeySource"] = !string.IsNullOrWhiteSpace(s.ApiKeyOverride) ? "panel" : (string.IsNullOrWhiteSpace(s.ApiKey) ? "none" : "env"),
        ["oneBotProtocol"] = s.OneBotProtocol,
        ["oneBotAddress"] = s.OneBotAddress,
        ["oneBotTokenMasked"] = Mask(s.OneBotToken),
        ["uin"] = s.NormalizedUin,
        ["dataDir"] = AppPaths.RuntimeRoot,
        ["healthPort"] = s.HealthPort,
        ["tz"] = Environment.GetEnvironmentVariable("TZ") ?? TimeZoneInfo.Local.Id
    };


    private static string Mask(string? secret) => string.IsNullOrEmpty(secret) ? string.Empty : "***";
}

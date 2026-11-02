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
        runtime["feishuWhitelistPrivates"] = s.FeishuWhitelistPrivates;
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

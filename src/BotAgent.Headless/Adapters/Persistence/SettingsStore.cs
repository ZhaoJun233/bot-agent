extern alias StorageModule;

using System.IO;
using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Services.Ports;
using BotAgent.Services;
using BotAgent.Services.Platforms;
using BotAgent.Domain.Platforms;
using StorageConfiguration = StorageModule::BotAgent.Storage;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 配置读写：行为配置存在 SQLite 的 <c>settings</c> 表（整份 JSON 一行）。
///
/// 为什么整份存 JSON 而不是拆成几十个列：这份配置永远是“整份读、整份写”
/// （面板一次提交整张表单），列化只会带来一版一版的 ALTER 与字段映射漂移；
/// 它仍然在库里 —— 备份、查询（<c>json_extract(json,'$.AiDesire')</c>）、事务都成立。
/// 老版本的 <c>settings.json</c> 由 LegacyJsonImporter 导入后归档。
/// </summary>
public sealed class SettingsStore : ISettingsRepository
{
    private const string ReadSettingsSql = "SELECT json FROM settings WHERE id = 1";
    private const string WriteSettingsSql = "INSERT INTO settings(id, json, updated_unix) VALUES(1, $json, $now) "
        + "ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_unix = excluded.updated_unix";
    private static readonly JsonSerializerOptions UserJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
    public string FilePath => AppDatabase.FilePath;

    /// <summary>库文件在不在盘上（启动横幅如实显示用；配置的真源始终是库，不是文件）。</summary>
    public bool ExistsOnDisk => File.Exists(FilePath);

    /// <summary>库里到底存过配置没有（用来判断“首次部署”：没存过才用环境变量当种子）。</summary>
    public bool HasStoredSettings()
    {
        try
        {
            return AppDatabase.Scalar<string>(ReadSettingsSql) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public AppSettings Load()
    {
        AppSettings settings;
        using var connection = AppDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = ReadSettingsSql;
            var json = command.ExecuteScalar() as string;
            settings = string.IsNullOrWhiteSpace(json) ? new AppSettings()
                : JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Config] 读取配置失败，使用默认值: {ex.GetType().Name}");
            settings = new AppSettings();
        }
        // Authoritative typed policy failures must never fall back to legacy/default authorization.
        ProjectPlatformPoliciesInTransaction(connection, settings);
        transaction.Commit();
        return settings;
    }

    public void Save(AppSettings settings) => Save(settings, null, null);

    /// <summary>Startup-only projection after controlled initialization, not a seed or a write.</summary>
    internal static bool TryProjectInitializedVerificationToken(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var connection = AppDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var marker = StorageDatabase.GetMetaInTransaction(connection, "verification_token_initialized");
        if (marker is null) return false;
        if (marker is not ("initialized" or "deleted"))
            throw new InvalidOperationException("verification_token_state_invalid");
        var value = StorageSecrets.LoadInTransaction(connection, WritableSecretSlot.FeishuVerificationToken);
        if (marker == "deleted" && value is not null)
            throw new InvalidOperationException("verification_token_state_invalid");
        settings.FeishuVerificationToken = marker == "deleted" ? string.Empty : NormalizeVerificationToken(value) ?? string.Empty;
        transaction.Commit();
        return true;
    }

    private static string? NormalizeVerificationToken(string? value)
    {
        if (value is null) return null;
        try
        {
            if (new System.Text.UTF8Encoding(false, true).GetByteCount(value) > 16384 || value.Contains('\0'))
                throw new ArgumentException();
            var normalized = value.Trim();
            return normalized.Length == 0 ? null : normalized;
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("verification_token_legacy_needs_review");
        }
    }

    /// <summary>Closed six-field legacy input; stored opaque fields never become newly writable.</summary>
    internal static ImmutableArray<DeviceConfiguration> ParseLegacyDevices(string? json, bool readStoredKeys)
    {
        if (string.IsNullOrWhiteSpace(json)) return ImmutableArray<DeviceConfiguration>.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 128)
                throw new InvalidOperationException();
            var result = ImmutableArray.CreateBuilder<DeviceConfiguration>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
                var properties = item.EnumerateObject().Select(property => property.Name).ToArray();
                if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
                    || (!readStoredKeys && properties.Any(name => name is not ("name" or "enable" or "model"
                        or "workdir" or "tools" or "timeoutSec")))) throw new InvalidOperationException();
                if (!item.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException();
                var name = (nameNode.GetString() ?? string.Empty).Trim();
                if (name.Length is 0 or > 128 || !names.Add(name)) throw new InvalidOperationException();
                DeviceKey? key = null;
                if (readStoredKeys && item.TryGetProperty("key", out var keyNode))
                {
                    if (keyNode.ValueKind != JsonValueKind.String || !Guid.TryParseExact(keyNode.GetString(), "N", out var id)
                        || id == Guid.Empty || keyNode.GetString() != id.ToString("N")) throw new InvalidOperationException();
                    key = new DeviceKey(id);
                }
                var enable = !item.TryGetProperty("enable", out var enabled) || enabled.ValueKind != JsonValueKind.False;
                var timeout = item.TryGetProperty("timeoutSec", out var number) && number.ValueKind == JsonValueKind.Number
                    && number.TryGetInt32(out var seconds) ? seconds : 0;
                result.Add(new(key, name, enable, LegacyDeviceString(item, "model"), LegacyDeviceString(item, "workdir"),
                    LegacyDeviceString(item, "tools"), timeout <= 0 ? 0 : Math.Clamp(timeout, 30, 7200)));
            }
            return result.ToImmutable();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("legacy_device_needs_review");
        }
    }

    private static string? LegacyDeviceString(JsonElement item, string field)
        => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    public void Save(AppSettings settings, BotAgent.Services.Ops.AuditEvent? auditEvent, BotAgent.Services.Ops.IAuditChain? auditChain)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (auditEvent is not null && auditChain is not AuditLogStore)
        {
            throw new InvalidOperationException("Settings audit requires the same SQLite transaction.");
        }
        try
        {
            AppDatabase.Write(conn =>
            {
                if (ConfigurationBridge.RequiresCoordinatedConfigurationWrites)
                    throw new InvalidOperationException("configuration_writer_required");
                SaveInTransaction(conn, settings, auditEvent, auditChain);
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Config] 写入配置失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>Pure scalar mapping of owned intent onto persisted user facts, not a runtime snapshot.</summary>
    internal static AppSettings ApplyScalarConfigurationFields(AppSettings persistedBaseline,
        BotAgent.Domain.Configuration.ConfigurationPatch patch)
    {
        ArgumentNullException.ThrowIfNull(persistedBaseline);
        ArgumentNullException.ThrowIfNull(patch);
        var next = persistedBaseline.Snapshot();
        if (patch.Agent is { } agent)
        {
            ApplyScalar(agent.AgentAllowedUsers, value => next.AgentAllowedUsers = value);
            ApplyScalar(agent.AgentPrefix, value => next.AgentPrefix = value);
            ApplyScalar(agent.AgentProgressSeconds, value => next.AgentProgressSeconds = value);
            ApplyScalar(agent.AgentPrompt, value => next.AgentPrompt = value);
            ApplyScalar(agent.AgentReasoningEffort, value => next.AgentReasoningEffort = value);
            ApplyScalar(agent.AgentReasoningLevels, value => next.AgentReasoningLevels = value);
            ApplyScalar(agent.AgentReplyMaxChars, value => next.AgentReplyMaxChars = value);
            ApplyScalar(agent.AgentServerBaseUrl, value => next.AgentServerBaseUrl = value);
            ApplyScalar(agent.AgentServerCommandTimeoutSeconds, value => next.AgentServerCommandTimeoutSeconds = value);
            ApplyScalar(agent.AgentServerDocker, value => next.AgentServerDocker = value);
            ApplyScalar(agent.AgentServerKeepContext, value => next.AgentServerKeepContext = value);
            ApplyScalar(agent.AgentServerMaxSteps, value => next.AgentServerMaxSteps = value);
            ApplyScalar(agent.AgentServerModel, value => next.AgentServerModel = value);
            ApplyScalar(agent.AgentServerQqActions, value => next.AgentServerQqActions = value);
            ApplyScalar(agent.AgentServerTools, value => next.AgentServerTools = value);
            ApplyScalar(agent.AgentServerUseGate, value => next.AgentServerUseGate = value);
            ApplyScalar(agent.AgentServerWorkDir, value => next.AgentServerWorkDir = value);
            ApplyScalar(agent.EnableAgentBridge, value => next.EnableAgentBridge = value);
            ApplyScalar(agent.EnableAgentMask, value => next.AgentMaskSensitive = value);
            ApplyScalar(agent.EnableServerAgent, value => next.EnableServerAgent = value);
            ApplyScalar(agent.PanelDeployEnabled, value => next.PanelDeployEnabled = value);
            ApplyScalar(agent.PanelDeployUrl, value => next.PanelDeployUrl = value);
        }
        if (patch.Behavior is { } behavior)
        {
            ApplyScalar(behavior.AiDesire, value => next.AiDesire = value);
            ApplyScalar(behavior.AiModeEnabled, value => next.AiModeEnabled = value);
            ApplyScalar(behavior.ApprovalApprovers, value => next.ApprovalApprovers = value);
            ApplyScalar(behavior.BotPersona, value => next.BotPersona = value);
            ApplyScalar(behavior.EnableApprovals, value => next.EnableApprovals = value);
            ApplyScalar(behavior.EnableAtmosphereDamping, value => next.EnableAtmosphereDamping = value);
            ApplyScalar(behavior.EnableLinkPreview, value => next.EnableLinkPreview = value);
            ApplyScalar(behavior.EnableParticipationGating, value => next.EnableParticipationGating = value);
            ApplyScalar(behavior.EnablePoke, value => next.EnablePoke = value);
            ApplyScalar(behavior.EnableProactive, value => next.EnableProactive = value);
            ApplyScalar(behavior.EnableProfileSummary, value => next.EnableProfileSummary = value);
            ApplyScalar(behavior.EnableQuestions, value => next.EnableQuestions = value);
            ApplyScalar(behavior.EnableStickers, value => next.EnableStickers = value);
            ApplyScalar(behavior.EnableWebSearch, value => next.EnableWebSearch = value);
            ApplyScalar(behavior.FilterActionNarration, value => next.FilterActionNarration = value);
            ApplyScalar(behavior.GroupCooldownSeconds, value => next.GroupCooldownSeconds = value);
            ApplyScalar(behavior.IdleFallbackSeconds, value => next.IdleFallbackSeconds = value);
            ApplyScalar(behavior.IgnoreBracketMessages, value => next.IgnoreBracketMessages = value);
            ApplyScalar(behavior.LinkPreviewMax, value => next.LinkPreviewMax = value);
            ApplyScalar(behavior.LinkPreviewTimeoutSeconds, value => next.LinkPreviewTimeoutSeconds = value);
            ApplyScalar(behavior.MaxAgentSteps, value => next.MaxAgentSteps = value);
            ApplyScalar(behavior.MaxConcurrentReplies, value => next.MaxConcurrentReplies = value);
            ApplyScalar(behavior.MaxContextMessages, value => next.MaxContextMessages = value);
            ApplyScalar(behavior.MaxMessagesPerConversation, value => next.MaxMessagesPerConversation = value);
            ApplyScalar(behavior.MaxProfileChars, value => next.MaxProfileChars = value);
            ApplyScalar(behavior.MoodTtlSeconds, value => next.MoodTtlSeconds = value);
            ApplyScalar(behavior.ParticipationCooldownSeconds, value => next.ParticipationCooldownSeconds = value);
            ApplyScalar(behavior.ParticipationMaxActiveLifetimeSeconds, value => next.ParticipationMaxActiveLifetimeSeconds = value);
            ApplyScalar(behavior.ParticipationMaxConsecutiveReplies, value => next.ParticipationMaxConsecutiveReplies = value);
            ApplyScalar(behavior.ParticipationMaxExitingLifetimeSeconds, value => next.ParticipationMaxExitingLifetimeSeconds = value);
            ApplyScalar(behavior.ParticipationProbingMaxReplies, value => next.ParticipationProbingMaxReplies = value);
            ApplyScalar(behavior.PokeCooldownSeconds, value => next.PokeCooldownSeconds = value);
            ApplyScalar(behavior.PrivateCooldownSeconds, value => next.PrivateCooldownSeconds = value);
            ApplyScalar(behavior.ProactiveCooldownSeconds, value => next.ProactiveCooldownSeconds = value);
            ApplyScalar(behavior.ProactiveQuietSeconds, value => next.ProactiveQuietSeconds = value);
            ApplyScalar(behavior.ProfileLookupCount, value => next.ProfileLookupCount = value);
            ApplyScalar(behavior.ProfileSummaryIntervalSeconds, value => next.ProfileSummaryIntervalSeconds = value);
            ApplyScalar(behavior.ProfileSummaryLines, value => next.ProfileSummaryLines = value);
            ApplyScalar(behavior.ProfileSummaryMaxChars, value => next.ProfileSummaryMaxChars = value);
            ApplyScalar(behavior.ProfileSummaryThreshold, value => next.ProfileSummaryThreshold = value);
            ApplyScalar(behavior.ScenarioPreset, value => next.ScenarioPreset = value);
            ApplyScalar(behavior.SegmentDelayMs, value => next.SegmentDelayMs = value);
            ApplyScalar(behavior.SplitReplies, value => next.SplitReplies = value);
            ApplyScalar(behavior.StickerCandidates, value => next.StickerCandidates = value);
            ApplyScalar(behavior.StickerCooldownSeconds, value => next.StickerCooldownSeconds = value);
            ApplyScalar(behavior.StickerCurateIntervalSeconds, value => next.StickerCurateIntervalSeconds = value);
            ApplyScalar(behavior.StickerLibraryMax, value => next.StickerLibraryMax = value);
            ApplyScalar(behavior.SuitabilityThreshold, value => next.SuitabilityThreshold = value);
            ApplyScalar(behavior.WebSearchCooldownSeconds, value => next.WebSearchCooldownSeconds = value);
            ApplyScalar(behavior.WebSearchMaxResults, value => next.WebSearchMaxResults = value);
            ApplyScalar(behavior.WebSearchSources, value => next.WebSearchSources = value);
            ApplyScalar(behavior.WebSearchTimeoutSeconds, value => next.WebSearchTimeoutSeconds = value);
            ApplyScalar(behavior.WebSearchUseModelSearch, value => next.WebSearchUseModelSearch = value);
        }
        if (patch.Model is { } model)
        {
            ApplyScalar(model.AdaptiveSamplingEnabled, value => next.AdaptiveSamplingEnabled = value);
            ApplyScalar(model.DefaultTemperature, value => next.DefaultTemperature = value);
            ApplyScalar(model.DefaultTopP, value => next.DefaultTopP = value);
            ApplyScalar(model.EmotionalTemperature, value => next.EmotionalTemperature = value);
            ApplyScalar(model.EmotionalTopP, value => next.EmotionalTopP = value);
            ApplyScalar(model.FastModel, value => next.FastModel = value);
            ApplyScalar(model.FastReply, value => next.FastReply = value);
            ApplyScalar(model.FastThinkingBudget, value => next.FastThinkingBudget = value);
            ApplyScalar(model.FastThinkingCustomBudget, value => next.FastThinkingCustomBudget = value);
            ApplyScalar(model.MaxTokens, value => next.MaxTokens = value);
            if (model.Model.TryGetValue(out var newModel))
            { next.ModelOverride = newModel; next.Model = newModel; }
            else if (model.Model.Kind == BotAgent.Domain.Configuration.EditKind.Clear)
            { next.ModelOverride = null; next.Model = new AppSettings().Model; }
            if (model.ModelBaseUrl.TryGetValue(out var newModelBaseUrl))
            { next.ModelBaseUrlOverride = newModelBaseUrl; next.ModelBaseUrl = newModelBaseUrl; }
            else if (model.ModelBaseUrl.Kind == BotAgent.Domain.Configuration.EditKind.Clear)
            { next.ModelBaseUrlOverride = null; next.ModelBaseUrl = new AppSettings().ModelBaseUrl; }
            ApplyScalar(model.RationalTemperature, value => next.RationalTemperature = value);
            ApplyScalar(model.RationalTopP, value => next.RationalTopP = value);
            ApplyScalar(model.ThinkingBudget, value => next.ThinkingBudget = value);
            ApplyScalar(model.ThinkingCustomBudget, value => next.ThinkingCustomBudget = value);
        }
        if (patch.Music is { } music)
        {
            ApplyScalar(music.EnableMusic, value => next.EnableMusic = value);
            ApplyScalar(music.MusicBitrate, value => next.MusicBitrate = value);
            ApplyScalar(music.MusicKeepAudio, value => next.MusicKeepAudio = value);
            ApplyScalar(music.MusicLibraryMax, value => next.MusicLibraryMax = value);
            ApplyScalar(music.MusicListenCooldownSeconds, value => next.MusicListenCooldownSeconds = value);
            ApplyScalar(music.MusicMaxAnalysisSeconds, value => next.MusicMaxAnalysisSeconds = value);
            ApplyScalar(music.MusicMaxDownloadMb, value => next.MusicMaxDownloadMb = value);
            ApplyScalar(music.MusicNoteTtlDays, value => next.MusicNoteTtlDays = value);
            ApplyScalar(music.MusicSendAudioToModel, value => next.MusicSendAudioToModel = value);
            ApplyScalar(music.MusicSources, value => next.MusicSources = value);
            ApplyScalar(music.MusicUnderstandModel, value => next.MusicUnderstandModel = value);
            ApplyScalar(music.NeteaseBaseUrl, value => next.NeteaseBaseUrl = value);
        }
        if (patch.Platform is { } platform)
        {
            ApplyScalar(platform.FeishuApiBase, value => next.FeishuApiBase = value);
            ApplyScalar(platform.FeishuAppId, value => next.FeishuAppId = value);
            ApplyScalar(platform.FeishuEnabled, value => next.FeishuEnabled = value);
            ApplyScalar(platform.FeishuWhitelist, value => next.FeishuWhitelist = value);
            ApplyScalar(platform.LocalChannelIds, value => next.LocalChannelIds = value);
            ApplyScalar(platform.MessageWhitelist, value => next.MessageWhitelist = value);
            ApplyScalar(platform.OfficialAppId, value => next.OfficialAppId = value);
            ApplyScalar(platform.OfficialChatEnabled, value => next.OfficialChatEnabled = value);
            ApplyScalar(platform.OfficialEnabled, value => next.OfficialEnabled = value);
            ApplyScalar(platform.OfficialSandbox, value => next.OfficialSandbox = value);
            ApplyScalar(platform.OfficialWhitelistGroups, value => next.OfficialWhitelistGroups = value);
            ApplyScalar(platform.OfficialWhitelistPrivates, value => next.OfficialWhitelistPrivates = value);
            ApplyScalar(platform.PrivateChatEnabled, value => next.PrivateChatEnabled = value);
            ApplyScalar(platform.WhitelistGroups, value => next.WhitelistGroups = value);
            ApplyScalar(platform.WhitelistPrivates, value => next.WhitelistPrivates = value);
        }
        if (patch.Reporting is { } reporting)
        {
            ApplyScalar(reporting.HealthReportEnabled, value => next.HealthReportEnabled = value);
            ApplyScalar(reporting.HealthReportTargets, value => next.HealthReportTargets = value);
            ApplyScalar(reporting.HealthReportTime, value => next.HealthReportTime = value);
        }
        if (patch.Voice is { } voice)
        {
            ApplyScalar(voice.EnableVoice, value => next.EnableVoice = value);
            ApplyScalar(voice.TtsApiBase, value => next.TtsApiBase = value);
            ApplyScalar(voice.TtsModel, value => next.TtsModel = value);
            ApplyScalar(voice.TtsProvider, value => next.TtsProvider = value);
            ApplyScalar(voice.TtsServiceUrl, value => next.TtsServiceUrl = value);
            ApplyScalar(voice.VoiceEagerness, value => next.VoiceEagerness = value);
            ApplyScalar(voice.VoiceEmotion, value => next.VoiceEmotion = value);
            ApplyScalar(voice.VoiceMaxChars, value => next.VoiceMaxChars = value);
            ApplyScalar(voice.VoiceName, value => next.VoiceName = value);
            ApplyScalar(voice.VoicePitch, value => next.VoicePitch = value);
            ApplyScalar(voice.VoiceSpeed, value => next.VoiceSpeed = value);
            ApplyScalar(voice.VoiceVol, value => next.VoiceVol = value);
        }
        return next;
    }

    private static void ApplyScalar<T>(BotAgent.Domain.Configuration.FieldEdit<T> edit, Action<T> assign)
    {
        if (edit.TryGetValue(out var value)) assign(value);
        else if (edit.Kind == BotAgent.Domain.Configuration.EditKind.Clear)
        {
            if (typeof(T) != typeof(string)) throw new ArgumentException("invalid_scalar_clear");
            assign((T)(object)string.Empty);
        }
    }

    /// <summary>Authorized typed read projection; user facts and effective provenance remain separate.</summary>
    internal static ConfigurationView ProjectConfigurationView(Microsoft.Data.Sqlite.SqliteConnection connection,
        AppSettings user, AppSettings effective, ConfigurationReadKind kind, ApplyState latestApply,
        IReadOnlySet<string> legacyProperties, Func<ConfigurationFieldId, ConfigurationValueSource> actualSource,
        Func<ConfigurationFieldId, FieldEffect> effect,
        ImmutableArray<PlatformPolicyEntryView> requestedPolicies, ImmutableArray<PlatformPolicyEntryView> effectivePolicies,
        RegisteredConfigurationView registered)
    {
        var fields = new ConfigurationReadFields(connection, kind, latestApply, legacyProperties, actualSource, effect);
        return new ConfigurationView(ProjectBehaviorView(user, effective, fields), ProjectModelView(user, effective, fields),
            ProjectAgentView(user, effective, fields),
            ProjectPlatformView(user, effective, fields, requestedPolicies, effectivePolicies),
            ProjectVoiceView(user, effective, fields), ProjectMusicView(user, effective, fields),
            ProjectReportingView(user, effective, fields), registered);
    }

    internal AppSettings LoadInitializedRuntimeSnapshot(AppSettings bootstrap)
    {
        var next = bootstrap.Snapshot();
        using var connection = AppDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        ProjectPlatformPoliciesInTransaction(connection, next);
        var marker = StorageDatabase.GetMetaInTransaction(connection, "verification_token_initialized");
        if (marker is not ("initialized" or "deleted")) throw new InvalidOperationException("verification_token_state_invalid");
        next.FeishuVerificationToken = marker == "deleted" ? string.Empty
            : NormalizeVerificationToken(StorageSecrets.LoadInTransaction(connection, WritableSecretSlot.FeishuVerificationToken)) ?? string.Empty;
        transaction.Commit();
        return next;
    }

    /// <summary>Read precisely the committed version; retain startup-only consumers until restart.</summary>
    internal AppSettings LoadCommittedRuntimeSnapshot(ConfigurationVersion expected, AppSettings previous,
        ConfigurationPatch changes, ImmutableArray<StorageConfiguration.SecretMutation> mutations)
    {
        using var connection = AppDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        if (ConfigurationBridge.ReadConfigurationVersion(connection) != expected)
            throw new InvalidOperationException("configuration_superseded");
        using var command = connection.CreateCommand();
        command.CommandText = ReadSettingsSql;
        var json = command.ExecuteScalar() as string ?? throw new JsonException("configuration_missing");
        var user = JsonSerializer.Deserialize<AppSettings>(json) ?? throw new JsonException("configuration_missing");
        var properties = JsonNode.Parse(json)!.AsObject().Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var emptyRegistered = new RegisteredConfigurationView(ImmutableArray<PluginConfigurationView>.Empty,
            ImmutableArray<ProviderConfigurationView>.Empty);
        var view = ProjectConfigurationView(connection, user, user, ConfigurationReadKind.Editable, ApplyState.NotStarted,
            properties, _ => ConfigurationValueSource.Unclassified, BotConfig.ConfigurationEffect,
            ImmutableArray<PlatformPolicyEntryView>.Empty, ImmutableArray<PlatformPolicyEntryView>.Empty, emptyRegistered);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var facts = JsonSerializer.SerializeToNode(view, options)!.AsObject();
        var patch = JsonSerializer.SerializeToNode(changes, options)!.AsObject();
        var model = patch["model"] as JsonObject ?? new JsonObject();
        if (patch["model"] is null) patch["model"] = model;
        foreach (var provider in changes.Registered?.Providers ?? ImmutableArray<ProviderEdit>.Empty)
        {
            if (provider.Key.Value != "primary") continue;
            if (provider.Changes.ModelName.Kind != EditKind.Keep)
                model["model"] = JsonSerializer.SerializeToNode(provider.Changes.ModelName, options);
            if (provider.Changes.BaseUrl.Kind != EditKind.Keep)
                model["modelBaseUrl"] = JsonSerializer.SerializeToNode(provider.Changes.BaseUrl, options);
        }
        var modelTouched = model["model"]?["kind"]?.GetValue<int>() is 1 or 2;
        var urlTouched = model["modelBaseUrl"]?["kind"]?.GetValue<int>() is 1 or 2;
        patch.Remove("registered");
        foreach (var partition in patch.ToArray())
        {
            if (partition.Value is not JsonObject edits) continue;
            foreach (var edit in edits.ToArray())
            {
                if (!Enum.TryParse<ConfigurationFieldId>(edit.Key, true, out var field)
                    || field is ConfigurationFieldId.AgentDevices or ConfigurationFieldId.PlatformPolicies
                    || BotConfig.ConfigurationEffect(field) is FieldEffect.Restart or FieldEffect.ReadOnly
                    || edit.Value?["kind"]?.GetValue<int>() is not (1 or 2)) { edits.Remove(edit.Key); continue; }
                if (edit.Value!["kind"]!.GetValue<int>() == 1)
                {
                    var read = facts[partition.Key]?[edit.Key] ?? throw new JsonException("configuration_projection_missing");
                    edit.Value["value"] = (read["value"] ?? read["nameRaw"])?.DeepClone();
                }
            }
        }
        var next = ApplyScalarConfigurationFields(previous, patch.Deserialize<ConfigurationPatch>(options)!);
        ProjectPlatformPoliciesInTransaction(connection, next);
        var sources = next.RuntimePlatformPolicyBaselineSources.ToBuilder();
        foreach (var pair in previous.RuntimePlatformPolicyBaselineSources)
            if (BotConfig.ConfigurationEffect(pair.Key) == FieldEffect.Restart) sources[pair.Key] = pair.Value;
        next.RuntimePlatformPolicyBaselineSources = sources.ToImmutable();
        BotConfig.ProjectModelOverrides(next, user, modelTouched, urlTouched);
        BotConfig.ProjectHotSaveSecrets(next, previous, mutations.Select(value => (value.Slot, value.Kind)).ToImmutableArray(),
            slot => StorageSecrets.LoadInTransaction(connection, slot));
        transaction.Commit();
        return next;
    }

    private sealed class ConfigurationReadFields
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private readonly ConfigurationReadKind _kind;
        private readonly ApplyState _latestApply;
        private readonly IReadOnlySet<string> _legacyProperties;
        private readonly Func<ConfigurationFieldId, ConfigurationValueSource> _actualSource;
        private readonly Func<ConfigurationFieldId, FieldEffect> _effect;

        internal ConfigurationReadFields(Microsoft.Data.Sqlite.SqliteConnection connection,
            ConfigurationReadKind kind, ApplyState latestApply, IReadOnlySet<string> legacyProperties,
            Func<ConfigurationFieldId, ConfigurationValueSource> actualSource, Func<ConfigurationFieldId, FieldEffect> effect)
        {
            _connection = connection; _kind = kind; _latestApply = latestApply;
            _legacyProperties = legacyProperties; _actualSource = actualSource; _effect = effect;
        }
        internal ReadField<T> Field<T>(ConfigurationFieldId id, T requested, T actual, string oldProperty,
            string? displayOverride = null)
        {
            var present = _legacyProperties.Contains(oldProperty);
            var state = ConfigurationBridge.ReadFieldState(_connection, id, present);
            var source = _actualSource(id);
            if (source == ConfigurationValueSource.UserConfiguration && !state.Tracked)
                source = present ? ConfigurationValueSource.LegacyCompatibility : ConfigurationValueSource.Default;
            var visibility = _kind == ConfigurationReadKind.Editable ? FieldVisibility.Editable : FieldVisibility.Hidden;
            var editable = visibility == FieldVisibility.Hidden ? default! : state.UserMode == UserValueKind.Explicit ? requested : actual;
            var display = displayOverride ?? (actual switch
            {
                string text => BotAgent.Services.Agent.AgentMask.Shorten(text),
                bool value => value ? "true" : "false",
                IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => "[configured]",
            });
            var rule = new FieldDescriptor(id, _effect(id), typeof(T) == typeof(string)
                || id is ConfigurationFieldId.PlatformPolicies,
                _kind == ConfigurationReadKind.Editable, present && !state.Tracked);
            return ConfigurationBridge.ProjectField(visibility, display, rule, state.UserMode,
                source, _latestApply, editable);
        }
    }

    private static BehaviorView ProjectBehaviorView(AppSettings user, AppSettings effective, ConfigurationReadFields fields)
        => new()
        {
            AiDesire = fields.Field(ConfigurationFieldId.AiDesire, user.AiDesire, effective.AiDesire, nameof(AppSettings.AiDesire)),
            AiModeEnabled = fields.Field(ConfigurationFieldId.AiModeEnabled, user.AiModeEnabled, effective.AiModeEnabled, nameof(AppSettings.AiModeEnabled)),
            ApprovalApprovers = fields.Field(ConfigurationFieldId.ApprovalApprovers, user.ApprovalApprovers, effective.ApprovalApprovers, nameof(AppSettings.ApprovalApprovers)),
            BotPersona = fields.Field(ConfigurationFieldId.BotPersona, user.BotPersona, effective.BotPersona, nameof(AppSettings.BotPersona)),
            EnableApprovals = fields.Field(ConfigurationFieldId.EnableApprovals, user.EnableApprovals, effective.EnableApprovals, nameof(AppSettings.EnableApprovals)),
            EnableAtmosphereDamping = fields.Field(ConfigurationFieldId.EnableAtmosphereDamping, user.EnableAtmosphereDamping, effective.EnableAtmosphereDamping, nameof(AppSettings.EnableAtmosphereDamping)),
            EnableLinkPreview = fields.Field(ConfigurationFieldId.EnableLinkPreview, user.EnableLinkPreview, effective.EnableLinkPreview, nameof(AppSettings.EnableLinkPreview)),
            EnableParticipationGating = fields.Field(ConfigurationFieldId.EnableParticipationGating, user.EnableParticipationGating, effective.EnableParticipationGating, nameof(AppSettings.EnableParticipationGating)),
            EnablePoke = fields.Field(ConfigurationFieldId.EnablePoke, user.EnablePoke, effective.EnablePoke, nameof(AppSettings.EnablePoke)),
            EnableProactive = fields.Field(ConfigurationFieldId.EnableProactive, user.EnableProactive, effective.EnableProactive, nameof(AppSettings.EnableProactive)),
            EnableProfileSummary = fields.Field(ConfigurationFieldId.EnableProfileSummary, user.EnableProfileSummary, effective.EnableProfileSummary, nameof(AppSettings.EnableProfileSummary)),
            EnableQuestions = fields.Field(ConfigurationFieldId.EnableQuestions, user.EnableQuestions, effective.EnableQuestions, nameof(AppSettings.EnableQuestions)),
            EnableStickers = fields.Field(ConfigurationFieldId.EnableStickers, user.EnableStickers, effective.EnableStickers, nameof(AppSettings.EnableStickers)),
            EnableWebSearch = fields.Field(ConfigurationFieldId.EnableWebSearch, user.EnableWebSearch, effective.EnableWebSearch, nameof(AppSettings.EnableWebSearch)),
            FilterActionNarration = fields.Field(ConfigurationFieldId.FilterActionNarration, user.FilterActionNarration, effective.FilterActionNarration, nameof(AppSettings.FilterActionNarration)),
            GroupCooldownSeconds = fields.Field(ConfigurationFieldId.GroupCooldownSeconds, user.GroupCooldownSeconds, effective.GroupCooldownSeconds, nameof(AppSettings.GroupCooldownSeconds)),
            IdleFallbackSeconds = fields.Field(ConfigurationFieldId.IdleFallbackSeconds, user.IdleFallbackSeconds, effective.IdleFallbackSeconds, nameof(AppSettings.IdleFallbackSeconds)),
            IgnoreBracketMessages = fields.Field(ConfigurationFieldId.IgnoreBracketMessages, user.IgnoreBracketMessages, effective.IgnoreBracketMessages, nameof(AppSettings.IgnoreBracketMessages)),
            LinkPreviewMax = fields.Field(ConfigurationFieldId.LinkPreviewMax, user.LinkPreviewMax, effective.LinkPreviewMax, nameof(AppSettings.LinkPreviewMax)),
            LinkPreviewTimeoutSeconds = fields.Field(ConfigurationFieldId.LinkPreviewTimeoutSeconds, user.LinkPreviewTimeoutSeconds, effective.LinkPreviewTimeoutSeconds, nameof(AppSettings.LinkPreviewTimeoutSeconds)),
            MaxAgentSteps = fields.Field(ConfigurationFieldId.MaxAgentSteps, user.MaxAgentSteps, effective.MaxAgentSteps, nameof(AppSettings.MaxAgentSteps)),
            MaxConcurrentReplies = fields.Field(ConfigurationFieldId.MaxConcurrentReplies, user.MaxConcurrentReplies, effective.MaxConcurrentReplies, nameof(AppSettings.MaxConcurrentReplies)),
            MaxContextMessages = fields.Field(ConfigurationFieldId.MaxContextMessages, user.MaxContextMessages, effective.MaxContextMessages, nameof(AppSettings.MaxContextMessages)),
            MaxMessagesPerConversation = fields.Field(ConfigurationFieldId.MaxMessagesPerConversation, user.MaxMessagesPerConversation, effective.MaxMessagesPerConversation, nameof(AppSettings.MaxMessagesPerConversation)),
            MaxProfileChars = fields.Field(ConfigurationFieldId.MaxProfileChars, user.MaxProfileChars, effective.MaxProfileChars, nameof(AppSettings.MaxProfileChars)),
            MoodTtlSeconds = fields.Field(ConfigurationFieldId.MoodTtlSeconds, user.MoodTtlSeconds, effective.MoodTtlSeconds, nameof(AppSettings.MoodTtlSeconds)),
            ParticipationCooldownSeconds = fields.Field(ConfigurationFieldId.ParticipationCooldownSeconds, user.ParticipationCooldownSeconds, effective.ParticipationCooldownSeconds, nameof(AppSettings.ParticipationCooldownSeconds)),
            ParticipationMaxActiveLifetimeSeconds = fields.Field(ConfigurationFieldId.ParticipationMaxActiveLifetimeSeconds, user.ParticipationMaxActiveLifetimeSeconds, effective.ParticipationMaxActiveLifetimeSeconds, nameof(AppSettings.ParticipationMaxActiveLifetimeSeconds)),
            ParticipationMaxConsecutiveReplies = fields.Field(ConfigurationFieldId.ParticipationMaxConsecutiveReplies, user.ParticipationMaxConsecutiveReplies, effective.ParticipationMaxConsecutiveReplies, nameof(AppSettings.ParticipationMaxConsecutiveReplies)),
            ParticipationMaxExitingLifetimeSeconds = fields.Field(ConfigurationFieldId.ParticipationMaxExitingLifetimeSeconds, user.ParticipationMaxExitingLifetimeSeconds, effective.ParticipationMaxExitingLifetimeSeconds, nameof(AppSettings.ParticipationMaxExitingLifetimeSeconds)),
            ParticipationProbingMaxReplies = fields.Field(ConfigurationFieldId.ParticipationProbingMaxReplies, user.ParticipationProbingMaxReplies, effective.ParticipationProbingMaxReplies, nameof(AppSettings.ParticipationProbingMaxReplies)),
            PokeCooldownSeconds = fields.Field(ConfigurationFieldId.PokeCooldownSeconds, user.PokeCooldownSeconds, effective.PokeCooldownSeconds, nameof(AppSettings.PokeCooldownSeconds)),
            PrivateCooldownSeconds = fields.Field(ConfigurationFieldId.PrivateCooldownSeconds, user.PrivateCooldownSeconds, effective.PrivateCooldownSeconds, nameof(AppSettings.PrivateCooldownSeconds)),
            ProactiveCooldownSeconds = fields.Field(ConfigurationFieldId.ProactiveCooldownSeconds, user.ProactiveCooldownSeconds, effective.ProactiveCooldownSeconds, nameof(AppSettings.ProactiveCooldownSeconds)),
            ProactiveQuietSeconds = fields.Field(ConfigurationFieldId.ProactiveQuietSeconds, user.ProactiveQuietSeconds, effective.ProactiveQuietSeconds, nameof(AppSettings.ProactiveQuietSeconds)),
            ProfileLookupCount = fields.Field(ConfigurationFieldId.ProfileLookupCount, user.ProfileLookupCount, effective.ProfileLookupCount, nameof(AppSettings.ProfileLookupCount)),
            ProfileSummaryIntervalSeconds = fields.Field(ConfigurationFieldId.ProfileSummaryIntervalSeconds, user.ProfileSummaryIntervalSeconds, effective.ProfileSummaryIntervalSeconds, nameof(AppSettings.ProfileSummaryIntervalSeconds)),
            ProfileSummaryLines = fields.Field(ConfigurationFieldId.ProfileSummaryLines, user.ProfileSummaryLines, effective.ProfileSummaryLines, nameof(AppSettings.ProfileSummaryLines)),
            ProfileSummaryMaxChars = fields.Field(ConfigurationFieldId.ProfileSummaryMaxChars, user.ProfileSummaryMaxChars, effective.ProfileSummaryMaxChars, nameof(AppSettings.ProfileSummaryMaxChars)),
            ProfileSummaryThreshold = fields.Field(ConfigurationFieldId.ProfileSummaryThreshold, user.ProfileSummaryThreshold, effective.ProfileSummaryThreshold, nameof(AppSettings.ProfileSummaryThreshold)),
            ScenarioPreset = fields.Field(ConfigurationFieldId.ScenarioPreset, user.ScenarioPreset, effective.ScenarioPreset, nameof(AppSettings.ScenarioPreset)),
            SegmentDelayMs = fields.Field(ConfigurationFieldId.SegmentDelayMs, user.SegmentDelayMs, effective.SegmentDelayMs, nameof(AppSettings.SegmentDelayMs)),
            SplitReplies = fields.Field(ConfigurationFieldId.SplitReplies, user.SplitReplies, effective.SplitReplies, nameof(AppSettings.SplitReplies)),
            StickerCandidates = fields.Field(ConfigurationFieldId.StickerCandidates, user.StickerCandidates, effective.StickerCandidates, nameof(AppSettings.StickerCandidates)),
            StickerCooldownSeconds = fields.Field(ConfigurationFieldId.StickerCooldownSeconds, user.StickerCooldownSeconds, effective.StickerCooldownSeconds, nameof(AppSettings.StickerCooldownSeconds)),
            StickerCurateIntervalSeconds = fields.Field(ConfigurationFieldId.StickerCurateIntervalSeconds, user.StickerCurateIntervalSeconds, effective.StickerCurateIntervalSeconds, nameof(AppSettings.StickerCurateIntervalSeconds)),
            StickerLibraryMax = fields.Field(ConfigurationFieldId.StickerLibraryMax, user.StickerLibraryMax, effective.StickerLibraryMax, nameof(AppSettings.StickerLibraryMax)),
            SuitabilityThreshold = fields.Field(ConfigurationFieldId.SuitabilityThreshold, user.SuitabilityThreshold, effective.SuitabilityThreshold, nameof(AppSettings.SuitabilityThreshold)),
            WebSearchCooldownSeconds = fields.Field(ConfigurationFieldId.WebSearchCooldownSeconds, user.WebSearchCooldownSeconds, effective.WebSearchCooldownSeconds, nameof(AppSettings.WebSearchCooldownSeconds)),
            WebSearchMaxResults = fields.Field(ConfigurationFieldId.WebSearchMaxResults, user.WebSearchMaxResults, effective.WebSearchMaxResults, nameof(AppSettings.WebSearchMaxResults)),
            WebSearchSources = fields.Field(ConfigurationFieldId.WebSearchSources, user.WebSearchSources, effective.WebSearchSources, nameof(AppSettings.WebSearchSources)),
            WebSearchTimeoutSeconds = fields.Field(ConfigurationFieldId.WebSearchTimeoutSeconds, user.WebSearchTimeoutSeconds, effective.WebSearchTimeoutSeconds, nameof(AppSettings.WebSearchTimeoutSeconds)),
            WebSearchUseModelSearch = fields.Field(ConfigurationFieldId.WebSearchUseModelSearch, user.WebSearchUseModelSearch, effective.WebSearchUseModelSearch, nameof(AppSettings.WebSearchUseModelSearch)),
        };

    private static ModelView ProjectModelView(AppSettings user, AppSettings effective, ConfigurationReadFields fields)
        => new()
        {
            AdaptiveSamplingEnabled = fields.Field(ConfigurationFieldId.AdaptiveSamplingEnabled, user.AdaptiveSamplingEnabled, effective.AdaptiveSamplingEnabled, nameof(AppSettings.AdaptiveSamplingEnabled)),
            DefaultTemperature = fields.Field(ConfigurationFieldId.DefaultTemperature, user.DefaultTemperature, effective.DefaultTemperature, nameof(AppSettings.DefaultTemperature)),
            DefaultTopP = fields.Field(ConfigurationFieldId.DefaultTopP, user.DefaultTopP, effective.DefaultTopP, nameof(AppSettings.DefaultTopP)),
            EmotionalTemperature = fields.Field(ConfigurationFieldId.EmotionalTemperature, user.EmotionalTemperature, effective.EmotionalTemperature, nameof(AppSettings.EmotionalTemperature)),
            EmotionalTopP = fields.Field(ConfigurationFieldId.EmotionalTopP, user.EmotionalTopP, effective.EmotionalTopP, nameof(AppSettings.EmotionalTopP)),
            FastModel = fields.Field(ConfigurationFieldId.FastModel, user.FastModel, effective.FastModel, nameof(AppSettings.FastModel)),
            FastReply = fields.Field(ConfigurationFieldId.FastReply, user.FastReply, effective.FastReply, nameof(AppSettings.FastReply)),
            FastThinkingBudget = fields.Field(ConfigurationFieldId.FastThinkingBudget, user.FastThinkingBudget, effective.FastThinkingBudget, nameof(AppSettings.FastThinkingBudget)),
            FastThinkingCustomBudget = fields.Field(ConfigurationFieldId.FastThinkingCustomBudget, user.FastThinkingCustomBudget, effective.FastThinkingCustomBudget, nameof(AppSettings.FastThinkingCustomBudget)),
            MaxTokens = fields.Field(ConfigurationFieldId.MaxTokens, user.MaxTokens, effective.MaxTokens, nameof(AppSettings.MaxTokens)),
            Model = fields.Field(ConfigurationFieldId.Model, user.Model, effective.Model, nameof(AppSettings.Model)),
            ModelBaseUrl = fields.Field(ConfigurationFieldId.ModelBaseUrl, user.ModelBaseUrl, effective.ModelBaseUrl, nameof(AppSettings.ModelBaseUrl)),
            RationalTemperature = fields.Field(ConfigurationFieldId.RationalTemperature, user.RationalTemperature, effective.RationalTemperature, nameof(AppSettings.RationalTemperature)),
            RationalTopP = fields.Field(ConfigurationFieldId.RationalTopP, user.RationalTopP, effective.RationalTopP, nameof(AppSettings.RationalTopP)),
            ThinkingBudget = fields.Field(ConfigurationFieldId.ThinkingBudget, user.ThinkingBudget, effective.ThinkingBudget, nameof(AppSettings.ThinkingBudget)),
            ThinkingCustomBudget = fields.Field(ConfigurationFieldId.ThinkingCustomBudget, user.ThinkingCustomBudget, effective.ThinkingCustomBudget, nameof(AppSettings.ThinkingCustomBudget)),
        };

    private static AgentView ProjectAgentView(AppSettings user, AppSettings effective, ConfigurationReadFields fields)
        => new()
        {
            AgentAllowedUsers = fields.Field(ConfigurationFieldId.AgentAllowedUsers, user.AgentAllowedUsers, effective.AgentAllowedUsers, nameof(AppSettings.AgentAllowedUsers)),
            AgentPrefix = fields.Field(ConfigurationFieldId.AgentPrefix, user.AgentPrefix, effective.AgentPrefix, nameof(AppSettings.AgentPrefix)),
            AgentProgressSeconds = fields.Field(ConfigurationFieldId.AgentProgressSeconds, user.AgentProgressSeconds, effective.AgentProgressSeconds, nameof(AppSettings.AgentProgressSeconds)),
            AgentPrompt = fields.Field(ConfigurationFieldId.AgentPrompt, user.AgentPrompt, effective.AgentPrompt, nameof(AppSettings.AgentPrompt)),
            AgentReasoningEffort = fields.Field(ConfigurationFieldId.AgentReasoningEffort, user.AgentReasoningEffort, effective.AgentReasoningEffort, nameof(AppSettings.AgentReasoningEffort)),
            AgentReasoningLevels = fields.Field(ConfigurationFieldId.AgentReasoningLevels, user.AgentReasoningLevels, effective.AgentReasoningLevels, nameof(AppSettings.AgentReasoningLevels)),
            AgentReplyMaxChars = fields.Field(ConfigurationFieldId.AgentReplyMaxChars, user.AgentReplyMaxChars, effective.AgentReplyMaxChars, nameof(AppSettings.AgentReplyMaxChars)),
            AgentServerBaseUrl = fields.Field(ConfigurationFieldId.AgentServerBaseUrl, user.AgentServerBaseUrl, effective.AgentServerBaseUrl, nameof(AppSettings.AgentServerBaseUrl)),
            AgentServerCommandTimeoutSeconds = fields.Field(ConfigurationFieldId.AgentServerCommandTimeoutSeconds, user.AgentServerCommandTimeoutSeconds, effective.AgentServerCommandTimeoutSeconds, nameof(AppSettings.AgentServerCommandTimeoutSeconds)),
            AgentServerDocker = fields.Field(ConfigurationFieldId.AgentServerDocker, user.AgentServerDocker, effective.AgentServerDocker, nameof(AppSettings.AgentServerDocker)),
            AgentServerKeepContext = fields.Field(ConfigurationFieldId.AgentServerKeepContext, user.AgentServerKeepContext, effective.AgentServerKeepContext, nameof(AppSettings.AgentServerKeepContext)),
            AgentServerMaxSteps = fields.Field(ConfigurationFieldId.AgentServerMaxSteps, user.AgentServerMaxSteps, effective.AgentServerMaxSteps, nameof(AppSettings.AgentServerMaxSteps)),
            AgentServerModel = fields.Field(ConfigurationFieldId.AgentServerModel, user.AgentServerModel, effective.AgentServerModel, nameof(AppSettings.AgentServerModel)),
            AgentServerQqActions = fields.Field(ConfigurationFieldId.AgentServerQqActions, user.AgentServerQqActions, effective.AgentServerQqActions, nameof(AppSettings.AgentServerQqActions)),
            AgentServerTools = fields.Field(ConfigurationFieldId.AgentServerTools, user.AgentServerTools, effective.AgentServerTools, nameof(AppSettings.AgentServerTools)),
            AgentServerUseGate = fields.Field(ConfigurationFieldId.AgentServerUseGate, user.AgentServerUseGate, effective.AgentServerUseGate, nameof(AppSettings.AgentServerUseGate)),
            AgentServerWorkDir = fields.Field(ConfigurationFieldId.AgentServerWorkDir, user.AgentServerWorkDir, effective.AgentServerWorkDir, nameof(AppSettings.AgentServerWorkDir)),
            EnableAgentBridge = fields.Field(ConfigurationFieldId.EnableAgentBridge, user.EnableAgentBridge, effective.EnableAgentBridge, nameof(AppSettings.EnableAgentBridge)),
            EnableAgentMask = fields.Field(ConfigurationFieldId.EnableAgentMask, user.AgentMaskSensitive, effective.AgentMaskSensitive, nameof(AppSettings.AgentMaskSensitive)),
            EnableServerAgent = fields.Field(ConfigurationFieldId.EnableServerAgent, user.EnableServerAgent, effective.EnableServerAgent, nameof(AppSettings.EnableServerAgent)),
            PanelDeployEnabled = fields.Field(ConfigurationFieldId.PanelDeployEnabled, user.PanelDeployEnabled, effective.PanelDeployEnabled, nameof(AppSettings.PanelDeployEnabled)),
            PanelDeployUrl = fields.Field(ConfigurationFieldId.PanelDeployUrl, user.PanelDeployUrl, effective.PanelDeployUrl, nameof(AppSettings.PanelDeployUrl)),
        };

    private static PlatformView ProjectPlatformView(AppSettings user, AppSettings effective, ConfigurationReadFields fields
        , ImmutableArray<PlatformPolicyEntryView> requestedPolicies, ImmutableArray<PlatformPolicyEntryView> effectivePolicies)
        => new()
        {
            FeishuApiBase = fields.Field(ConfigurationFieldId.FeishuApiBase, user.FeishuApiBase, effective.FeishuApiBase, nameof(AppSettings.FeishuApiBase)),
            FeishuAppId = fields.Field(ConfigurationFieldId.FeishuAppId, user.FeishuAppId, effective.FeishuAppId, nameof(AppSettings.FeishuAppId)),
            FeishuEnabled = fields.Field(ConfigurationFieldId.FeishuEnabled, user.FeishuEnabled, effective.FeishuEnabled, nameof(AppSettings.FeishuEnabled)),
            FeishuWhitelist = fields.Field(ConfigurationFieldId.FeishuWhitelist, user.FeishuWhitelist, effective.FeishuWhitelist, nameof(AppSettings.FeishuWhitelist)),
            LocalChannelIds = fields.Field(ConfigurationFieldId.LocalChannelIds, user.LocalChannelIds, effective.LocalChannelIds, nameof(AppSettings.LocalChannelIds)),
            MessageWhitelist = fields.Field(ConfigurationFieldId.MessageWhitelist, user.MessageWhitelist, effective.MessageWhitelist, nameof(AppSettings.MessageWhitelist)),
            OfficialAppId = fields.Field(ConfigurationFieldId.OfficialAppId, user.OfficialAppId, effective.OfficialAppId, nameof(AppSettings.OfficialAppId)),
            OfficialChatEnabled = fields.Field(ConfigurationFieldId.OfficialChatEnabled, user.OfficialChatEnabled, effective.OfficialChatEnabled, nameof(AppSettings.OfficialChatEnabled)),
            OfficialEnabled = fields.Field(ConfigurationFieldId.OfficialEnabled, user.OfficialEnabled, effective.OfficialEnabled, nameof(AppSettings.OfficialEnabled)),
            OfficialSandbox = fields.Field(ConfigurationFieldId.OfficialSandbox, user.OfficialSandbox, effective.OfficialSandbox, nameof(AppSettings.OfficialSandbox)),
            OfficialWhitelistGroups = fields.Field(ConfigurationFieldId.OfficialWhitelistGroups, user.OfficialWhitelistGroups, effective.OfficialWhitelistGroups, nameof(AppSettings.OfficialWhitelistGroups)),
            OfficialWhitelistPrivates = fields.Field(ConfigurationFieldId.OfficialWhitelistPrivates, user.OfficialWhitelistPrivates, effective.OfficialWhitelistPrivates, nameof(AppSettings.OfficialWhitelistPrivates)),
            PlatformPolicies = fields.Field(ConfigurationFieldId.PlatformPolicies, requestedPolicies, effectivePolicies,
                nameof(AppSettings.PlatformPolicies), effectivePolicies.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " items"),
            PrivateChatEnabled = fields.Field(ConfigurationFieldId.PrivateChatEnabled, user.PrivateChatEnabled, effective.PrivateChatEnabled, nameof(AppSettings.PrivateChatEnabled)),
            WhitelistGroups = fields.Field(ConfigurationFieldId.WhitelistGroups, user.WhitelistGroups, effective.WhitelistGroups, nameof(AppSettings.WhitelistGroups)),
            WhitelistPrivates = fields.Field(ConfigurationFieldId.WhitelistPrivates, user.WhitelistPrivates, effective.WhitelistPrivates, nameof(AppSettings.WhitelistPrivates)),
        };

    private static VoiceView ProjectVoiceView(AppSettings user, AppSettings effective, ConfigurationReadFields fields)
        => new()
        {
            EnableVoice = fields.Field(ConfigurationFieldId.EnableVoice, user.EnableVoice, effective.EnableVoice, nameof(AppSettings.EnableVoice)),
            TtsApiBase = fields.Field(ConfigurationFieldId.TtsApiBase, user.TtsApiBase, effective.TtsApiBase, nameof(AppSettings.TtsApiBase)),
            TtsModel = fields.Field(ConfigurationFieldId.TtsModel, user.TtsModel, effective.TtsModel, nameof(AppSettings.TtsModel)),
            TtsProvider = fields.Field(ConfigurationFieldId.TtsProvider, user.TtsProvider, effective.TtsProvider, nameof(AppSettings.TtsProvider)),
            TtsServiceUrl = fields.Field(ConfigurationFieldId.TtsServiceUrl, user.TtsServiceUrl, effective.TtsServiceUrl, nameof(AppSettings.TtsServiceUrl)),
            VoiceEagerness = fields.Field(ConfigurationFieldId.VoiceEagerness, user.VoiceEagerness, effective.VoiceEagerness, nameof(AppSettings.VoiceEagerness)),
            VoiceEmotion = fields.Field(ConfigurationFieldId.VoiceEmotion, user.VoiceEmotion, effective.VoiceEmotion, nameof(AppSettings.VoiceEmotion)),
            VoiceMaxChars = fields.Field(ConfigurationFieldId.VoiceMaxChars, user.VoiceMaxChars, effective.VoiceMaxChars, nameof(AppSettings.VoiceMaxChars)),
            VoiceName = fields.Field(ConfigurationFieldId.VoiceName, user.VoiceName, effective.VoiceName, nameof(AppSettings.VoiceName)),
            VoicePitch = fields.Field(ConfigurationFieldId.VoicePitch, user.VoicePitch, effective.VoicePitch, nameof(AppSettings.VoicePitch)),
            VoiceSpeed = fields.Field(ConfigurationFieldId.VoiceSpeed, user.VoiceSpeed, effective.VoiceSpeed, nameof(AppSettings.VoiceSpeed)),
            VoiceVol = fields.Field(ConfigurationFieldId.VoiceVol, user.VoiceVol, effective.VoiceVol, nameof(AppSettings.VoiceVol)),
        };

    private static MusicView ProjectMusicView(AppSettings user, AppSettings effective, ConfigurationReadFields fields)
        => new()
        {
            EnableMusic = fields.Field(ConfigurationFieldId.EnableMusic, user.EnableMusic, effective.EnableMusic, nameof(AppSettings.EnableMusic)),
            MusicBitrate = fields.Field(ConfigurationFieldId.MusicBitrate, user.MusicBitrate, effective.MusicBitrate, nameof(AppSettings.MusicBitrate)),
            MusicKeepAudio = fields.Field(ConfigurationFieldId.MusicKeepAudio, user.MusicKeepAudio, effective.MusicKeepAudio, nameof(AppSettings.MusicKeepAudio)),
            MusicLibraryMax = fields.Field(ConfigurationFieldId.MusicLibraryMax, user.MusicLibraryMax, effective.MusicLibraryMax, nameof(AppSettings.MusicLibraryMax)),
            MusicListenCooldownSeconds = fields.Field(ConfigurationFieldId.MusicListenCooldownSeconds, user.MusicListenCooldownSeconds, effective.MusicListenCooldownSeconds, nameof(AppSettings.MusicListenCooldownSeconds)),
            MusicMaxAnalysisSeconds = fields.Field(ConfigurationFieldId.MusicMaxAnalysisSeconds, user.MusicMaxAnalysisSeconds, effective.MusicMaxAnalysisSeconds, nameof(AppSettings.MusicMaxAnalysisSeconds)),
            MusicMaxDownloadMb = fields.Field(ConfigurationFieldId.MusicMaxDownloadMb, user.MusicMaxDownloadMb, effective.MusicMaxDownloadMb, nameof(AppSettings.MusicMaxDownloadMb)),
            MusicNoteTtlDays = fields.Field(ConfigurationFieldId.MusicNoteTtlDays, user.MusicNoteTtlDays, effective.MusicNoteTtlDays, nameof(AppSettings.MusicNoteTtlDays)),
            MusicSendAudioToModel = fields.Field(ConfigurationFieldId.MusicSendAudioToModel, user.MusicSendAudioToModel, effective.MusicSendAudioToModel, nameof(AppSettings.MusicSendAudioToModel)),
            MusicSources = fields.Field(ConfigurationFieldId.MusicSources, user.MusicSources, effective.MusicSources, nameof(AppSettings.MusicSources)),
            MusicUnderstandModel = fields.Field(ConfigurationFieldId.MusicUnderstandModel, user.MusicUnderstandModel, effective.MusicUnderstandModel, nameof(AppSettings.MusicUnderstandModel)),
            NeteaseBaseUrl = fields.Field(ConfigurationFieldId.NeteaseBaseUrl, user.NeteaseBaseUrl, effective.NeteaseBaseUrl, nameof(AppSettings.NeteaseBaseUrl)),
        };

    private static ReportingView ProjectReportingView(AppSettings user, AppSettings effective, ConfigurationReadFields fields)
        => new()
        {
            HealthReportEnabled = fields.Field(ConfigurationFieldId.HealthReportEnabled, user.HealthReportEnabled, effective.HealthReportEnabled, nameof(AppSettings.HealthReportEnabled)),
            HealthReportTargets = fields.Field(ConfigurationFieldId.HealthReportTargets, user.HealthReportTargets, effective.HealthReportTargets, nameof(AppSettings.HealthReportTargets)),
            HealthReportTime = fields.Field(ConfigurationFieldId.HealthReportTime, user.HealthReportTime, effective.HealthReportTime, nameof(AppSettings.HealthReportTime)),
        };

    /// <summary>Audit records facts only, never values, identities or an assumed application success.</summary>
    internal static void AppendConfigurationAuditInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection,
        ConfigurationReceipt receipt, ImmutableArray<ConfigurationFieldId> fields)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ContractRevision is not (ConfigurationContract.Revision or ConfigurationContract.LegacyRevision)
            || !Enum.IsDefined(receipt.Decision)
            || !Enum.IsDefined(receipt.CommitState) || !Enum.IsDefined(receipt.ApplyState)
            || fields.IsDefault || fields.Any(field => !Enum.IsDefined(field)))
            throw new ArgumentException("invalid_configuration_audit_fields");
        var ids = string.Join(",", fields.Distinct().OrderBy(field => (int)field)
            .Select(field => ((int)field).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var observed = receipt.ObservedVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var committed = receipt.CommittedVersion?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        var detail = $"fields={ids};observed={observed};committed={committed}"
            + $";decision={(int)receipt.Decision};commit={(int)receipt.CommitState};apply={(int)receipt.ApplyState}";
        new AuditLogStore().AppendInTransaction(connection, new BotAgent.Services.Ops.AuditEvent(
            "configuration_decision", "configuration-writer", "bot", detail, receipt.ContractRevision));
    }

    /// <summary>Trusted Host adapter: only DB user facts enter persistence; effective values are read projections.</summary>
    internal static ImmutableArray<ProviderConfigurationView> ProjectRegisteredProviderViews(
        Microsoft.Data.Sqlite.SqliteConnection connection, IReadOnlyList<PersistedModelProvider> desired,
        IReadOnlyList<PersistedModelProvider> captured, ConfigurationReadKind kind)
    {
        var modes = ModelProviderStore.LoadFieldModesInTransaction(connection);
        if (desired.Any(provider => string.IsNullOrWhiteSpace(provider.Id) || provider.Id.Trim() != provider.Id
                || ContainsEndpointUserInfo(provider.BaseUrl))
            || desired.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count() != desired.Count
            || captured.Any(provider => ContainsEndpointUserInfo(provider.BaseUrl)))
            throw new JsonException("provider_registration_needs_review");
        return desired.Select(provider =>
        {
            var running = captured.FirstOrDefault(candidate => candidate.Id == provider.Id);
            var changed = running is null || running.IsEnabled != provider.IsEnabled || running.Priority != provider.Priority
                || running.BaseUrl != provider.BaseUrl || running.ModelName != provider.ModelName
                || running.SecretKeyRef != provider.SecretKeyRef;
            var apply = changed ? ApplyState.RestartRequired : ApplyState.NotStarted;
            var binding = ModelProviderStore.ClassifyCredentialBinding(provider.SecretKeyRef);
            ReadField<string> Field(string requested, string display, int bit, bool name)
            {
                var explicitField = modes.TryGetValue(provider.Id, out var bits) && (bits & bit) != 0;
                var rule = new FieldDescriptor(ConfigurationFieldId.RegisteredProviders,
                    name ? FieldEffect.Hot : FieldEffect.Restart, name, kind == ConfigurationReadKind.Editable,
                    binding == CredentialBindingState.Unclassified);
                var visibility = kind == ConfigurationReadKind.Overview ? FieldVisibility.Hidden
                    : name ? FieldVisibility.NameEditable : FieldVisibility.Editable;
                return ConfigurationBridge.ProjectField(visibility, BotAgent.Services.Agent.AgentMask.Shorten(display), rule,
                    UserValueKind.Explicit, explicitField ? ConfigurationValueSource.UserConfiguration
                        : ConfigurationValueSource.LegacyCompatibility, apply, requested);
            }
            return new ProviderConfigurationView(new ProviderKey(provider.Id),
                Field(provider.Name, running?.Name ?? provider.Name, 1, true),
                Field(provider.BaseUrl, running?.BaseUrl ?? provider.BaseUrl, 2, false),
                Field(provider.ModelName, running?.ModelName ?? provider.ModelName, 4, false),
                provider.Priority, provider.IsEnabled, binding, apply);
        }).ToImmutableArray();
    }

    private static bool ContainsEndpointUserInfo(string? endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.UserInfo.Length != 0;

    private static ImmutableArray<ActionId> PolicyActions(AppSettings settings)
        => BotAgent.Services.Agent.QqActionCatalog.ParseAllowed(settings.AgentServerQqActions)
            .OrderBy(value => value, StringComparer.Ordinal).Select(value => new ActionId(value)).ToImmutableArray();

    internal static void ProjectPlatformPoliciesInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection,
        AppSettings settings)
    {
        var snapshot = ConfigurationBridge.LoadPlatformPolicies(connection);
        settings.RuntimePlatformPolicies = snapshot.Stored ? snapshot.Entries : null;
        settings.RuntimePlatformPolicyExplicitFields = snapshot.ExplicitFields;
        using var command = connection.CreateCommand();
        command.CommandText = ReadSettingsSql;
        var json = command.ExecuteScalar() as string;
        var properties = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json)!.AsObject();
        var sources = ImmutableDictionary.CreateBuilder<ConfigurationFieldId, ConfigurationValueSource>();
        foreach (var id in new[] { ConfigurationFieldId.PrivateChatEnabled, ConfigurationFieldId.OfficialEnabled,
            ConfigurationFieldId.OfficialChatEnabled, ConfigurationFieldId.FeishuEnabled, ConfigurationFieldId.LocalChannelIds,
            ConfigurationFieldId.WhitelistGroups, ConfigurationFieldId.WhitelistPrivates, ConfigurationFieldId.MessageWhitelist,
            ConfigurationFieldId.OfficialWhitelistGroups, ConfigurationFieldId.OfficialWhitelistPrivates,
            ConfigurationFieldId.FeishuWhitelist, ConfigurationFieldId.AgentServerQqActions })
        {
            var present = properties.ContainsKey(id.ToString());
            var state = ConfigurationBridge.ReadFieldState(connection, id, present);
            sources[id] = state.Tracked && state.UserMode == UserValueKind.Explicit ? ConfigurationValueSource.UserConfiguration
                : present ? ConfigurationValueSource.LegacyCompatibility : ConfigurationValueSource.Default;
        }
        settings.RuntimePlatformPolicyBaselineSources = sources.ToImmutable();
    }

    private static ImmutableArray<PlatformPolicyEntryView> PolicyViews(AppSettings desired, AppSettings runtime,
        ConfigurationReadKind kind, bool requested)
    {
        var settings = requested ? desired : runtime;
        var entries = settings.RuntimePlatformPolicies ?? ImmutableArray<PlatformPolicyEntry>.Empty;
        var targets = entries.Select(entry => entry.Target)
            .Concat(settings.DeclaredPlatformDrivers.Select(key => (PolicyTarget)new PolicyTarget.Instance(key)))
            .Distinct().OrderBy(target => JsonSerializer.Serialize(target), StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<PlatformPolicyEntryView>();
        foreach (var target in targets)
        {
            var entry = entries.SingleOrDefault(item => item.Target == target);
            var resolved = PlatformSwitchSettings.ResolvePolicyRules(settings, target, PolicyActions(settings));
            var effective = PlatformSwitchSettings.ResolvePolicyRules(runtime, target, PolicyActions(runtime));
            var sources = PlatformSwitchSettings.ResolvePolicySources(settings, target);
            var bits = settings.RuntimePlatformPolicyExplicitFields.GetValueOrDefault(target);
            ReadField<T> Field<T>(T ruleValue, T resolvedValue, T effectiveValue, bool inherit, int bit,
                ConfigurationValueSource source)
            {
                var mode = entry is null ? UserValueKind.Absent : inherit ? UserValueKind.Inherited : UserValueKind.Explicit;
                var visibility = kind == ConfigurationReadKind.Editable ? FieldVisibility.Editable : FieldVisibility.Hidden;
                var descriptor = new FieldDescriptor(ConfigurationFieldId.PlatformPolicies, FieldEffect.Restart, true,
                    kind == ConfigurationReadKind.Editable, entry is not null && (bits & bit) == 0);
                var applied = requested ? ApplyState.RestartRequired : runtime.RuntimePlatformPolicies.HasValue
                    ? ApplyState.Applied : ApplyState.NotStarted;
                var display = effectiveValue is ListRule list && list.TryGetExplicitValue(out var text)
                    ? BotAgent.Services.Agent.AgentMask.Shorten(text) : "[policy]";
                return ConfigurationBridge.ProjectField(visibility, display, descriptor, mode, source, applied,
                    mode == UserValueKind.Explicit ? ruleValue : resolvedValue);
            }
            var configuration = entry?.Configuration ?? resolved;
            result.Add(new(target,
                Field(configuration.Enabled, resolved.Enabled, effective.Enabled, configuration.Enabled == ToggleRule.Inherit, 1, sources.Enabled),
                Field(configuration.ChatEnabled, resolved.ChatEnabled, effective.ChatEnabled, configuration.ChatEnabled == ToggleRule.Inherit, 2, sources.ChatEnabled),
                Field(configuration.GroupWhitelist, resolved.GroupWhitelist, effective.GroupWhitelist, configuration.GroupWhitelist.Kind == ListRuleKind.Inherit, 4, sources.GroupWhitelist),
                Field(configuration.PrivateWhitelist, resolved.PrivateWhitelist, effective.PrivateWhitelist, configuration.PrivateWhitelist.Kind == ListRuleKind.Inherit, 8, sources.PrivateWhitelist),
                Field(configuration.AllowedActions, resolved.AllowedActions, effective.AllowedActions, configuration.AllowedActions.Kind == ActionRuleKind.Inherit, 16, sources.AllowedActions),
                Field(configuration.Features, resolved.Features, effective.Features, configuration.Features.Length == 0, 32, sources.Features)));
        }
        return result.ToImmutable();
    }

    internal sealed class ConfigurationParticipant : StorageConfiguration.ConfigurationStorageParticipant
    {
        private readonly SettingsBox _runtime;
        private readonly Func<ConfigurationCaller, ConfigurationAccess, bool> _authorization;
        private readonly Func<Microsoft.Data.Sqlite.SqliteConnection,
            ImmutableArray<StorageConfiguration.SecretMutation>, ImmutableArray<SecretMetadata>> _secretSources;
        private readonly Func<Microsoft.Data.Sqlite.SqliteConnection, ConfigurationReadKind,
            RegisteredConfigurationView> _registeredRead;
        private readonly Func<ConfigurationFieldId, ConfigurationValueSource> _effectiveSource;
        private readonly Func<ConfigurationFieldId, FieldEffect> _effect;
        private readonly bool _primaryRequiresCredential;
        private readonly string _modelFallback;
        private readonly string _baseUrlFallback;
        private ImmutableArray<ConfigurationFieldId> _initializationFields = ImmutableArray<ConfigurationFieldId>.Empty;

        internal ConfigurationParticipant(SettingsBox runtime,
            Func<ConfigurationCaller, ConfigurationAccess, bool> authorization,
            Func<Microsoft.Data.Sqlite.SqliteConnection, ImmutableArray<StorageConfiguration.SecretMutation>,
                ImmutableArray<SecretMetadata>> secretSources,
            Func<Microsoft.Data.Sqlite.SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registeredRead,
            Func<ConfigurationFieldId, ConfigurationValueSource> effectiveSource,
            Func<ConfigurationFieldId, FieldEffect> effect, bool primaryRequiresCredential = true)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
            _secretSources = secretSources ?? throw new ArgumentNullException(nameof(secretSources));
            _registeredRead = registeredRead ?? throw new ArgumentNullException(nameof(registeredRead));
            _effectiveSource = effectiveSource ?? throw new ArgumentNullException(nameof(effectiveSource));
            _effect = effect ?? throw new ArgumentNullException(nameof(effect));
            _primaryRequiresCredential = primaryRequiresCredential;
            var defaults = new AppSettings();
            _modelFallback = FixedModelEnvironment("BOTAGENT_MODEL", "QQCHAT_MODEL", "OPENAI_MODEL") ?? defaults.Model;
            _baseUrlFallback = FixedModelEnvironment("BOTAGENT_BASE_URL", "QQCHAT_BASE_URL", "OPENAI_BASE_URL")
                ?? defaults.ModelBaseUrl;
        }

        private static string? FixedModelEnvironment(string first, string second, string third)
        {
            foreach (var name in new[] { first, second, third })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }

        internal override bool Reauthorize(ConfigurationCaller caller, ConfigurationAccess required)
            => _authorization(caller, required);

        private (bool Changed, ImmutableArray<PlatformPolicyEntry> Entries,
            ImmutableDictionary<PolicyTarget, int> ExplicitFields, ImmutableArray<FieldError> Errors,
            ConfigurationResultCode Code) PreparePlatformPolicies(Microsoft.Data.Sqlite.SqliteConnection connection,
            AppSettings persistedUser, AppSettings scalarCandidate, ConfigurationPatch intent,
            StorageConfiguration.ConfigurationStorageProfile profile)
        {
            var errors = ImmutableArray<FieldError>.Empty;
            var empty = ImmutableArray<PlatformPolicyEntry>.Empty;
            var noModes = ImmutableDictionary<PolicyTarget, int>.Empty;
            try
            {
                var snapshot = ConfigurationBridge.LoadPlatformPolicies(connection);
                var runtime = _runtime.Current;
                var original = snapshot.Entries;
                if (!snapshot.Stored)
                {
                    persistedUser.DeclaredPlatformDrivers = runtime.DeclaredPlatformDrivers;
                    persistedUser.DeclaredPlatformActions = runtime.DeclaredPlatformActions;
                    var imported = PlatformSwitchSettings.ImportLegacyPolicies(persistedUser,
                        runtime.DeclaredPlatformDrivers, PolicyActions(persistedUser));
                    if (imported.Code != ConfigurationResultCode.Ok)
                        return (false, empty, noModes, imported.Errors, imported.Code);
                    original = imported.Entries;
                }
                var entries = original.ToDictionary(entry => entry.Target);
                var modes = snapshot.ExplicitFields.ToBuilder();
                var table = intent.Platform?.PlatformPolicies ?? default;
                if (table.Kind != EditKind.Keep)
                {
                    if (profile == StorageConfiguration.ConfigurationStorageProfile.Legacy && snapshot.Stored
                        && original.Any(entry => entry.Target is PolicyTarget.Instance
                            || NotLegacyList(entry.Configuration.GroupWhitelist)
                            || NotLegacyList(entry.Configuration.PrivateWhitelist)))
                        return (false, empty, noModes, ImmutableArray.Create(Error(ConfigurationFieldId.PlatformPolicies,
                            "legacy_policy_needs_review")), ConfigurationResultCode.LegacyNeedsReview);
                    entries = (table.TryGetValue(out var replacement) ? replacement : empty).ToDictionary(entry => entry.Target);
                    modes.Clear();
                    foreach (var target in entries.Keys) modes[target] = 63;
                }
                foreach (var edit in intent.Registered?.Policies ?? ImmutableArray<PolicyEdit>.Empty)
                {
                    entries[edit.Target] = new(edit.Target, edit.Configuration);
                    modes[edit.Target] = 63;
                }
                if (profile == StorageConfiguration.ConfigurationStorageProfile.Legacy && table.Kind == EditKind.Keep && intent.Platform is { } legacy)
                {
                    Sync(PlatformId.QqOfficial, AccountScope.Legacy, legacy.OfficialEnabled, legacy.OfficialChatEnabled);
                    Sync(PlatformId.QqPrivate, AccountScope.Legacy, default, legacy.PrivateChatEnabled);
                    Sync(PlatformId.Feishu, AccountScope.Default, legacy.FeishuEnabled, legacy.FeishuEnabled);
                    if (legacy.LocalChannelIds.TryGetValue(out var ids))
                    {
                        var enabled = FieldEdit<bool>.Set(!string.IsNullOrWhiteSpace(ids));
                        Sync(PlatformId.Local, AccountScope.Legacy, enabled, enabled);
                    }
                }
                void Sync(string platform, string account, FieldEdit<bool> enabled, FieldEdit<bool> chat)
                {
                    var target = new PolicyTarget.AccountDefault(platform, account);
                    if (enabled.Kind == EditKind.Keep && chat.Kind == EditKind.Keep) return;
                    if (!entries.TryGetValue(target, out var entry))
                        entry = new(target, new(ToggleRule.Inherit, ToggleRule.Inherit, ListRule.Inherit(), ListRule.Inherit(),
                            new(ActionRuleKind.Inherit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty));
                    var rules = entry.Configuration;
                    var bits = modes.GetValueOrDefault(target);
                    if (enabled.TryGetValue(out var e))
                    {
                        rules = rules with { Enabled = e ? ToggleRule.Enabled : ToggleRule.Disabled };
                        bits |= 1;
                    }
                    if (chat.TryGetValue(out var c))
                    {
                        rules = rules with { ChatEnabled = c ? ToggleRule.Enabled : ToggleRule.Disabled };
                        bits |= 2;
                    }
                    entries[target] = entry with { Configuration = rules };
                    modes[target] = bits;
                }
                var final = entries.Values.OrderBy(entry => JsonSerializer.Serialize(entry.Target), StringComparer.Ordinal).ToImmutableArray();
                errors = PlatformSwitchSettings.ValidatePolicyEntries(final, runtime.DeclaredPlatformDrivers, runtime.DeclaredPlatformActions);
                if (errors.Length != 0) return (false, final, modes.ToImmutable(), errors,
                    profile == StorageConfiguration.ConfigurationStorageProfile.Legacy
                        ? ConfigurationResultCode.LegacyNeedsReview : ConfigurationResultCode.ValidationFailed);
                var old = original.OrderBy(entry => JsonSerializer.Serialize(entry.Target), StringComparer.Ordinal).ToArray();
                var changed = JsonSerializer.Serialize(old) != JsonSerializer.Serialize(final)
                    || modes.Count != snapshot.ExplicitFields.Count
                    || modes.Any(pair => !snapshot.ExplicitFields.TryGetValue(pair.Key, out var bits) || bits != pair.Value);
                scalarCandidate.RuntimePlatformPolicies = final;
                scalarCandidate.RuntimePlatformPolicyExplicitFields = modes.ToImmutable();
                scalarCandidate.DeclaredPlatformDrivers = runtime.DeclaredPlatformDrivers;
                scalarCandidate.DeclaredPlatformActions = runtime.DeclaredPlatformActions;
                return (changed, final, modes.ToImmutable(), errors, ConfigurationResultCode.Ok);
            }
            catch (JsonException)
            {
                return (false, empty, noModes, ImmutableArray.Create(Error(ConfigurationFieldId.PlatformPolicies,
                    "platform_policy_needs_review")), ConfigurationResultCode.LegacyNeedsReview);
            }
        }

        private static bool NotLegacyList(ListRule list)
            => list.Kind == ListRuleKind.AllowAll
                || list.TryGetExplicitValue(out var text) && string.IsNullOrWhiteSpace(text);

        internal override bool Initialize(Microsoft.Data.Sqlite.SqliteConnection connection, long serverUnixSeconds)
        {
            _ = StorageConfiguration.PluginStore.LoadPluginStatesInTransaction(connection);
            var fields = ImmutableArray.CreateBuilder<ConfigurationFieldId>();
            var providerRows = ModelProviderStore.LoadAllInTransaction(connection);
            if (!providerRows.Any(row => row.Id == "primary"))
            {
                if (providerRows.Any(row => row.Id.Trim() == "primary"))
                    throw new InvalidOperationException("configuration_registration_needs_review");
                var bootstrap = _runtime.Current;
                ModelProviderStore.UpsertMetadataInTransaction(connection, new ModelProviderDefinition("primary", 0, "Primary",
                    bootstrap.ModelBaseUrl, bootstrap.Model,
                    string.IsNullOrWhiteSpace(bootstrap.ApiKeyOverride) ? "env:QQCHAT_API_KEY" : "secret:apiKey", true));
                fields.Add(ConfigurationFieldId.RegisteredProviders);
            }
            if (!ReadUserFacts(connection).HasStored)
            {
                SaveInTransaction(connection, BotConfig.CreateBehaviorSeed(), null, null);
                fields.Add(ConfigurationFieldId.AllowedSecretEdits);
            }
            if (InitializeVerificationToken(connection, serverUnixSeconds)) fields.Add(ConfigurationFieldId.AllowedSecretEdits);
            var stored = ReadUserFacts(connection);
            var policies = ConfigurationBridge.LoadPlatformPolicies(connection);
            if (!policies.Stored)
            {
                var runtime = _runtime.Current;
                stored.User.DeclaredPlatformDrivers = runtime.DeclaredPlatformDrivers;
                stored.User.DeclaredPlatformActions = runtime.DeclaredPlatformActions;
                var imported = PlatformSwitchSettings.ImportLegacyPolicies(stored.User, runtime.DeclaredPlatformDrivers,
                    PolicyActions(stored.User));
                if (imported.Code != ConfigurationResultCode.Ok
                    || PlatformSwitchSettings.ValidatePolicyEntries(imported.Entries, runtime.DeclaredPlatformDrivers,
                        runtime.DeclaredPlatformActions).Length != 0)
                    throw new JsonException("platform_policy_legacy_needs_review");
                ConfigurationBridge.WritePlatformPolicies(connection, imported.Entries,
                    imported.Entries.ToImmutableDictionary(entry => entry.Target, _ => 0));
                fields.Add(ConfigurationFieldId.PlatformPolicies);
            }
            // Legacy device rows remain in storage untouched; cfg-0.3.3 never reconciles or exposes them.
            _initializationFields = fields.ToImmutable();
            return fields.Count != 0;
        }

        private static bool InitializeVerificationToken(Microsoft.Data.Sqlite.SqliteConnection connection, long serverUnixSeconds)
        {
            var stored = ReadUserFacts(connection);
            var names = stored.Original.Select(property => property.Key).Where(name =>
                string.Equals(name, nameof(AppSettings.FeishuVerificationToken), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (names.Length > 1) throw new InvalidOperationException("verification_token_legacy_needs_review");
            string? legacy = null;
            if (names.Length == 1 && stored.Original[names[0]] is { } tokenNode)
            {
                if (tokenNode is not JsonValue token || !token.TryGetValue<string>(out var text))
                    throw new InvalidOperationException("verification_token_legacy_needs_review");
                legacy = NormalizeVerificationToken(text);
            }
            var marker = StorageDatabase.GetMetaInTransaction(connection, "verification_token_initialized");
            if (marker is not (null or "initialized" or "deleted"))
                throw new InvalidOperationException("verification_token_state_invalid");
            var existing = NormalizeVerificationToken(StorageSecrets.LoadInTransaction(connection,
                WritableSecretSlot.FeishuVerificationToken));
            if (marker == "deleted" && existing is not null)
                throw new InvalidOperationException("verification_token_state_invalid");
            if (marker is null)
            {
                if (existing is not null && legacy is not null && existing != legacy)
                    throw new InvalidOperationException("verification_token_legacy_needs_review");
                var seed = existing ?? legacy;
                if (seed is null && !stored.HasStored)
                {
                    var first = Environment.GetEnvironmentVariable("BOTAGENT_FEISHU_VERIFICATION_TOKEN");
                    seed = NormalizeVerificationToken(!string.IsNullOrWhiteSpace(first) ? first
                        : Environment.GetEnvironmentVariable("QQCHAT_FEISHU_VERIFICATION_TOKEN"));
                }
                if (seed is not null)
                    StorageSecrets.SaveInTransaction(connection, WritableSecretSlot.FeishuVerificationToken, seed, serverUnixSeconds);
                else StorageDatabase.SetMetaInTransaction(connection, "verification_token_initialized", "initialized");
            }
            foreach (var name in names) stored.Original.Remove(name);
            if (names.Length != 0)
                AppDatabase.Exec(connection, WriteSettingsSql,
                    ("$json", stored.Original.ToJsonString(UserJsonOptions)), ("$now", serverUnixSeconds));
            return marker is null || names.Length != 0;
        }

        internal override void AppendInitializationAudit(Microsoft.Data.Sqlite.SqliteConnection connection,
            ConfigurationVersion before, ConfigurationVersion after)
        {
            var fields = string.Join(",", _initializationFields.Select(field => ((int)field).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var detail = "fields=" + fields + (_initializationFields.Contains(ConfigurationFieldId.AllowedSecretEdits) ? ";slot=5" : string.Empty)
                + ";observed=" + before.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ";committed=" + after.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            new AuditLogStore().AppendInTransaction(connection, new BotAgent.Services.Ops.AuditEvent(
                "configuration_initialization", "configuration-writer", "bot", detail, ConfigurationContract.Revision));
        }

        internal override ImmutableArray<SecretMetadata> SecretSources(Microsoft.Data.Sqlite.SqliteConnection connection,
            ImmutableArray<StorageConfiguration.SecretMutation> proposed) => _secretSources(connection, proposed);

        internal override StorageConfiguration.PreparedConfigurationChange Prepare(
            Microsoft.Data.Sqlite.SqliteConnection connection, ConfigurationPatch ownedIntent,
            ImmutableArray<SecretMetadata> proposedSecrets,
            ImmutableArray<StorageConfiguration.SecretMutation> proposedMutations,
            StorageConfiguration.ConfigurationStorageProfile profile)
        {
            Dictionary<string, bool> pluginStates;
            var pluginsChanged = false;
            try
            {
                pluginStates = new(StorageConfiguration.PluginStore.LoadPluginStatesInTransaction(connection), StringComparer.Ordinal);
                var edits = ownedIntent.Registered?.Plugins ?? ImmutableArray<PluginEnabledEdit>.Empty;
                if (!edits.IsEmpty)
                {
                    var declared = _registeredRead(connection, ConfigurationReadKind.Editable).Plugins.Select(item => item.Key).ToHashSet();
                    foreach (var edit in edits)
                    {
                        if (profile == StorageConfiguration.ConfigurationStorageProfile.Legacy || !declared.Contains(edit.Key))
                            return new(false, ImmutableArray.Create(Error(ConfigurationFieldId.RegisteredPlugins, "registration_required",
                                new ConfigurationObjectAddress.Plugin(edit.Key))), ImmutableArray<DeviceKeyAssignment>.Empty, _ => { });
                        if (!pluginStates.TryGetValue(edit.Key.Value, out var old) || old != edit.Enabled) pluginsChanged = true;
                        pluginStates[edit.Key.Value] = edit.Enabled;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
            {
                return new(false, ImmutableArray.Create(Error(ConfigurationFieldId.RegisteredPlugins, "plugin_states_needs_review")),
                    ImmutableArray<DeviceKeyAssignment>.Empty, _ => { }, ConfigurationResultCode.LegacyNeedsReview);
            }
            var providers = ModelProviderStore.LoadAllInTransaction(connection);
            if (providers.Any(provider => string.IsNullOrWhiteSpace(provider.Id) || provider.Id.Trim() != provider.Id)
                || providers.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count() != providers.Count)
                return new(false, ImmutableArray.Create(Error(ConfigurationFieldId.RegisteredProviders, "registration_needs_review")),
                    ImmutableArray<DeviceKeyAssignment>.Empty, _ => { }, ConfigurationResultCode.LegacyNeedsReview);
            var primary = providers.FirstOrDefault(provider => provider.Id == "primary");
            var errors = RejectUnimplementedObjects(ownedIntent, primary is not null);
            var credential = proposedSecrets.Single(secret => secret.Slot == WritableSecretSlot.ModelApiKey);
            var credentialMutation = proposedMutations.FirstOrDefault(mutation => mutation.Slot == WritableSecretSlot.ModelApiKey);
            if (profile == StorageConfiguration.ConfigurationStorageProfile.Legacy && ownedIntent.Registered is { } registered)
            {
                foreach (var provider in registered.Providers)
                {
                    var address = new ConfigurationObjectAddress.Provider(provider.Key);
                    if (!IsPrimaryAliasMarker(provider))
                        errors.Add(Error(ConfigurationFieldId.RegisteredProviders, "adapter_unavailable", address));
                    else if (primary is null)
                        errors.Add(Error(ConfigurationFieldId.RegisteredProviders, "registration_required", address));
                }
            }
            if (profile == StorageConfiguration.ConfigurationStorageProfile.Strict)
            {
                if (primary is null)
                    errors.Add(Error(ConfigurationFieldId.RegisteredProviders, "registration_required",
                        new ConfigurationObjectAddress.Provider(new ProviderKey("primary"))));
                if (primary is not null && ownedIntent.Model is { } modelIntent
                    && modelIntent.ModelBaseUrl.Kind != EditKind.Keep)
                {
                    var endpoint = modelIntent.ModelBaseUrl.Kind == EditKind.Clear ? _baseUrlFallback
                        : modelIntent.ModelBaseUrl.TryGetValue(out var changedUrl) ? changedUrl : primary.BaseUrl;
                    if (!IsAuthorizedModelEndpoint(endpoint, primary.BaseUrl, providers))
                        errors.Add(Error(ConfigurationFieldId.ModelBaseUrl, "endpoint_authorization_required",
                            new ConfigurationObjectAddress.Provider(new ProviderKey("primary"))));
                }
            }
            if (errors.Count != 0)
                return new(false, errors.Take(32).ToImmutableArray(), ImmutableArray<DeviceKeyAssignment>.Empty, _ => { });
            var stored = ReadUserFacts(connection);
            var candidate = ApplyScalarConfigurationFields(stored.User, ownedIntent);
            var policyPlan = PreparePlatformPolicies(connection, stored.User, candidate, ownedIntent, profile);
            if (policyPlan.Code != ConfigurationResultCode.Ok)
                return new(false, policyPlan.Errors, ImmutableArray<DeviceKeyAssignment>.Empty, _ => { }, policyPlan.Code);
            var nextJson = MergeUserFacts(stored.Original, candidate);
            var changed = MergeUserFacts(stored.Original, stored.User) != nextJson;
            if (policyPlan.Changed) changed = true;
            var metadata = PrimaryProjection(primary, candidate, ownedIntent, credentialMutation.Kind, credential);
            ImmutableArray<ModelProviderDefinition> metadataWrites;
            ImmutableDictionary<string, int> fieldModes;
            var modesChanged = false;
            try
            {
                if (profile == StorageConfiguration.ConfigurationStorageProfile.Strict)
                {
                    var plan = PrepareRegisteredProviders(connection, providers, ownedIntent.Registered, metadata,
                        _runtime.Current, credentialMutation, credential);
                    if (plan.Errors.Length != 0)
                        return new(false, plan.Errors, ImmutableArray<DeviceKeyAssignment>.Empty, _ => { });
                    metadataWrites = plan.MetadataWrites;
                    fieldModes = plan.FieldModes;
                    modesChanged = plan.FieldModesChanged;
                }
                else
                {
                    metadataWrites = metadata is null ? ImmutableArray<ModelProviderDefinition>.Empty : ImmutableArray.Create(metadata);
                    fieldModes = ModelProviderStore.LoadFieldModesInTransaction(connection);
                }
            }
            catch (JsonException)
            {
                return new(false, ImmutableArray.Create(Error(ConfigurationFieldId.RegisteredProviders, "provider_modes_needs_review")),
                    ImmutableArray<DeviceKeyAssignment>.Empty, _ => { }, ConfigurationResultCode.LegacyNeedsReview);
            }
            if (metadataWrites.Length != 0 || modesChanged || pluginsChanged) changed = true;
            return new(changed, ImmutableArray<FieldError>.Empty, ImmutableArray<DeviceKeyAssignment>.Empty, conn =>
            {
                if (pluginsChanged) StorageConfiguration.PluginStore.SavePluginStatesInTransaction(conn, pluginStates);
                AppDatabase.Exec(conn, WriteSettingsSql, ("$json", nextJson), ("$now", Clock.Now.ToUnixTimeSeconds()));
                foreach (var provider in metadataWrites) ModelProviderStore.UpsertMetadataInTransaction(conn, provider);
                if (modesChanged) ModelProviderStore.WriteFieldModesInTransaction(conn, fieldModes);
                if (policyPlan.Changed) ConfigurationBridge.WritePlatformPolicies(conn, policyPlan.Entries, policyPlan.ExplicitFields);
            });
        }

        private readonly record struct PrimaryCredentialFacts(
            bool Known, bool Configured, bool KeyUsable, CredentialSource Source);

        private static PrimaryCredentialFacts ProjectProposedHotSavePrimaryFacts(string finalRef,
            StorageConfiguration.SecretMutation modelMutation, SecretMetadata proposedMain, AppSettings current)
            => ProjectProviderBindingFacts(finalRef, ObserveProposedHotSaveProviderFacts(modelMutation, proposedMain, current));

        private static (PrimaryCredentialFacts Database, PrimaryCredentialFacts LegacyEnvironment, PrimaryCredentialFacts LivePrimary)
            ObserveProposedHotSaveProviderFacts(StorageConfiguration.SecretMutation modelMutation,
                SecretMetadata proposedMain, AppSettings current)
        {
            if (modelMutation.Slot != WritableSecretSlot.ModelApiKey || !modelMutation.FactsKnown
                || modelMutation.DatabaseKeyUsable && !modelMutation.DatabaseConfigured
                || !Enum.IsDefined(modelMutation.Kind) || !Enum.IsDefined(proposedMain.Source))
                return default;
            var database = new PrimaryCredentialFacts(true, modelMutation.DatabaseConfigured,
                modelMutation.DatabaseKeyUsable,
                modelMutation.DatabaseConfigured ? CredentialSource.Database : CredentialSource.None);
            // The reference resolver observes raw fixed environment values before its final route trim.
            var qqchat = Environment.GetEnvironmentVariable("QQCHAT_API_KEY");
            var openai = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var main = modelMutation.Kind switch
            {
                SecretEditKind.Replace => database,
                SecretEditKind.Delete => ProjectPrimaryKeyFacts((qqchat ?? openai ?? string.Empty).Trim(), proposedMain.Source),
                _ => ProjectPrimaryKeyFacts(current.ApiKey, proposedMain.Source),
            };
            var legacy = !string.IsNullOrWhiteSpace(qqchat) ? ProjectPrimaryKeyFacts(qqchat, CredentialSource.Environment)
                : !string.IsNullOrWhiteSpace(openai) ? ProjectPrimaryKeyFacts(openai, CredentialSource.Environment) : main;
            return (database, legacy, main);
        }

        private static PrimaryCredentialFacts ProjectProviderBindingFacts(string reference,
            (PrimaryCredentialFacts Database, PrimaryCredentialFacts LegacyEnvironment, PrimaryCredentialFacts LivePrimary) facts)
            => reference switch
            {
                "secret:apiKey" => facts.Database,
                "env:QQCHAT_API_KEY" => facts.LegacyEnvironment,
                ModelProviderStore.PrimaryModelCredentialReference => facts.LivePrimary,
                ModelProviderStore.UnconfiguredModelCredentialReference => new(true, false, false, CredentialSource.None),
                _ => default,
            };

        private (ImmutableArray<ModelProviderDefinition> MetadataWrites, ImmutableArray<FieldError> Errors,
            ImmutableDictionary<string, int> FieldModes, bool FieldModesChanged) PrepareRegisteredProviders(
            Microsoft.Data.Sqlite.SqliteConnection connection, IReadOnlyList<PersistedModelProvider> prior,
            RegisteredConfigurationChanges? edits, ModelProviderDefinition? primaryProjection, AppSettings current,
            StorageConfiguration.SecretMutation modelMutation, SecretMetadata proposedMain)
        {
            var oldModes = ModelProviderStore.LoadFieldModesInTransaction(connection);
            var modes = oldModes.ToBuilder();
            var final = prior.ToDictionary(provider => provider.Id, Definition, StringComparer.Ordinal);
            if (primaryProjection is not null) final[primaryProjection.Id] = primaryProjection;
            var errors = ImmutableArray.CreateBuilder<FieldError>();
            foreach (var edit in edits?.Providers ?? ImmutableArray<ProviderEdit>.Empty)
            {
                var address = new ConfigurationObjectAddress.Provider(edit.Key);
                if (!final.TryGetValue(edit.Key.Value, out var original))
                {
                    errors.Add(Error(ConfigurationFieldId.RegisteredProviders, "registration_required", address));
                    continue;
                }
                var change = edit.Changes;
                if (edit.Key.Value == "primary"
                    && (change.Priority.Kind != EditKind.Keep || change.Enabled.Kind != EditKind.Keep))
                    errors.Add(Error(ConfigurationFieldId.RegisteredProviders, "field_read_only", address));
                var nextRef = change.Credential.Kind switch
                {
                    CredentialBindingEditKind.UsePrimaryModelCredential => ModelProviderStore.PrimaryModelCredentialReference,
                    CredentialBindingEditKind.Unconfigured => ModelProviderStore.UnconfiguredModelCredentialReference,
                    _ => original.SecretKeyRef,
                };
                if (change.Credential.Kind == CredentialBindingEditKind.UseRegisteredModelCredential)
                    errors.Add(Error(ConfigurationFieldId.RegisteredProviders, "credential_registry_unavailable", address));
                var next = original with
                {
                    Name = Text(change.NameRaw, original.Name),
                    BaseUrl = Text(change.BaseUrl, original.BaseUrl),
                    ModelName = Text(change.ModelName, original.ModelName),
                    Priority = change.Priority.TryGetValue(out var priority) ? priority : original.Priority,
                    IsEnabled = change.Enabled.TryGetValue(out var enabled) ? enabled : original.IsEnabled,
                    SecretKeyRef = nextRef,
                };
                var bits = modes.TryGetValue(original.Id, out var existingBits) ? existingBits : 0;
                if (change.NameRaw.Kind != EditKind.Keep) bits |= 1;
                if (original.Id != "primary" && change.BaseUrl.Kind != EditKind.Keep) bits |= 2;
                if (original.Id != "primary" && change.ModelName.Kind != EditKind.Keep) bits |= 4;
                if (bits != existingBits) modes[original.Id] = bits;
                if (change.BaseUrl.Kind != EditKind.Keep && !IsAuthorizedModelEndpoint(next.BaseUrl, original.BaseUrl, prior))
                    errors.Add(Error(ConfigurationFieldId.RegisteredProviders, "endpoint_authorization_required", address));
                final[original.Id] = next;
            }
            var facts = ObserveProposedHotSaveProviderFacts(modelMutation, proposedMain, current);
            foreach (var provider in final.Values)
            {
                var address = new ConfigurationObjectAddress.Provider(new ProviderKey(provider.Id));
                if (string.IsNullOrWhiteSpace(provider.BaseUrl) || string.IsNullOrWhiteSpace(provider.ModelName))
                    errors.Add(Error(ConfigurationFieldId.RegisteredProviders, "provider_metadata_required", address));
                else if (!TryModelEndpoint(provider.BaseUrl, out _))
                    errors.Add(Error(ConfigurationFieldId.RegisteredProviders, "provider_endpoint_invalid", address));
                if (!provider.IsEnabled || provider.Id == "primary" && !_primaryRequiresCredential) continue;
                var credential = ProjectProviderBindingFacts(provider.SecretKeyRef, facts);
                if (!credential.Known || !credential.Configured || !credential.KeyUsable)
                    errors.Add(Error(provider.Id == "primary" ? ConfigurationFieldId.AllowedSecretEdits : ConfigurationFieldId.RegisteredProviders,
                        credential.Known ? "credential_required" : "credential_facts_unknown",
                        provider.Id == "primary" ? new ConfigurationObjectAddress.Secret(WritableSecretSlot.ModelApiKey) : address));
            }
            var writes = prior.Where(provider => final[provider.Id] != Definition(provider))
                .Select(provider => final[provider.Id]).ToImmutableArray();
            var nextModes = modes.ToImmutable();
            var modeChanged = nextModes.Count != oldModes.Count || nextModes.Any(entry => !oldModes.TryGetValue(entry.Key, out var bits) || bits != entry.Value);
            return (writes, errors.Take(32).ToImmutableArray(), nextModes, modeChanged);

            static string Text(FieldEdit<string> edit, string original)
                => edit.Kind == EditKind.Clear ? string.Empty : edit.TryGetValue(out var value) ? value.Trim() : original;
            static ModelProviderDefinition Definition(PersistedModelProvider provider)
                => new(provider.Id, provider.Priority, provider.Name, provider.BaseUrl, provider.ModelName, provider.SecretKeyRef, provider.IsEnabled);
        }

        private static PrimaryCredentialFacts ProjectPrimaryKeyFacts(string? value, CredentialSource source)
        {
            var configured = !string.IsNullOrWhiteSpace(value);
            var usable = configured && !value!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            return new(true, configured, usable, configured ? source : CredentialSource.None);
        }

        private static bool IsAuthorizedModelEndpoint(string endpoint, string currentEndpoint,
            IReadOnlyList<PersistedModelProvider> registeredProviders)
        {
            if (!TryModelEndpoint(endpoint, out var requested)) return false;
            if (TryModelEndpoint(currentEndpoint, out var current)
                && string.Equals(requested.AbsoluteUri, current.AbsoluteUri, StringComparison.Ordinal))
                return true; // No new destination: preserve the existing endpoint's compatibility.
            if (!BotAgent.Services.Net.SafeUrl.TryValidate(endpoint, false, out _, out _)) return false;
            return registeredProviders.Any(provider => TryModelEndpoint(provider.BaseUrl, out var registered)
                && string.Equals(requested.AbsoluteUri, registered.AbsoluteUri, StringComparison.Ordinal));
        }

        private static bool TryModelEndpoint(string endpoint, out Uri uri)
        {
            uri = null!;
            return endpoint.Length <= 2048 && Uri.TryCreate(endpoint, UriKind.Absolute, out uri!)
                && (uri.Scheme is "http" or "https") && uri.UserInfo.Length == 0;
        }

        private ModelProviderDefinition? PrimaryProjection(PersistedModelProvider? primary, AppSettings candidate,
            ConfigurationPatch intent, SecretEditKind credentialEdit, SecretMetadata credential)
        {
            if (primary is null) return null; // Legacy keeps its historical unregistered startup state.
            var model = intent.Model?.Model.Kind switch
            {
                EditKind.Clear => _modelFallback,
                EditKind.Set => candidate.Model,
                _ => primary.ModelName,
            };
            var baseUrl = intent.Model?.ModelBaseUrl.Kind switch
            {
                EditKind.Clear => _baseUrlFallback,
                EditKind.Set => candidate.ModelBaseUrl,
                _ => primary.BaseUrl,
            };
            var secretRef = primary.SecretKeyRef;
            if (secretRef is "secret:apiKey" or "env:QQCHAT_API_KEY")
            {
                secretRef = credentialEdit switch
                {
                    SecretEditKind.Replace => "secret:apiKey",
                    SecretEditKind.Delete => credential.Source == CredentialSource.Environment
                        ? "env:QQCHAT_API_KEY" : "secret:apiKey",
                    _ => secretRef,
                };
            }
            if (model == primary.ModelName && baseUrl == primary.BaseUrl && secretRef == primary.SecretKeyRef) return null;
            return new(primary.Id, primary.Priority, primary.Name, baseUrl, model, secretRef, primary.IsEnabled);
        }

        private static ImmutableArray<FieldError>.Builder RejectUnimplementedObjects(ConfigurationPatch intent,
            bool primaryRegistered)
        {
            var errors = ImmutableArray.CreateBuilder<FieldError>();
            return errors;
        }

        private static bool IsPrimaryAliasMarker(ProviderEdit provider)
            => provider.Key.Value == "primary" && provider.Changes.NameRaw.Kind == EditKind.Keep
                && provider.Changes.Priority.Kind == EditKind.Keep && provider.Changes.BaseUrl.Kind == EditKind.Keep
                && provider.Changes.ModelName.Kind == EditKind.Keep && provider.Changes.Enabled.Kind == EditKind.Keep
                && provider.Changes.Credential.Kind == CredentialBindingEditKind.KeepExisting;

        private static FieldError Error(ConfigurationFieldId id, string code, ConfigurationObjectAddress? target = null)
            => new(new(id, target), code);

        internal override ConfigurationReadResult Read(Microsoft.Data.Sqlite.SqliteConnection connection,
            ConfigurationCaller caller, ConfigurationReadKind kind, ConfigurationVersion version,
            ConfigurationGeneration generation, long serverUnixSeconds, ApplyState latestApplyState)
        {
            var stored = ReadUserFacts(connection);
            var secrets = SecretSources(connection, ImmutableArray<StorageConfiguration.SecretMutation>.Empty);
            var effective = _runtime.Current.Snapshot();
            if (ContainsEndpointUserInfo(stored.User.ModelBaseUrl) || ContainsEndpointUserInfo(stored.User.ModelBaseUrlOverride)
                || ContainsEndpointUserInfo(effective.ModelBaseUrl) || ContainsEndpointUserInfo(effective.ModelBaseUrlOverride))
                return new(ConfigurationResultCode.LegacyNeedsReview, version, generation, ConfigurationContract.Revision,
                    serverUnixSeconds, null, secrets, ImmutableArray<FieldDescriptor>.Empty, latestApplyState);
            RegisteredConfigurationView registered;
            try
            {
                ProjectPlatformPoliciesInTransaction(connection, stored.User);
                if (!stored.User.RuntimePlatformPolicies.HasValue && (stored.User.PlatformPolicies?.Count ?? 0) != 0)
                    throw new JsonException("platform_policy_legacy_needs_review");
                stored.User.DeclaredPlatformDrivers = effective.DeclaredPlatformDrivers;
                stored.User.DeclaredPlatformActions = effective.DeclaredPlatformActions;
                if (PlatformSwitchSettings.ValidatePolicyEntries(stored.User.RuntimePlatformPolicies ?? ImmutableArray<PlatformPolicyEntry>.Empty,
                    effective.DeclaredPlatformDrivers, effective.DeclaredPlatformActions).Length != 0)
                    throw new JsonException("platform_policy_legacy_needs_review");
                _ = ModelProviderStore.LoadFieldModesInTransaction(connection);
                registered = _registeredRead(connection, kind);
            }
            catch (JsonException)
            {
                return new(ConfigurationResultCode.LegacyNeedsReview, version, generation, ConfigurationContract.Revision,
                    serverUnixSeconds, null, secrets, ImmutableArray<FieldDescriptor>.Empty, latestApplyState);
            }
            var view = ProjectConfigurationView(connection, stored.User, effective, kind, latestApplyState,
                stored.Properties, _effectiveSource, _effect,
                PolicyViews(stored.User, effective, kind, true), PolicyViews(stored.User, effective, kind, false), registered);
            view = view with
            {
                Registered = registered with
                {
                    Providers = registered.Providers.Select(provider => provider.Key.Value == "primary"
                        ? provider with { ModelName = view.Model.Model, BaseUrl = view.Model.ModelBaseUrl } : provider).ToImmutableArray(),
                },
            };
            return new(ConfigurationResultCode.Ok, version, generation, ConfigurationContract.Revision, serverUnixSeconds,
                view, secrets, CollectFieldDescriptors(view), latestApplyState);
        }

        internal override void AppendAudit(Microsoft.Data.Sqlite.SqliteConnection connection,
            ConfigurationReceipt receipt, ImmutableArray<ConfigurationFieldId> fields)
            => AppendConfigurationAuditInTransaction(connection, receipt, fields);

        private static (AppSettings User, JsonObject Original, HashSet<string> Properties, bool HasStored) ReadUserFacts(
            Microsoft.Data.Sqlite.SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ReadSettingsSql;
            var json = command.ExecuteScalar() as string;
            var original = string.IsNullOrWhiteSpace(json) ? new JsonObject()
                : JsonNode.Parse(json) as JsonObject ?? throw new JsonException("invalid_user_configuration");
            var user = string.IsNullOrWhiteSpace(json) ? new AppSettings()
                : JsonSerializer.Deserialize<AppSettings>(json) ?? throw new JsonException("invalid_user_configuration");
            return (user, original, original.Select(property => property.Key).ToHashSet(StringComparer.Ordinal), json is not null);
        }

        private static ImmutableArray<FieldDescriptor> CollectFieldDescriptors(ConfigurationView view)
            => ImmutableArray.Create(
                view.Agent.AgentAllowedUsers.Rule,
                view.Agent.AgentPrefix.Rule,
                view.Agent.AgentProgressSeconds.Rule,
                view.Agent.AgentPrompt.Rule,
                view.Agent.AgentReasoningEffort.Rule,
                view.Agent.AgentReasoningLevels.Rule,
                view.Agent.AgentReplyMaxChars.Rule,
                view.Agent.AgentServerBaseUrl.Rule,
                view.Agent.AgentServerCommandTimeoutSeconds.Rule,
                view.Agent.AgentServerDocker.Rule,
                view.Agent.AgentServerKeepContext.Rule,
                view.Agent.AgentServerMaxSteps.Rule,
                view.Agent.AgentServerModel.Rule,
                view.Agent.AgentServerQqActions.Rule,
                view.Agent.AgentServerTools.Rule,
                view.Agent.AgentServerUseGate.Rule,
                view.Agent.AgentServerWorkDir.Rule,
                view.Agent.EnableAgentBridge.Rule,
                view.Agent.EnableAgentMask.Rule,
                view.Agent.EnableServerAgent.Rule,
                view.Agent.PanelDeployEnabled.Rule,
                view.Agent.PanelDeployUrl.Rule,
                view.Behavior.AiDesire.Rule,
                view.Behavior.AiModeEnabled.Rule,
                view.Behavior.ApprovalApprovers.Rule,
                view.Behavior.BotPersona.Rule,
                view.Behavior.EnableApprovals.Rule,
                view.Behavior.EnableAtmosphereDamping.Rule,
                view.Behavior.EnableLinkPreview.Rule,
                view.Behavior.EnableParticipationGating.Rule,
                view.Behavior.EnablePoke.Rule,
                view.Behavior.EnableProactive.Rule,
                view.Behavior.EnableProfileSummary.Rule,
                view.Behavior.EnableQuestions.Rule,
                view.Behavior.EnableStickers.Rule,
                view.Behavior.EnableWebSearch.Rule,
                view.Behavior.FilterActionNarration.Rule,
                view.Behavior.GroupCooldownSeconds.Rule,
                view.Behavior.IdleFallbackSeconds.Rule,
                view.Behavior.IgnoreBracketMessages.Rule,
                view.Behavior.LinkPreviewMax.Rule,
                view.Behavior.LinkPreviewTimeoutSeconds.Rule,
                view.Behavior.MaxAgentSteps.Rule,
                view.Behavior.MaxConcurrentReplies.Rule,
                view.Behavior.MaxContextMessages.Rule,
                view.Behavior.MaxMessagesPerConversation.Rule,
                view.Behavior.MaxProfileChars.Rule,
                view.Behavior.MoodTtlSeconds.Rule,
                view.Behavior.ParticipationCooldownSeconds.Rule,
                view.Behavior.ParticipationMaxActiveLifetimeSeconds.Rule,
                view.Behavior.ParticipationMaxConsecutiveReplies.Rule,
                view.Behavior.ParticipationMaxExitingLifetimeSeconds.Rule,
                view.Behavior.ParticipationProbingMaxReplies.Rule,
                view.Behavior.PokeCooldownSeconds.Rule,
                view.Behavior.PrivateCooldownSeconds.Rule,
                view.Behavior.ProactiveCooldownSeconds.Rule,
                view.Behavior.ProactiveQuietSeconds.Rule,
                view.Behavior.ProfileLookupCount.Rule,
                view.Behavior.ProfileSummaryIntervalSeconds.Rule,
                view.Behavior.ProfileSummaryLines.Rule,
                view.Behavior.ProfileSummaryMaxChars.Rule,
                view.Behavior.ProfileSummaryThreshold.Rule,
                view.Behavior.ScenarioPreset.Rule,
                view.Behavior.SegmentDelayMs.Rule,
                view.Behavior.SplitReplies.Rule,
                view.Behavior.StickerCandidates.Rule,
                view.Behavior.StickerCooldownSeconds.Rule,
                view.Behavior.StickerCurateIntervalSeconds.Rule,
                view.Behavior.StickerLibraryMax.Rule,
                view.Behavior.SuitabilityThreshold.Rule,
                view.Behavior.WebSearchCooldownSeconds.Rule,
                view.Behavior.WebSearchMaxResults.Rule,
                view.Behavior.WebSearchSources.Rule,
                view.Behavior.WebSearchTimeoutSeconds.Rule,
                view.Behavior.WebSearchUseModelSearch.Rule,
                view.Model.AdaptiveSamplingEnabled.Rule,
                view.Model.DefaultTemperature.Rule,
                view.Model.DefaultTopP.Rule,
                view.Model.EmotionalTemperature.Rule,
                view.Model.EmotionalTopP.Rule,
                view.Model.FastModel.Rule,
                view.Model.FastReply.Rule,
                view.Model.FastThinkingBudget.Rule,
                view.Model.FastThinkingCustomBudget.Rule,
                view.Model.MaxTokens.Rule,
                view.Model.Model.Rule,
                view.Model.ModelBaseUrl.Rule,
                view.Model.RationalTemperature.Rule,
                view.Model.RationalTopP.Rule,
                view.Model.ThinkingBudget.Rule,
                view.Model.ThinkingCustomBudget.Rule,
                view.Music.EnableMusic.Rule,
                view.Music.MusicBitrate.Rule,
                view.Music.MusicKeepAudio.Rule,
                view.Music.MusicLibraryMax.Rule,
                view.Music.MusicListenCooldownSeconds.Rule,
                view.Music.MusicMaxAnalysisSeconds.Rule,
                view.Music.MusicMaxDownloadMb.Rule,
                view.Music.MusicNoteTtlDays.Rule,
                view.Music.MusicSendAudioToModel.Rule,
                view.Music.MusicSources.Rule,
                view.Music.MusicUnderstandModel.Rule,
                view.Music.NeteaseBaseUrl.Rule,
                view.Platform.FeishuApiBase.Rule,
                view.Platform.FeishuAppId.Rule,
                view.Platform.FeishuEnabled.Rule,
                view.Platform.FeishuWhitelist.Rule,
                view.Platform.LocalChannelIds.Rule,
                view.Platform.MessageWhitelist.Rule,
                view.Platform.OfficialAppId.Rule,
                view.Platform.OfficialChatEnabled.Rule,
                view.Platform.OfficialEnabled.Rule,
                view.Platform.OfficialSandbox.Rule,
                view.Platform.OfficialWhitelistGroups.Rule,
                view.Platform.OfficialWhitelistPrivates.Rule,
                view.Platform.PlatformPolicies.Rule,
                view.Platform.PrivateChatEnabled.Rule,
                view.Platform.WhitelistGroups.Rule,
                view.Platform.WhitelistPrivates.Rule,
                view.Reporting.HealthReportEnabled.Rule,
                view.Reporting.HealthReportTargets.Rule,
                view.Reporting.HealthReportTime.Rule,
                view.Voice.EnableVoice.Rule,
                view.Voice.TtsApiBase.Rule,
                view.Voice.TtsModel.Rule,
                view.Voice.TtsProvider.Rule,
                view.Voice.TtsServiceUrl.Rule,
                view.Voice.VoiceEagerness.Rule,
                view.Voice.VoiceEmotion.Rule,
                view.Voice.VoiceMaxChars.Rule,
                view.Voice.VoiceName.Rule,
                view.Voice.VoicePitch.Rule,
                view.Voice.VoiceSpeed.Rule,
                view.Voice.VoiceVol.Rule);

        private static string MergeUserFacts(JsonObject original, AppSettings candidate)
        {
            var merged = (JsonObject)original.DeepClone();
            var known = JsonSerializer.SerializeToNode(candidate, UserJsonOptions)!.AsObject();
            known.Remove(nameof(AppSettings.FeishuVerificationToken));
            foreach (var name in merged.Select(property => property.Key).Where(name =>
                string.Equals(name, nameof(AppSettings.FeishuVerificationToken), StringComparison.OrdinalIgnoreCase)).ToArray())
                merged.Remove(name);
            foreach (var property in known) merged[property.Key] = property.Value?.DeepClone();
            return merged.ToJsonString(UserJsonOptions);
        }
    }

    /// <summary>Participates in the caller's transaction; never creates or nests a writer.</summary>
    internal static void SaveInTransaction(Microsoft.Data.Sqlite.SqliteConnection conn, AppSettings settings,
        BotAgent.Services.Ops.AuditEvent? auditEvent = null, BotAgent.Services.Ops.IAuditChain? auditChain = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(settings);
        if (auditEvent is not null && auditChain is not AuditLogStore)
            throw new InvalidOperationException("Settings audit requires the same SQLite transaction.");
        var json = JsonSerializer.Serialize(settings, UserJsonOptions);
        AppDatabase.Exec(conn, WriteSettingsSql, ("$json", json), ("$now", Clock.Now.ToUnixTimeSeconds()));

        if (auditEvent is not null)
        {
            ((AuditLogStore)auditChain!).AppendInTransaction(conn, auditEvent);
        }
    }
}

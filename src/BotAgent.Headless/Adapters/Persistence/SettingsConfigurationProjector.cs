extern alias StorageModule;

using System.Collections.Immutable;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Model;
using BotAgent.Services;
using BotAgent.Services.Platforms;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 配置只读视图投影器（从 <see cref="SettingsStore" /> 拆出的独立协作类）：
/// 负责把持久化用户事实与运行期生效值投影为结构化只读视图、策略视图、供应商视图与字段描述表，
/// 不持有数据库连接或写入事务所有权。
/// </summary>
internal static class SettingsConfigurationProjector
{
    internal sealed class ReadFields
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private readonly ConfigurationReadKind _kind;
        private readonly ApplyState _latestApply;
        private readonly IReadOnlySet<string> _legacyProperties;
        private readonly Func<ConfigurationFieldId, ConfigurationValueSource> _actualSource;
        private readonly Func<ConfigurationFieldId, FieldEffect> _effect;
        private readonly ConfigurationRuntimeEffectCollector? _runtimeEffects;

        internal ReadFields(Microsoft.Data.Sqlite.SqliteConnection connection,
            ConfigurationReadKind kind, ApplyState latestApply, IReadOnlySet<string> legacyProperties,
            Func<ConfigurationFieldId, ConfigurationValueSource> actualSource, Func<ConfigurationFieldId, FieldEffect> effect,
            ConfigurationRuntimeEffectCollector? runtimeEffects)
        {
            _connection = connection; _kind = kind; _latestApply = latestApply;
            _legacyProperties = legacyProperties; _actualSource = actualSource; _effect = effect; _runtimeEffects = runtimeEffects;
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
            _runtimeEffects?.Observe(id, rule.Effect,
                state.Tracked || state.UserMode == UserValueKind.Explicit ? requested : actual, actual);
            return ConfigurationBridge.ProjectField(visibility, display, rule, state.UserMode,
                source, _latestApply, editable);
        }
    }

    internal static ConfigurationView ProjectConfigurationView(Microsoft.Data.Sqlite.SqliteConnection connection,
        AppSettings user, AppSettings effective, ConfigurationReadKind kind, ApplyState latestApply,
        IReadOnlySet<string> legacyProperties, Func<ConfigurationFieldId, ConfigurationValueSource> actualSource,
        Func<ConfigurationFieldId, FieldEffect> effect,
        ImmutableArray<PlatformPolicyEntryView> requestedPolicies, ImmutableArray<PlatformPolicyEntryView> effectivePolicies,
        RegisteredConfigurationView registered, ConfigurationRuntimeEffectCollector? runtimeEffects = null)
    {
        var fields = new ReadFields(connection, kind, latestApply, legacyProperties, actualSource, effect, runtimeEffects);
        return new ConfigurationView(ProjectBehaviorView(user, effective, fields), ProjectModelView(user, effective, fields),
            ProjectAgentView(user, effective, fields),
            ProjectPlatformView(user, effective, fields, requestedPolicies, effectivePolicies),
            ProjectVoiceView(user, effective, fields), ProjectMusicView(user, effective, fields),
            ProjectReportingView(user, effective, fields), registered);
    }

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
            // 供应商级字段现在全部热应用（#128）：保存后模型投影步会重建路由与候选表，
            // 运行期立即采用，所以这里不再把"与启动快照不一致"报成需重启。
            // running 只用于展示"此刻正在生效"的显示值（名字/地址），不参与需重启判定。
            var apply = ApplyState.NotStarted;
            var binding = ModelProviderStore.ClassifyCredentialBinding(provider.SecretKeyRef);
            ReadField<string> Field(string requested, string display, int bit, bool name)
            {
                var explicitField = modes.TryGetValue(provider.Id, out var bits) && (bits & bit) != 0;
                var rule = new FieldDescriptor(ConfigurationFieldId.RegisteredProviders,
                    FieldEffect.Hot, name, kind == ConfigurationReadKind.Editable,
                    binding == CredentialBindingState.Unclassified);
                var visibility = kind == ConfigurationReadKind.Overview ? FieldVisibility.Hidden
                    : name ? FieldVisibility.NameEditable : FieldVisibility.Editable;
                return ConfigurationBridge.ProjectField(visibility, BotAgent.Services.Agent.AgentMask.Shorten(display), rule,
                    UserValueKind.Explicit, explicitField ? ConfigurationValueSource.UserConfiguration
                        : ConfigurationValueSource.LegacyCompatibility, apply, requested);
            }
            // 凭据“是否已配置”只投影存在性：主凭据看固定槽位，已登记凭据看 model:<key> 那一行，永不回显正文。
            var credentialKey = ModelProviderStore.TryGetRegisteredCredentialKey(provider.SecretKeyRef, out var registered)
                ? registered : (ModelCredentialKey?)null;
            var credentialConfigured = credentialKey is { } key
                ? ModelCredentialStore.LoadInTransaction(connection, key) is not null
                : binding == CredentialBindingState.PrimaryModel
                    && !string.IsNullOrWhiteSpace(StorageSecrets.LoadInTransaction(connection, WritableSecretSlot.ModelApiKey));
            return new ProviderConfigurationView(new ProviderKey(provider.Id),
                Field(provider.Name, running?.Name ?? provider.Name, 1, true),
                Field(provider.BaseUrl, running?.BaseUrl ?? provider.BaseUrl, 2, false),
                Field(provider.ModelName, running?.ModelName ?? provider.ModelName, 4, false),
                provider.Priority, provider.IsEnabled, binding, apply, provider.Protocol, provider.Options,
                credentialKey, credentialConfigured);
        }).ToImmutableArray();
    }

    internal static ImmutableArray<ModelCredentialView> ProjectModelCredentials(
        Microsoft.Data.Sqlite.SqliteConnection connection, IReadOnlyList<PersistedModelProvider> desired)
    {
        var builder = ImmutableArray.CreateBuilder<ModelCredentialView>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in desired)
        {
            if (!ModelProviderStore.TryGetRegisteredCredentialKey(provider.SecretKeyRef, out var key)
                || !seen.Add(key.Value)) continue;
            var value = ModelCredentialStore.LoadInTransaction(connection, key);
            builder.Add(new ModelCredentialView(key, !string.IsNullOrWhiteSpace(value), IsUsable(value)));
        }
        return builder.ToImmutable();
    }

    internal static bool ContainsEndpointUserInfo(string? endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.UserInfo.Length != 0;

    internal static ImmutableArray<ActionId> PolicyActions(AppSettings settings)
        => BotAgent.Services.Agent.QqActionCatalog.ParseAllowed(settings.AgentServerQqActions)
            .OrderBy(value => value, StringComparer.Ordinal).Select(value => new ActionId(value)).ToImmutableArray();

    internal static ImmutableArray<PlatformPolicyEntryView> PolicyViews(AppSettings desired, AppSettings runtime,
        ConfigurationReadKind kind, bool requested, ConfigurationRuntimeEffectCollector? runtimeEffects = null)
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
            runtimeEffects?.ObservePolicy(target, resolved, effective);
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
                // #129 S5：名单三态必须读得出来。「空」有两种相反的语义 —— 官方通道的标量留空是全收
                // （AllowAll 规则），条目里的显式空串是全拦；以前两种都显示成 "[policy]" / 空串，面板上看不出区别。
                var display = effectiveValue is not ListRule list ? "[policy]"
                    : list.Kind == ListRuleKind.AllowAll ? "[policy] 全收（不限）"
                    : list.Kind != ListRuleKind.Explicit ? "[policy] 跟随基线"
                    : list.TryGetExplicitValue(out var text) && !string.IsNullOrWhiteSpace(text)
                        ? BotAgent.Services.Agent.AgentMask.Shorten(text)
                        : "[policy] 显式空（全拦名单外所有）";
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

    internal static ImmutableArray<FieldDescriptor> CollectFieldDescriptors(ConfigurationView view)
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
            view.Agent.EnableNaturalAgent.Rule,
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
            view.Behavior.GroupInterjectionProbability.Rule,
            view.Behavior.GroupInterjectionCooldownSeconds.Rule,
            view.Behavior.GroupContinuationWaitSeconds.Rule,
            view.Behavior.PrivateContinuationWaitSeconds.Rule,
            view.Behavior.AiModeEnabled.Rule,
            view.Behavior.ApprovalApprovers.Rule,
            view.Behavior.BotPersona.Rule,
            view.Behavior.EnableApprovals.Rule,
            view.Behavior.EnableLinkPreview.Rule,
            view.Behavior.EnablePoke.Rule,
            view.Behavior.EnableProactive.Rule,
            view.Behavior.EnableProfileSummary.Rule,
            view.Behavior.EnableQuestions.Rule,
            view.Behavior.EnableStickers.Rule,
            view.Behavior.EnableWebSearch.Rule,
            view.Behavior.FilterActionNarration.Rule,
            view.Behavior.IgnoreBracketMessages.Rule,
            view.Behavior.LinkPreviewMax.Rule,
            view.Behavior.LinkPreviewTimeoutSeconds.Rule,
            view.Behavior.MaxAgentSteps.Rule,
            view.Behavior.MaxConcurrentReplies.Rule,
            view.Behavior.MaxContextMessages.Rule,
            view.Behavior.MaxMessagesPerConversation.Rule,
            view.Behavior.MaxProfileChars.Rule,
            view.Behavior.MoodTtlSeconds.Rule,
            view.Behavior.PokeCooldownSeconds.Rule,
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
            view.Platform.FeishuWhitelistPrivates.Rule,
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

    private static bool IsUsable(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static BehaviorView ProjectBehaviorView(AppSettings user, AppSettings effective, ReadFields fields)
        => new()
        {
            GroupInterjectionProbability = fields.Field(ConfigurationFieldId.GroupInterjectionProbability, user.GroupInterjectionProbability, effective.GroupInterjectionProbability, nameof(AppSettings.GroupInterjectionProbability)),
            GroupInterjectionCooldownSeconds = fields.Field(ConfigurationFieldId.GroupInterjectionCooldownSeconds, user.GroupInterjectionCooldownSeconds, effective.GroupInterjectionCooldownSeconds, nameof(AppSettings.GroupInterjectionCooldownSeconds)),
            GroupContinuationWaitSeconds = fields.Field(ConfigurationFieldId.GroupContinuationWaitSeconds, user.GroupContinuationWaitSeconds, effective.GroupContinuationWaitSeconds, nameof(AppSettings.GroupContinuationWaitSeconds)),
            PrivateContinuationWaitSeconds = fields.Field(ConfigurationFieldId.PrivateContinuationWaitSeconds, user.PrivateContinuationWaitSeconds, effective.PrivateContinuationWaitSeconds, nameof(AppSettings.PrivateContinuationWaitSeconds)),
            AiModeEnabled = fields.Field(ConfigurationFieldId.AiModeEnabled, user.AiModeEnabled, effective.AiModeEnabled, nameof(AppSettings.AiModeEnabled)),
            ApprovalApprovers = fields.Field(ConfigurationFieldId.ApprovalApprovers, user.ApprovalApprovers, effective.ApprovalApprovers, nameof(AppSettings.ApprovalApprovers)),
            BotPersona = fields.Field(ConfigurationFieldId.BotPersona, user.BotPersona, effective.BotPersona, nameof(AppSettings.BotPersona)),
            EnableApprovals = fields.Field(ConfigurationFieldId.EnableApprovals, user.EnableApprovals, effective.EnableApprovals, nameof(AppSettings.EnableApprovals)),
            EnableLinkPreview = fields.Field(ConfigurationFieldId.EnableLinkPreview, user.EnableLinkPreview, effective.EnableLinkPreview, nameof(AppSettings.EnableLinkPreview)),
            EnablePoke = fields.Field(ConfigurationFieldId.EnablePoke, user.EnablePoke, effective.EnablePoke, nameof(AppSettings.EnablePoke)),
            EnableProactive = fields.Field(ConfigurationFieldId.EnableProactive, user.EnableProactive, effective.EnableProactive, nameof(AppSettings.EnableProactive)),
            EnableProfileSummary = fields.Field(ConfigurationFieldId.EnableProfileSummary, user.EnableProfileSummary, effective.EnableProfileSummary, nameof(AppSettings.EnableProfileSummary)),
            EnableQuestions = fields.Field(ConfigurationFieldId.EnableQuestions, user.EnableQuestions, effective.EnableQuestions, nameof(AppSettings.EnableQuestions)),
            EnableStickers = fields.Field(ConfigurationFieldId.EnableStickers, user.EnableStickers, effective.EnableStickers, nameof(AppSettings.EnableStickers)),
            EnableWebSearch = fields.Field(ConfigurationFieldId.EnableWebSearch, user.EnableWebSearch, effective.EnableWebSearch, nameof(AppSettings.EnableWebSearch)),
            FilterActionNarration = fields.Field(ConfigurationFieldId.FilterActionNarration, user.FilterActionNarration, effective.FilterActionNarration, nameof(AppSettings.FilterActionNarration)),
            IgnoreBracketMessages = fields.Field(ConfigurationFieldId.IgnoreBracketMessages, user.IgnoreBracketMessages, effective.IgnoreBracketMessages, nameof(AppSettings.IgnoreBracketMessages)),
            LinkPreviewMax = fields.Field(ConfigurationFieldId.LinkPreviewMax, user.LinkPreviewMax, effective.LinkPreviewMax, nameof(AppSettings.LinkPreviewMax)),
            LinkPreviewTimeoutSeconds = fields.Field(ConfigurationFieldId.LinkPreviewTimeoutSeconds, user.LinkPreviewTimeoutSeconds, effective.LinkPreviewTimeoutSeconds, nameof(AppSettings.LinkPreviewTimeoutSeconds)),
            MaxAgentSteps = fields.Field(ConfigurationFieldId.MaxAgentSteps, user.MaxAgentSteps, effective.MaxAgentSteps, nameof(AppSettings.MaxAgentSteps)),
            MaxConcurrentReplies = fields.Field(ConfigurationFieldId.MaxConcurrentReplies, user.MaxConcurrentReplies, effective.MaxConcurrentReplies, nameof(AppSettings.MaxConcurrentReplies)),
            MaxContextMessages = fields.Field(ConfigurationFieldId.MaxContextMessages, user.MaxContextMessages, effective.MaxContextMessages, nameof(AppSettings.MaxContextMessages)),
            MaxMessagesPerConversation = fields.Field(ConfigurationFieldId.MaxMessagesPerConversation, user.MaxMessagesPerConversation, effective.MaxMessagesPerConversation, nameof(AppSettings.MaxMessagesPerConversation)),
            MaxProfileChars = fields.Field(ConfigurationFieldId.MaxProfileChars, user.MaxProfileChars, effective.MaxProfileChars, nameof(AppSettings.MaxProfileChars)),
            MoodTtlSeconds = fields.Field(ConfigurationFieldId.MoodTtlSeconds, user.MoodTtlSeconds, effective.MoodTtlSeconds, nameof(AppSettings.MoodTtlSeconds)),
            PokeCooldownSeconds = fields.Field(ConfigurationFieldId.PokeCooldownSeconds, user.PokeCooldownSeconds, effective.PokeCooldownSeconds, nameof(AppSettings.PokeCooldownSeconds)),
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

    private static ModelView ProjectModelView(AppSettings user, AppSettings effective, ReadFields fields)
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

    private static AgentView ProjectAgentView(AppSettings user, AppSettings effective, ReadFields fields)
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
            EnableNaturalAgent = fields.Field(ConfigurationFieldId.EnableNaturalAgent, user.EnableNaturalAgent, effective.EnableNaturalAgent, nameof(AppSettings.EnableNaturalAgent)),
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

    private static PlatformView ProjectPlatformView(AppSettings user, AppSettings effective, ReadFields fields,
        ImmutableArray<PlatformPolicyEntryView> requestedPolicies, ImmutableArray<PlatformPolicyEntryView> effectivePolicies)
        => new()
        {
            FeishuApiBase = fields.Field(ConfigurationFieldId.FeishuApiBase, user.FeishuApiBase, effective.FeishuApiBase, nameof(AppSettings.FeishuApiBase)),
            FeishuAppId = fields.Field(ConfigurationFieldId.FeishuAppId, user.FeishuAppId, effective.FeishuAppId, nameof(AppSettings.FeishuAppId)),
            FeishuEnabled = fields.Field(ConfigurationFieldId.FeishuEnabled, user.FeishuEnabled, effective.FeishuEnabled, nameof(AppSettings.FeishuEnabled)),
            FeishuWhitelist = fields.Field(ConfigurationFieldId.FeishuWhitelist, user.FeishuWhitelist, effective.FeishuWhitelist, nameof(AppSettings.FeishuWhitelist)),
            FeishuWhitelistPrivates = fields.Field(ConfigurationFieldId.FeishuWhitelistPrivates, user.FeishuWhitelistPrivates, effective.FeishuWhitelistPrivates, nameof(AppSettings.FeishuWhitelistPrivates)),
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

    private static VoiceView ProjectVoiceView(AppSettings user, AppSettings effective, ReadFields fields)
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

    private static MusicView ProjectMusicView(AppSettings user, AppSettings effective, ReadFields fields)
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

    private static ReportingView ProjectReportingView(AppSettings user, AppSettings effective, ReadFields fields)
        => new()
        {
            HealthReportEnabled = fields.Field(ConfigurationFieldId.HealthReportEnabled, user.HealthReportEnabled, effective.HealthReportEnabled, nameof(AppSettings.HealthReportEnabled)),
            HealthReportTargets = fields.Field(ConfigurationFieldId.HealthReportTargets, user.HealthReportTargets, effective.HealthReportTargets, nameof(AppSettings.HealthReportTargets)),
            HealthReportTime = fields.Field(ConfigurationFieldId.HealthReportTime, user.HealthReportTime, effective.HealthReportTime, nameof(AppSettings.HealthReportTime)),
        };
}

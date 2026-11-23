extern alias StorageModule;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BotAgent.Adapters.Panel;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Tools;
using BotAgent.Services;
using BotAgent.Services.Model;
using BotAgent.Services.Ops;
using BotAgent.Services.Permissions;
using BotAgent.Services.Platforms;
using BotAgent.Services.Settings;
using BotAgent.Services.Tools;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;

namespace BotAgent.ReviewRemediationProbe;

internal static class ConversationProfileTests
{
    internal const string ChildArgument = "--conversation-profiles-child";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        RunPureDomainAndPromptChecks(check);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        process.StartInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        process.StartInfo.ArgumentList.Add(ChildArgument);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorsTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await outputTask;
        var errors = await errorsTask;
        check(process.ExitCode == 0 && output.Contains("CONVERSATION_PROFILES_STORAGE_OK", StringComparison.Ordinal),
            "conversation profile transactional storage, v15 zero-migration, privacy & hot-reload"
            + (process.ExitCode == 0 ? string.Empty : $" (exit={process.ExitCode} err={errors} out={output})"));
    }

    private static void RunPureDomainAndPromptChecks(Action<bool, string> check)
    {
        var qqDriver = new DriverKey(PlatformId.QqPrivate, AccountScope.Legacy, "");
        var feishuDriver = new DriverKey(PlatformId.Feishu, AccountScope.Default, "");
        var officialDriver = new DriverKey(PlatformId.QqOfficial, AccountScope.Legacy, "");
        var localDriver = new DriverKey(PlatformId.Local, AccountScope.Legacy, "");

        // 1. Key prefix & validation rules
        check(ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.QqPrivate, "group:10001")
            && ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.QqPrivate, "private:20001")
            && !ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.QqPrivate, "feishu:group:oc_1")
            && !ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.QqPrivate, "group:notdigits"),
            "conversation profile key prefix validates qq.private strictly");

        check(ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.Feishu, "feishu:group:oc_10001")
            && ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.Feishu, "feishu:private:ou_20001")
            && !ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.Feishu, "group:10001"),
            "conversation profile key prefix validates feishu strictly");

        check(ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.QqOfficial, "official:group:80001")
            && !ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.QqOfficial, "group:10001")
            && ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.Local, "local:room-1")
            && !ConversationProfileValidator.MatchesPlatformPrefix(PlatformId.Local, "group:10001"),
            "conversation profile key prefix validates qq.official and local strictly");

        var oversizePersonaRejected = Throws<ArgumentException>(() => ConversationProfileValidator.FreezeProfile(
            new ConversationProfile(new(qqDriver, "group:10001"), "群A",
                Style: new(PersonaOverride: new string('a', 900), PersonaAppend: new string('b', 400)))));
        check(oversizePersonaRejected, "combined PersonaOverride + PersonaAppend exceeding 1200 chars is rejected");

        var rangeRejected = Throws<ArgumentException>(() => ConversationProfileValidator.FreezeProfile(
            new ConversationProfile(new(qqDriver, "group:10001"), "群A",
                Pacing: new(GroupInterjectionProbability: 101))));
        check(rangeRejected, "out-of-range pacing probability is rejected");

        var duplicateFeatureRejected = Throws<ArgumentException>(() => ConversationProfileValidator.FreezeProfile(
            new ConversationProfile(new(qqDriver, "group:10001"), "群A",
                Capabilities: new(ImmutableArray.Create(
                    new FeatureOverride(PlatformFeatureId.Voice, true),
                    new FeatureOverride(PlatformFeatureId.Voice, false))))));
        check(duplicateFeatureRejected, "duplicate FeatureOverride in conversation profile is rejected");

        // 2. Three-tier inheritance & hard ceiling invariants
        var global = new AppSettings
        {
            AiModeEnabled = true,
            EnableVoice = false,
            EnableStickers = true,
            EnablePoke = true,
            EnableMusic = true,
            EnableWebSearch = false,
            EnableLinkPreview = true,
            EnableProactive = false,
            EnableApprovals = true,
            EnableQuestions = true,
            FilterActionNarration = true,
            VoiceMaxChars = 100,
            VoiceEagerness = 30,
            SuitabilityThreshold = 40,
            MaxContextMessages = 30,
            GroupInterjectionProbability = 15,
            GroupInterjectionCooldownSeconds = 45,
            GroupContinuationWaitSeconds = 5,
            PrivateContinuationWaitSeconds = 2,
            ProactiveQuietSeconds = 300,
        };

        // Feishu lacks voice & poke hardware capabilities; platform explicitly disables music
        var feishuCaps = new PlatformCapabilities(
            SupportsText: true, SupportsDirect: true, SupportsGroup: true,
            SupportsImage: true, SupportsVoice: false, SupportsQuote: true,
            SupportsRecall: true, SupportsThread: true, SupportsStickers: true,
            SupportsMusic: false, SupportsPoke: false) { SupportsMusicTextFallback = true };
        var feishuPolicy = new EffectivePlatformPolicy(
            new PlatformContext(PlatformId.Feishu, AccountScope.Default, ""),
            Registered: true, Connected: true, Enabled: true, ChatEnabled: true,
            Capabilities: feishuCaps,
            AllowedActions: ImmutableHashSet<string>.Empty,
            FeatureOverrides: ImmutableDictionary<string, bool>.Empty.Add("music", false),
            Reasons: Array.Empty<string>());

        var feishuProfile = new ConversationProfile(
            new(feishuDriver, "feishu:group:oc_10001"), "协作群",
            Style: new(PersonaAppend: "结论先行", FilterActionNarration: false, VoiceMaxChars: 60, VoiceEagerness: 80),
            Pacing: new(GroupInterjectionProbability: 0, ProactiveEnabled: ToggleRule.Enabled, ProactiveQuietSeconds: 120, SuitabilityThreshold: 70, MaxContextMessages: 16),
            Capabilities: new(
                ImmutableArray.Create(
                    new FeatureOverride(PlatformFeatureId.Voice, true),
                    new FeatureOverride(PlatformFeatureId.Poke, true),
                    new FeatureOverride(PlatformFeatureId.Music, true),
                    new FeatureOverride(PlatformFeatureId.WebSearch, true),
                    new FeatureOverride(PlatformFeatureId.Stickers, false),
                    new FeatureOverride(PlatformFeatureId.LinkPreview, false)),
                ConversationToolTier.ReadOnlyTools));

        var resolvedFeishu = ConversationProfileResolver.Resolve(global, feishuPolicy, feishuProfile);
        check(!resolvedFeishu.FeatureEnabled("voice") && !resolvedFeishu.FeatureEnabled("poke"),
            "conversation profile cannot revive unsupported platform capabilities (feishu voice/poke)");
        check(!resolvedFeishu.FeatureEnabled("music"),
            "conversation profile cannot revive feature explicitly disabled by platform policy");
        check(resolvedFeishu.FeatureEnabled("websearch"),
            "conversation profile can locally enable globally-off feature when platform permits");
        check(!resolvedFeishu.FeatureEnabled("stickers") && !resolvedFeishu.FeatureEnabled("linkpreview")
            && !resolvedFeishu.EffectivePolicy.Feature("stickers", globallyEnabled: true).Enabled,
            "conversation profile tightens stickers/linkpreview and propagates deny into EffectivePolicy");
        check(resolvedFeishu.ProactiveEnabled && resolvedFeishu.ProactiveQuietSeconds == 120
            && resolvedFeishu.Pacing.GroupInterjectionProbability == 0
            && resolvedFeishu.SuitabilityThreshold == 70 && resolvedFeishu.MaxContextMessages == 16
            && !resolvedFeishu.FilterActionNarration && resolvedFeishu.VoiceMaxChars == 60 && resolvedFeishu.VoiceEagerness == 80,
            "conversation profile resolves style, pacing, proactive, and context overrides accurately");

        // Disabled platform or AI off cannot be revived
        var disabledPlatform = feishuPolicy with { ChatEnabled = false };
        var resolvedDisabledPlatform = ConversationProfileResolver.Resolve(global, disabledPlatform, feishuProfile);
        check(!resolvedDisabledPlatform.ProactiveEnabled && !resolvedDisabledPlatform.FeatureEnabled("websearch"),
            "conversation profile cannot bypass disabled platform chat");

        var aiOffGlobal = global.Snapshot();
        aiOffGlobal.AiModeEnabled = false;
        check(!ConversationProfileResolver.Resolve(aiOffGlobal, feishuPolicy, feishuProfile).ProactiveEnabled,
            "conversation profile cannot bypass AiModeEnabled=false for proactive");

        // 3. ToolVisibility + ConversationToolTier enforcement
        var qqCaps = PlatformCapabilities.QqOneBot;
        var qqPolicy = new EffectivePlatformPolicy(
            new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy, ""),
            Registered: true, Connected: true, Enabled: true, ChatEnabled: true,
            Capabilities: qqCaps,
            AllowedActions: ImmutableHashSet<string>.Empty,
            FeatureOverrides: ImmutableDictionary<string, bool>.Empty,
            Reasons: Array.Empty<string>());

        var webSearchSpec = new ToolSpec("web.search", ToolCategory.WebRead, "联网搜索",
            ReadOnly: true, "web", "query", ToolDefaultPolicy.FollowSwitch, FeatureKey: ToolFeature.WebSearch);
        var catalog = new ChatToolCatalog(new IToolBinding[] { new FakeToolBinding(webSearchSpec) });
        var visibility = new ToolVisibility(webReady: () => true, catalog: () => catalog);

        var chatOnlySettings = ConversationProfileResolver.Resolve(global, qqPolicy,
            new ConversationProfile(new(qqDriver, "group:10001"), "纯聊群",
                Capabilities: new(ImmutableArray.Create(new FeatureOverride(PlatformFeatureId.WebSearch, true),
                    new FeatureOverride(PlatformFeatureId.Voice, true)), ConversationToolTier.ChatOnly)));
        check(!visibility.Allows(global, chatOnlySettings.EffectivePolicy, "web.search", catalog, chatOnlySettings)
            && !visibility.Allows(global, chatOnlySettings.EffectivePolicy, ApprovalFlow.FixedToolId, catalog, chatOnlySettings)
            && !visibility.Allows(global, chatOnlySettings.EffectivePolicy, ApprovalFlow.QuestionToolId, catalog, chatOnlySettings)
            && visibility.Allows(global, chatOnlySettings.EffectivePolicy, "sticker.send", catalog, chatOnlySettings)
            && visibility.Allows(global, chatOnlySettings.EffectivePolicy, "voice.speak", catalog, chatOnlySettings)
            && !chatOnlySettings.AllowsNaturalAgent && !chatOnlySettings.AllowsAsk && !chatOnlySettings.AllowsToolRequest,
            "ChatOnly tier blocks search/approval/ask/natural-agent while preserving feature-axis media");

        var readOnlySettings = ConversationProfileResolver.Resolve(global, qqPolicy,
            new ConversationProfile(new(qqDriver, "group:10002"), "只读群",
                Capabilities: new(ImmutableArray.Create(new FeatureOverride(PlatformFeatureId.WebSearch, true)),
                    ConversationToolTier.ReadOnlyTools)));
        check(visibility.Allows(global, readOnlySettings.EffectivePolicy, "web.search", catalog, readOnlySettings)
            && visibility.Allows(global, readOnlySettings.EffectivePolicy, ApprovalFlow.QuestionToolId, catalog, readOnlySettings)
            && !visibility.Allows(global, readOnlySettings.EffectivePolicy, ApprovalFlow.FixedToolId, catalog, readOnlySettings)
            && !readOnlySettings.AllowsNaturalAgent && !readOnlySettings.AllowsToolRequest,
            "ReadOnlyTools tier allows web.search/ask while blocking approval tool request and natural agent");

        var managedSettings = ConversationProfileResolver.Resolve(global, qqPolicy,
            new ConversationProfile(new(qqDriver, "private:20001"), "私聊助手",
                Capabilities: new(ImmutableArray.Create(new FeatureOverride(PlatformFeatureId.WebSearch, true)),
                    ConversationToolTier.ManagedTools)));
        check(managedSettings.AllowsNaturalAgent && managedSettings.AllowsToolRequest
            && visibility.Allows(global, managedSettings.EffectivePolicy, "web.search", catalog, managedSettings)
            && visibility.Allows(global, managedSettings.EffectivePolicy, ApprovalFlow.FixedToolId, catalog, managedSettings),
            "ManagedTools tier allows read-only tools plus existing controlled tool-request and natural-agent gates");

        // 3b. ApprovalUseCase.CapabilitiesFor unlocks globally-off -> locally-on features while keeping ChatOnly blocked
        var allOffGlobal = global.Snapshot();
        allOffGlobal.EnableWebSearch = false;
        allOffGlobal.EnableVoice = false;
        allOffGlobal.EnableStickers = false;
        var approvals = new ApprovalUseCase(new SettingsBox(allOffGlobal),
            new ApprovalHooks(_ => { }, (_, _) => "synthetic", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
            new SessionPolicyLedger(), new TurnTraceStore(), catalog: () => catalog);
        var localOnConv = ConversationProfileResolver.Resolve(allOffGlobal, qqPolicy,
            new ConversationProfile(new(qqDriver, "group:10002"), "局部开启群",
                Capabilities: new(ImmutableArray.Create(
                    new FeatureOverride(PlatformFeatureId.WebSearch, true),
                    new FeatureOverride(PlatformFeatureId.Voice, true),
                    new FeatureOverride(PlatformFeatureId.Stickers, true)), ConversationToolTier.ReadOnlyTools)));
        var localOnCaps = approvals.CapabilitiesFor(allOffGlobal, localOnConv);
        check(!approvals.Capabilities.Policy.AllowedTools.Contains("web.search")
            && localOnCaps.Policy.AllowedTools.Contains("web.search")
            && localOnCaps.Policy.AllowedTools.Contains("voice.speak")
            && localOnCaps.Policy.AllowedTools.Contains("sticker.send")
            && !localOnCaps.Policy.AllowedTools.Contains(ApprovalFlow.FixedToolId),
            "ApprovalUseCase.CapabilitiesFor unlocks locally-enabled features when globally off while respecting ReadOnlyTools");

        var chatOnlyOffCaps = approvals.CapabilitiesFor(allOffGlobal,
            ConversationProfileResolver.Resolve(allOffGlobal, qqPolicy,
                new ConversationProfile(new(qqDriver, "group:10001"), "仅聊群",
                    Capabilities: new(ImmutableArray.Create(new FeatureOverride(PlatformFeatureId.WebSearch, true)),
                        ConversationToolTier.ChatOnly))));
        check(!chatOnlyOffCaps.Policy.AllowedTools.Contains("web.search"),
            "ApprovalUseCase.CapabilitiesFor keeps web.search blocked under ChatOnly tier");

        // 3c. FindProfile unique-driver fallback for empty instanceId (Feishu) and fail-closed on multi-driver
        var feishuMainDriver = new DriverKey(PlatformId.Feishu, AccountScope.Default, "feishu-main");
        var feishuMainProfile = new ConversationProfile(new(feishuMainDriver, "feishu:group:oc_10001"), "飞书主群");
        var feishuFallbackSettings = global.Snapshot();
        feishuFallbackSettings.DeclaredPlatformDrivers = ImmutableArray.Create(feishuMainDriver);
        feishuFallbackSettings.RuntimeConversationProfiles = ImmutableArray.Create(feishuMainProfile);
        check(ConversationProfileResolver.FindProfile(feishuFallbackSettings,
                new PlatformContext(PlatformId.Feishu, AccountScope.Default, ""), "feishu:group:oc_10001") is not null,
            "FindProfile resolves unique declared Feishu driver when channel-only context has empty instanceId");
        feishuFallbackSettings.DeclaredPlatformDrivers = ImmutableArray.Create(
            feishuMainDriver, new DriverKey(PlatformId.Feishu, AccountScope.Default, "feishu-alt"));
        check(ConversationProfileResolver.FindProfile(feishuFallbackSettings,
                new PlatformContext(PlatformId.Feishu, AccountScope.Default, ""), "feishu:group:oc_10001") is null,
            "FindProfile fails closed when empty instanceId matches multiple declared drivers");

        // 3d. Control character & InstanceId whitespace hardening
        var controlRejected = false;
        try { ConversationProfileValidator.FreezeProfile(new(new(qqDriver, "group:10001"), "非法\n换行备注")); }
        catch (ArgumentException) { controlRejected = true; }
        var spaceInstanceRejected = false;
        try { ConversationProfileValidator.FreezeKey(new(new(PlatformId.Feishu, AccountScope.Default, " feishu-main "), "feishu:group:oc_1")); }
        catch (ArgumentException) { spaceInstanceRejected = true; }
        check(controlRejected && spaceInstanceRejected,
            "Validator rejects control characters in DisplayLabel and leading/trailing whitespace in InstanceId");

        // 4. PromptBuilder: empty base persona + PersonaAppend & platform-neutral zero-QQ check
        ClockBindings.InitializeModel();
        var neutralParts = PromptBuilder.BuildParts(new PromptBuilder.PromptRequest(
            SystemPrompt: "你是运行在聊天平台里的聊天机器人「Bot Agent」。",
            BotIdentity: "bot-feishu-1",
            Persona: "",
            Window: new[] { new ChatMessage { Role = MessageRole.Peer, SenderName = "群友A", Text = "看看文档", QqMessageId = 101 } },
            QuotableIds: new long[] { 101 },
            ProfilesText: null,
            Stickers: null,
            PokeContext: true,
            Proactive: false,
            MoodText: "心情不错",
            MusicText: null,
            LinkText: null,
            RecallText: null,
            GroupRolesText: null,
            VibeHint: null,
            SearchText: null,
            SuitabilityThreshold: 50,
            EnableListen: false,
            EnableVoice: false,
            VoiceMaxChars: 100,
            VoiceEagerness: 30,
            EnableWebSearch: false,
            EnableAsk: false,
            EnableToolRequest: false,
            PlatformDisplayName: "飞书",
            PersonaAppend: "简明直接，结论先行。",
            PlatformNeutral: true));
        check(neutralParts.Stable.Contains("[机器人人设档案]\n简明直接，结论先行。", StringComparison.Ordinal),
            "PromptBuilder emits PersonaAppend into Stable prefix even when global BotPersona is empty");
        check(!neutralParts.Text.Contains("QQ", StringComparison.Ordinal)
            && neutralParts.Stable.Contains("当前平台（飞书）", StringComparison.Ordinal)
            && neutralParts.Text.Length < 4200,
            "PromptBuilder neutral mode removes all QQ hardcoding and stays within 4200-char soft budget");
    }

    internal static async Task<int> RunChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-conv-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        try
        {
            ClockBindings.InitializePlatforms();
            ClockBindings.InitializeStorage();
            AppDatabase.Initialize();

            var qqDriver = new DriverKey(PlatformId.QqPrivate, AccountScope.Legacy, "");
            var feishuDriver = new DriverKey(PlatformId.Feishu, AccountScope.Default, "");
            var initial = new AppSettings
            {
                AgentMaskSensitive = true,
                Model = "synthetic-model",
                ModelOverride = "synthetic-model",
                ModelBaseUrl = "https://example.com/v1",
                ModelBaseUrlOverride = "https://example.com/v1",
                DeclaredPlatformDrivers = ImmutableArray.Create(qqDriver, feishuDriver),
            };
            new SettingsStore().Save(initial);
            new ModelProviderStore().EnsurePrimary(initial);

            var runtime = new SettingsBox(initial);
            Func<ConfigurationCaller, ConfigurationAccess, bool> authorization = (_, _) => true;
            Func<SqliteConnection, ImmutableArray<StorageConfiguration.SecretMutation>, ImmutableArray<SecretMetadata>> secrets
                = (_, _) => Enum.GetValues<WritableSecretSlot>()
                    .Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray();
            Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) =>
                new(ImmutableArray<PluginConfigurationView>.Empty, ImmutableArray<ProviderConfigurationView>.Empty,
                    ImmutableArray<ModelCredentialView>.Empty);
            Func<ConfigurationFieldId, ConfigurationValueSource> source = _ => ConfigurationValueSource.UserConfiguration;
            var configEffectMethod = typeof(BotConfig).GetMethod("ConfigurationEffect", BindingFlags.NonPublic | BindingFlags.Static)!;
            Func<ConfigurationFieldId, FieldEffect> effect = id => (FieldEffect)configEffectMethod.Invoke(null, new object[] { id })!;
            var adapterType = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)!;
            var participant = (StorageConfiguration.ConfigurationStorageParticipant)adapterType
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single().Invoke(
                    new object[] { runtime, authorization, secrets, registered, source, effect, false });
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var caller = new ConfigurationCaller("synthetic-host", "synthetic-owner", "bot",
                ConfigurationAccess.ReadOverview | ConfigurationAccess.ReadEditable
                | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.ValidateConfiguration);

            Require(effect(ConfigurationFieldId.ConversationProfiles) == FieldEffect.Hot, "effect_hot");

            var keyA = new ConversationProfileKey(qqDriver, "group:10001");
            var keyB = new ConversationProfileKey(feishuDriver, "feishu:group:oc_20002");
            var profileA = new ConversationProfile(keyA, "项目协作群A",
                Style: new(PersonaAppend: "结论先行", FilterActionNarration: false),
                Pacing: new(GroupInterjectionProbability: 0),
                Capabilities: new(ImmutableArray.Create(new FeatureOverride(PlatformFeatureId.Stickers, false)), ConversationToolTier.ReadOnlyTools));
            var profileB = new ConversationProfile(keyB, "飞书测试群B",
                Style: new(PersonaOverride: "活泼幽默"),
                Pacing: new(GroupInterjectionProbability: 80),
                Capabilities: new(ImmutableArray.Create(new FeatureOverride(PlatformFeatureId.WebSearch, true)), ConversationToolTier.ChatOnly));

            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            var save1 = await store.SaveAsync(caller, new(ConfigurationContract.Revision, header.Version,
                new(header.Generation, header.ServerUnixSeconds, Guid.NewGuid()),
                new(null, null, null, null, null, null, null,
                    new RegisteredConfigurationChanges(
                        ImmutableArray<PluginEnabledEdit>.Empty,
                        ImmutableArray<ProviderEdit>.Empty,
                        ImmutableArray<PolicyEdit>.Empty,
                        ImmutableArray.Create(
                            new ConversationProfileEdit(keyA, ConversationProfileEditOperation.Upsert, profileA),
                            new ConversationProfileEdit(keyB, ConversationProfileEditOperation.Upsert, profileB)))),
                ImmutableArray<SecretEdit>.Empty));
            Require(save1.Code == ConfigurationResultCode.Ok && save1.Receipt?.Decision == ConfigurationDecision.Changed,
                "save1_failed:" + save1.Code);

            // Verify Schema remains v15 and configuration_field_modes has ZERO rows for field_id=153
            Require(AppDatabase.Scalar<long>("PRAGMA user_version") == 15, "schema_version_must_remain_15");
            Require(AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_field_modes WHERE field_id=153") == 0,
                "no_field_modes_row_for_153");

            // Simulate hot publish into runtime snapshot
            var loadCommitted = typeof(SettingsStore).GetMethod("LoadCommittedRuntimeSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var committedSnapshot = (AppSettings)loadCommitted.Invoke(new SettingsStore(), new object[]
            {
                save1.Receipt!.CommittedVersion!.Value, runtime.Current,
                new ConfigurationPatch(null, null, null, null, null, null, null, null),
                ImmutableArray<StorageConfiguration.SecretMutation>.Empty,
            })!;
            runtime.Apply(next => next.RuntimeConversationProfiles = committedSnapshot.RuntimeConversationProfiles);
            Require(runtime.Current.RuntimeConversationProfiles.Length == 2, "runtime_loaded_2");

            // Verify SettingsStore.Load() also projects RuntimeConversationProfiles
            var loadedDirect = new SettingsStore().Load();
            Require(loadedDirect.RuntimeConversationProfiles.Length == 2, "settings_store_load_projects_profiles");

            // Verify Overview hides raw labels/personas, masks keys, AND never leaks raw keys in runtimeEffects
            var overview = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            var overviewProfiles = overview.View!.Registered.ConversationProfiles;
            var configJsonBase = (JsonSerializerOptions)typeof(SettingsStore).Assembly
                .GetType("BotAgent.Adapters.Panel.PanelConfigurationRequestValidation")!
                .GetField("ConfigurationJson", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
            var overviewJson = JsonSerializer.Serialize(overview, new JsonSerializerOptions(configJsonBase)
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            Require(overviewProfiles.Length == 2
                && overviewProfiles.All(item => item.Visibility == FieldVisibility.Hidden
                    && item.DisplayLabelRaw is null && item.Style is null && item.Pacing is null && item.Capabilities is null)
                && overviewProfiles.All(item => item.Key.ConversationKey != "group:10001" && item.DisplayLabel != "项目协作群A")
                && !overviewJson.Contains("group:10001", StringComparison.Ordinal)
                && !overviewJson.Contains("feishu:group:oc_20002", StringComparison.Ordinal)
                && !overviewJson.Contains("项目协作群A", StringComparison.Ordinal)
                && !overviewJson.Contains("飞书测试群B", StringComparison.Ordinal)
                && !overviewJson.Contains("结论先行", StringComparison.Ordinal)
                && overview.RuntimeEffects!.Value.All(item => item.Field.Object is not ConfigurationObjectAddress.ConversationProfile)
                && overview.RuntimeEffects.Value.Any(item =>
                    item.Field.Field == ConfigurationFieldId.ConversationProfiles && item.Field.Object is null
                    && item.State == ConfigurationRuntimeState.Current),
                "overview_must_hide_raw_and_mask_including_runtime_effects");

            var editable = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            var editableProfiles = editable.View!.Registered.ConversationProfiles;
            Require(editableProfiles.Length == 2
                && editableProfiles.Any(item => item.Key == keyA && item.DisplayLabelRaw == "项目协作群A"
                    && item.Style?.PersonaAppend == "结论先行" && item.DisplayLabel != "项目协作群A")
                && editableProfiles.Any(item => item.Key == keyB && item.DisplayLabelRaw == "飞书测试群B"),
                "editable_returns_raw_and_masked_display");

            var convEffect = editable.RuntimeEffects!.Value.Single(item =>
                item.Field.Field == ConfigurationFieldId.ConversationProfiles && item.Field.Object is null);
            Require(convEffect.State == ConfigurationRuntimeState.Current, "runtime_effect_hot_current");

            // CAS stale expectedVersion -> VersionConflict
            var staleSave = await store.SaveAsync(caller, new(ConfigurationContract.Revision, header.Version,
                new(editable.Generation, editable.ServerUnixSeconds, Guid.NewGuid()),
                new(null, null, null, null, null, null, null,
                    new RegisteredConfigurationChanges(
                        ImmutableArray<PluginEnabledEdit>.Empty,
                        ImmutableArray<ProviderEdit>.Empty,
                        ImmutableArray<PolicyEdit>.Empty,
                        ImmutableArray.Create(new ConversationProfileEdit(keyA, ConversationProfileEditOperation.Delete)))),
                ImmutableArray<SecretEdit>.Empty));
            Require(staleSave.Code == ConfigurationResultCode.VersionConflict, "cas_conflict_expected");

            // Row-level Delete of keyA must preserve keyB
            var deleteA = await store.SaveAsync(caller, new(ConfigurationContract.Revision, editable.Version,
                new(editable.Generation, editable.ServerUnixSeconds, Guid.NewGuid()),
                new(null, null, null, null, null, null, null,
                    new RegisteredConfigurationChanges(
                        ImmutableArray<PluginEnabledEdit>.Empty,
                        ImmutableArray<ProviderEdit>.Empty,
                        ImmutableArray<PolicyEdit>.Empty,
                        ImmutableArray.Create(new ConversationProfileEdit(keyA, ConversationProfileEditOperation.Delete)))),
                ImmutableArray<SecretEdit>.Empty));
            Require(deleteA.Code == ConfigurationResultCode.Ok, "delete_a_ok");
            var afterDelete = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Require(afterDelete.View!.Registered.ConversationProfiles.Length == 1
                && afterDelete.View.Registered.ConversationProfiles[0].Key == keyB,
                "row_delete_preserves_other_profiles");

            // >32 undeclared-driver errors must return ValidationFailed with capped 32 errors (never 503 StorageUnavailable)
            var undeclaredDriver = new DriverKey(PlatformId.QqPrivate, AccountScope.Legacy, "undeclared-inst");
            var tooManyBad = Enumerable.Range(1, 40)
                .Select(i =>
                {
                    var badKey = new ConversationProfileKey(undeclaredDriver, $"group:{20000 + i}");
                    return new ConversationProfileEdit(badKey, ConversationProfileEditOperation.Upsert, new(badKey, $"群{i}"));
                }).ToImmutableArray();
            var validateMethod = typeof(StorageConfiguration.ConfigurationStore).GetMethod("ValidateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var valTask = (ValueTask<BotAgent.Domain.Management.ManagedValidationResult>)validateMethod.Invoke(store, new object[]
            {
                caller,
                new ConfigurationValidationRequest(ConfigurationContract.Revision, afterDelete.Version,
                    new(null, null, null, null, null, null, null,
                        new RegisteredConfigurationChanges(
                            ImmutableArray<PluginEnabledEdit>.Empty,
                            ImmutableArray<ProviderEdit>.Empty,
                            ImmutableArray<PolicyEdit>.Empty,
                            tooManyBad)),
                    ImmutableArray<SecretEdit>.Empty),
                CancellationToken.None,
            })!;
            var valResult = await valTask;
            Require(valResult.Code == BotAgent.Domain.Management.ManagedResultCode.ValidationFailed && valResult.Errors.Length == 32,
                $"validate_error_cap_failed:code={valResult.Code},len={valResult.Errors.Length}");

            // Backward-compatible legacy JSON without conversationProfiles in registered
            var legacyPayload = JsonDocument.Parse(
                """{"contractRevision":"cfg-0.3.3","expectedVersion":"1","key":"cfg1:11111111111111111111111111111111:1:22222222222222222222222222222222","changes":{"registered":{"plugins":[],"providers":[],"policies":[]}},"secrets":[]}""");
            var validatorType = typeof(SettingsStore).Assembly.GetType("BotAgent.Adapters.Panel.PanelConfigurationRequestValidation")!;
            var parseMethod = validatorType.GetMethod("ParseConfigurationSave", BindingFlags.NonPublic | BindingFlags.Static)!;
            var parsedTuple = parseMethod.Invoke(null, new object[] { legacyPayload.RootElement })!;
            var parsed = (ConfigurationSaveRequest)parsedTuple.GetType().GetField("Item1")!.GetValue(parsedTuple)!;
            var ownerObj = parsedTuple.GetType().GetField("Item2")!.GetValue(parsedTuple)!;
            (ownerObj as IDisposable)?.Dispose();
            Require(parsed.Changes.Registered is not null && parsed.Changes.Registered.ConversationProfiles.IsEmpty,
                "legacy_wire_omitting_conversationProfiles_accepted");

            Console.WriteLine("CONVERSATION_PROFILES_STORAGE_OK");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class FakeToolBinding(ToolSpec spec) : IToolBinding
    {
        public string ProviderId => "synthetic";
        public string Generation => "gen-1";
        public ToolDefinition Definition { get; } = new(spec, ImmutableArray<ArgField>.Empty);
        public bool IsReady => true;
        public Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct = default)
            => Task.FromResult(new ToolOutcome(true, "ok", null));
        public T GetContribution<T>() where T : class, BotAgent.Domain.Plugins.IPluginContribution
            => throw new NotSupportedException();
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}

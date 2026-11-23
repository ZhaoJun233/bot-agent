extern alias StorageModule;

using BotAgent.Domain.Configuration;
using System.Collections.Immutable;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;
using ConfigurationModule = StorageModule::BotAgent.Storage;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Panel;
using BotAgent.Adapters.Panel.Zba;
using BotAgent.Adapters.Model;
using BotAgent.Adapters.Platforms;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Model;
using BotAgent.Services.Platforms;
using BotAgent.Services.Resilience;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.Official;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Qq;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;
using BotAgent.Services.Links;
using BotAgent.Services.Local;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.Reply;
using BotAgent.Services.Conversations;
using BotAgent.Services.Poke;
using BotAgent.Services.Permissions;
using BotAgent.Services.Participation;
using BotAgent.Services.Panel;
using BotAgent.Services.Settings;
using BotAgent.Services.Plugins;
using BotAgent.Services.Evaluation;
using BotAgent.Services.Mcp;
namespace BotAgent.Host;

internal static partial class CompositionRoot
{
    /// <summary>
    /// 面板扫码登录（#54）：把 NapCat 的二维码搬进面板。单独抽出来只是为了让 Build 保持在棘轮线内。
    /// </summary>
    /// <summary>每次宿主启动把 <c>startup_count</c> 加一（读不到就从 1 开始）。</summary>
    private static void RecordStartup()
    {
        var previous = AppDatabase.GetMeta(AppDatabase.StartupCountMetaKey);
        var count = long.TryParse(previous, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value + 1 : 1;
        AppDatabase.SetMeta(AppDatabase.StartupCountMetaKey,
            count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static LoginQrService CreateLoginQr(AppSettings settings, Action<IDisposable>? registerTransport = null)
    {
        var loginQrHttp = new HttpFetcher(TimeSpan.FromSeconds(8), msg => FileLog.Write("Net", msg), "login-qr");
        registerTransport?.Invoke(loginQrHttp);
        return new(settings.NapCatWebUiUrl, settings.NapCatWebUiToken, loginQrHttp);
    }

    /// <summary>
    /// 每日热备（#121）。用例层只吃委托：Services 层一旦出现 BotAgent.Adapters 会被架构探针 R9 判红，
    /// 所以这里把「备份怎么做、上次成没成写在哪个键」都收在装配点，用例只看 Func/Action。
    /// </summary>
    private static DatabaseBackupService CreateBackups() =>
        new(
            () => AppDatabase.VacuumIntoBackup(null, DatabaseBackupService.MaxRetained),
            () => long.TryParse(AppDatabase.GetMeta(DatabaseBackupService.LastBackupMetaKey),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var utcMs)
                ? utcMs
                : null,
            () => AppDatabase.GetMeta(DatabaseBackupService.LastFailureMetaKey),
            (tag, message) => FileLog.Write(tag, message),
            (ok, error) =>
            {
                if (ok)
                {
                    AppDatabase.SetMeta(DatabaseBackupService.LastBackupMetaKey,
                        Clock.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                AppDatabase.SetMeta(DatabaseBackupService.LastFailureMetaKey, ok ? string.Empty : error ?? string.Empty);
            });

    private static ZbaRuntime BuildZba(
        SettingsBox box, SettingsHotReload hotReload, ManagedCapabilityBinding management,
        IAgentSessionStore sessions, AgentCommandService agent, ApprovalUseCase? approvals,
        BotAgent.Domain.Plugins.IPluginRegistry? plugins, ConversationRegistry registry,
        WebUiServer web, bool hadStoredConfigurationBeforeStartup, PluginRuntime pluginRuntime,
        DatabaseBackupService backups, Func<IReadOnlyList<PersistedModelProvider>> capturedProviders,
        TurnTraceStore traces, PlatformPolicyResolver platformPolicies, ZbaControlPlane? controlPlane = null,
        Func<(bool MarkdownEnabled, bool MarkdownPermissionDenied)>? officialRuntimeState = null,
        Func<(BotAgent.Domain.Memory.UnifiedMemoryGovernanceSummary Summary, bool AdminConfigured)>? memoryGovernance = null,
        BotAgent.Engine.Runtime.AgentTaskRuntime? sharedTaskRuntime = null,
        Func<BotAgent.Domain.Agent.AgentTaskRequest, BotAgent.Domain.Agent.AgentTaskRequest>? sharedPrepare = null,
        McpManagedWiring? mcpManaged = null,
        ConfigurationModule.McpServerStore? mcpStore = null,
        ConfigurationModule.AccessRoleStore? accessRoleStore = null,
        AccessRoleResolver? accessRoles = null,
        StorageModule.BotAgent.Storage.ScheduledJobStore? scheduledJobStore = null,
        BotAgent.Engine.Scheduler.ScheduledJobRunner? scheduledRunner = null,
        Services.Todo.PersonalTodoHostPortsAdapter? todoPorts = null,
        StorageModule.BotAgent.Storage.PersonalTodoStore? todoStore = null,
        BotAgent.Domain.Identity.IIdentityLinkResolver? identityLinks = null,
        StorageModule.BotAgent.Storage.WorkspaceConversationStore? workspaceStore = null,
        OpenAiClient? brain = null,
        Action<Action>? registerWorkspaceNotifier = null,
        Action<string>? onClearWorkspaceDocuments = null,
        ITokenUsageRecorder? usage = null)
    {
        // 提供商发现复用测试通道的受限出网（同一条拒绝私网/禁跳转的策略），不另开 socket。
        var discovery = management.ProviderTransport is { } providerHttp
            ? new BotAgent.Model.ProviderModelDiscovery(providerHttp)
            : null;
        var runtime = ZbaRuntime.Create(box, hotReload, management.Capabilities, sessions, agent, approvals, plugins,
            registry, () => BotAgent.Adapters.Time.Clock.Now, hadStoredConfigurationBeforeStartup,
            pluginRuntime.Market, providerKey => TryBuildProviderTestRouteWithPlugins(providerKey, box.Current, capturedProviders, pluginRuntime.Registry), discovery,
            backups: backups,
            traces: traces, platformPolicies: platformPolicies, controlPlane: controlPlane,
            quotas: pluginRuntime.Registry.CreateControlEntryPoint<IQuotaOperations>("preset.feature.token-quota"),
            officialRuntimeState: officialRuntimeState,
            memoryGovernance: memoryGovernance,
            sharedTaskRuntime: sharedTaskRuntime,
            sharedPrepare: sharedPrepare,
            mcpManaged: mcpManaged,
            mcpStoreObj: mcpStore,
            accessRoleStoreObj: accessRoleStore,
            accessRoles: accessRoles,
            scheduledJobStoreObj: scheduledJobStore,
            scheduledRunner: scheduledRunner,
            todoPorts: todoPorts,
            todoStoreObj: todoStore,
            identityLinks: identityLinks,
            workspaceStoreObj: workspaceStore,
            brain: brain,
            registerWorkspaceNotifier: registerWorkspaceNotifier,
            onClearWorkspaceDocuments: onClearWorkspaceDocuments,
            usage: usage);
        web.AttachZba(runtime.Endpoint);
        return runtime;
    }

    /// <summary>
    /// 管理面绑定：驱动是否就绪 / 凭据是否配齐 / 模型 provider 测试通道，全部现取配置。
    /// 构造顺序保持原样（先建测试出网，再交给绑定；出网由绑定负责释放）。
    /// </summary>
    private static ManagedCapabilityBinding BuildManagedCapabilityBinding(
        ConfigurationModule.ConfigurationStore configuration,
        SettingsHotReload.ConfigurationRequestAuthorization authorization,
        PlatformDriverOperations driverOperations,
        SettingsBox settingsBox,
        PlatformPolicyResolver platformPolicies,
        AppSettings settings,
        FeishuBotGateway? feishu,
        IPlatformRegistry platformRegistry,
        Func<IReadOnlyList<PersistedModelProvider>> capturedProviders,
        BotAgent.Domain.Management.IManagedAuthorization? managedAuthorization,
        Func<ImmutableArray<BotAgent.Domain.Management.ManagedDescriptor>>? plugins = null,
        PluginManager? pluginManager = null,
        Func<bool>? isPluginRuntimeStarted = null)
    {
        bool DriverReady(DriverKey key)
        {
            if (driverOperations.ShouldEnforceContributions && driverOperations.ResolveContribution(key.PlatformId) is null)
                return false;
            var adapter = platformRegistry.GetAdapter(new(key.PlatformId, key.AccountScope, key.InstanceId));
            if (adapter is null || !DriverIdentityMatches(settings, settingsBox.Current, key.PlatformId)) return false;
            return key.PlatformId switch
            {
                PlatformId.Feishu => feishu?.IsConfigured == true,
                PlatformId.QqOfficial => !string.IsNullOrWhiteSpace(settingsBox.Current.OfficialAppId)
                    && !string.IsNullOrWhiteSpace(settingsBox.Current.OfficialAppSecret),
                PlatformId.QqPrivate => !string.IsNullOrWhiteSpace(settingsBox.Current.OneBotAddress),
                PlatformId.Local => true,
                _ => false
            };
        }
        var providerTestHttp = new HttpFetcher(TimeSpan.FromSeconds(15), _ => { }, "provider-test", allowAutoRedirect: false);
        var providerTester = new BotAgent.Model.ProviderConnectionTester(providerTestHttp);
        return new ManagedCapabilityBinding(configuration, authorization, driverOperations,
            () => settingsBox.Current.DeclaredPlatformDrivers,
            key => platformPolicies.Resolve(new(key.PlatformId, key.AccountScope, key.InstanceId)).Enabled,
            key => DriverCredentialConfigured(key, settingsBox.Current, feishu?.IsLongConnectionMode == true), DriverReady,
            () => ManagedProviderDescriptors(capturedProviders(), settingsBox.Current),
            async (command, ct) =>
            {
                // Explicit administrator tests use current metadata, including newly saved disabled providers.
                // Chat/failover routes remain bound to the startup registry until a restart.
                var activePlugins = isPluginRuntimeStarted is null || isPluginRuntimeStarted() ? pluginManager : null;
                var route = TryBuildProviderTestRouteWithPlugins(command.Key.Value, settingsBox.Current, capturedProviders, activePlugins);
                if (route is null) return new ConfigurationModule.ManagedDispatchResult(
                    BotAgent.Domain.Management.ManagedOperationState.Failed, "model_route_unavailable");
                var result = await providerTester.TestAsync(route, ct).ConfigureAwait(false);
                return new ConfigurationModule.ManagedDispatchResult(result.Succeeded
                    ? BotAgent.Domain.Management.ManagedOperationState.Completed : BotAgent.Domain.Management.ManagedOperationState.Failed,
                    result.Succeeded ? null : result.ErrorCode);
            }, managedAuthorization, providerTestHttp, plugins);
    }

    private static WebUiServer BuildWebUiServer(
        AppSettings settings,
        SettingsBox settingsBox,
        OneBotGateway gateway,
        IQqChatSource source,
        BotAgentHost agent,
        LoginQrService loginQr,
        SettingsHotReload settingsHotReload,
        PanelNotifier ui,
        StickerService stickers,
        MoodStore mood,
        VoiceUseCase voice,
        MusicUseCase music,
        ResearchUseCase research,
        ConversationRegistry registry,
        MemberProfileStore profiles,
        ISecretsRepository secrets,
        SettingsStore settingsStore,
        BotIdentity identity,
        BotScheduler scheduler,
        ReplyPipeline reply,
        ParticipationUseCase participation,
        AgentCommandService agentCmds,
        IQuotaOperations quotas,
        HealthReportService healthReports,
        DatabaseBackupService backups,
        SessionPolicyLedger sessionPolicies,
        TurnTraceStore traces,
        HostMetrics hostFacts,
        AuditLogStore audit,
        ApprovalUseCase approvals,
        LocalChannelSource? local,
        OfficialBotGateway? official,
        FeishuBotGateway? feishu,
        IPlatformRegistry platformRegistry,
        PlatformPolicyResolver platformPolicies,
        Func<IReadOnlyList<Domain.Ops.CircuitStatusSnapshot>> circuitStatusProvider,
        BackgroundEvaluatorQueue background,
        Domain.Plugins.IPluginRegistry? plugins = null,
        BotAgent.Services.Mcp.McpToolUseCase? mcp = null,
        Action<IDisposable>? registerTransport = null)
    {
        // 面板自己的两条出网（模型列表探测 20s / 自建网易云登录 15s）
        var panelHttp = new HttpFetcher(TimeSpan.FromSeconds(20), msg => FileLog.Write("Net", msg), "panel-models");
        var neteaseHttp = new HttpFetcher(TimeSpan.FromSeconds(15), msg => FileLog.Write("Net", msg), "panel-netease");
        registerTransport?.Invoke(panelHttp);
        registerTransport?.Invoke(neteaseHttp);

        return new WebUiServer(settings.HealthPort, settingsBox, gateway, source, agent, loginQr, panelHttp, neteaseHttp,
            settingsHotReload, ui, stickers, mood, voice, music, research, registry, profiles, secrets, settingsStore, identity, scheduler,
            reply, participation, agentCmds, quotas, healthReports,
            backups: backups,
            sessionPolicies: sessionPolicies,
            traces: traces,
            hostFacts: hostFacts,
            audit: audit,
            approvals: approvals,
            localChannel: local,
            onRestart: () =>
            {
                FileLog.Write("Host", "一键重启：即将退出，让 Docker 把容器重新拉起来…");
                foreach (var svc in new IDisposable?[] { official, feishu })
                {
                    try
                    {
                        svc?.Dispose();
                    }
                    catch (Exception)
                    {
                        // 退出路径，收尾失败不阻塞
                    }
                }

                Environment.Exit(0);
            },
            circuitStatusProvider: circuitStatusProvider,
            platformRegistry: platformRegistry,
            feishuGateway: feishu,
            platformPolicies: platformPolicies,
            plugins: plugins,
            background: background,
            mcp: mcp);
    }

    /// <summary>
    /// ⑦ 装配阶段七：面板与 ZBA（#110 第 2 步拆出）。
    ///
    /// 这是「把上面所有件暴露出去」的那一层，所以排最后：扫码登录、热备、健康日报、宿主事实
    /// 都依赖调度器/网关/回复链；热更新在这里被绑到配置运行时上（此前它只持有组件）。
    /// </summary>
    private static void AssembleSurface(Assembly a, BotAgent.Domain.Management.IManagedAuthorization? managedAuthorization)
    {
        // 面板内的扫码登录：把 NapCat 的登录二维码搬进机器人面板
        // （用户打开面板看不到二维码，是远程部署卡住最久的原因）
        a.LoginQr = CreateLoginQr(a.Settings, a.OwnedTransports.Add);
        // 每日热备（#121）：VACUUM INTO 早就写好了，只是从来没人按时调它。
        a.Backups = CreateBackups();
        // 服务器健康日报（每天定时私聊一条状态）：整条链路只用机器人自己 + 协议端，
        // **不经过外部设备 agent**（那台电脑可能根本没开）—— 管理员 2026-09-18 明确要求。
        // 宿主事实（cgroup 内存上限 / 负载）：健康日报与面板仪表盘共用同一份只读端口
        a.HostFacts = new HostMetrics();
        var healthHttp = new HttpFetcher(TimeSpan.FromSeconds(6), msg => FileLog.Write("Net", msg), "health");
        a.OwnedTransports.Add(healthHttp);
        a.HealthReports = new HealthReportService(a.Box, a.Registry, a.Reply, a.Identity, a.Scheduler, a.Voice,
            a.Gateway, healthHttp,
            a.HostFacts, a.Backups, plugins: a.Plugins);
        a.PluginServices.RegisterForPlugin<IHealthReportHostPorts>("system.health-reporting", a.HealthReports.CreateHostPorts());
        a.HotReload.BindConfiguration(a.Configuration, a.Authorization, a.SettingsStore.LoadCommittedRuntimeSnapshot,
            (caller, key, intent) => ConfigurationBridge.ApplyLegacyMood(a.Configuration, caller, key, intent, a.Mood),
            a.HealthReports.Reapply, WebUiServer.ProjectTtsConfiguration, BotConfig.ConfigurationEffect,
            version => InspectModelProjectionWithPlugins(version, a.ModelRegistry, a.Box, a.Secrets, a.PluginRuntime.HasStarted ? a.Plugins : null),
            () => a.PluginRuntime.ReloadAsync());
        a.CircuitStatus = BuildCircuitStatusProvider(a.Brain, a.TtsBreaker, a.SttBreaker, a.SearchBreaker, a.RiskBackoff);
        a.Web = BuildWebUiServer(a.Settings, a.Box, a.Gateway, a.Source, a.Agent, a.LoginQr,
            a.HotReload, a.Ui, a.Stickers, a.Mood, a.Voice, a.Music, a.Research, a.Registry, a.Profiles,
            a.Secrets, a.SettingsStore, a.Identity, a.Scheduler, a.Reply, a.Participation, a.AgentCmds, a.Quotas,
            a.HealthReports, a.Backups, a.SessionPolicies, a.Traces, a.HostFacts, a.Audit, a.Approvals, a.Local,
            a.Official, a.Feishu, a.PlatformRegistry, a.PlatformPolicies, a.CircuitStatus, a.Background, a.Plugins, a.Mcp.UseCase,
            a.OwnedTransports.Add);

        a.Management = BuildManagedCapabilityBinding(a.Configuration, a.Authorization, a.DriverOperations, a.Box,
            a.PlatformPolicies, a.Settings, a.Feishu, a.PlatformRegistry, () => a.ModelRegistry.Captured,
            managedAuthorization, plugins: a.PluginRuntime.Describe, pluginManager: a.Plugins,
            isPluginRuntimeStarted: () => a.PluginRuntime.HasStarted);
        RegisterLiveSettingsConsumers(a);
        a.Zba = BuildZba(a.Box, a.HotReload, a.Management, a.Sessions, a.AgentCmds, a.Approvals,
            a.Plugins, a.Registry, a.Web, BotConfig.HadStoredSettingsAtLoad, a.PluginRuntime, a.Backups,
            () => a.ModelRegistry.Captured, a.Traces, a.PlatformPolicies, a.ControlPlane,
            officialRuntimeState: () => a.Official is { } off
                ? (off.EnableInteractiveMarkdown, off.IsMarkdownPermissionDenied)
                : (ReadDeclaredBool(a.PluginRuntime.TryReadEffectiveValues("system.platform-qq-official"), "enableInteractiveMarkdown"), false),
            memoryGovernance: () => (a.UnifiedMemory.GetGovernanceSummary(), a.MemoryAdminConfigured),
            sharedTaskRuntime: a.TaskRuntime,
            sharedPrepare: a.TaskPrepare,
            mcpManaged: a.McpManaged,
            mcpStore: a.McpStore,
            accessRoleStore: a.AccessRoleStore,
            accessRoles: a.AccessRoles,
            scheduledJobStore: a.ScheduledJobStore,
            scheduledRunner: a.ScheduledJobs,
            todoPorts: a.TodoPorts,
            todoStore: a.PersonalTodoStore,
            identityLinks: a.IdentityLinks,
            workspaceStore: a.WorkspaceStore,
            brain: a.Brain,
            registerWorkspaceNotifier: fn => a.NotifyWorkspaceAppended = fn,
            onClearWorkspaceDocuments: key => a.DocumentPorts?.EvictConversation(key),
            usage: a.Usage);
    }

    private static bool ReadDeclaredBool(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? values, string key)
    {
        if (values is null || !values.TryGetValue(key, out var el)) return false;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String => bool.TryParse(el.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static void RegisterLiveSettingsConsumers(Assembly a)
    {
        a.PluginRuntime.RegisterLiveSettingsConsumer("preset.feature.voice",
            _ => a.PluginRuntime.Registry.IsReady("preset.feature.voice"));
        a.PluginRuntime.RegisterLiveSettingsConsumer("preset.feature.documents",
            _ => a.PluginRuntime.Registry.IsReady("preset.feature.documents"));
        a.PluginRuntime.RegisterLiveSettingsConsumer("preset.feature.digest",
            _ => a.PluginRuntime.Registry.IsReady("preset.feature.digest"));
        a.PluginRuntime.RegisterLiveSettingsConsumer("preset.feature.knowledge",
            _ => a.PluginRuntime.Registry.IsReady("preset.feature.knowledge"));
        a.PluginRuntime.RegisterLiveSettingsConsumer("preset.feature.scheduler",
            _ => a.PluginRuntime.Registry.IsReady("preset.feature.scheduler"));
        a.PluginRuntime.RegisterLiveSettingsConsumer("preset.feature.personal-todo",
            _ => a.PluginRuntime.Registry.IsReady("preset.feature.personal-todo"));
        a.PluginRuntime.RegisterLiveSettingsConsumer("preset.feature.profiles", _ =>
        {
            a.Scheduler.RebuildIfNeeded();
            return a.PluginRuntime.Registry.IsReady("preset.feature.profiles");
        });
        a.PluginRuntime.RegisterLiveSettingsConsumer("system.platform-feishu", _ =>
        {
            if (a.Feishu is null) return false;
            a.Feishu.ReconcileTransportMode();
            return true;
        });
        a.PluginRuntime.RegisterLiveSettingsConsumer("system.platform-qq-official", _ =>
        {
            if (a.Official is null) return false;
            var previousIntents = a.Official.CurrentIdentifyIntents;
            a.Official.NotifyPluginSettingsChanged();
            return !a.Official.IsConnected || a.Official.CurrentIdentifyIntents == previousIntents;
        });
    }
}

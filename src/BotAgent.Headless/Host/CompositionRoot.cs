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

/// <summary>
/// 整个进程的**唯一装配点**：谁依赖谁、谁先谁后，只有这一处说了算。
///
/// 为什么要把这段从 <c>Program.Main</c> 里搬出来：
///   • 顺序是**语义**（"先建库再读配置""先起官方再起私域"），散在启动脚本里没人守得住；
///   • 以前还有第二个装配点 —— <c>BotAgentHost</c> 构造函数自己 <c>new</c> 了十几种具体实现，
///     想知道"谁真正拥有音乐服务"得读两个文件；
///   • 测试要替身时没有插口：现在只改这里就能换实现（见 architecture-optimization.md §6.1）。
/// Program 只剩：参数、启停、优雅退出。
/// </summary>
internal sealed record AppGraph(
    AppSettings Settings,
    SettingsBox SettingsBox,
    IQqChatSource Source,
    OneBotGateway Gateway,
    OfficialBotGateway? Official,
    OpenAiClient Brain,
    BotAgentHost Agent,
    BootReport BootReport,
    LoginQrService LoginQr,
    HealthReportService HealthReports,
    DatabaseBackupService Backups,
    WebUiServer Web,
    PluginRuntime PluginRuntime,
    BackgroundEvaluatorQueue Background,
    PlatformDriverOperations DriverOperations,
    ManagedCapabilityBinding Management,
    McpRuntime? Mcp,
    ZbaRuntime? Zba) : IAsyncDisposable
{
    internal PluginManager Plugins => PluginRuntime.Registry;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        try { await Background.StopAsync().ConfigureAwait(false); } catch (Exception exception) { failures.Add(exception); }
        try { if (Mcp is not null) await Mcp.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { failures.Add(exception); }
        try { if (Zba is not null) await Zba.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { failures.Add(exception); }
        try { HealthReports.Dispose(); } catch (Exception exception) { failures.Add(exception); }
        try { Backups.Dispose(); } catch (Exception exception) { failures.Add(exception); }
        try { await Management.StopAsync().ConfigureAwait(false); } catch (Exception exception) { failures.Add(exception); }
        try { await Management.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { failures.Add(exception); }
        try { Agent.Dispose(); } catch (Exception exception) { failures.Add(exception); }
        try { await PluginRuntime.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { failures.Add(exception); }
        try { (Source as IDisposable)?.Dispose(); } catch (Exception exception) { failures.Add(exception); }
        if (failures.Count > 0) throw new AggregateException("host_shutdown_incomplete", failures);
    }
}


internal static partial class CompositionRoot
{
    /// <summary>
    /// 阶段一：数据层 → 配置 → 日志。
    /// ⚠ 顺序很重要：<see cref="BotConfig.Load" /> 会从 settings 表读配置，之前必须先把库准备好。
    /// </summary>
    public static AppSettings BootstrapSettings()
    {
        BotAgent.Adapters.Time.ClockBindings.InitializePlatforms();
        BotAgent.Adapters.Time.ClockBindings.InitializeEngine();
        BotAgent.Adapters.Time.ClockBindings.InitializeModel();
        BotAgent.Adapters.Time.ClockBindings.InitializeStorage();
        AppDatabase.Initialize();
        // 启动计数（#121 水位）：容器重启了几次看得出来；写在 meta 表里，重启后接着上次的数往上加。
        RecordStartup();
        LegacyJsonImporter.ImportIfNeeded();

        var settings = BotConfig.Load();
        FileLog.Verbose = settings.VerboseLog;
        FileLog.WriteToFile = EnvironmentAliases.Get("BOTAGENT_LOG_FILE", "QQCHAT_LOG_FILE") != "0";

        // 面板日志页的首屏历史：从日志文件尾部回填一段（刷新页面 / 重启进程之后也不是空的）。
        // 回填要放在往文件里写第一行之前，否则会把"本次启动"的那几行再攒一遍（虽只是重复，没必要）。
        FileLog.PreloadRecent();
        return settings;
    }

    /// <summary>
    /// 阶段二：按依赖顺序把服务接起来。
    /// 顺序：协议端（必要时两条 → 聚合器）→ 模型客户端 → 仓储 → host 用例 → 面板。
    /// </summary>
    /// <summary>
    /// 装配前置：固定时钟绑定并声明平台目录。必须落在任何组件读时间/平台之前，
    /// 所以集中成 Build 的最前面一行，避免以后有人在中间插入组件时把顺序打乱。
    /// </summary>
    private static void InitializeAssemblyPrerequisites(AppSettings settings)
    {
            BotAgent.Adapters.Time.ClockBindings.InitializePlatforms();
            BotAgent.Adapters.Time.ClockBindings.InitializeEngine();
            BotAgent.Adapters.Time.ClockBindings.InitializeModel();
            BotAgent.Adapters.Time.ClockBindings.InitializeStorage();
            DeclarePlatformCatalog(settings);
    }

    public static AppGraph Build(AppSettings settings, BotAgent.Domain.Management.IManagedAuthorization? managedAuthorization = null)
    {
        InitializeAssemblyPrerequisites(settings);
        // 装配顺序就是契约：每个 AssembleXxx 只往同一个容器里写自己那一段，
        // 顺序被调换或漏掉一段都会由 Assembly.Complete() 在返回图之前立刻发现。
        var assembly = new Assembly(settings);
        AssembleCore(assembly);
        AssembleChannels(assembly);
        AssembleModel(assembly);
        AssembleStores(assembly);
        AssembleTools(assembly);
        AssembleReply(assembly);
        AssembleSurface(assembly, managedAuthorization);
        return assembly.Complete();
    }

    /// <summary>
    /// 上行通道层：私域协议端 →（配齐了 appid/secret 且开关打开时）官方通道 → 聚合器。
    /// 两条路交给**同一个** Agent，隔离靠会话 key 的通道前缀（见 Channels.Key）。顺序与日志措辞逐字搬来。
    /// </summary>
    internal static void DeclarePlatformCatalog(AppSettings settings)
    {
        settings.DeclaredPlatformDrivers = ImmutableArray.Create(
            new DriverKey(PlatformId.QqPrivate, AccountScope.Legacy, string.Empty),
            new DriverKey(PlatformId.QqOfficial, AccountScope.Legacy, string.Empty),
            new DriverKey(PlatformId.Local, AccountScope.Legacy, string.Empty),
            new DriverKey(PlatformId.Feishu, AccountScope.Default, "feishu-main"));
        settings.DeclaredPlatformActions = QqActionCatalog.All.Select(spec => new ActionId(spec.Name)).ToImmutableArray();
    }

    /// <summary>
    /// 装配容器：**只由 <see cref="Build"/> 内部使用**，进程里不会有第二个实例。
    ///
    /// 规则只有三条（见 docs/decisions/0001-composition-root-assembly-split.md）：
    /// ① 每个成员只有一个阶段写入（写入点 grep 得到）；
    /// ② 阶段只读自己声明的上游成员 —— 顺序写在 <see cref="Build"/> 的八行里；
    /// ③ <see cref="Complete"/> 按依赖顺序判空，漏写立刻抛 <c>assembly_incomplete:&lt;阶段&gt;.&lt;成员&gt;</c>，
    ///    而不是让某个下游组件在几百毫秒之后才炸。
    ///
    /// 这里存的是**已经造好的实例**，不是延迟工厂：构造期的副作用（订阅 Changed、载入心情、注册平台目录）
    /// 仍然发生在 <see cref="Build"/> 返回之前，与拆分前完全一致。
    /// </summary>
    private sealed class Assembly(AppSettings settings)
    {
        public AppSettings Settings { get; set; } = settings;

        // ① AssembleCore：配置发布点与全进程共用的无状态服务
        public SettingsBox Box { get; set; } = null!;
        public StorageSecrets Secrets { get; set; } = null!;
        public SettingsStore SettingsStore { get; set; } = null!;
        public ModelProviderStore ProviderStore { get; set; } = null!;
        public SettingsHotReload.ConfigurationRequestAuthorization Authorization { get; set; } = null!;
        public ModelProviderRegistry ModelRegistry { get; set; } = null!;
        public PanelNotifier Ui { get; set; } = null!;
        public TenantQuotaStore Quotas { get; set; } = null!;
        public PluginRuntime PluginRuntime { get; set; } = null!;
        public Func<Microsoft.Data.Sqlite.SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> ReadRegistered { get; set; } = null!;
        public SettingsStore.ConfigurationParticipant Participant { get; set; } = null!;
        public ConfigurationModule.ConfigurationStore Configuration { get; set; } = null!;
        public ProtocolRiskBackoff RiskBackoff { get; set; } = null!;
        public FeishuWebhookDedupStore InboundDedup { get; set; } = null!;

        // ② AssembleChannels：协议端与平台策略
        public OneBotGateway Gateway { get; set; } = null!;
        public IQqChatSource Source { get; set; } = null!;
        public OfficialBotGateway? Official { get; set; }
        public LocalChannelSource? Local { get; set; }
        public FeishuBotGateway? Feishu { get; set; }
        public IPlatformRegistry PlatformRegistry { get; set; } = null!;
        public PlatformDriverOperations DriverOperations { get; set; } = null!;
        public PlatformPolicyResolver PlatformPolicies { get; set; } = null!;

        // ③ AssembleModel：模型客户端与媒体件
        public ToolCircuitBreaker TtsBreaker { get; set; } = null!;
        public ToolCircuitBreaker SearchBreaker { get; set; } = null!;
        public OpenAiClient Brain { get; set; } = null!;
        public ConversationStore Store { get; set; } = null!;
        public MemberProfileStore Profiles { get; set; } = null!;
        public StickerService Stickers { get; set; } = null!;
        public VoiceUseCase Voice { get; set; } = null!;
        public MusicUseCase Music { get; set; } = null!;
        public LinkPreviewer Links { get; set; } = null!;
        public ResearchUseCase Research { get; set; } = null!;

        // ④ AssembleStores：用例层共享状态与各域用例
        public BotIdentity Identity { get; set; } = null!;
        public BotLifetime Lifetime { get; set; } = null!;
        public WhitelistGate Whitelist { get; set; } = null!;
        public ConversationRegistry Registry { get; set; } = null!;
        public OwnMessageLedger OwnLedger { get; set; } = null!;
        public MoodStore Mood { get; set; } = null!;
        public ThrottledLog Throttled { get; set; } = null!;
        public TurnTraceStore Traces { get; set; } = null!;
        public AuditLogStore Audit { get; set; } = null!;
        public McpWiring Mcp { get; set; } = null!;
        public PlainSender Plain { get; set; } = null!;
        public VibeTracker Vibes { get; set; } = null!;
        public MemberRoleUseCase Roles { get; set; } = null!;
        public ParticipationUseCase Participation { get; set; } = null!;
        public PokeUseCase Poke { get; set; } = null!;
        public SessionPolicyLedger SessionPolicies { get; set; } = null!;
        public ApprovalUseCase Approvals { get; set; } = null!;

        // ⑤ AssembleTools：插件门禁、agent 命令、后台评估队列
        public PluginManager Plugins { get; set; } = null!;
        public PluginGate PluginGate { get; set; } = null!;
        public AgentSessionStore Sessions { get; set; } = null!;
        public ServerAgentRunner ServerAgent { get; set; } = null!;
        public AgentCommandService AgentCmds { get; set; } = null!;
        public BackgroundEvaluatorQueue Background { get; set; } = null!;

        // ⑥ AssembleReply：回复主链、调度、热更新扇出、宿主
        public ReplyPipeline Reply { get; set; } = null!;
        public BotScheduler Scheduler { get; set; } = null!;
        public SettingsHotReload HotReload { get; set; } = null!;
        public BotAgentHost Agent { get; set; } = null!;
        public BootReport BootReport { get; set; } = null!;

        // ⑦ AssembleSurface：面板与 ZBA（最后暴露出去的那一层）
        public LoginQrService LoginQr { get; set; } = null!;
        public DatabaseBackupService Backups { get; set; } = null!;
        public HostMetrics HostFacts { get; set; } = null!;
        public HealthReportService HealthReports { get; set; } = null!;
        public Func<IReadOnlyList<Domain.Ops.CircuitStatusSnapshot>> CircuitStatus { get; set; } = null!;
        public WebUiServer Web { get; set; } = null!;
        public ManagedCapabilityBinding Management { get; set; } = null!;
        public ZbaRuntime? Zba { get; set; }

        public AppGraph Complete()
        {
            Require(Settings, "Core.Settings");
            Require(Box, "Core.SettingsBox");
            Require(Secrets, "Core.Secrets");
            Require(SettingsStore, "Core.SettingsStore");
            Require(ProviderStore, "Core.ProviderStore");
            Require(Authorization, "Core.Authorization");
            Require(ModelRegistry, "Core.ModelRegistry");
            Require(Ui, "Core.Ui");
            Require(Quotas, "Core.Quotas");
            Require(PluginRuntime, "Core.PluginRuntime");
            Require(ReadRegistered, "Core.ReadRegistered");
            Require(Participant, "Core.Participant");
            Require(Configuration, "Core.Configuration");
            Require(RiskBackoff, "Core.RiskBackoff");
            Require(InboundDedup, "Core.InboundDedup");
            Require(Gateway, "Channels.Gateway");
            Require(Source, "Channels.Source");
            Require(PlatformRegistry, "Channels.PlatformRegistry");
            Require(DriverOperations, "Channels.DriverOperations");
            Require(PlatformPolicies, "Channels.PlatformPolicies");
            Require(TtsBreaker, "Model.TtsBreaker");
            Require(SearchBreaker, "Model.SearchBreaker");
            Require(Brain, "Model.Brain");
            Require(Store, "Model.Store");
            Require(Profiles, "Model.Profiles");
            Require(Stickers, "Model.Stickers");
            Require(Voice, "Model.Voice");
            Require(Music, "Model.Music");
            Require(Links, "Model.Links");
            Require(Research, "Model.Research");
            Require(Identity, "Stores.Identity");
            Require(Lifetime, "Stores.Lifetime");
            Require(Whitelist, "Stores.Whitelist");
            Require(Registry, "Stores.Registry");
            Require(OwnLedger, "Stores.OwnLedger");
            Require(Mood, "Stores.Mood");
            Require(Throttled, "Stores.Throttled");
            Require(Traces, "Stores.Traces");
            Require(Audit, "Stores.Audit");
            Require(Mcp, "Stores.Mcp");
            Require(Plain, "Stores.Plain");
            Require(Vibes, "Stores.Vibes");
            Require(Roles, "Stores.Roles");
            Require(Participation, "Stores.Participation");
            Require(Poke, "Stores.Poke");
            Require(SessionPolicies, "Stores.SessionPolicies");
            Require(Approvals, "Stores.Approvals");
            Require(Plugins, "Tools.Plugins");
            Require(PluginGate, "Tools.PluginGate");
            Require(Sessions, "Tools.Sessions");
            Require(ServerAgent, "Tools.ServerAgent");
            Require(AgentCmds, "Tools.AgentCmds");
            Require(Background, "Tools.Background");
            Require(Reply, "Reply.Reply");
            Require(Scheduler, "Reply.Scheduler");
            Require(HotReload, "Reply.HotReload");
            Require(Agent, "Reply.Agent");
            Require(BootReport, "Reply.BootReport");
            Require(LoginQr, "Surface.LoginQr");
            Require(Backups, "Surface.Backups");
            Require(HostFacts, "Surface.HostFacts");
            Require(HealthReports, "Surface.HealthReports");
            Require(CircuitStatus, "Surface.CircuitStatus");
            Require(Web, "Surface.Web");
            Require(Management, "Surface.Management");
            return new AppGraph(Settings, Box, Source, Gateway, Official, Brain, Agent, BootReport, LoginQr,
                HealthReports, Backups, Web, PluginRuntime, Background, DriverOperations, Management, Mcp.Runtime, Zba);
        }

        private static void Require(object? value, string member)
        {
            if (value is null) throw new InvalidOperationException("assembly_incomplete:" + member);
        }
    }
}

using BotAgent.Adapters.Agent;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Runtime;
using BotAgent.Panel;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Permissions;
using BotAgent.Services.Platforms;
using BotAgent.Services.Settings;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>ZBA 的组合根：选项来自环境变量，后端只接宿主已有的端口。</summary>
internal sealed class ZbaRuntime : IAsyncDisposable
{
    private readonly ZbaHostBackend _backend;

    private ZbaRuntime(ZbaEndpoint endpoint, ZbaHostBackend backend)
    {
        Endpoint = endpoint;
        _backend = backend;
    }

    internal ZbaEndpoint Endpoint { get; }
    internal IZbaBackend Backend => _backend;

    internal static ZbaRuntime Create(
        SettingsBox box,
        SettingsHotReload hotReload,
        IManagedCapabilities capabilities,
        IAgentSessionStore sessions,
        AgentCommandService agent,
        ApprovalUseCase? approvals,
        IPluginRegistry? plugins,
        ConversationRegistry registry,
        Func<DateTimeOffset> now,
        bool hadStoredConfigurationBeforeStartup,
        BotAgent.Adapters.Plugins.PluginMarketService? market = null,
        Func<string, BotAgent.Domain.Model.ModelProviderRoute?>? providerRoute = null,
        BotAgent.Model.ProviderModelDiscovery? discovery = null,
        IQuotaOperations? quotas = null,
        BotAgent.Services.Ops.DatabaseBackupService? backups = null,
        BotAgent.Services.Ops.TurnTraceStore? traces = null,
        IPlatformPolicyResolver? platformPolicies = null, ZbaControlPlane? controlPlane = null)
    {
        var context = controlPlane?.Context ?? ZbaRuntimeContext.Create(now, hadStoredConfigurationBeforeStartup);
        var options = context.Options;
        var version = context.Version;
        var instanceId = context.InstanceId;
        var events = context.Events;
        var taskReceipts = context.TaskReceipts;
        var dto = new PanelDto(box, registry);
        Func<bool> serverEnabled = () => box.Current.EnableServerAgent;
        Action<string> log = message => FileLog.Write("Zba", message);
        var quotaStore = quotas;
        // 保留原后端构造时序：参数先求值，再采样启动时间，最后启动任务台账。
        var startedAt = context.StartedAt;
        // 执行与状态在 Engine 的任务运行时（ADR-0002）；registry 只做通道适配（幂等/回执/事件/结果投影）。
        // 台账（#116）在这里注入：运行时只管写，SQLite 细节留在 Adapters/Persistence。
        var ledger = new AgentTaskLedgerStore();
        ledger.Prune(0);
        // #137：任务级前置审批闸门。审批总开关关掉时它返回 null，不会凭空多出待批任务。
        var approvalGate = new TaskApprovalGate(now, () => box.Current.EnableApprovals,
            () => box.Current, () => DockerAvailability.IsAvailable, agent.CaptureTaskPythonLeases);
        var tasks = new ZbaTaskRegistry(sessions,
            new AgentTaskRuntime(
                new AgentTaskExecutor(agent), now, log: log, ledger: ledger, bootSeq: ledger.BootSeq,
                approvalGate: approvalGate),
            now, serverEnabled, events, taskReceipts, instanceId, log,
            request => approvalGate.Prepare(request, agent.GetTaskMcpToolNames()));
        var backend = new ZbaHostBackend(
            box,
            hotReload,
            capabilities,
            sessions,
            tasks,
            approvals,
            plugins,
            dto,
            serverEnabled,
            now,
            startedAt,
            version,
            events,
            log,
            market,
            providerRoute,
            discovery,
            registry,
            quotaStore,
            backups,
            traces,
            platformPolicies);
        var endpoint = controlPlane?.Endpoint ?? new ZbaEndpoint(backend, options, now, version, instanceId,
            message => FileLog.Write("Zba", message));
        return new ZbaRuntime(endpoint, backend);
    }

    public ValueTask DisposeAsync() => _backend.DisposeAsync();
}

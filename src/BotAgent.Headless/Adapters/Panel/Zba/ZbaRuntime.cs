using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Panel;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Permissions;
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
        TenantQuotaStore? quotas = null)
    {
        var options = FromEnvironment();
        var version = typeof(ZbaRuntime).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
        // 每个进程一个实例标识：回执用它区分“本进程淘汰”与“上一进程受理”，meta 用它让客户端发现重启。
        var instanceId = "zi-" + now().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                         + "-" + Guid.NewGuid().ToString("N")[..8];
        var events = new ZbaEventBuffer(new ZbaEventSequenceStore(
            Path.Combine(AppPaths.DataDir, "zba-event-sequence"), now,
            message => FileLog.Write("Zba", message)), hadStoredConfigurationBeforeStartup);
        var taskReceipts = new ZbaTaskReceiptStore(
            Path.Combine(AppPaths.DataDir, "zba-task-receipts.json"), now,
            message => FileLog.Write("Zba", message));
        var backend = new ZbaHostBackend(
            box,
            hotReload,
            capabilities,
            sessions,
            agent,
            approvals,
            plugins,
            new PanelDto(box, registry),
            () => box.Current.EnableServerAgent,
            now,
            version,
            events,
            taskReceipts,
            instanceId,
            message => FileLog.Write("Zba", message),
            market,
            providerRoute,
            discovery,
            registry,
            quotas ?? new TenantQuotaStore());
        var endpoint = new ZbaEndpoint(backend, options, now, version, instanceId,
            message => FileLog.Write("Zba", message));
        return new ZbaRuntime(endpoint, backend);
    }

    private static ZbaOptions FromEnvironment()
    {
        var admin = Clean(EnvironmentAliases.Get("BOTAGENT_ZBA_TOKEN", "QQCHAT_ZBA_TOKEN"));
        var read = Clean(EnvironmentAliases.Get("BOTAGENT_ZBA_READ_TOKEN", "QQCHAT_ZBA_READ_TOKEN"));
        var allowLoopback = EnvironmentAliases.Get("BOTAGENT_ZBA_ALLOW_LOOPBACK", "QQCHAT_ZBA_ALLOW_LOOPBACK")
            ?.Trim() is "1" or "true" or "TRUE" or "True";
        FileLog.Write("Zba", admin is null && read is null && !allowLoopback
            ? "[Zba] 未配置 bearer；管理端点 fail-closed（503），仅 /zba/v1/health 可读"
            : "[Zba] v1 已挂载（bearer；写路径需要 admin）");
        return new ZbaOptions
        {
            AdminToken = admin,
            ReadToken = read,
            AllowLoopbackWithoutToken = allowLoopback
        };
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public ValueTask DisposeAsync() => _backend.DisposeAsync();
}

extern alias StorageModule;

using System.Collections.Immutable;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Panel;
using BotAgent.Services;
using BotAgent.Services.Settings;
using ConfigurationStore = StorageModule::BotAgent.Storage.ConfigurationStore;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>Authenticated recovery remains available before and without a business backend.</summary>
internal sealed partial class ZbaControlBackend : IZbaBackend, IZbaHttpContributions
{
    public IReadOnlyList<BotAgent.Domain.Plugins.PluginHttpBinding<ConfigurationCaller>> HttpContributions =>
    [
        QuotaHttpContract.Bind<ConfigurationCaller>(ListQuotasAsync, SaveQuotaAsync, FindQuotaAsync),
        McpHttpContract.Bind<ConfigurationCaller>(
            (c, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ListMcpServersAsync(c, ct)
                ?? throw new ZbaBackendException(503, "mcp_management_unavailable", "MCP 管理不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.SaveMcpServerAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "mcp_management_unavailable", "MCP 管理不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.DeleteMcpServerAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "mcp_management_unavailable", "MCP 管理不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.TestMcpServerAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "mcp_management_unavailable", "MCP 管理不可用。"),
            (c, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ReloadMcpServersAsync(c, ct)
                ?? throw new ZbaBackendException(503, "mcp_management_unavailable", "MCP 管理不可用。")),
        AccessRoleHttpContract.Bind<ConfigurationCaller>(
            (c, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ListAccessRolesAsync(c, ct)
                ?? throw new ZbaBackendException(503, "access_roles_unavailable", "权限绑定管理不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.SaveAccessRolesAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "access_roles_unavailable", "权限绑定管理不可用。")),
        SchedulerHttpContract.Bind<ConfigurationCaller>(
            (c, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ListScheduledJobsAsync(c, ct)
                ?? throw new ZbaBackendException(503, "scheduler_unavailable", "定时引擎不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.SaveScheduledJobAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "scheduler_unavailable", "定时引擎不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.DeleteScheduledJobAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "scheduler_unavailable", "定时引擎不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ListScheduledRunsAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "scheduler_unavailable", "定时引擎不可用。")),
        PersonalTodoHttpContract.Bind<ConfigurationCaller>(
            (c, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ListPersonalTodosAsync(c, ct)
                ?? throw new ZbaBackendException(503, "personal_todo_unavailable", "个人待办不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.SavePersonalTodoAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "personal_todo_unavailable", "个人待办不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.DeletePersonalTodoAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "personal_todo_unavailable", "个人待办不可用。")),
        WorkspaceHttpContract.Bind<ConfigurationCaller>(
            (c, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ReadWorkspaceMessagesAsync(c, ct)
                ?? throw new ZbaBackendException(503, "workspace_unavailable", "个人工作台不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.SendWorkspaceMessageAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "workspace_unavailable", "个人工作台不可用。"),
            (c, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ClearWorkspaceMessagesAsync(c, ct)
                ?? throw new ZbaBackendException(503, "workspace_unavailable", "个人工作台不可用。"),
            (c, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ReadWorkspaceIdentityAsync(c, ct)
                ?? throw new ZbaBackendException(503, "workspace_unavailable", "个人工作台不可用。"),
            (c, req, ct) => (RequireBusiness(ct) as ZbaHostBackend)?.ToggleWorkspaceIdentityAsync(c, req, ct)
                ?? throw new ZbaBackendException(503, "workspace_unavailable", "个人工作台不可用。"))
    ];

    private readonly ConfigurationStore _configuration;
    private readonly SettingsHotReload.ConfigurationRequestAuthorization _authorization;
    private readonly IManagedAuthorization _managedAuthorization;
    private readonly ZbaRuntimeContext _context;
    private readonly Func<DateTimeOffset> _now;
    private readonly ZbaPluginManagement _pluginManagement;
    private IZbaBackend? _business;
    private int _starting = 1;

    internal ZbaControlBackend(ConfigurationStore configuration,
        SettingsHotReload.ConfigurationRequestAuthorization authorization, IManagedAuthorization managedAuthorization,
        PluginMarketService market, ZbaRuntimeContext context, Func<DateTimeOffset> now)
    {
        _configuration = configuration;
        _authorization = authorization;
        _managedAuthorization = managedAuthorization;
        _context = context;
        _now = now;
        _pluginManagement = new(market, Authorize, ReadConfigurationAsync, SaveConfigurationAsync);
    }

    internal void CompleteStartup(IZbaBackend? business)
    {
        if (business is not null && Interlocked.CompareExchange(ref _business, business, null) is not null)
            throw new InvalidOperationException("business_backend_already_attached");
        Volatile.Write(ref _starting, 0);
    }

    internal void DetachBusiness() => Interlocked.Exchange(ref _business, null);

    private IZbaBackend RequireBusiness(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Volatile.Read(ref _business)
            ?? throw new ZbaBackendException(503, "business_unavailable", "业务组件不可用；管理与原操作键查询仍可使用。");
    }

    private IDisposable Authorize(ConfigurationCaller caller, ConfigurationAccess required)
        => Authorize(caller, required, allowDuringStartup: false);

    private IDisposable Authorize(ConfigurationCaller caller, ConfigurationAccess required, bool allowDuringStartup)
    {
        if (caller.AuthorityId != "zba" || caller.ResourceId != "bot" || (caller.Access & required) != required)
            throw new ZbaBackendException(403, "forbidden", "管理权限不足。");
        if (!allowDuringStartup && Volatile.Read(ref _starting) != 0 && required == ConfigurationAccess.SaveConfiguration)
            throw new ZbaBackendException(503, "host_starting", "宿主正在组装，稍后重试。");
        return _authorization.Enter(caller,
            () => caller.AuthorityId == "zba" && caller.ResourceId == "bot" && (caller.Access & required) == required);
    }

    public ValueTask<ZbaHealth> GetHealthAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var business = Volatile.Read(ref _business);
        return business is not null ? business.GetHealthAsync(ct)
            : ValueTask.FromResult(new ZbaHealth("degraded", false, false, ZbaContract.Version, _now()));
    }

    public async ValueTask<ZbaStatus> GetStatusAsync(ConfigurationCaller caller, CancellationToken ct = default)
    {
        var business = Volatile.Read(ref _business);
        if (business is not null) return await business.GetStatusAsync(caller, ct).ConfigureAwait(false);
        var configuration = await ReadConfigurationAsync(caller, ConfigurationReadKind.Overview, ct).ConfigureAwait(false);
        var now = _now();
        return new ZbaStatus("degraded", false, _context.Version, _context.StartedAt, now,
            configuration.Code == ConfigurationResultCode.Ok ? configuration.Version.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            configuration.LatestApplyState.ToString().ToLowerInvariant(),
            [new ZbaComponent("control-plane", "subsystem", "management", "running", true, null, now),
             new ZbaComponent("business", "subsystem", "business", "notloaded", false,
                 Volatile.Read(ref _starting) != 0 ? "host_starting" : "business_unavailable", now)],
            _context.Events.RecentErrors());
    }

    public async ValueTask<ConfigurationReadResult> ReadConfigurationAsync(
        ConfigurationCaller caller, ConfigurationReadKind kind, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, kind == ConfigurationReadKind.Editable
            ? ConfigurationAccess.ReadEditable : ConfigurationAccess.ReadOverview);
        return await _configuration.ReadAsync(caller, kind, ct).ConfigureAwait(false);
    }

    public async ValueTask<ConfigurationSaveResult> SaveConfigurationAsync(
        ConfigurationCaller caller, ZbaConfigurationSaveRequest request, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        var business = Volatile.Read(ref _business);
        if (business is not null) return await business.SaveConfigurationAsync(caller, request, ct).ConfigureAwait(false);
        var (converted, owned) = ZbaSecretEdits.ConvertSecrets(request.Secrets);
        try
        {
            var result = await _configuration.SaveAsync(caller,
                new(request.ContractRevision, request.ExpectedVersion, request.Key, request.Changes, converted), ct).ConfigureAwait(false);
            if (result.Code == ConfigurationResultCode.Ok && !result.Replayed && result.Receipt?.CommitState == CommitState.Committed)
            {
                var reports = Enum.GetValues<ApplyStep>().Select(step => new ApplyStepReport(step, ApplyState.RestartRequired, "restart_required")).ToImmutableArray();
                var recorded = _configuration.RecordApply(caller, request.Key, reports);
                result = result with { Receipt = recorded.Receipt ?? result.Receipt with { ApplyState = ApplyState.Unknown } };
            }
            ZbaConfigurationEvents.Append(_context.Events, result, _now());
            return result;
        }
        finally { foreach (var input in owned) input.Dispose(); }
    }

    public async ValueTask<ConfigurationFindResult> FindConfigurationAsync(
        ConfigurationCaller caller, ConfigurationOperationKey key, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration, allowDuringStartup: true);
        return await _configuration.FindAsync(caller, key, ct).ConfigureAwait(false);
    }

    public ValueTask<ManagedOperationResult> QueryCapabilityAsync(ConfigurationCaller caller, ManagedOperationKey key, CancellationToken ct = default)
        => _configuration.QueryManagedAsync(caller, key, _managedAuthorization, ct);

    public async ValueTask<ManagedValidationResult> ValidateCapabilityAsync(
        ConfigurationCaller caller, ZbaConfigurationValidationRequest request, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ValidateConfiguration);
        var (converted, owned) = ZbaSecretEdits.ConvertSecrets(request.Secrets);
        try
        {
            return await _configuration.ValidateAsync(caller,
                new(request.ContractRevision, request.ExpectedVersion, request.Changes, converted), ct).ConfigureAwait(false);
        }
        finally { foreach (var input in owned) input.Dispose(); }
    }

    public ValueTask<ZbaTaskFindResult> FindTaskAsync(
        ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        var business = Volatile.Read(ref _business);
        if (business is not null) return business.FindTaskAsync(caller, idempotencyKey, ct);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_context.TaskReceipts.FindRecovery(idempotencyKey, _context.InstanceId));
    }

    public ValueTask<ZbaEventPage> ReadEventsAsync(ConfigurationCaller caller, long after, int limit, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_context.Events.Read(after, limit, _now()));
    }

    public ValueTask<ZbaLogFeedPage> ReadLogFeedAsync(ConfigurationCaller caller, ZbaLogFeedCursor? cursor, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(FileLog.ReadRuntimeFeed(cursor));
    }

    public ValueTask<ZbaLogPage> ListLogsAsync(ConfigurationCaller caller, int limit, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        var bounded = Math.Clamp(limit, 1, ZbaContract.MaxLogEntries);
        return ValueTask.FromResult(RuntimeLogProjection.Project(FileLog.RecentRuntimeLines(bounded), bounded));
    }
}

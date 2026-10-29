using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Panel;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Permissions;
using BotAgent.Services.Settings;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>
/// ZBA 的宿主侧实现：只读投影来自 SettingsBox / 管理能力目录；写路径复用已有的
/// 结构化配置事务与管理动作回执；任务/审批/事件在宿主边界内收口。
/// </summary>
internal sealed partial class ZbaHostBackend : IZbaBackend, IAsyncDisposable
{
    private readonly SettingsBox _box;
    private readonly SettingsHotReload _hotReload;
    private readonly IManagedCapabilities _capabilities;
    private readonly IAgentSessionStore _sessions;
    private readonly Func<bool> _serverEnabled;
    private readonly ApprovalUseCase? _approvals;
    private readonly IPluginRegistry? _plugins;
    private readonly PanelDto _dto;
    private readonly Func<DateTimeOffset> _now;
    private readonly DateTimeOffset _startedAt;
    private readonly string _version;
    private readonly ZbaEventBuffer _events;
    private readonly ZbaTaskRegistry _tasks;
    private readonly Action<string>? _log;
    private int _disposed;

    internal ZbaHostBackend(
        SettingsBox box,
        SettingsHotReload hotReload,
        IManagedCapabilities capabilities,
        IAgentSessionStore sessions,
        AgentCommandService agent,
        ApprovalUseCase? approvals,
        IPluginRegistry? plugins,
        PanelDto dto,
        Func<bool> serverEnabled,
        Func<DateTimeOffset> now,
        string version,
        ZbaEventBuffer events,
        ZbaTaskReceiptStore taskReceipts,
        string instanceId,
        Action<string>? log = null,
        BotAgent.Adapters.Plugins.PluginMarketService? market = null,
        Func<string, BotAgent.Domain.Model.ModelProviderRoute?>? providerRoute = null,
        BotAgent.Model.ProviderModelDiscovery? discovery = null)
    {
        _box = box;
        _hotReload = hotReload;
        _capabilities = capabilities;
        _sessions = sessions;
        _serverEnabled = serverEnabled;
        _approvals = approvals;
        _plugins = plugins;
        _dto = dto;
        _now = now;
        _startedAt = now();
        _version = version;
        _events = events;
        _log = log;
        _market = market;
        _providerRoute = providerRoute;
        _discovery = discovery;
        _tasks = new ZbaTaskRegistry(sessions, agent, now, serverEnabled, events, taskReceipts, instanceId, log);
    }

    internal ZbaTaskRegistry Tasks => _tasks;

    public ValueTask<ZbaHealth> GetHealthAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var disposed = Volatile.Read(ref _disposed) != 0;
        return ValueTask.FromResult(new ZbaHealth(
            disposed ? "unhealthy" : "ok",
            Ready: !disposed,
            Configured: false,
            ZbaContract.Version,
            _now()));
    }

    public async ValueTask<ZbaStatus> GetStatusAsync(ConfigurationCaller caller, CancellationToken ct = default)
    {
        var now = _now();
        var components = new List<ZbaComponent>();
        ManagedPage page;
        try
        {
            page = await _capabilities.ListAsync(caller,
                new ManagedPageRequest(null, ZbaContract.MaxCapabilityPageSize, null), ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            page = new ManagedPage(ManagedResultCode.Unknown, ImmutableArray<ManagedDescriptor>.Empty, null, string.Empty);
            _events.AppendError("capabilities", exception.GetType().Name, now);
        }

        if (page.Code != ManagedResultCode.Ok)
        {
            components.Add(new ZbaComponent("capabilities", "subsystem", "capability-catalog",
                "faulted", false, page.Code.ToString(), now));
        }
        else
        {
            foreach (var descriptor in page.Items) components.Add(Map(descriptor, now));
        }

        if (_plugins is not null)
        {
            var seen = components.Select(component => component.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var plugin in _plugins.GetAll())
            {
                var id = "plugin:" + plugin.Id;
                if (!seen.Add(id)) continue;
                components.Add(new ZbaComponent(id, "plugin", "plugin",
                    plugin.IsEnabled ? "running" : "disabled", plugin.IsEnabled, null, now));
            }
        }

        var counts = _tasks.Counts();
        components.Add(new ZbaComponent("agent-tasks", "task", "agent-tasks",
            counts.Active > 0 ? "running" : "idle", true, $"{counts.Active}/{counts.Total}", now));

        var configuration = await ReadConfigurationQuietAsync(caller, ct).ConfigureAwait(false);
        var faulted = components.Any(component => component.State is "faulted" or "error");
        var ready = page.Code == ManagedResultCode.Ok && !faulted;
        return new ZbaStatus(
            ready ? "ready" : "degraded",
            ready,
            _version,
            _startedAt,
            now,
            configuration?.Version.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            configuration?.LatestApplyState.ToString().ToLowerInvariant() ?? "unknown",
            components,
            _events.RecentErrors());
    }

    public ValueTask<ZbaSessionPage> ListSessionsAsync(
        ConfigurationCaller caller, string sourceKey, int limit, string? cursor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var offset = 0;
        if (cursor is not null
            && (!int.TryParse(cursor, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out offset) || offset < 0))
        {
            throw new ZbaBackendException(400, "invalid_cursor", "游标无效。");
        }

        var sessions = _sessions.List(sourceKey);
        var items = sessions.Skip(offset).Take(limit)
            .Select(session => Project(sourceKey, session))
            .ToArray();
        var next = offset + items.Length < sessions.Count
            ? (offset + items.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return ValueTask.FromResult(new ZbaSessionPage(sourceKey, items, next));
    }

    public ValueTask<ZbaSessionEnsureResult> EnsureSessionAsync(
        ConfigurationCaller caller, string sourceKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_serverEnabled())
            throw new ZbaBackendException(409, "server_agent_disabled", "服务器内置 agent 未启用。");

        var before = _sessions.FindCurrent(sourceKey, "server");
        var session = _sessions.EnsureCurrent(sourceKey, "server");

        // created 只表示“本次观察到的当前会话变了”，不是强一致事实（并发 ensure 时可被另一个调用者抢先）。
        var created = before is null || before.Id != session.Id;
        return ValueTask.FromResult(new ZbaSessionEnsureResult(Project(sourceKey, session), created));
    }

    private ZbaSessionSummary Project(string sourceKey, AgentSession session) => new(
        session.Id,
        session.Name,
        session.Backend,
        _sessions.IsCurrent(sourceKey, session),
        session.Turns,
        session.History.Sum(entry => entry.Text.Length),
        session.CreatedAt,
        session.UpdatedAt,
        session.AutoNamed);

    public ValueTask<ManagedPage> ListCapabilitiesAsync(
        ConfigurationCaller caller, ManagedPageRequest request, CancellationToken ct = default)
        => _capabilities.ListAsync(caller, request, ct);

    public ValueTask<ManagedInspectResult> InspectCapabilityAsync(
        ConfigurationCaller caller, ManagedObjectKey key, CancellationToken ct = default)
        => _capabilities.InspectAsync(caller, key, ct);

    public async ValueTask<ManagedValidationResult> ValidateCapabilityAsync(
        ConfigurationCaller caller, ZbaConfigurationValidationRequest request, CancellationToken ct = default)
    {
        var (converted, owned) = ConvertSecrets(request.Secrets);
        try
        {
            using var scope = Authorize(caller, ConfigurationAccess.ValidateConfiguration);
            var validation = new ConfigurationValidationRequest(
                request.ContractRevision, request.ExpectedVersion, request.Changes, converted);
            return await _capabilities.ValidateAsync(caller, validation, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var input in owned) input.Dispose();
        }
    }

    public async ValueTask<ManagedOperationResult> ExecuteCapabilityAsync(
        ConfigurationCaller caller, ManagedExecuteRequest request, CancellationToken ct = default)
    {
        var result = await _capabilities.ExecuteAsync(caller, request, ct).ConfigureAwait(false);
        if (result.Code == ManagedResultCode.Ok)
        {
            _events.Append("capability.executed", null, result.Receipt?.State.ToString().ToLowerInvariant(),
                request.ExpectedDescriptorRevision, result.Receipt?.ErrorCode, _now());
        }
        else
        {
            _events.AppendError("capability", result.Code.ToString(), _now());
        }

        return result;
    }

    public ValueTask<ManagedOperationResult> QueryCapabilityAsync(
        ConfigurationCaller caller, ManagedOperationKey key, CancellationToken ct = default)
        => _capabilities.QueryAsync(caller, key, ct);

    public ValueTask<ManagedCancellationResult> CancelCapabilityAsync(
        ConfigurationCaller caller, ManagedOperationKey key, CancellationToken ct = default)
        => _capabilities.CancelAsync(caller, key, ct);

    public async ValueTask<ConfigurationReadResult> ReadConfigurationAsync(
        ConfigurationCaller caller, ConfigurationReadKind kind, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        return await _hotReload.ReadConfigurationAsync(kind, ct).ConfigureAwait(false);
    }

    public async ValueTask<ConfigurationSaveResult> SaveConfigurationAsync(
        ConfigurationCaller caller, ZbaConfigurationSaveRequest request, CancellationToken ct = default)
    {
        var (converted, owned) = ConvertSecrets(request.Secrets);
        try
        {
            using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
            var save = new ConfigurationSaveRequest(
                request.ContractRevision, request.ExpectedVersion, request.Key, request.Changes, converted);
            var result = await _hotReload.SaveConfigurationAsync(save, ct).ConfigureAwait(false);
            if (result.Code is ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges)
            {
                _events.Append("configuration.saved", null, ConfigurationEventState(result),
                    result.CurrentVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    result.Receipt?.ApplyState.ToString().ToLowerInvariant(), _now());
            }
            else
            {
                _events.AppendError("configuration", result.Code.ToString(), _now());
            }

            return result;
        }
        finally
        {
            foreach (var input in owned) input.Dispose();
        }
    }

    public async ValueTask<ConfigurationFindResult> FindConfigurationAsync(
        ConfigurationCaller caller, ConfigurationOperationKey key, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        var outcome = await _hotReload.FindConfigurationAsync(key, ct).ConfigureAwait(false);
        return outcome.ConfigurationResult;
    }

    // 保存成功只说明已提交；事件 state 必须投影真实 ApplyState，不能一律写 applied。
    private static string ConfigurationEventState(ConfigurationSaveResult result)
    {
        if (result.Code == ConfigurationResultCode.NoChanges) return "no_changes";

        return result.Receipt?.ApplyState switch
        {
            ApplyState.PartiallyApplied => "partially_applied",
            ApplyState.RestartRequired => "restart_required",
            ApplyState.Failed => "failed",
            ApplyState.Unknown => "unknown",
            ApplyState.Applied => "applied",
            _ => "not_started"
        };
    }

    public ValueTask<ZbaTaskAccepted> SubmitTaskAsync(
        ConfigurationCaller caller, ZbaTaskSubmission submission, string idempotencyKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_tasks.Submit(submission, idempotencyKey, out _));
    }

    public ValueTask<ZbaTaskPage> ListTasksAsync(
        ConfigurationCaller caller, int limit, string? cursor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_tasks.List(limit, cursor));
    }

    public ValueTask<ZbaTaskSummary?> GetTaskAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_tasks.Get(id));
    }

    public ValueTask<ZbaTaskResult?> GetTaskResultAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_tasks.GetResult(id));
    }

    public ValueTask<ZbaTaskFindResult> FindTaskAsync(
        ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_tasks.Find(idempotencyKey));
    }

    public ValueTask<ZbaTaskCancelResult> CancelTaskAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_tasks.Cancel(id));
    }

    public ValueTask<IReadOnlyList<ZbaApprovalSummary>> ListApprovalsAsync(
        ConfigurationCaller caller, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_approvals is null) return ValueTask.FromResult<IReadOnlyList<ZbaApprovalSummary>>(Array.Empty<ZbaApprovalSummary>());
        var items = _approvals.PendingApprovals()
            .Select(request => new ZbaApprovalSummary(
                request.RequestId,
                request.ToolId,
                request.Summary,
                _dto.Mask(request.ConversationKey),
                request.ExpiresAt,
                request.PolicyVersion))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<ZbaApprovalSummary>>(items);
    }

    public ValueTask<ZbaApprovalDecisionResult> DecideApprovalAsync(
        ConfigurationCaller caller, string requestId, bool approve, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_approvals is null) throw new ZbaBackendException(503, "approvals_unavailable", "审批组件不可用。");
        if (!_box.Current.EnableApprovals) throw new ZbaBackendException(403, "approvals_disabled", "人工审批总开关是关的。");
        var (ok, reason) = _approvals.PanelDecide(requestId, approve);
        _events.Append("approval.decided", null, ok ? (approve ? "approved" : "rejected") : "rejected",
            null, reason, _now());
        if (!ok) _events.AppendError("approval", reason, _now());
        return ValueTask.FromResult(new ZbaApprovalDecisionResult(ok, approve ? "approve" : "reject", reason));
    }

    public ValueTask<ZbaEventPage> ReadEventsAsync(
        ConfigurationCaller caller, long after, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_events.Read(after, limit, _now()));
    }

    private async ValueTask<ConfigurationReadResult?> ReadConfigurationQuietAsync(
        ConfigurationCaller caller, CancellationToken ct)
    {
        try
        {
            using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
            var result = await _hotReload.ReadConfigurationAsync(ConfigurationReadKind.Overview, ct).ConfigureAwait(false);
            return result.Code == ConfigurationResultCode.Ok ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private IDisposable Authorize(ConfigurationCaller caller, ConfigurationAccess required)
        => _hotReload.ConfigurationAuthorization.Enter(caller,
            () => caller.AuthorityId == "zba" && caller.ResourceId == "bot"
                  && (caller.Access & required) == required);

    private static ZbaComponent Map(ManagedDescriptor descriptor, DateTimeOffset now)
    {
        var (id, kind) = descriptor.Key switch
        {
            ManagedObjectKey.Driver driver => (
                $"driver:{driver.Key.PlatformId}/{driver.Key.AccountScope}/{driver.Key.InstanceId}", "driver"),
            ManagedObjectKey.Plugin plugin => ($"plugin:{plugin.Key.Value}", "plugin"),
            ManagedObjectKey.Provider provider => ($"provider:{provider.Key.Value}", "provider"),
            ManagedObjectKey.Device device => ($"device:{device.Key.Value}", "device"),
            ManagedObjectKey.Mcp mcp => ($"mcp:{mcp.RegisteredId}", "mcp"),
            _ => ("unknown", "unknown")
        };
        var available = descriptor.RuntimeState == ManagedRuntimeState.Running
            || descriptor.Key is ManagedObjectKey.Provider && descriptor.CredentialConfigured && descriptor.DesiredEnabled != false;
        var reason = descriptor.ErrorCode;
        if (reason is null && descriptor.CredentialConfigured == false
            && descriptor.Key is not (ManagedObjectKey.Plugin or ManagedObjectKey.Mcp))
            reason = "credential_missing";
        return new ZbaComponent(id, kind, kind, descriptor.RuntimeState.ToString().ToLowerInvariant(),
            available, reason, now);
    }

    private static (ImmutableArray<SecretEdit> Edits, List<SensitiveInput> Owned) ConvertSecrets(
        IReadOnlyList<ZbaSecretEdit>? secrets)
    {
        var owned = new List<SensitiveInput>();
        var edits = ImmutableArray.CreateBuilder<SecretEdit>();
        foreach (var edit in secrets ?? Array.Empty<ZbaSecretEdit>())
        {
            // Core 的 SecretEdit 工厂对非法目标抛 ArgumentException；那会被 ZbaEndpoint 兜成 500。
            // 正常路径上 ZbaRules.ValidateSecrets 已经拦过（400 invalid_secret_slot），
            // 这里只是宿主侧的最后一道防线，保证任何漏网目标仍然是 400 而不是 500。
            if (!IsResolvableSecretTarget(edit))
                throw new ZbaBackendException(400, "invalid_secret_slot", "秘密编辑目标无效。");
            try
            {
                switch (edit.Kind)
                {
                    case SecretEditKind.Replace:
                        var bytes = Encoding.UTF8.GetBytes(edit.Value ?? string.Empty);
                        try
                        {
                            var input = SensitiveInput.FromUtf8(bytes);
                            owned.Add(input);
                            edits.Add(Replace(edit, input));
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(bytes);
                        }

                        break;
                    case SecretEditKind.Delete:
                        edits.Add(Delete(edit));
                        break;
                    default:
                        edits.Add(Keep(edit));
                        break;
                }
            }
            catch (ArgumentException)
            {
                throw new ZbaBackendException(400, "invalid_secret_slot", "秘密编辑目标无效。");
            }
        }

        return (edits.ToImmutable(), owned);
    }

    /// <summary>目标必须恰好是固定槽位或已登记凭据键之一，且自身合法。</summary>
    private static bool IsResolvableSecretTarget(ZbaSecretEdit edit)
    {
        var hasSlot = edit.Slot is not null;
        var hasKey = edit.CredentialKey is not null;
        if (hasSlot == hasKey) return false;
        if (edit.Slot is { } slot) return Enum.IsDefined(slot);
        var key = edit.CredentialKey!;
        return !string.IsNullOrWhiteSpace(key) && key.Length <= 128 && key.Trim() == key && !key.Contains('\0');
    }

    /// <summary>有 <c>credentialKey</c> 时走 Core 的已登记凭据重载，否则维持固定槽位语义。</summary>
    private static SecretEdit Replace(ZbaSecretEdit edit, SensitiveInput input)
        => edit.CredentialKey is { } key
            ? SecretEdit.Replace(new ModelCredentialKey(key), input)
            : SecretEdit.Replace(edit.Slot!.Value, input);

    private static SecretEdit Delete(ZbaSecretEdit edit)
        => edit.CredentialKey is { } key
            ? SecretEdit.Delete(new ModelCredentialKey(key))
            : SecretEdit.Delete(edit.Slot!.Value);

    private static SecretEdit Keep(ZbaSecretEdit edit)
        => edit.CredentialKey is { } key
            ? SecretEdit.Keep(new ModelCredentialKey(key))
            : SecretEdit.Keep(edit.Slot!.Value);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _tasks.DisposeAsync().ConfigureAwait(false);
    }
}

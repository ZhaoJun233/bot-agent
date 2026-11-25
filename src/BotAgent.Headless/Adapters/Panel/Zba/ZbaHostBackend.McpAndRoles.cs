extern alias StorageModule;

using BotAgent.Domain.Configuration;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Plugins;
using BotAgent.Host;
using BotAgent.Panel;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Permissions;
using StorageMcpStore = StorageModule::BotAgent.Storage.McpServerStore;
using StorageMcpMutation = StorageModule::BotAgent.Storage.ConsoleMcpServerMutation;
using StorageAccessRoleStore = StorageModule::BotAgent.Storage.AccessRoleStore;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaHostBackend
{
    private McpManagedWiring? _mcpManaged;
    private StorageMcpStore? _mcpStore;
    private StorageAccessRoleStore? _roleStore;
    private AccessRoleResolver? _roleResolver;

    internal void AttachMcpAndRoles(
        McpManagedWiring? mcpManaged,
        StorageMcpStore? mcpStore,
        StorageAccessRoleStore? roleStore,
        AccessRoleResolver? roleResolver)
    {
        _mcpManaged = mcpManaged;
        _mcpStore = mcpStore;
        _roleStore = roleStore;
        _roleResolver = roleResolver;
    }

    internal ValueTask<ZbaMcpServersResponse> ListMcpServersAsync(ConfigurationCaller caller, CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        if (_mcpManaged is null || _mcpStore is null)
            throw new PluginHttpException(503, "mcp_management_unavailable");
        var redactEndpoint = (caller.Access & ConfigurationAccess.SaveConfiguration) == 0;
        return ValueTask.FromResult(BuildMcpServersResponse(_mcpManaged, _mcpStore, redactEndpoint));
    }

    internal async ValueTask<ZbaMcpServerWriteResult> SaveMcpServerAsync(
        ConfigurationCaller caller,
        ZbaMcpServerSaveRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_mcpManaged is null || _mcpStore is null)
            throw new PluginHttpException(503, "mcp_management_unavailable");
        if (request is null)
            throw new PluginHttpException(400, "invalid_request");

        var id = (request.Id ?? string.Empty).Trim();
        if (!StorageMcpStore.ValidId(id))
            throw new PluginHttpException(400, "invalid_server_id");
        var envServers = _mcpManaged.ListEnvServers();
        if (envServers.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new PluginHttpException(409, "env_server_locked");

        var currentConsole = _mcpStore.Load();
        if (!currentConsole.Servers.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal))
            && envServers.Count + currentConsole.Servers.Length >= BotAgent.Domain.Mcp.McpLimits.MaxServers)
        {
            throw new PluginHttpException(400, "mcp_server_limit_exceeded");
        }

        var urlCheck = _mcpManaged.ValidateEndpoint(request.Endpoint);
        if (!urlCheck.Ok)
            throw new PluginHttpException(400, "ssrf_endpoint_blocked");

        StorageModule::BotAgent.Storage.McpServerStore.WriteReceipt receipt;
        try
        {
            receipt = _mcpStore.Upsert(
                request.ExpectedRevision,
                request.IdempotencyKey,
                CallerScope(caller),
                new StorageMcpMutation(
                    id,
                    request.Enabled,
                    request.Endpoint,
                    request.AllowedTools ?? Array.Empty<string>(),
                    request.ReadOnlyTools,
                    request.CallTimeoutSeconds,
                    request.MaxResultBytes,
                    request.AuthToken,
                    request.ClearAuthToken),
                _mcpManaged.ValidateEndpoint);
        }
        catch (InvalidOperationException ex) when (ex.Message == "mcp_servers_idempotency_conflict")
        {
            throw new PluginHttpException(409, "idempotency_conflict");
        }
        catch (InvalidOperationException ex) when (ex.Message == "mcp_server_limit_exceeded")
        {
            throw new PluginHttpException(400, "mcp_server_limit_exceeded");
        }

        if (!receipt.Accepted)
        {
            var status = receipt.ErrorCode == "revision_conflict" ? 409 : 400;
            throw new PluginHttpException(status, receipt.ErrorCode ?? "mcp_save_rejected");
        }

        string? postCommitReason = receipt.ErrorCode;
        if (!receipt.Replayed)
        {
            postCommitReason = await SafePostCommitReloadAsync(_mcpManaged, postCommitReason).ConfigureAwait(false);
        }

        return new ZbaMcpServerWriteResult(true, receipt.Replayed, postCommitReason,
            BuildMcpServersResponse(_mcpManaged, _mcpStore));
    }

    internal async ValueTask<ZbaMcpServerWriteResult> DeleteMcpServerAsync(
        ConfigurationCaller caller,
        ZbaMcpServerDeleteRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_mcpManaged is null || _mcpStore is null)
            throw new PluginHttpException(503, "mcp_management_unavailable");
        if (request is null)
            throw new PluginHttpException(400, "invalid_request");

        var id = (request.Id ?? string.Empty).Trim();
        if (!StorageMcpStore.ValidId(id))
            throw new PluginHttpException(400, "invalid_server_id");
        if (_mcpManaged.ListEnvServers().Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new PluginHttpException(409, "env_server_locked");

        StorageModule::BotAgent.Storage.McpServerStore.WriteReceipt receipt;
        try
        {
            receipt = _mcpStore.Delete(request.ExpectedRevision, request.IdempotencyKey, CallerScope(caller), id);
        }
        catch (InvalidOperationException ex) when (ex.Message == "mcp_servers_idempotency_conflict")
        {
            throw new PluginHttpException(409, "idempotency_conflict");
        }

        if (!receipt.Accepted)
        {
            var status = receipt.ErrorCode switch
            {
                "revision_conflict" => 409,
                "server_not_found" => 404,
                _ => 400
            };
            throw new PluginHttpException(status, receipt.ErrorCode ?? "mcp_delete_rejected");
        }

        string? postCommitReason = receipt.ErrorCode;
        if (!receipt.Replayed)
        {
            postCommitReason = await SafePostCommitReloadAsync(_mcpManaged, postCommitReason).ConfigureAwait(false);
        }

        return new ZbaMcpServerWriteResult(true, receipt.Replayed, postCommitReason,
            BuildMcpServersResponse(_mcpManaged, _mcpStore));
    }

    internal async ValueTask<ZbaMcpServerTestResult> TestMcpServerAsync(
        ConfigurationCaller caller,
        ZbaMcpServerTestRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_mcpManaged is null)
            throw new PluginHttpException(503, "mcp_management_unavailable");
        if (request is null)
            throw new PluginHttpException(400, "invalid_request");

        var check = _mcpManaged.ValidateEndpoint(request.Endpoint);
        if (!check.Ok)
            throw new PluginHttpException(400, "ssrf_endpoint_blocked");
        if (!StorageMcpStore.ValidAuthToken(request.AuthToken))
            throw new PluginHttpException(400, "invalid_auth_token");

        var timeoutSec = request.CallTimeoutSeconds <= 0 ? 10 : request.CallTimeoutSeconds;
        var outcome = await _mcpManaged.ProbeAsync(request.Id, request.Endpoint, request.AuthToken, timeoutSec, ct)
            .ConfigureAwait(false);
        return new ZbaMcpServerTestResult(
            outcome.Ok,
            outcome.ReasonCode,
            outcome.Tools.Select(t => new ZbaMcpProbeToolView(t.Name, t.Description, t.ReadOnlyHint)).ToArray());
    }

    internal async ValueTask<ZbaMcpReloadResult> ReloadMcpServersAsync(
        ConfigurationCaller caller,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_mcpManaged is null || _mcpStore is null)
            throw new PluginHttpException(503, "mcp_management_unavailable");

        var count = await _mcpManaged.ReloadAsync(ct).ConfigureAwait(false);
        return new ZbaMcpReloadResult(true, count, BuildMcpServersResponse(_mcpManaged, _mcpStore));
    }

    private static ZbaMcpServersResponse BuildMcpServersResponse(McpManagedWiring wiring, StorageMcpStore store, bool redactEndpoint = false)
    {
        var env = wiring.ListEnvServers();
        var envIds = env.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var consoleSnap = store.Load();
        var servers = new List<ZbaMcpServerView>();
        foreach (var e in env)
        {
            servers.Add(redactEndpoint ? e with { Endpoint = RedactEndpointUrl(e.Endpoint) } : e);
        }
        foreach (var item in consoleSnap.Servers)
        {
            if (envIds.Contains(item.Id)) continue;
            servers.Add(new ZbaMcpServerView(
                item.Id,
                item.Enabled,
                Source: "console",
                ReadOnlyLocked: false,
                Endpoint: redactEndpoint ? RedactEndpointUrl(item.Endpoint) : item.Endpoint,
                AllowedTools: item.AllowedTools.ToArray(),
                ReadOnlyTools: item.ReadOnlyTools.ToArray(),
                CallTimeoutSeconds: item.CallTimeoutSeconds,
                MaxResultBytes: item.MaxResultBytes,
                HasAuthToken: item.HasAuthToken));
        }

        var discovered = wiring.UseCase.Tools.Select(t => new ZbaMcpDiscoveredToolView(
            t.Name, t.QualifiedName, t.ServerId, t.Description, t.ReadOnlyHint, t.ReadOnlyConfirmed)).ToArray();

        return new ZbaMcpServersResponse(
            consoleSnap.Revision,
            wiring.AllowPrivate(),
            servers,
            discovered);
    }

    private static string RedactEndpointUrl(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return endpoint;
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
            return builder.Uri.GetLeftPart(UriPartial.Path);
        }
        var cut = endpoint.IndexOfAny(['?', '#']);
        return cut >= 0 ? endpoint[..cut] : endpoint;
    }

    internal ValueTask<ZbaAccessRolesResponse> ListAccessRolesAsync(ConfigurationCaller caller, CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        if (_roleStore is null)
            throw new PluginHttpException(503, "access_roles_unavailable");
        var includeRaw = (caller.Access & ConfigurationAccess.SaveConfiguration) != 0;
        return ValueTask.FromResult(BuildAccessRolesResponse(_roleStore.Load(), includeRaw));
    }

    internal ValueTask<ZbaAccessRolesSaveResult> SaveAccessRolesAsync(
        ConfigurationCaller caller,
        ZbaAccessRolesSaveRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_roleStore is null || _roleResolver is null)
            throw new PluginHttpException(503, "access_roles_unavailable");
        if (request is null || request.Bindings is null)
            throw new PluginHttpException(400, "invalid_request");
        if (request.Bindings.Any(b => b is null))
            throw new PluginHttpException(400, "invalid_access_role");

        var entries = request.Bindings.Select(b => new AccessRoleBinding(
            new AccessRoleKey(
                b.PlatformId ?? string.Empty,
                b.AccountScope ?? string.Empty,
                b.InstanceId ?? string.Empty,
                string.IsNullOrWhiteSpace(b.ScopeConversationKey) ? null : b.ScopeConversationKey.Trim(),
                b.NativeUserId ?? string.Empty),
            b.Role,
            b.Note)).ToArray();

        StorageAccessRoleStore.SaveReceipt receipt;
        var published = false;
        try
        {
            receipt = _roleStore.Save(
                request.ExpectedRevision,
                request.IdempotencyKey,
                CallerScope(caller),
                entries,
                frozen =>
                {
                    published = true;
                    _roleResolver.ReplaceAll(frozen);
                });
        }
        catch (InvalidOperationException ex) when (ex.Message == "access_roles_idempotency_conflict")
        {
            throw new PluginHttpException(409, "idempotency_conflict");
        }
        catch (InvalidOperationException ex) when (ex.Message == "access_roles_payload_too_large")
        {
            throw new PluginHttpException(400, "access_roles_payload_too_large");
        }

        if (!receipt.Accepted)
        {
            var status = receipt.ErrorCode == "revision_conflict" ? 409 : 400;
            throw new PluginHttpException(status, receipt.ErrorCode ?? "access_roles_save_rejected");
        }

        if (!published && !receipt.Replayed)
            _roleResolver.ReplaceAll(receipt.Snapshot.Bindings);

        return ValueTask.FromResult(new ZbaAccessRolesSaveResult(
            true, receipt.Replayed, receipt.ErrorCode, BuildAccessRolesResponse(receipt.Snapshot, includeRaw: true)));
    }

    private ZbaAccessRolesResponse BuildAccessRolesResponse(StorageAccessRoleStore.Snapshot snapshot, bool includeRaw)
    {
        var masked = _box.Current.AgentMaskSensitive;
        var items = snapshot.Bindings.Select(b => new ZbaAccessRoleItem(
            b.Key.PlatformId,
            b.Key.AccountScope,
            b.Key.InstanceId,
            ScopeConversationKey: b.Key.ScopeConversationKey is { Length: > 0 } k
                ? (masked ? MaskScopeConversationKey(k) : k)
                : null,
            ScopeConversationKeyRaw: includeRaw ? b.Key.ScopeConversationKey : null,
            NativeUserId: masked ? AgentMask.Shorten(b.Key.NativeUserId) : b.Key.NativeUserId,
            NativeUserIdRaw: includeRaw ? b.Key.NativeUserId : null,
            Role: b.Role,
            Note: b.Note is { Length: > 0 } n ? (masked ? AgentMask.Shorten(n) : n) : null,
            NoteRaw: includeRaw ? b.Note : null)).ToArray();
        return new ZbaAccessRolesResponse(snapshot.Revision, masked, items);
    }

    private static string MaskScopeConversationKey(string key)
    {
        var colon = key.LastIndexOf(':');
        return colon >= 0 && colon + 1 < key.Length
            ? string.Concat(key.AsSpan(0, colon + 1), AgentMask.Shorten(key[(colon + 1)..]))
            : AgentMask.Shorten(key);
    }

    private static async Task<string?> SafePostCommitReloadAsync(McpManagedWiring wiring, string? currentReason)
    {
        try
        {
            using var reloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await wiring.ReloadAsync(reloadCts.Token).ConfigureAwait(false);
            return currentReason;
        }
        catch
        {
            return "mcp_reload_failed";
        }
    }
}

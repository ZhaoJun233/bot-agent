using System.Text.Json;
using System.Text;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.ZbaProbe;

internal static class Phase4ZbaChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var serverItems = new List<ZbaMcpServerView>
        {
            new("env-docs", true, "env", ReadOnlyLocked: true, "https://env.example.com/mcp",
                new[] { "lookup" }, new[] { "lookup" }, 15, 16384, HasAuthToken: true)
        };
        var roleItems = new List<ZbaAccessRoleItem>();
        long mcpRev = 0;
        long roleRev = 0;

        var mcpBinding = McpHttpContract.Bind<ConfigurationCaller>(
            (caller, ct) => ValueTask.FromResult(new ZbaMcpServersResponse(mcpRev, false, serverItems.ToArray(), Array.Empty<ZbaMcpDiscoveredToolView>())),
            (caller, req, ct) =>
            {
                if (req.Id == "env-docs") throw new PluginHttpException(409, "env_server_locked");
                if (req.Endpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
                    throw new PluginHttpException(400, "ssrf_endpoint_blocked");
                mcpRev++;
                serverItems.RemoveAll(s => s.Id == req.Id);
                serverItems.Add(new ZbaMcpServerView(
                    req.Id, req.Enabled, "console", false, req.Endpoint,
                    req.AllowedTools, req.ReadOnlyTools ?? Array.Empty<string>(),
                    req.CallTimeoutSeconds, req.MaxResultBytes,
                    HasAuthToken: !req.ClearAuthToken && !string.IsNullOrWhiteSpace(req.AuthToken)));
                var snap = new ZbaMcpServersResponse(mcpRev, false, serverItems.ToArray(), Array.Empty<ZbaMcpDiscoveredToolView>());
                return ValueTask.FromResult(new ZbaMcpServerWriteResult(true, false, null, snap));
            },
            (caller, req, ct) =>
            {
                if (req.Id == "env-docs") throw new PluginHttpException(409, "env_server_locked");
                mcpRev++;
                serverItems.RemoveAll(s => s.Id == req.Id);
                var snap = new ZbaMcpServersResponse(mcpRev, false, serverItems.ToArray(), Array.Empty<ZbaMcpDiscoveredToolView>());
                return ValueTask.FromResult(new ZbaMcpServerWriteResult(true, false, null, snap));
            },
            (caller, req, ct) =>
            {
                if (req.Endpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
                    throw new PluginHttpException(400, "ssrf_endpoint_blocked");
                return ValueTask.FromResult(new ZbaMcpServerTestResult(true, "ok", new[]
                {
                    new ZbaMcpProbeToolView("search_docs", "Search synthetic docs", true)
                }));
            },
            (caller, ct) => ValueTask.FromResult(new ZbaMcpReloadResult(
                true, serverItems.Sum(s => s.AllowedTools.Count),
                new ZbaMcpServersResponse(mcpRev, false, serverItems.ToArray(), Array.Empty<ZbaMcpDiscoveredToolView>()))));

        var roleBinding = AccessRoleHttpContract.Bind<ConfigurationCaller>(
            (caller, ct) => ValueTask.FromResult(new ZbaAccessRolesResponse(roleRev, Masked: true, roleItems.ToArray())),
            (caller, req, ct) =>
            {
                try
                {
                    AccessRoleValidator.FreezeAll(req.Bindings.Select(b => new AccessRoleBinding(
                        new AccessRoleKey(b.PlatformId, b.AccountScope, b.InstanceId, b.ScopeConversationKey, b.NativeUserId),
                        b.Role, b.Note)));
                }
                catch (ArgumentException ex)
                {
                    throw new PluginHttpException(400, ex.Message);
                }
                roleRev++;
                roleItems.Clear();
                foreach (var b in req.Bindings)
                {
                    roleItems.Add(new ZbaAccessRoleItem(
                        b.PlatformId,
                        b.AccountScope,
                        b.InstanceId,
                        ScopeConversationKey: b.ScopeConversationKey is { Length: > 0 } s ? "group:***01" : null,
                        ScopeConversationKeyRaw: b.ScopeConversationKey,
                        NativeUserId: "ou_***01",
                        NativeUserIdRaw: b.NativeUserId,
                        Role: b.Role,
                        Note: b.Note));
                }
                return ValueTask.FromResult(new ZbaAccessRolesSaveResult(
                    true, false, null, new ZbaAccessRolesResponse(roleRev, Masked: true, roleItems.ToArray())));
            });

        var endpoint = new ZbaEndpoint(
            new SyntheticZbaBackend { CustomHttpContributions = [mcpBinding, roleBinding] },
            new ZbaOptions { AdminToken = "p4-admin", ReadToken = "p4-read" },
            () => Program.FixedNow, "synthetic", "p4-inst");

        async Task<ZbaResponse> Send(string method, string path, string token, string body = "")
            => await endpoint.ProcessAsync(new(method, path, "",
                new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                Encoding.UTF8.GetBytes(body), "203.0.113.30"));

        var meta = JsonDocument.Parse((await Send("GET", "/zba/v1/meta", "p4-read")).Body);
        check(meta.RootElement.GetProperty("mcpManagement").GetBoolean(), "P4 meta.mcpManagement == true when MCP contract bound");
        var features = meta.RootElement.GetProperty("features").EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var op in new[] { "mcp.servers.list", "mcp.servers.save", "mcp.servers.delete", "mcp.servers.test", "mcp.reload", "permissions.roles.get", "permissions.roles.save" })
            check(features.Contains(op), "P4 meta exposes feature " + op);

        var openapi = JsonDocument.Parse((await Send("GET", "/zba/v1/openapi.json", "p4-read")).Body).RootElement.GetProperty("paths");
        check(openapi.TryGetProperty("/zba/v1/mcp/servers", out _) && openapi.TryGetProperty("/zba/v1/permissions/roles/save", out _),
            "P4 OpenAPI declares MCP & RBAC routes");

        // Write guard on read token
        check((await Send("POST", "/zba/v1/mcp/reload", "p4-read")).Status == 403, "P4 read token cannot reload MCP");

        // Env lock (409) & SSRF block (400) & non-echo secret
        var envLocked = await Send("POST", "/zba/v1/mcp/servers/save", "p4-admin",
            """{"expectedRevision":0,"idempotencyKey":"k1","id":"env-docs","enabled":true,"endpoint":"https://evil.example.com","allowedTools":["x"]}""");
        var envBody = Encoding.UTF8.GetString(envLocked.Body);
        check(envLocked.Status == 409 && envBody.Contains("env_server_locked"), "P4 env server overwrite returns 409 env_server_locked");

        var ssrfBlocked = await Send("POST", "/zba/v1/mcp/servers/save", "p4-admin",
            """{"expectedRevision":0,"idempotencyKey":"k2","id":"local-ops","enabled":true,"endpoint":"http://127.0.0.1:9000/mcp","allowedTools":["x"]}""");
        var ssrfBody = Encoding.UTF8.GetString(ssrfBlocked.Body);
        check(ssrfBlocked.Status == 400 && ssrfBody.Contains("ssrf_endpoint_blocked"), "P4 private MCP endpoint returns 400 ssrf_endpoint_blocked");

        var saved = await Send("POST", "/zba/v1/mcp/servers/save", "p4-admin",
            """{"expectedRevision":0,"idempotencyKey":"k3","id":"docs-con","enabled":true,"endpoint":"https://docs.example.com/mcp","allowedTools":["search_docs"],"readOnlyTools":["search_docs"],"authToken":"sk-synthetic-never-echo"}""");
        var savedBody = Encoding.UTF8.GetString(saved.Body);
        check(saved.Status == 200 && !savedBody.Contains("sk-synthetic-never-echo"), "P4 saved MCP response never echoes authToken");

        // GroupManager requires group scope (400) & masked vs *Raw round-trip
        var invalidMgr = await Send("POST", "/zba/v1/permissions/roles/save", "p4-admin",
            """{"expectedRevision":0,"idempotencyKey":"rk1","bindings":[{"platformId":"feishu","accountScope":"default","instanceId":"feishu-main","scopeConversationKey":null,"nativeUserId":"ou_synthetic_1","role":3}]}""");
        var invalidBody = Encoding.UTF8.GetString(invalidMgr.Body);
        check(invalidMgr.Status == 400 && invalidBody.Contains("group_manager_requires_group_scope"),
            "P4 GroupManager without scopeConversationKey rejected with 400");

        var validMgr = await Send("POST", "/zba/v1/permissions/roles/save", "p4-admin",
            """{"expectedRevision":0,"idempotencyKey":"rk2","bindings":[{"platformId":"feishu","accountScope":"default","instanceId":"feishu-main","scopeConversationKey":"feishu:group:oc_g1","nativeUserId":"ou_synthetic_1","role":3}]}""");
        var validBody = Encoding.UTF8.GetString(validMgr.Body);
        check(validMgr.Status == 200
            && validBody.Contains("\"nativeUserId\":\"ou_***01\"")
            && validBody.Contains("\"nativeUserIdRaw\":\"ou_synthetic_1\""),
            "P4 access roles response keeps masked display and preserves nativeUserIdRaw for admin round-trip");

        // Null binding item rejected (400) and read-only caller accepts nullable nativeUserIdRaw=null
        roleItems.Clear();
        roleItems.Add(new ZbaAccessRoleItem(
            "feishu", "default", "feishu-main",
            "feishu:group:oc_***g1", null, "ou_***01", null, WorkspaceRole.GroupManager, null, null));
        var readRoles = await Send("GET", "/zba/v1/permissions/roles", "p4-read");
        var readRolesBody = Encoding.UTF8.GetString(readRoles.Body);
        check(readRoles.Status == 200
            && readRolesBody.Contains("\"scopeConversationKey\":\"feishu:group:oc_***g1\"")
            && !readRolesBody.Contains("ou_synthetic_1"),
            "P4 read-only roles query masks non-numeric Feishu group key and omits raw user IDs");
    }
}

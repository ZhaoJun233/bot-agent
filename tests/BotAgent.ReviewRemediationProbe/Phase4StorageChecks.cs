extern alias StorageModule;

using System.Collections.Immutable;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;
using StorageMcpStore = StorageModule::BotAgent.Storage.McpServerStore;
using StorageMcpMutation = StorageModule::BotAgent.Storage.ConsoleMcpServerMutation;
using StorageAccessRoleStore = StorageModule::BotAgent.Storage.AccessRoleStore;

namespace BotAgent.ReviewRemediationProbe;

internal static class Phase4StorageChecks
{
    public static Task RunAsync(Action<bool, string> check)
        => ManagedCapabilityContractTests.RunIsolatedAsync(
            "--phase4",
            "Phase4Storage: 18 passed, 0 failed",
            check,
            "P4 McpServerStore & AccessRoleStore durable CAS, secret separation, SSRF gate and hot-reload publish");

    public static async Task RunInCurrentProcessAsync(Action<bool, string> check)
    {
        StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, new MinimalParticipant());
        var userVersion = AppDatabase.Scalar<int>("PRAGMA user_version");
        check(userVersion == 15, $"P4 stores preserve SQLite PRAGMA user_version == 15 (actual={userVersion})");

        await RunMcpServerStoreChecks(check);
        RunAccessRoleStoreChecks(check);
    }

    private sealed class MinimalParticipant : StorageConfiguration.ConfigurationStorageParticipant
    {
        private readonly object _writeGate = new();
        internal override void CoordinateWrite(Action write)
        {
            lock (_writeGate) write();
        }
        internal override bool Reauthorize(ConfigurationCaller caller, ConfigurationAccess required) => true;
        internal override ImmutableArray<SecretMetadata> SecretSources(SqliteConnection connection,
            ImmutableArray<StorageConfiguration.SecretMutation> proposed)
            => Enum.GetValues<WritableSecretSlot>().Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray();
        internal override StorageConfiguration.PreparedConfigurationChange Prepare(SqliteConnection connection,
            ConfigurationPatch ownedIntent, ImmutableArray<SecretMetadata> proposedSecrets,
            ImmutableArray<StorageConfiguration.SecretMutation> proposedMutations, StorageConfiguration.ConfigurationStorageProfile profile)
            => new(false, ImmutableArray<FieldError>.Empty, ImmutableArray<DeviceKeyAssignment>.Empty, _ => throw new InvalidOperationException());
        internal override ConfigurationReadResult Read(SqliteConnection connection, ConfigurationCaller caller,
            ConfigurationReadKind kind, ConfigurationVersion version, ConfigurationGeneration generation,
            long serverUnixSeconds, ApplyState latestApplyState)
            => new(ConfigurationResultCode.Ok, version, generation, "cfg-0.3.3", serverUnixSeconds, null,
                ImmutableArray<SecretMetadata>.Empty, ImmutableArray<FieldDescriptor>.Empty, latestApplyState);
        internal override void AppendAudit(SqliteConnection connection, ConfigurationReceipt receipt,
            ImmutableArray<ConfigurationFieldId> fields) { }
    }

    private static async Task RunMcpServerStoreChecks(Action<bool, string> check)
    {
        var store = new StorageMcpStore();
        var initial = store.Load();
        check(initial.Revision == 0 && initial.Servers.Length == 0, "McpServerStore starts empty at revision 0");

        // Reject SSRF private endpoint
        var ssrfDenied = store.Upsert(
            0,
            "p4-mcp-ssrf",
            "test:admin",
            new StorageMcpMutation(
                "local-bad",
                true,
                "http://127.0.0.1:8080/mcp",
                new[] { "echo" }),
            url => url.Contains("127.0.0.1", StringComparison.Ordinal)
                ? (false, string.Empty, "ssrf_endpoint_blocked")
                : (true, url, "ok"));
        check(!ssrfDenied.Accepted && ssrfDenied.ErrorCode == "ssrf_endpoint_blocked",
            "McpServerStore rejects private endpoint when ValidateEndpoint denies it");

        // Save valid console server with write-only Bearer token
        const string token = "sk-p4-synthetic-storage-secret";
        var published = false;
        var saved = store.Upsert(
            0,
            "p4-mcp-save-1",
            "test:admin",
            new StorageMcpMutation(
                "ops-docs",
                true,
                "https://mcp.example.com/mcp",
                new[] { "search_docs", "create_ticket" },
                new[] { "search_docs" },
                12,
                8192,
                AuthToken: token),
            url => (true, url, "ok"),
            onPublish: () => published = true);

        check(saved.Accepted && published && saved.Snapshot.Revision == 1 && saved.Snapshot.Servers.Length == 1,
            "McpServerStore upserts console server cleanly and fires onPublish");

        var rec = saved.Snapshot.Servers[0];
        check(rec.HasAuthToken && rec.AllowedTools.SequenceEqual(new[] { "search_docs", "create_ticket" })
            && rec.ReadOnlyTools.SequenceEqual(new[] { "search_docs" }),
            "McpServerStore snapshot exposes HasAuthToken=true and exact tool allowlists");

        var rawMeta = AppDatabase.GetMeta(StorageMcpStore.MetaKey) ?? string.Empty;
        check(!rawMeta.Contains(token, StringComparison.Ordinal),
            "McpServerStore keeps Bearer token out of meta JSON payload");

        var runtime = store.LoadRuntimeServers(url => (true, url, "ok"));
        check(runtime.Count == 1 && runtime[0].Token == token
            && (runtime[0].ReadOnlyTools?.SequenceEqual(new[] { "search_docs" }) ?? false),
            "McpServerStore.LoadRuntimeServers resolves secret token from SecretsStore");

        // CAS revision conflict & idempotency conflict
        var conflict = store.Upsert(
            0,
            "p4-mcp-stale",
            "test:admin",
            new StorageMcpMutation("ops-docs", true, "https://mcp.example.com/mcp", new[] { "search_docs" }),
            url => (true, url, "ok"));
        check(!conflict.Accepted && conflict.ErrorCode == "revision_conflict",
            "McpServerStore enforces CAS revision_conflict on stale expectedRevision");

        var idemConflict = false;
        try
        {
            store.Upsert(
                1,
                "p4-mcp-save-1",
                "test:admin",
                new StorageMcpMutation("ops-docs", true, "https://changed.example.com/mcp", new[] { "search_docs" }),
                url => (true, url, "ok"));
        }
        catch (InvalidOperationException ex) when (ex.Message == "mcp_servers_idempotency_conflict")
        {
            idemConflict = true;
        }
        check(idemConflict, "McpServerStore throws mcp_servers_idempotency_conflict on mutated payload reuse");

        var emptyTools = store.Upsert(
            1,
            "p4-mcp-empty-tools",
            "test:admin",
            new StorageMcpMutation("ops-empty", true, "https://mcp.example.com/mcp", Array.Empty<string>()),
            url => (true, url, "ok"));
        check(!emptyTools.Accepted && emptyTools.ErrorCode == "allowed_tools_required",
            "McpServerStore rejects empty allowedTools with allowed_tools_required");

        var badToken = store.Upsert(
            1,
            "p4-mcp-bad-token",
            "test:admin",
            new StorageMcpMutation("ops-docs", true, "https://mcp.example.com/mcp", new[] { "search_docs" }, AuthToken: "bad\r\ntoken"));
        check(!badToken.Accepted && badToken.ErrorCode == "invalid_auth_token",
            "McpServerStore rejects CRLF authToken with invalid_auth_token");

        var imdsDenied = store.Upsert(
            1,
            "p4-mcp-imds",
            "test:admin",
            new StorageMcpMutation("ops-imds", true, "http://169.254.169.254/latest/meta-data/", new[] { "search_docs" }));
        check(!imdsDenied.Accepted && imdsDenied.ErrorCode == "private_endpoint_rejected",
            "McpServerStore DefaultValidateEndpoint rejects cloud IMDS 169.254.169.254");

        // AuthToken difference with reused idempotencyKey conflicts
        var tokenConflict = false;
        try
        {
            store.Upsert(
                1,
                "p4-mcp-save-1",
                "test:admin",
                new StorageMcpMutation("ops-docs", true, "https://mcp.example.com/mcp", new[] { "search_docs", "create_ticket" },
                    new[] { "search_docs" }, 12, 8192, AuthToken: "sk-p4-synthetic-different-secret"),
                url => (true, url, "ok"));
        }
        catch (InvalidOperationException ex) when (ex.Message == "mcp_servers_idempotency_conflict")
        {
            tokenConflict = true;
        }
        check(tokenConflict, "McpServerStore includes authToken digest in idempotency conflict detection");

        // McpConfiguration.BuildManaged ProbeAsync hits local trap endpoint under ALLOW_PRIVATE=1 and NEVER sends stored Bearer token to a different endpoint
        string? observedAuthHeader = null;
        using var trapListener = new System.Net.HttpListener();
        var trapPort = ReserveLoopbackPort();
        trapListener.Prefixes.Add($"http://127.0.0.1:{trapPort}/");
        trapListener.Start();
        var trapTask = Task.Run(async () =>
        {
            var ctx = await trapListener.GetContextAsync();
            observedAuthHeader = ctx.Request.Headers["Authorization"];
            ctx.Response.StatusCode = 405;
            ctx.Response.Close();
        });
        var prevAllowPrivate = Environment.GetEnvironmentVariable("BOTAGENT_MCP_ALLOW_PRIVATE");
        Environment.SetEnvironmentVariable("BOTAGENT_MCP_ALLOW_PRIVATE", "1");
        try
        {
            var headlessAsm = typeof(BotAgent.Services.Agent.ServerAgentRunner).Assembly;
            var mcpConfigType = headlessAsm.GetType("BotAgent.Host.McpConfiguration", throwOnError: true)!;
            var buildManagedMethod = mcpConfigType.GetMethod("BuildManaged",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var managed = buildManagedMethod.Invoke(null, new object?[]
            {
                new SecretsStore(), null, (Action<string>)(_ => { }), null, null, store
            })!;
            var probeDelegate = managed.GetType().GetProperty("ProbeAsync")!.GetValue(managed)!;
            var runtimeObj = (IAsyncDisposable)managed.GetType().GetProperty("Runtime")!.GetValue(managed)!;
            var probeTask = (Task<BotAgent.Mcp.McpProbeOutcome>)probeDelegate.GetType().GetMethod("Invoke")!
                .Invoke(probeDelegate, new object?[] { "ops-docs", $"http://127.0.0.1:{trapPort}/steal", null, 2, CancellationToken.None })!;
            await probeTask;
            await runtimeObj.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTAGENT_MCP_ALLOW_PRIVATE", prevAllowPrivate);
            trapListener.Stop();
        }
        _ = trapTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        check(observedAuthHeader is null, "McpConfiguration.ProbeAsync never leaks stored Bearer token when probed endpoint differs");

        var deleted = store.Delete(1, "p4-mcp-del-1", "test:admin", "ops-docs");
        check(deleted.Accepted && deleted.Snapshot.Servers.Length == 0 && store.LoadToken("ops-docs") is null,
            "McpServerStore.Delete removes server metadata and purges stored secret");
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void RunAccessRoleStoreChecks(Action<bool, string> check)
    {
        var store = new StorageAccessRoleStore();

        var invalidGroupMgr = store.Save(
            0,
            "p4-role-bad-1",
            "test:admin",
            new[]
            {
                new AccessRoleBinding(
                    new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "feishu-main", null, "ou_synthetic_mgr"),
                    WorkspaceRole.GroupManager)
            });
        check(!invalidGroupMgr.Accepted && invalidGroupMgr.ErrorCode == "group_manager_requires_group_scope",
            "AccessRoleStore rejects GroupManager without group conversation key");

        var mismatch = store.Save(
            0,
            "p4-role-bad-2",
            "test:admin",
            new[]
            {
                new AccessRoleBinding(
                    new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "feishu-main", "group:10001", "ou_synthetic_mgr"),
                    WorkspaceRole.GroupManager)
            });
        check(!mismatch.Accepted && mismatch.ErrorCode == "access_role_scope_platform_mismatch",
            "AccessRoleStore rejects platform/conversation prefix mismatch");

        var publishedCount = -1;
        var saved = store.Save(
            0,
            "p4-role-save-1",
            "test:admin",
            new[]
            {
                new AccessRoleBinding(
                    new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "feishu-main", "feishu:group:oc_10001", "ou_synthetic_mgr"),
                    WorkspaceRole.GroupManager,
                    "oncall")
            },
            onPublish: frozen => publishedCount = frozen.Length);
        check(saved.Accepted && publishedCount == 1 && saved.Snapshot.Revision == 1 && saved.Snapshot.Bindings.Length == 1,
            "AccessRoleStore saves valid RBAC bindings atomically and fires hot-reload callback");
    }
}

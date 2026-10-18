extern alias StorageModule;
extern alias ModelModule;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Services;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Model;
using BotAgent.Domain.Jargon;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Conversation;
using BotAgent.Services.Ops;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;

namespace BotAgent.ReviewRemediationProbe;

internal static class ConfigurationContractTests
{
    public static Task<int?> TryRunChildAsync(string[] args)
    {
        if (args.Length == 1 && args[0] == "--configuration-write-boundaries")
            return Task.FromResult<int?>(WriteBoundariesChild());
        if (args.Length == 1 && args[0] == "--configuration-http")
            return ConfigurationHttpChildAsync();
        if (args.Length == 3 && args[0] == "--configuration-whole-rollback")
            return WholeRollbackChildAsync(args[1], args[2]);
        if (args.Length == 1 && args[0] == "--configuration-feishu-whitelist")
            return Task.FromResult<int?>(FeishuWhitelistChild());
        if (args.Length == 1 && args[0] == "--configuration-platform-compatibility")
            return PlatformCompatibilityChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-legacy-mood")
            return LegacyMoodChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-legacy-mood-audit")
            return LegacyMoodChildAsync(audit: true);
        if (args.Length == 3 && args[0] == "--configuration-legacy-mood-reopen")
            return LegacyMoodReopenChildAsync(args[1], args[2]);
        if (args.Length == 1 && args[0] == "--configuration-publish-permission")
            return PublishPermissionChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-bridge-devices")
            return BridgeDeviceGateChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-legacy-plan")
            return Task.FromResult<int?>(LegacyConfigurationPlanChild());
        if (args.Length == 4 && args[0] == "--configuration-capture-preimage")
            return Task.FromResult<int?>(CaptureLegacyPreimage(args[1], args[2], args[3]));
        if (args.Length == 5 && args[0] == "--configuration-capture-bundle")
            return Task.FromResult<int?>(CaptureBaselineBundle(args[1], args[2], args[3], args[4]));
        if (args.Length == 4 && args[0] == "--configuration-restart")
            return RestartChildAsync(args);
        if (args.Length == 1 && args[0] == "--configuration-host-adapter")
            return HostAdapterChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-initialization")
            return Task.FromResult<int?>(InitializationChild());
        if (args.Length == 1 && args[0] == "--configuration-devices")
            return DeviceChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-host-devices")
            return HostDeviceChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-host-endpoints")
            return HostEndpointChildAsync();
        if (args.Length == 1 && args[0] is "--configuration-primary-aliases" or "--configuration-alias-codecs")
            return AliasChildAsync(args[0] == "--configuration-alias-codecs");
        if (args.Length == 1 && args[0] == "--configuration-provider-routing")
            return ProviderRoutingChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-provider-refs")
            return Task.FromResult<int?>(ProviderRefsChild());
        if (args.Length == 1 && args[0] == "--configuration-primary-facts")
            return PrimaryFactsChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-registered-providers")
            return RegisteredProvidersChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-provider-writer-fence")
            return Task.FromResult<int?>(ProviderWriterFenceChild());
        if (args.Length == 1 && args[0] == "--configuration-mood-persistence")
            return MoodPersistenceChildAsync();
        if (args.Length == 1 && args[0] == "--configuration-platform-policies")
            return PlatformPoliciesChildAsync();
        if (args.Length == 4 && args[0] == "--configuration-device-restart")
            return DeviceRestartChildAsync(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--configuration-claim-restart")
            return ClaimRestartChildAsync(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--configuration-capacity-restart")
            return CapacityRestartChildAsync(args[1], args[2]);
        if (args.Length == 1 && args[0] is "--configuration-apply-claims" or "--configuration-global-capacity")
            return GovernanceChildAsync(args[0]);
        if (args.Length == 2 && args[0] == "--configuration-token-case")
            return TokenCaseChildAsync(args[1]);
        if (args.Length == 3 && args[0] == "--configuration-token-restart")
            return TokenRestartChildAsync(args[1], args[2]);
        if (args.Length != 1 || args[0] is not ("--configuration-future-module" or "--configuration-module-atomic-fault"))
            return Task.FromResult<int?>(null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        try
        {
            ClockBindings.InitializeStorage();
            var path = Path.Combine(root, "synthetic.db");
            using (var connection = OpenFixture(path))
            {
                StorageDatabase.Exec(connection, "PRAGMA user_version=9; CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);");
                if (args[0] == "--configuration-future-module")
                    StorageDatabase.Exec(connection, "INSERT INTO meta(key,value) VALUES('configuration_schema','2');");
                else
                    StorageDatabase.Exec(connection, """
                        CREATE TRIGGER synthetic_module_fault BEFORE INSERT ON meta
                        WHEN NEW.key='configuration_schema'
                        BEGIN SELECT RAISE(ABORT,'synthetic_module_fault'); END;
                        """);
            }
            if (args[0] == "--configuration-future-module")
            {
                var hash = SHA256.HashData(File.ReadAllBytes(path));
                var rejected = false;
                try { StorageDatabase.Initialize(path); }
                catch (InvalidOperationException ex) when (ex.Message == "configuration_schema_newer") { rejected = true; }
                Check(rejected, "future module rejection code");
                SqliteConnection.ClearAllPools();
                Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "future module unchanged bytes");
                Check(!StorageDatabase.IsReady, "future module not ready");
                StorageDatabase.Initialize(Path.Combine(root, "retry.db"));
                Check(StorageDatabase.IsReady && StorageDatabase.GetMeta("configuration_schema") == "1",
                    "future module rejection permits clean retry");
                Console.WriteLine($"CFG_CHILD future passed={passed} failed=0");
            }
            else
            {
                var rejected = false;
                try { StorageDatabase.Initialize(path); }
                catch (SqliteException) { rejected = true; }
                Check(rejected && !StorageDatabase.IsReady, "module initialization fault not ready");
                using (var connection = OpenFixture(path))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'configuration_%'";
                    Check(Convert.ToInt64(command.ExecuteScalar()) == 0, "module tables rolled back together");
                    command.CommandText = "SELECT COUNT(*) FROM meta WHERE key='configuration_schema'";
                    Check(Convert.ToInt64(command.ExecuteScalar()) == 0, "module marker rolled back");
                    command.CommandText = "PRAGMA user_version";
                    Check(Convert.ToInt64(command.ExecuteScalar()) == 9, "legacy physical revision unchanged");
                    StorageDatabase.Exec(connection, "DROP TRIGGER synthetic_module_fault;");
                }
                StorageDatabase.Initialize(path);
                Check(StorageDatabase.IsReady && StorageDatabase.GetMeta("configuration_schema") == "1",
                    "module initialization retry succeeds");
                Check(StorageDatabase.Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'configuration_%'") == 6
                    && StorageDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 0,
                    "module retry creates complete initial state");
                Console.WriteLine($"CFG_CHILD atomic passed={passed} failed=0");
            }
            return Task.FromResult<int?>(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD failed=1 passed={passed} type={ex.GetType().Name}");
            return Task.FromResult<int?>(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-module-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> DeviceChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-devices-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock);
            AppDatabase.Initialize();
            new SettingsStore().Save(new AppSettings { AiDesire = 10 });
            var participant = new SyntheticParticipant { ManageDevices = true };
            participant.DeviceRuntimeAudit = (conn, _) => new AuditLogStore().AppendInTransaction(conn,
                new AuditEvent("device_activation", "synthetic-writer", "bot", "synthetic_device", "cfg-0.3.2"));
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-device-owner", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            ConfigurationSaveRequest Request(ImmutableArray<DeviceConfiguration> devices, bool clear = false)
                => new("cfg-0.3.2", new(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1")),
                    new(header.Generation, clock.WallSeconds, Guid.NewGuid()),
                    new(null, null, new AgentChanges { AgentDevices = clear
                        ? FieldEdit<ImmutableArray<DeviceConfiguration>>.Clear() : FieldEdit<ImmutableArray<DeviceConfiguration>>.Set(devices) },
                        null, null, null, null, null), ImmutableArray<SecretEdit>.Empty);
            DeviceConfiguration Device(string name, DeviceKey? key = null, bool enabled = true)
                => new(key, name, enabled, null, null, null, 0);
            ImmutableArray<StorageConfiguration.ConfigurationDeviceState> States()
            {
                using var conn = AppDatabase.Open();
                return StorageConfiguration.ConfigurationStorageBridge.LoadDevices(conn);
            }
            long Version() => AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            long AuditCount() => AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log WHERE event_type='device_activation'");
            Check(StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-unknown").Allowed, "unknown legacy hello"); // 1
            Check(StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-unknown", null), "unknown legacy dispatch"); // 2
            Check(!StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-unknown", new(Guid.NewGuid())), "unknown bound dispatch denied"); // 3
            var creation = Request(ImmutableArray.Create(Device(" synthetic-device-a "), Device("synthetic-device-b")));
            var created = await store.SaveAsync(caller, creation);
            var assignments = created.Receipt!.DeviceKeyAssignments;
            Check(created.Code == ConfigurationResultCode.Ok && assignments.Length == 2
                && assignments.Select(x => x.InputOrdinal).SequenceEqual(new[] { 0, 1 }), "creation assignments"); // 4
            var a = assignments[0].Key; var b = assignments[1].Key;
            Check(a.Value != Guid.Empty && a != b && States().Single(x => x.Key == a).DesiredName == "synthetic-device-a", "durable canonical identities"); // 5
            var version = Version();
            var replay = await store.SaveAsync(caller, creation);
            Check(replay.Replayed && replay.Receipt!.DeviceKeyAssignments.SequenceEqual(assignments)
                && Version() == version && States().Length == 2, "replay preserves allocations"); // 6
            var trimmed = creation with { Changes = creation.Changes with { Agent = new AgentChanges {
                AgentDevices = FieldEdit<ImmutableArray<DeviceConfiguration>>.Set(ImmutableArray.Create(Device("synthetic-device-a"), Device("synthetic-device-b"))) } } };
            Check((await store.SaveAsync(caller, trimmed)).Replayed, "trim canonical HMAC replay"); // 7
            Check((await store.SaveAsync(caller, Request(ImmutableArray.Create(Device("same"), Device(" same "))))).Code
                == ConfigurationResultCode.ValidationFailed && Version() == version, "trim collision rejected"); // 8
            Check((await store.SaveAsync(caller, Request(ImmutableArray.Create(Device("synthetic-device-a", new(Guid.NewGuid())))))).Code
                == ConfigurationResultCode.ValidationFailed && Version() == version, "unknown provided key rejected"); // 9
            Check((await store.SaveAsync(caller, Request(ImmutableArray.Create(Device("synthetic-device-b", a), Device("synthetic-device-a", b))))).Code
                == ConfigurationResultCode.ValidationFailed, "cross-key name swap rejected"); // 10
            await store.SaveAsync(caller, Request(ImmutableArray.Create(Device("synthetic-device-a", a, false), Device("synthetic-device-b", b))));
            Check(!StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-device-a").Allowed
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-a", a), "committed disabled gate"); // 11
            await store.SaveAsync(caller, Request(ImmutableArray.Create(Device("synthetic-device-a", a), Device("synthetic-device-b", b))));
            Check(StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-a", a), "explicit re-enable not deleted"); // 12
            var rename = Request(ImmutableArray.Create(Device("synthetic-device-renamed", a), Device("synthetic-device-b", b)));
            await store.SaveAsync(caller, rename);
            Check(States().Single(x => x.Key == a).RenamePending
                && StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-a", a)
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-renamed", a), "desired rename remains pending"); // 13
            Check(StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-device-a") is { Allowed: true, RenamePending: true }, "old active pending hello"); // 14
            version = Version(); var audits = AuditCount();
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_device_confirm_fault BEFORE INSERT ON security_audit_log WHEN NEW.event_type='device_activation'
                BEGIN SELECT RAISE(ABORT,'synthetic_device_confirm_fault'); END;
                """));
            Check(!StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-device-renamed").Allowed
                && States().Single(x => x.Key == a).RenamePending && Version() == version && AuditCount() == audits,
                "confirm audit fault atomically denies"); // 15
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_device_confirm_fault"));
            var confirmed = StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-device-renamed");
            Check(confirmed is { Allowed: true, RenamePending: false } && confirmed.Key == a
                && Version() == version && AuditCount() == audits + 1
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-a", a), "runtime confirm preserves user version and revokes old name"); // 16
            Check(StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-renamed", a)
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-renamed", b), "server-bound key required to match"); // 17
            StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-device-renamed");
            Check(AuditCount() == audits + 1 && (await store.FindAsync(caller, rename.Key)).Receipt!.CommittedVersion!.Value.Value == version,
                "confirmed hello idempotent no fake receipt"); // 18
            var oldName = Request(ImmutableArray.Create(Device("synthetic-device-a"), Device("synthetic-device-b", b)));
            Check((await store.SaveAsync(caller, oldName)).Code == ConfigurationResultCode.ValidationFailed
                && (await store.SaveLegacyAsync(caller, oldName.Key with { Nonce = Guid.NewGuid() }, oldName.Changes, oldName.Secrets)).Code
                    == ConfigurationResultCode.LegacyNeedsReview, "revoked name strict and legacy rejection"); // 19
            var legacy = Request(ImmutableArray.Create(Device("synthetic-device-renamed"), Device("synthetic-device-b")));
            var legacyResult = await store.SaveLegacyAsync(caller, legacy.Key, legacy.Changes, legacy.Secrets);
            Check(legacyResult.Code == ConfigurationResultCode.NoChanges && States().Single(x => x.DesiredName == "synthetic-device-renamed").Key == a,
                "legacy unique names preserve keys"); // 20
            var legacyRename = Request(ImmutableArray.Create(Device("synthetic-device-legacy-new"), Device("synthetic-device-b")));
            var legacyRenamed = await store.SaveLegacyAsync(caller, legacyRename.Key, legacyRename.Changes, legacyRename.Secrets);
            var c = legacyRenamed.Receipt!.DeviceKeyAssignments.Single().Key;
            Check(c != a && States().Single(x => x.Key == a).Deleted
                && !StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-device-renamed").Allowed,
                "legacy rename delete-create persists tombstone"); // 21
            Check(StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-legacy-new", c)
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-a", null), "deleted historical aliases never default allow"); // 22
            Check((await store.SaveAsync(caller, Request(ImmutableArray.Create(Device("synthetic-device-resurrected", a))))).Code
                == ConfigurationResultCode.ValidationFailed, "deleted key cannot resurrect"); // 23
            var deletedName = Request(ImmutableArray.Create(Device("synthetic-device-renamed")));
            Check((await store.SaveLegacyAsync(caller, deletedName.Key, deletedName.Changes, deletedName.Secrets)).Code
                == ConfigurationResultCode.LegacyNeedsReview, "deleted legacy name cannot resurrect"); // 24
            var clearing = Request(ImmutableArray<DeviceConfiguration>.Empty, clear: true);
            var cleared = await store.SaveAsync(caller, clearing);
            Check(cleared.Code == ConfigurationResultCode.Ok && States().All(x => x.Deleted)
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-b", b), "clear full table durable delete"); // 25
            Check((await store.SaveAsync(caller, clearing)).Replayed && States().All(x => x.Deleted), "delete replay no resurrection"); // 26
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "ALTER TABLE configuration_devices RENAME TO synthetic_hidden_devices"));
            Check(!StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-unknown", null)
                && !StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-unknown").Allowed, "gate storage read failure closes even unknown fallback"); // 27
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "ALTER TABLE synthetic_hidden_devices RENAME TO configuration_devices"));
            version = Version(); var count = States().Length;
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_device_save_fault BEFORE INSERT ON configuration_devices WHEN NEW.desired_name='synthetic-device-fault'
                BEGIN SELECT RAISE(ABORT,'synthetic_device_save_fault'); END;
                """));
            using var credential = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-device-fault-credential"));
            var fault = Request(ImmutableArray.Create(Device("synthetic-device-first"), Device("synthetic-device-fault")));
            fault = fault with { Secrets = ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.TtsApiKey, credential)) };
            var faultResult = await store.SaveAsync(caller, fault);
            Check(faultResult.Code == ConfigurationResultCode.CommitFailed && Version() == version && States().Length == count
                && new SecretsStore().LoadTtsKey() is null && (await store.FindAsync(caller, fault.Key)).Code == ConfigurationResultCode.NotFound,
                "device partial upsert fault rolls back whole save"); // 28
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_device_save_fault"));
            var retry = await store.SaveAsync(caller, fault);
            Check(retry.Code == ConfigurationResultCode.Ok && retry.Receipt!.DeviceKeyAssignments.Length == 2
                && new SecretsStore().LoadTtsKey() == "synthetic-device-fault-credential", "failed save can retry same original operation"); // 29
            Check(States().All(x => x.ToString() == "ConfigurationDeviceState") && States().Single(x => x.Key == a).RevokedNames
                .Contains("synthetic-device-a"), "durable histories and safe diagnostics"); // 30
            var restore = Path.Combine(root, "device-restore");
            Directory.CreateDirectory(Path.Combine(restore, "data"));
            File.Copy(AppDatabase.VacuumIntoBackup(), Path.Combine(restore, "data", "qqchat.db"));
            var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(ConfigurationContractTests).Assembly.Location);
            start.ArgumentList.Add("--configuration-device-restart");
            start.ArgumentList.Add(restore);
            start.ArgumentList.Add(JsonSerializer.Serialize(clearing));
            start.ArgumentList.Add(a.Value.ToString("N"));
            using var child = Process.Start(start) ?? throw new InvalidOperationException("configuration_device_restart_start");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
            var text = await output;
            Console.Write(text);
            Check(child.ExitCode == 0 && text.Contains("CFG_CHILD device-restart passed=7 failed=0", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(await error), "fresh process restores keys and revoked gates"); // 31
            Check(Version() == version + 1 && States().Count(x => !x.Deleted) == 2,
                "backup child never replaces live desired state"); // 32
            Console.WriteLine($"CFG_CHILD devices passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD devices passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-devices-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> DeviceRestartChildAsync(string directory, string requestJson, string deletedKey)
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(full)!;
        if (Path.GetFileName(full) != "device-restore"
            || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(parent), "^botagent-configuration-devices-[0-9a-f]{32}$")
            || !string.Equals(Path.GetDirectoryName(parent), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(deletedKey, "N", out var deleted) || deleted == Guid.Empty)
            return 1;
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", full);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", full);
        var passed = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("synthetic_device_restart_assertion"); passed++; }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock);
            AppDatabase.Initialize();
            var initialVersion = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            var audits = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log");
            var participant = new SyntheticParticipant { ManageDevices = true };
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var request = JsonSerializer.Deserialize<ConfigurationSaveRequest>(requestJson)!;
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-device-owner", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets);
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Check(read.Generation != request.Key.Generation && read.Version.Value == initialVersion); // 1
            ImmutableArray<StorageConfiguration.ConfigurationDeviceState> states;
            using (var conn = AppDatabase.Open()) states = StorageConfiguration.ConfigurationStorageBridge.LoadDevices(conn);
            Check(states.Single(x => x.Key.Value == deleted) is { Deleted: true, DesiredEnabled: false }
                && !StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-device-renamed").Allowed); // 2
            Check(!StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-a", null)
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-b", null)
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-device-renamed", new(deleted))); // 3
            var active = states.Single(x => x.DesiredName == "synthetic-device-first");
            Check(!active.Deleted && StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice(active.ActiveName, active.Key)); // 4
            var found = await store.FindAsync(caller, request.Key);
            Check(found.Code == ConfigurationResultCode.Ok && found.Receipt is { CommitState: CommitState.Committed }); // 5
            var replay = await store.SaveAsync(caller, request);
            using (var conn = AppDatabase.Open()) states = StorageConfiguration.ConfigurationStorageBridge.LoadDevices(conn);
            Check(replay.Replayed && replay.Receipt!.CommittedVersion == found.Receipt!.CommittedVersion
                && states.Count(x => !x.Deleted) == 2 && participant.Writes == 0); // 6
            Check(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == initialVersion
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == audits
                && new SecretsStore().LoadTtsKey() == "synthetic-device-fault-credential"); // 7
            Console.WriteLine($"CFG_CHILD device-restart passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD device-restart passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static async Task<int?> HostEndpointChildAsync()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        const string allowed = "https://example.com/registered/v1";
        const string qualified = "https://example.com/registered/query?tenant=synthetic#fragment";
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-endpoints-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("BOTAGENT_BASE_URL", qualified);
        var passed = 0;
        void Check(bool condition, string label)
        { if (!condition) { Console.WriteLine("FAIL CFG_ENDPOINT " + label); throw new InvalidOperationException(label); } passed++; }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock);
            AppDatabase.Initialize();
            var initial = new AppSettings { Model = "synthetic-model", ModelBaseUrl = "https://example.com/original" };
            new SettingsStore().Save(initial);
            var providers = new ModelProviderStore();
            foreach (var entry in new[] { ("primary", initial.ModelBaseUrl), ("allowed", allowed), ("qualified", qualified),
                ("private", "http://127.0.0.1/v1") })
                providers.UpsertMetadata(new(entry.Item1, 0, "synthetic-provider", entry.Item2, "synthetic-model", "secret:apiKey", false));
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source,
                CreateActualTokenAdapter(new SettingsBox(initial)));
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-endpoint-owner", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            long Version() => AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            ConfigurationSaveRequest Request(FieldEdit<string> endpoint, RegisteredConfigurationChanges? registered = null)
                => new("cfg-0.3.2", new(Version()), new(header.Generation, clock.WallSeconds, Guid.NewGuid()),
                    new(null, new ModelChanges { ModelBaseUrl = endpoint }, null, null, null, null, null, registered), ImmutableArray<SecretEdit>.Empty);
            var before = Version();
            var cold = await store.SaveAsync(caller, Request(FieldEdit<string>.Set("https://example.com/unregistered")));
            Check(cold.Code == ConfigurationResultCode.ValidationFailed && cold.Errors.Any(error => error.Code == "endpoint_authorization_required")
                && Version() == before && new SecretsStore().LoadApiKey() is null, "cold credential cannot self-register arbitrary destination"); // 1
            var canonical = await store.SaveAsync(caller, Request(FieldEdit<string>.Set("https://EXAMPLE.com:443/registered/v1")));
            Check(canonical.Code == ConfigurationResultCode.Ok && new Uri(providers.Find("primary")!.BaseUrl).AbsoluteUri == allowed,
                "registered destination permits standard URI normalization"); // 2
            Check((await store.SaveAsync(caller, Request(FieldEdit<string>.Set(allowed)))).Code is ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges,
                "same canonical current endpoint remains compatible"); // 3
            using var credential = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-endpoint-credential"));
            var binding = Request(FieldEdit<string>.Keep()) with {
                Secrets = ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, credential)) };
            Check((await store.SaveAsync(caller, binding)).Code == ConfigurationResultCode.Ok,
                "registered purpose accepts allowed credential binding"); // 4
            before = Version();
            var rejected = await store.SaveAsync(caller, Request(FieldEdit<string>.Set("https://example.com/warm-unregistered")));
            Check(rejected.Code == ConfigurationResultCode.ValidationFailed && Version() == before
                && providers.Find("primary")!.BaseUrl == allowed, "warm credential unknown destination rejected atomically"); // 5
            Check((await store.SaveAsync(caller, Request(FieldEdit<string>.Set(qualified)))).Code == ConfigurationResultCode.Ok
                && providers.Find("primary")!.BaseUrl == qualified, "registered full path-query-fragment permitted"); // 6
            foreach (var endpoint in new[] { "https://example.com/registered/other?tenant=synthetic#fragment",
                "https://example.com/registered/query?tenant=different#fragment", "https://example.com/registered/query?tenant=synthetic#other" })
            {
                before = Version();
                Check((await store.SaveAsync(caller, Request(FieldEdit<string>.Set(endpoint)))).Code == ConfigurationResultCode.ValidationFailed
                    && Version() == before, "same host cannot authorize different path-query-fragment"); // 7..9
            }
            before = Version();
            Check((await store.SaveAsync(caller, Request(FieldEdit<string>.Set("http://127.0.0.1/v1")))).Code == ConfigurationResultCode.ValidationFailed
                && Version() == before, "registered private address still subject to static target policy"); // 10
            Check((await store.SaveAsync(caller, Request(FieldEdit<string>.Set("https://synthetic:credential@example.com/registered/v1")))).Code
                == ConfigurationResultCode.ValidationFailed && Version() == before, "userinfo rejected before credential use"); // 11
            await store.SaveAsync(caller, Request(FieldEdit<string>.Set(allowed)));
            Check((await store.SaveAsync(caller, Request(FieldEdit<string>.Clear()))).Code == ConfigurationResultCode.Ok
                && providers.Find("primary")!.BaseUrl == qualified, "inherited fallback needs existing registered purpose"); // 12
            var self = new RegisteredConfigurationChanges(ImmutableArray<PluginEnabledEdit>.Empty,
                ImmutableArray.Create(new ProviderEdit(new ProviderKey("allowed"), new ProviderMetadataPatch(FieldEdit<string>.Keep(),
                    FieldEdit<int>.Keep(), FieldEdit<string>.Set("https://example.com/self-authorized"), FieldEdit<string>.Keep(),
                    FieldEdit<bool>.Keep(), CredentialBindingEdit.KeepExisting()))), ImmutableArray<PolicyEdit>.Empty);
            before = Version();
            var selfAuthorized = await store.SaveAsync(caller, Request(FieldEdit<string>.Set("https://example.com/self-authorized"), self));
            Check(selfAuthorized.Code == ConfigurationResultCode.ValidationFailed && Version() == before
                && selfAuthorized.Errors.Any(error => error.Code == "endpoint_authorization_required") && providers.Find("allowed")!.BaseUrl == allowed,
                "request body cannot authorize its own endpoint in the same transaction"); // 13
            var legacy = Request(FieldEdit<string>.Set("https://example.com/legacy-unregistered"));
            Check((await store.SaveLegacyAsync(caller, legacy.Key, legacy.Changes, legacy.Secrets)).Code == ConfigurationResultCode.Ok,
                "Legacy retains historical endpoint management"); // 14
            var privateLegacy = Request(FieldEdit<string>.Set("http://127.0.0.1/v1"));
            await store.SaveLegacyAsync(caller, privateLegacy.Key, privateLegacy.Changes, privateLegacy.Secrets);
            Check((await store.SaveAsync(caller, Request(FieldEdit<string>.Set("http://127.0.0.1:80/v1")))).Code is
                ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges, "unchanged historical private destination is not falsely treated as new"); // 15
            var safeErrors = JsonSerializer.Serialize(rejected.Errors);
            Check(!safeErrors.Contains("example.com", StringComparison.Ordinal) && !safeErrors.Contains("synthetic-endpoint-credential", StringComparison.Ordinal)
                && new SecretsStore().LoadApiKey() == "synthetic-endpoint-credential", "rejection metadata never prints destination or credential"); // 16
            Console.WriteLine($"CFG_CHILD host-endpoints passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD host-endpoints passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-endpoints-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> HostDeviceChildAsync()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-hostdevices-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0; var failed = 0;
        void Check(bool condition, string label)
        {
            if (condition) passed++;
            else { failed++; Console.WriteLine("FAIL CFG_HOST_DEV " + label); }
        }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock);
            AppDatabase.Initialize();
            var parseMethod = typeof(SettingsStore).GetMethod("ParseLegacyDevices", BindingFlags.NonPublic | BindingFlags.Static)!;
            ImmutableArray<DeviceConfiguration> Parse(string json, bool stored = false)
            {
                try { return (ImmutableArray<DeviceConfiguration>)parseMethod.Invoke(null, new object[] { json, stored })!; }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            }
            var normalized = Parse("[{\"name\":\" synthetic-normalized \",\"timeoutSec\":-1,\"enable\":0}]")[0];
            Check(normalized.NameRaw == "synthetic-normalized" && normalized.TimeoutSeconds == 0 && normalized.Enable && normalized.Key is null,
                "legacy six-field normalization"); // 1
            Check(new[] { "[{\"name\":\"a\",\"password\":\"synthetic-private\"}]",
                "[{\"name\":\"a\",\"key\":\"00000000000000000000000000000000\"}]",
                "[{\"name\":\"a\"},{\"name\":\" A \"}]" }.All(json => Throws<InvalidOperationException>(() => Parse(json))),
                "new body unknown and colliding identity rejection"); // 2
            var legacyJson = "[{\"name\":\"synthetic-host-a\",\"enable\":true,\"model\":\"\",\"workdir\":\"\",\"tools\":\"\",\"timeoutSec\":99999,\"opaque\":{\"password\":\"synthetic-opaque-private\"}},"
                + "{\"name\":\"synthetic-host-b\",\"enable\":true,\"model\":\"synthetic-old-model\",\"workdir\":\"/synthetic\",\"tools\":\"synthetic-tools\",\"timeoutSec\":180}]";
            var initial = new AppSettings { AgentDevices = legacyJson, AgentModel = "synthetic-runtime-model",
                AgentWorkDir = "/synthetic-runtime", AgentTools = "synthetic-runtime-tools", Model = "synthetic-model",
                ModelBaseUrl = "https://example.com/v1", AgentMaskSensitive = true };
            new SettingsStore().Save(initial);
            AppDatabase.SetMeta("verification_token_initialized", "initialized");
            AppDatabase.Write(conn => AppDatabase.Exec(conn,
                "INSERT INTO configuration_field_modes(field_id,user_mode) VALUES($field,$mode)",
                ("$field", (int)ConfigurationFieldId.AgentDevices), ("$mode", (int)UserValueKind.Explicit)));
            new ModelProviderStore().UpsertMetadata(new("primary", 0, "synthetic-primary", "https://example.com/v1",
                "synthetic-model", "secret:apiKey", false));
            var runtime = new SettingsBox(initial);
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, CreateActualTokenAdapter(runtime));
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-hostdevice-owner", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.ReadOverview | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets);
            long Version() => AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            string Json() => AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1")!;
            string DevicesJson() => JsonNode.Parse(Json())!["AgentDevices"]!.GetValue<string>();
            ImmutableArray<StorageConfiguration.ConfigurationDeviceState> States()
            { using var conn = AppDatabase.Open(); return StorageConfiguration.ConfigurationStorageBridge.LoadDevices(conn); }
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            read.View!.Agent.AgentDevices.TryGetEditableValue(out var views);
            var a = views[0].Key; var b = views[1].Key;
            Check(read.Code == ConfigurationResultCode.Ok && Version() == 1 && a != b && DevicesJson() == legacyJson,
                "actual initializer allocates stable keys once without rewriting legacy payload"); // 3
            Check(views[0].Model.UserMode == UserValueKind.Inherited
                && views[0].Model.TryGetEditableValue(out var inheritedModel) && inheritedModel == "synthetic-runtime-model"
                && views[0].WorkDir.TryGetEditableValue(out var inheritedDirectory) && inheritedDirectory == "/synthetic-runtime"
                && views[0].Tools.TryGetEditableValue(out var inheritedTools) && inheritedTools == "synthetic-runtime-tools",
                "inherited empty requested fields expose actual effective values"); // 4
            var overview = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            var safe = JsonSerializer.Serialize(overview);
            Check(!overview.View!.Agent.AgentDevices.TryGetEditableValue(out _)
                && !safe.Contains("synthetic-opaque-private", StringComparison.Ordinal)
                && !safe.Contains("synthetic-host-a", StringComparison.Ordinal), "overview retains no raw device or opaque values"); // 5
            ConfigurationSaveRequest Request(ImmutableArray<DeviceConfiguration> devices, bool clear = false)
                => new("cfg-0.3.2", new(Version()), new(read.Generation, clock.WallSeconds, Guid.NewGuid()),
                    new(null, null, new AgentChanges { AgentDevices = clear ? FieldEdit<ImmutableArray<DeviceConfiguration>>.Clear()
                        : FieldEdit<ImmutableArray<DeviceConfiguration>>.Set(devices) }, null, null, null, null, null), ImmutableArray<SecretEdit>.Empty);
            var desired = Parse(legacyJson, true).Select((item, ordinal) => item with { Key = ordinal == 0 ? a : b }).ToImmutableArray();
            var oldVersion = Version();
            var same = Request(Parse("[{\"name\":\"synthetic-host-a\",\"enable\":true,\"model\":\"\",\"workdir\":\"\",\"tools\":\"\",\"timeoutSec\":99999},"
                + "{\"name\":\"synthetic-host-b\",\"enable\":true,\"model\":\"synthetic-old-model\",\"workdir\":\"/synthetic\",\"tools\":\"synthetic-tools\",\"timeoutSec\":180}]"));
            var noChanges = await store.SaveLegacyAsync(caller, same.Key, same.Changes, same.Secrets);
            Check(noChanges.Code == ConfigurationResultCode.NoChanges && Version() == oldVersion && DevicesJson() == legacyJson,
                "compact format alone is not a configuration change"); // 6
            Check(JsonNode.Parse(DevicesJson())![0]!["timeoutSec"]!.GetValue<int>() == 99999,
                "legacy equivalent timeout retains historical raw fact"); // 7
            var parameterRequest = Request(desired.SetItem(1, desired[1] with { Model = "synthetic-new-model" }));
            var parameters = await store.SaveAsync(caller, parameterRequest);
            Check(parameters.Code == ConfigurationResultCode.Ok && parameters.Receipt!.DeviceKeyAssignments.IsEmpty
                && States().Single(x => x.Key == b).DesiredName == "synthetic-host-b", "same-key parameters participate in Changed"); // 8
            var changedRead = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            changedRead.View!.Agent.AgentDevices.TryGetEditableValue(out var changedViews);
            Check(changedViews[1].Model.TryGetEditableValue(out var requestedModel) && requestedModel == "synthetic-new-model"
                && changedViews[1].Model.Display != "synthetic-new-model" && runtime.Current.AgentDevices == legacyJson,
                "requested values are separate from unapplied runtime"); // 9
            Check(DevicesJson().Contains("synthetic-opaque-private", StringComparison.Ordinal)
                && JsonNode.Parse(DevicesJson())!.AsArray().All(node => node!["key"] is null), "opaque DB facts retained without new legacy key fields"); // 10
            var renamed = desired.SetItem(1, desired[1] with { NameRaw = "synthetic-host-renamed", Model = "synthetic-new-model" });
            await store.SaveAsync(caller, Request(renamed));
            Check(States().Single(x => x.Key == b).RenamePending
                && StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-host-b", b)
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-host-renamed", b), "actual Host rename remains pending"); // 11
            oldVersion = Version();
            var confirmed = StorageConfiguration.ConfigurationStorageBridge.AuthorizeDeviceHello("synthetic-host-renamed");
            var audit = AppDatabase.Scalar<string>("SELECT action_detail FROM security_audit_log WHERE event_type='configuration_device_runtime' ORDER BY id DESC LIMIT 1")!;
            Check(confirmed.Allowed && confirmed.Key == b && Version() == oldVersion && audit.Contains(b.Value.ToString("N"), StringComparison.Ordinal)
                && !audit.Contains("synthetic-host", StringComparison.Ordinal), "actual Host runtime audit is atomic and safe"); // 12
            var renameRead = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            renameRead.View!.Agent.AgentDevices.TryGetEditableValue(out var renamedViews);
            Check(renamedViews[1].Name.TryGetEditableValue(out var nameRaw) && nameRaw == "synthetic-host-renamed"
                && renamedViews[1].RenameState == ApplyState.Applied, "confirmed identity editable as real nameRaw"); // 13
            var before = Json(); oldVersion = Version();
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_host_device_secret_fault BEFORE INSERT ON secrets WHEN NEW.name='ttsKey'
                BEGIN SELECT RAISE(ABORT,'synthetic_host_device_secret_fault'); END;
                """));
            using var credential = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-host-device-credential"));
            var fault = Request(renamed.SetItem(1, renamed[1] with { Enable = false, Model = "synthetic-fault-model" }));
            fault = fault with { Secrets = ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.TtsApiKey, credential)) };
            var failedSave = await store.SaveAsync(caller, fault);
            Check(failedSave.Code == ConfigurationResultCode.CommitFailed && Json() == before && Version() == oldVersion
                && States().Single(x => x.Key == b).DesiredEnabled && new SecretsStore().LoadTtsKey() is null,
                "actual user-device-secret-version transaction rolls back completely"); // 14
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_host_device_secret_fault"));
            var clearing = Request(ImmutableArray<DeviceConfiguration>.Empty, true);
            var deleted = await store.SaveAsync(caller, clearing);
            Check(deleted.Code == ConfigurationResultCode.Ok && (await store.SaveAsync(caller, clearing)).Replayed,
                "actual clear receipt replays without applying"); // 15
            Check(DevicesJson() == "[]" && States().All(state => state.Deleted)
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-host-b", null)
                && !StorageConfiguration.ConfigurationStorageBridge.CanDispatchDevice("synthetic-host-renamed", null), "actual clear persists all tombstones"); // 16
            var resurrect = Request(ImmutableArray.Create(new DeviceConfiguration(null, "synthetic-host-b", true, null, null, null, 0)));
            Check((await store.SaveLegacyAsync(caller, resurrect.Key, resurrect.Changes, resurrect.Secrets)).Code == ConfigurationResultCode.LegacyNeedsReview,
                "actual Legacy tombstone reuse requires review"); // 17
            var added = await store.SaveAsync(caller, Request(ImmutableArray.Create(new DeviceConfiguration(null, "synthetic-host-new", true, null, null, null, 0))));
            Check(added.Code == ConfigurationResultCode.Ok && added.Receipt!.DeviceKeyAssignments is { Length: 1 }
                && added.Receipt.DeviceKeyAssignments[0].Key != a && added.Receipt.DeviceKeyAssignments[0].Key != b,
                "actual new device receives new persistent identity"); // 18
            Console.WriteLine($"CFG_CHILD host-devices passed={passed} failed={failed}");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD host-devices passed={passed} failed={failed + 1} type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-hostdevices-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> GovernanceChildAsync(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-governance-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); passed++; }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock);
            AppDatabase.Initialize(); new SettingsStore().Save(new AppSettings { AiDesire = 10 });
            var participant = new SyntheticParticipant();
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            ConfigurationCaller Caller(int ordinal) => new("synthetic-authority", "synthetic-governance-" + ordinal, "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var caller = Caller(0);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            long Version() => AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            long Rows() => AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations");
            ConfigurationSaveRequest Request(int? desire = null) => new("cfg-0.3.2", new(Version()),
                new(header.Generation, clock.WallSeconds, Guid.NewGuid()), new(desire is null ? null : new BehaviorChanges {
                    AiDesire = FieldEdit<int>.Set(desire.Value) }, null, null, null, null, null, null, null), ImmutableArray<SecretEdit>.Empty);
            if (mode == "--configuration-global-capacity")
            {
                var original = Request();
                for (var subject = 0; subject < 16; subject++)
                    for (var ordinal = 0; ordinal < 256; ordinal++)
                    {
                        var result = await store.SaveAsync(Caller(subject), subject == 0 && ordinal == 0 ? original : Request());
                        if (result.Code != ConfigurationResultCode.NoChanges || result.Replayed)
                            throw new InvalidOperationException("synthetic_capacity_fill");
                    }
                Check(Rows() == 4096 && Version() == 0 && participant.Writes == 0, "4096 real NoChanges ledger entries"); // 1
                Check((await store.SaveAsync(Caller(16), Request())).Code == ConfigurationResultCode.CapacityExceeded
                    && Rows() == 4096, "global capacity rejects new caller without eviction"); // 2
                Check((await store.SaveAsync(caller, original)).Replayed && Rows() == 4096, "replay precedes global capacity"); // 3
                var differing = original with { Changes = Request(99).Changes };
                Check((await store.SaveAsync(caller, differing)).Code == ConfigurationResultCode.IdempotencyConflict,
                    "different parameters conflict before global capacity"); // 4
                Check(AppDatabase.Scalar<long>("SELECT COALESCE(MAX(length(CAST(receipt AS BLOB))),0) FROM configuration_operations") <= 8192,
                    "real receipt UTF8 budget holds at capacity"); // 5
                var restore = Path.Combine(root, "capacity-restore");
                Directory.CreateDirectory(Path.Combine(restore, "data"));
                File.Copy(AppDatabase.VacuumIntoBackup(), Path.Combine(restore, "data", "qqchat.db"));
                var start = new ProcessStartInfo("dotnet")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(typeof(ConfigurationContractTests).Assembly.Location);
                start.ArgumentList.Add("--configuration-capacity-restart");
                start.ArgumentList.Add(restore);
                start.ArgumentList.Add(JsonSerializer.Serialize(original));
                using var child = Process.Start(start) ?? throw new InvalidOperationException("configuration_capacity_restart_start");
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await child.WaitForExitAsync(timeout.Token); }
                catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
                var text = await output; Console.Write(text);
                Check(child.ExitCode == 0 && text.Contains("CFG_CHILD capacity-restart passed=8 failed=0", StringComparison.Ordinal)
                    && string.IsNullOrWhiteSpace(await error), "fresh generation shares capacity with all retained old-generation receipts");
                clock.AdvanceSeconds(86400);
                Check((await store.SaveAsync(Caller(16), Request())).Code == ConfigurationResultCode.NoChanges
                    && Rows() == 1, "TTL boundary prunes expired terminal receipts only"); // 6
                var pending = Request(20);
                Check((await store.SaveAsync(Caller(16), pending)).Code == ConfigurationResultCode.Ok
                    && store.TryStartApplyStep(Caller(16), pending.Key, ApplyStep.ContextWindow).CanRun,
                    "real pending uncertain receipt created after prune"); // 7
                clock.AdvanceSeconds(86400);
                Check((await store.SaveAsync(Caller(17), Request())).Code == ConfigurationResultCode.NoChanges
                    && Rows() == 2 && Version() == 1, "expired uncertain receipt is not pruned"); // 8
                Check(!store.TryStartApplyStep(Caller(16), pending.Key, ApplyStep.ContextWindow).CanRun
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations WHERE terminal=0") == 1,
                    "capacity cleanup never permits uncertain action replay"); // 9
                Console.WriteLine($"CFG_CHILD global-capacity passed={passed} failed=0");
            }
            else
            {
                var noChanges = Request(); await store.SaveAsync(caller, noChanges);
                Check(!store.TryStartApplyStep(caller, noChanges.Key, ApplyStep.TtsFileProjection).CanRun,
                    "NoChanges cannot claim configuration effect"); // 1
                Check(store.TryStartApplyStep(caller, Request().Key, ApplyStep.TtsFileProjection).Result.Code == ConfigurationResultCode.NotFound,
                    "uncommitted operation cannot claim effect"); // 2
                var changed = Request(20); await store.SaveAsync(caller, changed);
                Check(store.TryStartApplyStep(caller, changed.Key, (ApplyStep)99).Result.Code == ConfigurationResultCode.ValidationFailed,
                    "unknown effect rejected"); // 3
                participant.Allow = false;
                Check(store.TryStartApplyStep(caller, changed.Key, ApplyStep.TtsFileProjection).Result.Code == ConfigurationResultCode.Forbidden,
                    "claim reauthorizes trusted caller"); // 4
                participant.Allow = true;
                store.RecordApply(caller, changed.Key, ImmutableArray.Create(new ApplyStepReport(ApplyStep.TtsFileProjection, ApplyState.NotStarted, null)));
                var claimed = store.TryStartApplyStep(caller, changed.Key, ApplyStep.TtsFileProjection);
                Check(claimed.CanRun && claimed.Result.Receipt!.Steps.Single().State == ApplyState.Unknown
                    && claimed.Result.Receipt.Steps.Single().ErrorCode == "apply_unknown", "persist uncertain claim before action"); // 5
                Check(!store.TryStartApplyStep(caller, changed.Key, ApplyStep.TtsFileProjection).CanRun,
                    "uncertain claim cannot run twice"); // 6
                Check(store.RecordApply(caller, changed.Key, ImmutableArray.Create(new ApplyStepReport(ApplyStep.TtsFileProjection, ApplyState.NotStarted, null))).Code
                    == ConfigurationResultCode.ValidationFailed, "claimed stage cannot regress to unstarted"); // 7
                store.RecordApply(caller, changed.Key, ImmutableArray.Create(new ApplyStepReport(ApplyStep.TtsFileProjection, ApplyState.Applied, null)));
                Check(!store.TryStartApplyStep(caller, changed.Key, ApplyStep.TtsFileProjection).CanRun,
                    "finished effect not restarted"); // 8
                AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                    CREATE TRIGGER synthetic_claim_fault BEFORE UPDATE ON configuration_operations
                    BEGIN SELECT RAISE(ABORT,'synthetic_claim_fault'); END;
                    """));
                var failed = store.TryStartApplyStep(caller, changed.Key, ApplyStep.ContextWindow);
                Check(!failed.CanRun && failed.Result.Code == ConfigurationResultCode.StorageUnavailable
                    && !(await store.FindAsync(caller, changed.Key)).Receipt!.Steps.Any(x => x.Step == ApplyStep.ContextWindow),
                    "ledger failure cannot permit action"); // 9
                AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_claim_fault"));
                Check(store.TryStartApplyStep(caller, changed.Key, ApplyStep.ContextWindow).CanRun,
                    "rolled-back claim may retry safely"); // 10
                var winners = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
                    store.TryStartApplyStep(caller, changed.Key, ApplyStep.Schedulers).CanRun)));
                Check(winners.Count(x => x) == 1, "concurrent stage claim has one winner"); // 11
                await store.SaveAsync(caller, Request(30));
                var superseded = store.TryStartApplyStep(caller, changed.Key, ApplyStep.ModelProjection);
                Check(!superseded.CanRun && superseded.Result.Receipt!.Steps.Any(x => x.Step == ApplyStep.ModelProjection
                    && x.State == ApplyState.Failed && x.ErrorCode == "configuration_superseded")
                    && superseded.Result.Receipt.Steps.Single(x => x.Step == ApplyStep.TtsFileProjection).State == ApplyState.Applied,
                    "superseded unstarted effect blocked without rewriting history"); // 12
                Check(Version() == 2 && participant.Writes == 2 && new SecretsStore().LoadTtsKey() is null,
                    "claims never mutate user configuration or secrets"); // 13
                var restore = Path.Combine(root, "claim-restore");
                Directory.CreateDirectory(Path.Combine(restore, "data"));
                File.Copy(AppDatabase.VacuumIntoBackup(), Path.Combine(restore, "data", "qqchat.db"));
                var start = new ProcessStartInfo("dotnet")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(typeof(ConfigurationContractTests).Assembly.Location);
                start.ArgumentList.Add("--configuration-claim-restart");
                start.ArgumentList.Add(restore);
                start.ArgumentList.Add(JsonSerializer.Serialize(changed));
                start.ArgumentList.Add(JsonSerializer.Serialize(new ConfigurationVersion(Version())));
                using var child = Process.Start(start) ?? throw new InvalidOperationException("configuration_claim_restart_start");
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await child.WaitForExitAsync(timeout.Token); }
                catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
                var text = await output; Console.Write(text);
                Check(child.ExitCode == 0 && text.Contains("CFG_CHILD claim-restart passed=9 failed=0", StringComparison.Ordinal)
                    && string.IsNullOrWhiteSpace(await error), "fresh process never reruns durable uncertain or completed stages"); // 14
                Console.WriteLine($"CFG_CHILD apply-claims passed={passed} failed=0");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD governance passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-governance-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> ClaimRestartChildAsync(string directory, string requestJson, string versionJson)
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(full)!;
        if (Path.GetFileName(full) != "claim-restore"
            || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(parent), "^botagent-configuration-governance-[0-9a-f]{32}$")
            || !string.Equals(Path.GetDirectoryName(parent), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return 1;
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", full);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", full);
        var passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); passed++; }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock);
            AppDatabase.Initialize();
            var audits = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log");
            var participant = new SyntheticParticipant();
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-governance-0", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var request = JsonSerializer.Deserialize<ConfigurationSaveRequest>(requestJson)!;
            var version = JsonSerializer.Deserialize<ConfigurationVersion>(versionJson);
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Check(read.Code == ConfigurationResultCode.Ok && read.Generation != request.Key.Generation && read.Version == version,
                "fresh generation preserves configuration version"); // 1
            var found = await store.FindAsync(caller, request.Key);
            Check(found.Code == ConfigurationResultCode.Ok && found.Receipt is { CommitState: CommitState.Committed }
                && found.Receipt.Steps.Count(x => x.State == ApplyState.Unknown && x.ErrorCode == "apply_unknown") == 2
                && found.Receipt.Steps.Single(x => x.Step == ApplyStep.TtsFileProjection).State == ApplyState.Applied,
                "uncertain claims and completed effect survive process loss"); // 2
            Check(found.Receipt!.Steps.Single(x => x.Step == ApplyStep.ModelProjection) is
                { State: ApplyState.Failed, ErrorCode: "configuration_superseded" }, "supersession history survives restore"); // 3
            Check(!store.TryStartApplyStep(caller, request.Key, ApplyStep.ContextWindow).CanRun
                && !store.TryStartApplyStep(caller, request.Key, ApplyStep.Schedulers).CanRun,
                "uncertain restored effects cannot be executed again"); // 4
            Check(!store.TryStartApplyStep(caller, request.Key, ApplyStep.TtsFileProjection).CanRun,
                "completed restored effect cannot be executed again"); // 5
            Check(store.RecordApply(caller, request.Key, ImmutableArray.Create(new ApplyStepReport(
                ApplyStep.ContextWindow, ApplyState.NotStarted, null))).Code == ConfigurationResultCode.ValidationFailed,
                "restored uncertain stage cannot regress to unstarted"); // 6
            var replay = await store.SaveAsync(caller, request);
            Check(replay.Replayed && replay.Code == ConfigurationResultCode.Ok
                && replay.Receipt!.Steps.SequenceEqual(found.Receipt.Steps)
                && replay.Receipt.CommittedVersion == found.Receipt.CommittedVersion,
                "historical replay preserves the actual apply ledger"); // 7
            Check((await store.SaveAsync(caller, request with { Key = request.Key with { Nonce = Guid.NewGuid() } })).Code
                == ConfigurationResultCode.KeyGenerationMismatch, "old generation cannot mint new operations"); // 8
            Check(participant.Writes == 0 && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == version.Value
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == audits
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations WHERE terminal=0") == 2
                && new SecretsStore().LoadTtsKey() is null,
                "restart queries and rejected claims cause no user or secret writes and retain nonterminal facts"); // 9
            Console.WriteLine($"CFG_CHILD claim-restart passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD claim-restart passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static async Task<int?> CapacityRestartChildAsync(string directory, string requestJson)
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(full)!;
        if (Path.GetFileName(full) != "capacity-restore"
            || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(parent), "^botagent-configuration-governance-[0-9a-f]{32}$")
            || !string.Equals(Path.GetDirectoryName(parent), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return 1;
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", full);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", full);
        var passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); passed++; }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var audits = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log");
            var participant = new SyntheticParticipant();
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            ConfigurationCaller Caller(int ordinal) => new("synthetic-authority", "synthetic-governance-" + ordinal, "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var original = JsonSerializer.Deserialize<ConfigurationSaveRequest>(requestJson)!;
            var read = await store.ReadAsync(Caller(0), ConfigurationReadKind.Editable);
            long Rows() => AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations");
            ConfigurationSaveRequest Request() => new("cfg-0.3.2", read.Version,
                new(read.Generation, clock.WallSeconds, Guid.NewGuid()), new(null, null, null, null, null, null, null, null),
                ImmutableArray<SecretEdit>.Empty);
            Check(read.Code == ConfigurationResultCode.Ok && read.Generation != original.Key.Generation && read.Version.Value == 0,
                "controlled restart creates a new generation without changing configuration"); // 1
            Check(Rows() == 4096 && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_generations") == 2
                && AppDatabase.Scalar<long>("SELECT COUNT(DISTINCT generation) FROM configuration_operations") == 1,
                "all 4096 real old-generation records and their HMAC generation remain"); // 2
            Check((await store.SaveAsync(Caller(17), Request())).Code == ConfigurationResultCode.CapacityExceeded && Rows() == 4096,
                "new generation and new subject cannot bypass global capacity"); // 3
            var replay = await store.SaveAsync(Caller(0), original);
            Check(replay.Replayed && replay.Code == ConfigurationResultCode.NoChanges && Rows() == 4096,
                "old generation replays before the full-capacity gate"); // 4
            var conflict = original with { Changes = original.Changes with { Behavior = new BehaviorChanges { AiDesire = FieldEdit<int>.Set(40) } } };
            Check((await store.SaveAsync(Caller(0), conflict)).Code == ConfigurationResultCode.IdempotencyConflict && Rows() == 4096,
                "old generation protected intent conflict precedes capacity"); // 5
            var found = await store.FindAsync(Caller(0), original.Key);
            Check(found.Code == ConfigurationResultCode.Ok && found.Receipt is { Decision: ConfigurationDecision.NoChanges }
                && found.Receipt.Key == replay.Receipt!.Key
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == audits,
                "old-generation recovery returns the preserved receipt without new decision audits"); // 6
            clock.AdvanceSeconds(86400);
            Check((await store.SaveAsync(Caller(17), Request())).Code == ConfigurationResultCode.NoChanges && Rows() == 1
                && (await store.FindAsync(Caller(0), original.Key)).Code == ConfigurationResultCode.KeyExpired,
                "expired terminal old-generation receipts alone release capacity"); // 7
            Check(participant.Writes == 0 && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 0
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == audits + 1
                && AppDatabase.Scalar<long>("SELECT COALESCE(MIN(length(hmac_key)),0) FROM configuration_generations") == 32
                && new SecretsStore().LoadTtsKey() is null,
                "one new NoChanges decision audits once without configuration or secret effects"); // 8
            Console.WriteLine($"CFG_CHILD capacity-restart passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD capacity-restart passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static StorageConfiguration.ConfigurationStorageParticipant CreateActualTokenAdapter(SettingsBox runtime)
    {
        Func<ConfigurationCaller, ConfigurationAccess, bool> authorization = (_, _) => true;
        Func<SqliteConnection, ImmutableArray<StorageConfiguration.SecretMutation>, ImmutableArray<SecretMetadata>> sources
            = (connection, edits) => Enum.GetValues<WritableSecretSlot>().Select(slot =>
            {
                var kind = edits.FirstOrDefault(edit => edit.Slot == slot).Kind;
                var configured = kind == SecretEditKind.Replace || (kind == SecretEditKind.Keep
                    && StorageSecrets.LoadInTransaction(connection, slot) is not null);
                return new SecretMetadata(slot, configured, configured ? CredentialSource.Database : CredentialSource.None);
            }).ToImmutableArray();
        Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) =>
            new(ImmutableArray<PluginConfigurationView>.Empty, ImmutableArray<ProviderConfigurationView>.Empty);
        Func<ConfigurationFieldId, ConfigurationValueSource> provenance = _ => ConfigurationValueSource.UserConfiguration;
        Func<ConfigurationFieldId, FieldEffect> effect = _ => FieldEffect.Restart;
        var type = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("actual_host_adapter_missing");
        return (StorageConfiguration.ConfigurationStorageParticipant)type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single().Invoke(new object[] { runtime, authorization, sources, registered, provenance, effect, true });
    }

    private static async Task<int?> TokenCaseChildAsync(string mode)
    {
        var cases = new[] { "legacy", "empty", "missing", "new", "deleted", "initialized", "delete-restart", "collision", "bad-type",
            "bad-unicode", "oversize", "nul", "conflict", "audit-fault", "version-fault", "generation-fault", "overflow" };
        if (!cases.Contains(mode, StringComparer.Ordinal)) return 1;
        // Only environment names are enumerated; no values are displayed or reused by this fixture.
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-module-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("BOTAGENT_FEISHU_VERIFICATION_TOKEN", "synthetic-environment-seed");
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL configuration Token " + mode + " " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            ClockBindings.InitializePlatforms();
            var clock = new SyntheticClock();
            Clock.Use(clock);
            AppDatabase.Initialize();
            var initial = new AppSettings { FeishuVerificationToken = "synthetic-legacy-token", AiDesire = 25,
                Model = "synthetic-model", ModelBaseUrl = "https://example.com/v1" };
            if (mode != "new")
            {
                var original = JsonSerializer.SerializeToNode(initial)!.AsObject();
                original["SyntheticUnknownFact"] = "synthetic-preserved";
                if (mode == "empty") original[nameof(AppSettings.FeishuVerificationToken)] = "  ";
                if (mode == "missing") original.Remove(nameof(AppSettings.FeishuVerificationToken));
                if (mode == "collision") original["feishuverificationtoken"] = "synthetic-alias-token";
                if (mode == "bad-type") original[nameof(AppSettings.FeishuVerificationToken)] = true;
                if (mode == "oversize") original[nameof(AppSettings.FeishuVerificationToken)] = new string('x', 16385);
                if (mode == "nul") original[nameof(AppSettings.FeishuVerificationToken)] = "synthetic\0token";
                var json = original.ToJsonString();
                if (mode == "bad-unicode") json = "{\"FeishuVerificationToken\":\"\\ud800\",\"SyntheticUnknownFact\":\"synthetic-preserved\"}";
                AppDatabase.Write(connection => AppDatabase.Exec(connection,
                    "INSERT INTO settings(id,json,updated_unix) VALUES(1,$json,0)", ("$json", json)));
            }
            if (mode is "initialized" or "conflict")
            {
                AppDatabase.Write(connection => StorageSecrets.SaveInTransaction(connection,
                    WritableSecretSlot.FeishuVerificationToken, "synthetic-existing-slot", clock.WallSeconds));
                if (mode == "conflict") AppDatabase.Write(connection => AppDatabase.Exec(connection,
                    "DELETE FROM meta WHERE key='verification_token_initialized'"));
            }
            if (mode == "deleted") AppDatabase.SetMeta("verification_token_initialized", "deleted");
            if (mode == "overflow") AppDatabase.Write(connection => AppDatabase.Exec(connection,
                "UPDATE configuration_state SET version=$v WHERE id=1", ("$v", long.MaxValue)));
            var fault = mode switch { "audit-fault" => "security_audit_log", "version-fault" => "configuration_state",
                "generation-fault" => "configuration_generations", _ => null };
            if (fault is not null) AppDatabase.Write(connection => AppDatabase.Exec(connection,
                "CREATE TRIGGER synthetic_token_fault BEFORE " + (mode == "version-fault" ? "UPDATE" : "INSERT")
                    + " ON " + fault + " BEGIN SELECT RAISE(ABORT,'synthetic_token_fault'); END;"));
            var originalJson = AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1");
            var originalMarker = AppDatabase.GetMeta("verification_token_initialized");
            string? originalSlot;
            using (var connection = AppDatabase.Open()) originalSlot = StorageSecrets.LoadInTransaction(connection, WritableSecretSlot.FeishuVerificationToken);
            var adapter = CreateActualTokenAdapter(new SettingsBox(initial));
            StorageConfiguration.ConfigurationStore? store = null;
            Exception? failure = null;
            try { store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, adapter); }
            catch (Exception ex) { failure = ex; }
            var invalid = mode is "collision" or "bad-type" or "bad-unicode" or "oversize" or "nul" or "conflict" or "overflow";
            if (invalid || fault is not null)
            {
                Check(failure is not null && store is null, "invalid or injected migration cannot activate");
                string? slot;
                using (var connection = AppDatabase.Open()) slot = StorageSecrets.LoadInTransaction(connection, WritableSecretSlot.FeishuVerificationToken);
                Check(AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1") == originalJson
                    && AppDatabase.GetMeta("verification_token_initialized") == originalMarker && slot == originalSlot,
                    "migration failure preserves old JSON slot and marker");
                Check(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == (mode == "overflow" ? long.MaxValue : 0)
                    && AppDatabase.Scalar<long>("SELECT floor FROM configuration_state WHERE id=1") == 0
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_generations") == 0
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == 0,
                    "migration failure rolls back version floor generation and audit");
            }
            else
            {
                Check(failure is null && store is not null, "valid controlled migration activates");
                var expected = mode switch { "new" => "synthetic-environment-seed", "initialized" => "synthetic-existing-slot",
                    "legacy" or "delete-restart" => "synthetic-legacy-token", _ => null };
                using (var connection = AppDatabase.Open())
                    Check(StorageSecrets.LoadInTransaction(connection, WritableSecretSlot.FeishuVerificationToken) == expected,
                        "slot respects old fact marker and first-deployment seed precedence");
                Check(AppDatabase.GetMeta("verification_token_initialized") == (mode == "deleted" ? "deleted" : "initialized"),
                    "old empty is initialized not explicit deletion");
                var json = AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1");
                Check(json is null || !JsonNode.Parse(json)!.AsObject().Any(property =>
                    string.Equals(property.Key, nameof(AppSettings.FeishuVerificationToken), StringComparison.OrdinalIgnoreCase)),
                    "plain property removed from persisted user facts");
                Check(mode == "new" || AppDatabase.Scalar<string>("SELECT json_extract(json,'$.SyntheticUnknownFact') FROM settings WHERE id=1")
                    == "synthetic-preserved", "migration preserves opaque legacy facts");
                Check(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 1
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == 1
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_generations") == 1,
                    "actual token migration commits exactly one version audit and generation");
                Check(ReferenceEquals(store, StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, adapter))
                    && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 1,
                    "same activation is idempotent");
                var storedBeforeBoot = json;
                BotConfig.IgnoredBehaviorEnvVars.Clear();
                var loaded = BotConfig.Load();
                Check(loaded.FeishuVerificationToken == (expected ?? string.Empty), "actual BotConfig uses initialized slot not environment reseed");
                Check(AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1") == storedBeforeBoot,
                    "marker-ready BotConfig never persists runtime overlay or plain token");
                Check(BotConfig.IgnoredBehaviorEnvVars.All(warning => !warning.Contains("synthetic-", StringComparison.Ordinal)
                    && !warning.Contains("legacy-token", StringComparison.Ordinal) && !warning.Contains("environment-seed", StringComparison.Ordinal)),
                    "behavior warnings contain neither secret values nor fragments");
                var caller = new ConfigurationCaller("synthetic-token-authority", "synthetic-token-owner", "bot",
                    ConfigurationAccess.ReadOverview | ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration
                        | ConfigurationAccess.EditAllowedSecrets);
                var read = await store!.ReadAsync(caller, ConfigurationReadKind.Overview);
                Check(read.Secrets.Single(secret => secret.Slot == WritableSecretSlot.FeishuVerificationToken).Configured == (expected is not null)
                    && !JsonSerializer.Serialize(read).Contains("synthetic-legacy-token", StringComparison.Ordinal),
                    "actual configuration read is secret metadata only");
                if (mode == "delete-restart")
                {
                    SeedSyntheticProviderMetadata(new("primary", 0, "synthetic-primary", "https://example.com/v1",
                        "synthetic-model", "secret:apiKey", false));
                    var key = new ConfigurationOperationKey(read.Generation, clock.WallSeconds, Guid.NewGuid());
                    var request = new ConfigurationSaveRequest("cfg-0.3.2", read.Version, key,
                        new(null, null, null, null, null, null, null, null),
                        ImmutableArray.Create(SecretEdit.Delete(WritableSecretSlot.FeishuVerificationToken)));
                    var deleted = await store.SaveAsync(caller, request);
                    Check(deleted.Code == ConfigurationResultCode.Ok && deleted.CurrentVersion.Value == 2
                        && AppDatabase.GetMeta("verification_token_initialized") == "deleted",
                        "actual explicit Delete commits irreversible seed suppression marker");
                    var restore = Path.Combine(root, "token-restore");
                    Directory.CreateDirectory(Path.Combine(restore, "data"));
                    File.Copy(AppDatabase.VacuumIntoBackup(), Path.Combine(restore, "data", "qqchat.db"));
                    var start = new ProcessStartInfo("dotnet")
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add(typeof(ConfigurationContractTests).Assembly.Location);
                    start.ArgumentList.Add("--configuration-token-restart");
                    start.ArgumentList.Add(restore);
                    start.ArgumentList.Add(JsonSerializer.Serialize(key));
                    using var child = Process.Start(start) ?? throw new InvalidOperationException("configuration_token_restart_start");
                    var output = child.StandardOutput.ReadToEndAsync();
                    var error = child.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    try { await child.WaitForExitAsync(timeout.Token); }
                    catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
                    var text = await output;
                    Console.Write(text);
                    Check(child.ExitCode == 0 && text.Contains("CFG_CHILD token-restart passed=5 failed=0", StringComparison.Ordinal)
                        && string.IsNullOrWhiteSpace(await error), "fresh process restores Delete without environment resurrection");
                    using var connection = AppDatabase.Open();
                    Check(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 2
                        && StorageSecrets.LoadInTransaction(connection, WritableSecretSlot.FeishuVerificationToken) is null,
                        "backup child never replaces live committed token state");
                }
            }
            Console.WriteLine($"CFG_CHILD token-{mode} passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD token-{mode} passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-module-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_token_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> TokenRestartChildAsync(string directory, string keyJson)
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(full)!;
        if (Path.GetFileName(full) != "token-restore"
            || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(parent), "^botagent-configuration-module-[0-9a-f]{32}$")
            || !string.Equals(Path.GetDirectoryName(parent), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return 1;
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", full);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", full);
        Environment.SetEnvironmentVariable("BOTAGENT_FEISHU_VERIFICATION_TOKEN", "synthetic-restart-environment");
        var passed = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("synthetic_token_restart_assertion"); passed++; }
        try
        {
            ClockBindings.InitializePlatforms();
            Clock.Use(new SyntheticClock());
            AppDatabase.Initialize();
            var originalJson = AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1");
            var audits = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log");
            var adapter = CreateActualTokenAdapter(new SettingsBox(new AppSettings()));
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, adapter);
            var key = JsonSerializer.Deserialize<ConfigurationOperationKey>(keyJson);
            using (var connection = AppDatabase.Open())
                Check(AppDatabase.GetMeta("verification_token_initialized") == "deleted"
                    && StorageSecrets.LoadInTransaction(connection, WritableSecretSlot.FeishuVerificationToken) is null
                    && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 2);
            BotConfig.IgnoredBehaviorEnvVars.Clear();
            var settings = BotConfig.Load();
            Check(settings.FeishuVerificationToken == string.Empty
                && AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1") == originalJson
                && BotConfig.IgnoredBehaviorEnvVars.All(warning => !warning.Contains("synthetic-", StringComparison.Ordinal)));
            var caller = new ConfigurationCaller("synthetic-token-authority", "synthetic-token-owner", "bot",
                ConfigurationAccess.ReadOverview | ConfigurationAccess.SaveConfiguration);
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            Check(read.Generation != key.Generation && read.Version.Value == 2);
            var found = await store.FindAsync(caller, key);
            Check(found.Code == ConfigurationResultCode.Ok && found.Receipt is { CommittedVersion.Value: 2 }
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == audits);
            Check(!read.Secrets.Single(secret => secret.Slot == WritableSecretSlot.FeishuVerificationToken).Configured
                && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 2);
            Console.WriteLine($"CFG_CHILD token-restart passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD token-restart passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static int InitializationChild()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-module-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        try
        {
            ClockBindings.InitializePlatforms();
            var clock = new SyntheticClock();
            Clock.Use(clock);
            AppDatabase.Initialize();
            AppDatabase.SetMeta("synthetic:init-proof", "original");
            var auditFault = true;
            var participant = new SyntheticParticipant
            {
                Initialization = (connection, now) =>
                {
                    if (now != clock.WallSeconds) throw new InvalidOperationException("synthetic_init_clock");
                    StorageDatabase.SetMetaInTransaction(connection, "synthetic:init-proof", "candidate");
                    return true;
                },
                InitializationAudit = (connection, before, after) =>
                {
                    if (after.Value != before.Value + 1) throw new InvalidOperationException("synthetic_init_version");
                    new AuditLogStore().AppendInTransaction(connection,
                        new AuditEvent("configuration_migration", "synthetic-writer", "bot", "synthetic_initialization", "cfg-0.3.2"));
                    if (auditFault) throw new IOException("synthetic_init_audit_fault");
                }
            };
            Check(Throws<IOException>(() => StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant)),
                "initialization audit fault propagates");
            Check(AppDatabase.GetMeta("synthetic:init-proof") == "original"
                && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 0
                && AppDatabase.Scalar<long>("SELECT floor FROM configuration_state WHERE id=1") == 0
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_generations") == 0
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == 0,
                "initialization audit fault rolls back facts version floor audit generation");
            auditFault = false;
            AppDatabase.Write(connection => AppDatabase.Exec(connection,
                "UPDATE configuration_state SET version=$v WHERE id=1", ("$v", long.MaxValue)));
            Check(Throws<OverflowException>(() => StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant)),
                "initialization version overflow rejects");
            Check(AppDatabase.GetMeta("synthetic:init-proof") == "original"
                && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == long.MaxValue
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_generations") == 0,
                "initialization overflow rolls back prior writes");
            AppDatabase.Write(connection => AppDatabase.Exec(connection, "UPDATE configuration_state SET version=0 WHERE id=1"));
            clock.Monotonic = -1;
            Check(Throws<InvalidOperationException>(() => StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant))
                && participant.InitializationCalls == 2, "invalid activation clock rejects before initialization");
            clock.Monotonic = 0;
            AppDatabase.Write(connection => AppDatabase.Exec(connection, """
                CREATE TRIGGER synthetic_init_version_fault BEFORE UPDATE ON configuration_state WHEN NEW.version>0
                BEGIN SELECT RAISE(ABORT,'synthetic_init_version_fault'); END;
                """));
            try
            {
                Check(Throws<SqliteException>(() => StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant))
                    && AppDatabase.GetMeta("synthetic:init-proof") == "original"
                    && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 0,
                    "initialization version write fault rolls back facts");
            }
            finally { AppDatabase.Write(connection => AppDatabase.Exec(connection, "DROP TRIGGER synthetic_init_version_fault")); }
            var active = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            Check(AppDatabase.GetMeta("synthetic:init-proof") == "candidate"
                && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 1
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_generations") == 1
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == 1,
                "successful retry commits initialization version audit generation together");
            Check(ReferenceEquals(active, StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant))
                && participant.InitializationCalls == 4 && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 1,
                "same binding activation never repeats initialization");
            Console.WriteLine($"CFG_CHILD initialization passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD initialization passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-module-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_init_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> HostAdapterChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-module-" + Guid.NewGuid().ToString("N"));
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition)
            {
                Console.WriteLine("FAIL configuration real Host adapter " + label);
                throw new InvalidOperationException(label);
            }
            passed++;
            Console.WriteLine("PASS configuration real Host adapter " + label);
        }
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        foreach (var name in new[] { "BOTAGENT_MODEL", "QQCHAT_MODEL", "OPENAI_MODEL",
            "BOTAGENT_BASE_URL", "QQCHAT_BASE_URL", "OPENAI_BASE_URL" })
            Environment.SetEnvironmentVariable(name, null);
        Environment.SetEnvironmentVariable("BOTAGENT_MODEL", "synthetic-env-model");
        Environment.SetEnvironmentVariable("BOTAGENT_BASE_URL", "https://example.com/env");
        try
        {
            ClockBindings.InitializePlatforms();
            var clock = new SyntheticClock();
            Clock.Use(clock);
            AppDatabase.Initialize();
            var initial = new AppSettings { AiDesire = 10, BotPersona = "synthetic-private-persona",
                Model = "synthetic-user-model", ModelOverride = "synthetic-user-model",
                ModelBaseUrl = "https://example.com/user", ModelBaseUrlOverride = "https://example.com/user",
                AgentDevices = string.Empty, PlatformPolicies = new() };
            var settings = new SettingsStore();
            settings.Save(initial);
            AppDatabase.Write(conn => AppDatabase.Exec(conn,
                "UPDATE settings SET json=json_set(json,'$.SyntheticUnknownFact','synthetic-opaque') WHERE id=1"));
            var providers = new ModelProviderStore();
            providers.EnsurePrimary(initial);
            var runtime = new SettingsBox(initial);
            var allow = true;
            Func<ConfigurationCaller, ConfigurationAccess, bool> authorization = (caller, _) => allow
                && caller.AuthorityId == "synthetic-host-authority" && caller.SubjectId == "synthetic-host-owner";
            Func<SqliteConnection, ImmutableArray<StorageConfiguration.SecretMutation>, ImmutableArray<SecretMetadata>> sources
                = (connection, edits) => Enum.GetValues<WritableSecretSlot>().Select(slot =>
                {
                    var kind = edits.FirstOrDefault(edit => edit.Slot == slot).Kind;
                    var configured = kind == SecretEditKind.Replace || (kind == SecretEditKind.Keep
                        && StorageSecrets.LoadInTransaction(connection, slot) is not null);
                    return new SecretMetadata(slot, configured, configured ? CredentialSource.Database : CredentialSource.None);
                }).ToImmutableArray();
            Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) =>
                new(ImmutableArray<PluginConfigurationView>.Empty, ImmutableArray<ProviderConfigurationView>.Empty);
            Func<ConfigurationFieldId, ConfigurationValueSource> provenance = _ => ConfigurationValueSource.UserConfiguration;
            Func<ConfigurationFieldId, FieldEffect> effect = _ => FieldEffect.Restart;
            var adapterType = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("actual_host_adapter_missing");
            var constructor = adapterType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var adapter = (StorageConfiguration.ConfigurationStorageParticipant)constructor.Invoke(
                new object[] { runtime, authorization, sources, registered, provenance, effect, true });
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, adapter);
            var caller = new ConfigurationCaller("synthetic-host-authority", "synthetic-host-owner", "bot",
                ConfigurationAccess.ReadOverview | ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration
                    | ConfigurationAccess.EditAllowedSecrets);
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            var generation = read.Generation;
            var initialVersion = read.Version.Value;
            ConfigurationSaveRequest Request(ConfigurationPatch? changes = null, ImmutableArray<SecretEdit> secrets = default)
                => new("cfg-0.3.2", new(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1")),
                    new(generation, clock.WallSeconds, Guid.NewGuid()), changes ?? new(null, null, null, null, null, null, null, null),
                    secrets.IsDefault ? ImmutableArray<SecretEdit>.Empty : secrets);
            var empty = Request();
            var noChanges = await StorageConfiguration.ConfigurationStorageBridge.SaveLegacyAsync(store, caller,
                empty.Key, empty.Changes, empty.Secrets);
            Check(noChanges.Code == ConfigurationResultCode.NoChanges && noChanges.CurrentVersion.Value == initialVersion
                && providers.Find("primary")?.SecretKeyRef == "env:QQCHAT_API_KEY", "all Keep cannot rewrite empty primary binding");
            Check(read.Code == ConfigurationResultCode.Ok && read.View is not null && read.Fields.Length == 142
                && read.Fields.Select(rule => (int)rule.Id).OrderBy(id => id).SequenceEqual(Enumerable.Range(1, 142)),
                "actual 142 typed projection and descriptors");
            Check(read.View!.Behavior.BotPersona.Visibility == FieldVisibility.Hidden
                && !read.View.Behavior.BotPersona.TryGetEditableValue(out _)
                && !JsonSerializer.Serialize(read).Contains(initial.BotPersona, StringComparison.Ordinal), "Overview drops raw editable value");
            var editable = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Check(editable.View!.Behavior.BotPersona.TryGetEditableValue(out var persona) && persona == initial.BotPersona
                && editable.View.Behavior.BotPersona.Rule.LegacyNeedsReview, "legacy user facts are not fake tracked values");
            using var input = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-host-credential"));
            var saveRequest = Request(new(new() { AiDesire = FieldEdit<int>.Set(42) }, null, null, null, null, null, null, null),
                ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, input)));
            var saved = await store.SaveAsync(caller, saveRequest);
            Check(saved.Code == ConfigurationResultCode.Ok && saved.CurrentVersion.Value == initialVersion + 1 && settings.Load().AiDesire == 42
                && new SecretsStore().LoadApiKey() == "synthetic-host-credential", "real settings and fixed secret commit together");
            Check(providers.Find("primary")?.SecretKeyRef == "secret:apiKey"
                && AppDatabase.Scalar<long>("SELECT user_mode FROM configuration_field_modes WHERE field_id=26") == 2,
                "real primary metadata and field mode share commit");
            var audit = AppDatabase.Scalar<string>("SELECT action_detail FROM security_audit_log ORDER BY id DESC LIMIT 1")!;
            Check(!audit.Contains("synthetic-host-credential", StringComparison.Ordinal)
                && !audit.Contains(initial.BotPersona, StringComparison.Ordinal), "real audit contains no synthetic secret or persona");
            var audits = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log");
            var replay = await store.SaveAsync(caller, saveRequest);
            Check(replay.Replayed && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == audits
                && runtime.Current.AiDesire == 10, "replay cannot apply or audit again");
            editable = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Check(editable.View!.Behavior.AiDesire.TryGetEditableValue(out var requested) && requested == 42
                && editable.View.Behavior.AiDesire.Display == "10" && editable.LatestApplyState == ApplyState.NotStarted,
                "user facts differ from actual un-applied runtime");
            var existingPrimary = providers.Find("primary")!;
            SeedSyntheticProviderMetadata(new(existingPrimary.Id, existingPrimary.Priority, existingPrimary.Name,
                existingPrimary.BaseUrl, existingPrimary.ModelName, "env:QQCHAT_API_KEY", existingPrimary.IsEnabled));
            using var rotated = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-host-rotated"));
            var rotation = await store.SaveAsync(caller, Request(secrets: ImmutableArray.Create(
                SecretEdit.Replace(WritableSecretSlot.ModelApiKey, rotated))));
            Check(rotation.Code == ConfigurationResultCode.Ok && providers.Find("primary")?.SecretKeyRef == "secret:apiKey"
                && new SecretsStore().LoadApiKey() == "synthetic-host-rotated", "explicit Replace selects primary DB binding even when source metadata is unchanged");
            var unauthorizedEndpoint = await store.SaveAsync(caller, Request(new(new() { AiDesire = FieldEdit<int>.Set(44) },
                new() { ModelBaseUrl = FieldEdit<string>.Set("https://example.com/unapproved") }, null, null, null, null, null, null)));
            Check(unauthorizedEndpoint.Code == ConfigurationResultCode.ValidationFailed
                && unauthorizedEndpoint.Errors.Any(error => error.Address.Field == ConfigurationFieldId.ModelBaseUrl)
                && settings.Load().AiDesire == 42 && settings.Load().ModelBaseUrlOverride == initial.ModelBaseUrlOverride,
                "missing trusted endpoint authorization rejects credential-bearing endpoint change");
            var deletion = Request(new(new() { AiDesire = FieldEdit<int>.Set(43) }, null, null, null, null, null, null, null),
                ImmutableArray.Create(SecretEdit.Delete(WritableSecretSlot.ModelApiKey)));
            var rejected = await store.SaveAsync(caller, deletion);
            Check(rejected.Code == ConfigurationResultCode.ValidationFailed && rejected.Errors.Any(error => error.Code == "credential_required")
                && settings.Load().AiDesire == 42 && new SecretsStore().LoadApiKey() is not null && rejected.CurrentVersion.Value == initialVersion + 2,
                "Strict rejects enabled provider deletion before any partial write");
            var legacyDelete = await StorageConfiguration.ConfigurationStorageBridge.SaveLegacyAsync(store, caller,
                deletion.Key, deletion.Changes, deletion.Secrets);
            Check(legacyDelete.Code == ConfigurationResultCode.Ok && legacyDelete.CurrentVersion.Value == initialVersion + 3
                && new SecretsStore().LoadApiKey() is null && settings.Load().AiDesire == 43, "Legacy preserves allowed historical deletion");
            Check(AppDatabase.Scalar<string>("SELECT json_extract(json,'$.SyntheticUnknownFact') FROM settings WHERE id=1") == "synthetic-opaque",
                "unrecognized old DB fact is preserved not client edited");
            var primary = providers.Find("primary")!;
            SeedSyntheticProviderMetadata(new(primary.Id, primary.Priority, primary.Name, primary.BaseUrl, primary.ModelName, primary.SecretKeyRef, false));
            var absentDelete = await store.SaveAsync(caller, Request(secrets: ImmutableArray.Create(SecretEdit.Delete(WritableSecretSlot.ModelApiKey))));
            Check(absentDelete.Code == ConfigurationResultCode.NoChanges, "disabled provider permits unchanged empty credential");
            var model = await store.SaveAsync(caller, Request(new(null, new() { Model = FieldEdit<string>.Set("synthetic-updated-model") },
                null, null, null, null, null, null)));
            Check(model.Code == ConfigurationResultCode.Ok && settings.Load().ModelOverride == "synthetic-updated-model"
                && providers.Find("primary")?.ModelName == "synthetic-updated-model", "model override and primary metadata use real same-connection primitives");
            var beforeClear = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            var unregisteredClear = await store.SaveAsync(caller, Request(new(null,
                new() { Model = FieldEdit<string>.Clear(), ModelBaseUrl = FieldEdit<string>.Clear() }, null, null, null, null, null, null)));
            Check(unregisteredClear.Code == ConfigurationResultCode.ValidationFailed
                && unregisteredClear.Errors.Any(error => error.Code == "endpoint_authorization_required")
                && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == beforeClear
                && settings.Load().ModelOverride == "synthetic-updated-model"
                && providers.Find("primary") is { ModelName: "synthetic-updated-model", BaseUrl: "https://example.com/user" },
                "Clear to unregistered fallback rejects the whole batch without version or metadata changes");
            // Trusted catalog setup for this synthetic fixture, not body self-authorization.
            SeedSyntheticProviderMetadata(new("synthetic-fallback", 10, "Synthetic fallback", "https://example.com/env",
                "synthetic-env-model", "secret:apiKey", false));
            var cleared = await store.SaveAsync(caller, Request(new(null,
                new() { Model = FieldEdit<string>.Clear(), ModelBaseUrl = FieldEdit<string>.Clear() }, null, null, null, null, null, null)));
            var user = settings.Load();
            Check(cleared.Code == ConfigurationResultCode.Ok && user.ModelOverride is null && user.ModelBaseUrlOverride is null
                && user.Model != "synthetic-env-model" && user.ModelBaseUrl != "https://example.com/env"
                && providers.Find("primary") is { ModelName: "synthetic-env-model", BaseUrl: "https://example.com/env" },
                "Clear inherits captured fixed fallback without persisting environment user values");
            Environment.SetEnvironmentVariable("BOTAGENT_MODEL", "synthetic-later-env-model");
            editable = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Check(editable.View!.Model.Model.TryGetEditableValue(out var effectiveModel) && effectiveModel == initial.Model
                && providers.Find("primary")?.ModelName == "synthetic-env-model", "Read reports actual runtime and does not reread environment");
            var before = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            var complex = await store.SaveAsync(caller, Request(new(new() { AiDesire = FieldEdit<int>.Set(44) }, null,
                new() { AgentDevices = FieldEdit<ImmutableArray<DeviceConfiguration>>.Set(ImmutableArray.Create(
                    new DeviceConfiguration(new DeviceKey(Guid.NewGuid()), "synthetic-device", true, null, null, null, 0))) }, null, null, null, null, null)));
            Check(complex.Code == ConfigurationResultCode.ValidationFailed && complex.Errors.Any(error => error.Code == "invalid_device_identity")
                && settings.Load().AiDesire == 43 && complex.CurrentVersion.Value == before, "invalid device identity rejects entire batch safely");
            // Deliberately corrupt only this synthetic legacy fact; a normal Save is now correctly fenced.
            AppDatabase.Write(conn => AppDatabase.Exec(conn,
                "UPDATE settings SET json=json_set(json,'$.AgentDevices',$devices) WHERE id=1",
                ("$devices", "[{\"name\":\"synthetic-unmapped-device\"}]")));
            var unsupportedRead = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            Check(unsupportedRead.Code == ConfigurationResultCode.LegacyNeedsReview && unsupportedRead.View is null
                && unsupportedRead.Version.Value == before && unsupportedRead.Generation == generation,
                "unmigrated legacy compound fact is not fabricated empty view");
            allow = false;
            var denied = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Check(denied.Code == ConfigurationResultCode.Forbidden && denied.View is null
                && (await store.SaveAsync(caller, Request())).Code == ConfigurationResultCode.Forbidden, "trusted authorization denies forged flags");
            Check(AppDatabase.FilePath == StorageDatabase.FilePath && StorageDatabase.Scalar<long>("PRAGMA user_version") == 9,
                "real adapter uses the sole bound Storage database");
            Console.WriteLine($"CFG_CHILD host passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD host passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-module-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_host_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> RestartChildAsync(string[] args)
    {
        var full = Path.GetFullPath(args[1]);
        var parent = Path.GetDirectoryName(full)!;
        if (Path.GetFileName(full) != "configuration-restore"
            || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(parent), "^botagent-review-remediation-[0-9a-f]{32}$")
            || !string.Equals(Path.GetDirectoryName(parent), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return 1;
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", full);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", full);
        try
        {
            ClockBindings.InitializePlatforms();
            var clock = new SyntheticClock();
            Clock.Use(clock);
            AppDatabase.Initialize();
            var participant = new SyntheticParticipant();
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var request = JsonSerializer.Deserialize<ConfigurationSaveRequest>(args[2])!;
            var version = JsonSerializer.Deserialize<ConfigurationVersion>(args[3]);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-owner", "bot",
                ConfigurationAccess.ReadOverview | ConfigurationAccess.SaveConfiguration);
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            Check(read.Code == ConfigurationResultCode.Ok && read.Generation != request.Key.Generation
                && read.Version == version, "restart advances generation but preserves version");
            Check(StorageDatabase.Scalar<long>("SELECT user_mode FROM configuration_field_modes WHERE field_id=26") == 2
                && StorageDatabase.GetMeta("configuration_schema") == "1", "restart restores module and user facts");
            Check(StorageDatabase.GetMeta("verification_token_initialized") == "deleted",
                "restart restores explicit token deletion marker");
            var found = await store.FindAsync(caller, request.Key);
            Check(found.Code == ConfigurationResultCode.Ok && found.Receipt is { ApplyState: ApplyState.Failed },
                "restart preserves historical failed application");
            var replay = await store.SaveAsync(caller, request);
            Check(replay.Replayed && replay.Code == ConfigurationResultCode.Ok
                && replay.Receipt?.Key == found.Receipt?.Key
                && replay.Receipt?.CommittedVersion == found.Receipt?.CommittedVersion
                && replay.Receipt?.ApplyState == found.Receipt?.ApplyState
                && replay.Receipt!.Steps.SequenceEqual(found.Receipt!.Steps),
                "restart validates old generation HMAC and replays receipt");
            var missingOld = await store.SaveAsync(caller, request with { Key = request.Key with { Nonce = Guid.NewGuid() } });
            Check(missingOld.Code == ConfigurationResultCode.KeyGenerationMismatch, "old generation cannot accept new operation");
            var modified = await store.SaveAsync(caller, request with
                { Changes = request.Changes with { Behavior = new() { AiDesire = FieldEdit<int>.Set(22) } } });
            Check(modified.Code == ConfigurationResultCode.IdempotencyConflict, "old generation rejects altered protected intent");
            Check(participant.Writes == 0 && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == version.Value,
                "restart and recovery do not repeat user writes or application");
            Console.WriteLine($"CFG_CHILD restart passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD restart failed=1 passed={passed} type={ex.GetType().Name}");
            return 1;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static SqliteConnection OpenFixture(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public static async Task ModuleRecoveryAsync(Action<bool, string> report)
    {
        foreach (var fixture in new[] { ("--configuration-future-module", "CFG_CHILD future passed=4 failed=0"),
            ("--configuration-module-atomic-fault", "CFG_CHILD atomic passed=6 failed=0"),
            ("--configuration-initialization", "CFG_CHILD initialization passed=8 failed=0"),
            ("--configuration-host-adapter", "CFG_CHILD host passed=23 failed=0"),
            ("--configuration-host-devices", "CFG_CHILD host-devices passed=18 failed=0"),
            ("--configuration-host-endpoints", "CFG_CHILD host-endpoints passed=16 failed=0"),
            ("--configuration-primary-aliases", "CFG_CHILD primary-aliases passed=17 failed=0"),
            ("--configuration-alias-codecs", "CFG_CHILD alias-codecs passed=9 failed=0"),
            ("--configuration-provider-routing", "CFG_CHILD provider-routing passed=25 failed=0"),
            ("--configuration-provider-refs", "CFG_CHILD provider-refs passed=34 failed=0"),
            ("--configuration-primary-facts", "CFG_CHILD primary-facts passed=20 failed=0"),
            ("--configuration-registered-providers", "CFG_CHILD registered-providers passed=62 failed=0"),
            ("--configuration-provider-writer-fence", "CFG_CHILD provider-writer-fence passed=11 failed=0"),
            ("--configuration-mood-persistence", "CFG_CHILD mood-persistence passed=22 failed=0"),
            ("--configuration-platform-policies", "CFG_CHILD platform-policies passed=72 failed=0"),
            ("--configuration-feishu-whitelist", "CFG_CHILD feishu-whitelist passed=16 failed=0"),
            ("--configuration-platform-compatibility", "CFG_CHILD platform-compatibility passed=20 failed=0"),
            ("--configuration-legacy-plan", "CFG_CHILD legacy-plan passed=14 failed=0"),
            ("--configuration-bridge-devices", "CFG_CHILD bridge-devices passed=12 failed=0"),
            ("--configuration-publish-permission", "CFG_CHILD publish-permission passed=14 failed=0"),
            ("--configuration-write-boundaries", "CFG_CHILD write-boundaries passed=27 failed=0"),
            ("--configuration-http", "CFG_CHILD configuration-http passed=27 failed=0"),
            ("--configuration-legacy-mood", "CFG_CHILD legacy-mood passed=21 failed=0"),
            ("--configuration-legacy-mood-audit", "CFG_CHILD legacy-mood-audit passed=10 failed=0"),
            ("--configuration-devices", "CFG_CHILD devices passed=32 failed=0"),
            ("--configuration-apply-claims", "CFG_CHILD apply-claims passed=14 failed=0"),
            ("--configuration-global-capacity", "CFG_CHILD global-capacity passed=10 failed=0") })
        {
            var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(ConfigurationContractTests).Assembly.Location);
            start.ArgumentList.Add(fixture.Item1);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("configuration_child_start");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
            var text = await output;
            Console.Write(text);
            report(child.ExitCode == 0 && text.Contains(fixture.Item2, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(await error), "configuration isolated module recovery " + fixture.Item1);
        }
        foreach (var mode in new[] { "legacy", "empty", "missing", "new", "deleted", "initialized", "delete-restart", "collision", "bad-type",
            "bad-unicode", "oversize", "nul", "conflict", "audit-fault", "version-fault", "generation-fault", "overflow" })
        {
            var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(ConfigurationContractTests).Assembly.Location);
            start.ArgumentList.Add("--configuration-token-case");
            start.ArgumentList.Add(mode);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("configuration_token_child_start");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
            var text = await output;
            Console.Write(text);
            var count = mode == "delete-restart" ? 14 : mode is "legacy" or "empty" or "missing" or "new" or "deleted" or "initialized" ? 11 : 3;
            report(child.ExitCode == 0 && text.Contains($"CFG_CHILD token-{mode} passed={count} failed=0", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(await error), "configuration actual Token migration " + mode);
        }
    }

    private static int CaptureBaselineBundle(string repository, string capture, string expectedHeadlessHash, string revision)
    {
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string BoundAsset(string root, string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(part => part is "" or "." or ".."))
                throw new InvalidOperationException("capture_asset_path_invalid");
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("capture_asset_path_escape");
            for (string? ancestor = full; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("capture_asset_reparse_point");
            return full;
        }
        try
        {
            repository = Path.GetFullPath(repository).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            capture = Path.GetFullPath(capture);
            if (!string.Equals(repository, Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(repository, "src", "BotAgent.Headless", "BotAgent.Headless.csproj"))
                || !string.Equals(Path.GetDirectoryName(capture), Path.Combine(repository, ".git"), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(capture), "^agent01-wave1-rollback-[0-9a-f]{32}$")
                || Directory.Exists(capture) || File.Exists(capture)
                || !System.Text.RegularExpressions.Regex.IsMatch(expectedHeadlessHash, "^[0-9A-F]{64}$")
                || !System.Text.RegularExpressions.Regex.IsMatch(revision, "^[0-9a-f]{40}$"))
                throw new InvalidOperationException("capture_scope_invalid");
            var source = Path.Combine(repository, "src", "BotAgent.Headless", "bin", "Release", "net8.0");
            Check(Hash(BoundAsset(source, "BotAgent.Headless.dll")) == expectedHeadlessHash, "capture_baseline_drift");
            var deps = JsonNode.Parse(File.ReadAllText(BoundAsset(source, "BotAgent.Headless.deps.json")))!.AsObject();
            var target = deps["targets"]![deps["runtimeTarget"]!["name"]!.GetValue<string>()]!.AsObject();
            var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in target)
            foreach (var kind in new[] { "runtime", "native", "runtimeTargets" })
            {
                if (library.Value?[kind] is not JsonObject declared) continue;
                foreach (var asset in declared)
                {
                    var relative = kind == "runtime" ? Path.GetFileName(asset.Key) : asset.Key;
                    if (Path.GetExtension(relative) is not (".dll" or ".so" or ".dylib" or ".a") || !assets.Add(relative))
                        throw new InvalidOperationException("capture_asset_not_allowed");
                    _ = BoundAsset(source, relative);
                }
            }
            foreach (var host in new[] { "BotAgent.Headless.deps.json", "BotAgent.Headless.runtimeconfig.json", "BotAgent.Headless.exe" })
                if (!assets.Add(host)) throw new InvalidOperationException("capture_duplicate_host_asset");
            Check(assets.Count == 37 && new[] { "BotAgent.Headless.dll", "BotAgent.Core.dll", "BotAgent.Platforms.dll", "BotAgent.Storage.dll" }
                .All(assets.Contains), "capture_declared_baseline_asset_set");
            var fingerprints = assets.OrderBy(value => value, StringComparer.Ordinal).ToDictionary(value => value,
                value => Hash(BoundAsset(source, value)), StringComparer.Ordinal);
            var sourceFiles = new[] { "src/BotAgent.Storage/ConfigurationStore.cs", "src/BotAgent.Storage/ConfigurationStorageBridge.cs",
                "src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs", "src/BotAgent.Headless/Host/CompositionRoot.cs",
                "tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs", "docs/plans/agents/00-c3-c4-contract-candidate.md" };
            var sourceFingerprints = sourceFiles.ToDictionary(value => value, value => Hash(BoundAsset(repository, value)), StringComparer.Ordinal);
            var bundle = Path.Combine(capture, "bundle");
            _ = BoundAsset(Path.Combine(repository, ".git"), Path.GetFileName(capture) + "/bundle/BotAgent.Headless.dll");
            Directory.CreateDirectory(bundle);
            foreach (var asset in fingerprints)
            {
                var destination = BoundAsset(bundle, asset.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(BoundAsset(source, asset.Key), destination, overwrite: false);
                Check(Hash(destination) == asset.Value && Hash(BoundAsset(source, asset.Key)) == asset.Value, "capture_copy_drift");
            }
            Check(Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories).Count() == assets.Count
                && sourceFiles.All(value => Hash(BoundAsset(repository, value)) == sourceFingerprints[value]), "capture_exact_bundle_and_source_freeze");
            var manifest = JsonSerializer.Serialize(new
            {
                Format = "wave1-baseline-bundle-1", Purpose = "synthetic-rollback-baseline-only", Revision = revision,
                ContractRevision = "cfg-0.3.2", CapturedAtUtc = DateTimeOffset.UtcNow, HeadlessSha256 = expectedHeadlessHash,
                Assets = fingerprints, SourceFingerprints = sourceFingerprints, PreimageCaptured = false, RollbackVerified = false
            }, new JsonSerializerOptions { WriteIndented = true });
            var manifestPath = BoundAsset(capture, "manifest.json");
            using (var file = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(manifest); file.Write(bytes); file.Flush(flushToDisk: true);
            }
            Check(File.ReadAllText(manifestPath) == manifest, "capture_manifest_durable");
            Console.WriteLine($"CFG_CHILD bundle-capture passed={passed} failed=0 assets={assets.Count} manifestSha256={Hash(manifestPath)}");
            return 0;
        }
        catch (Exception ex)
        {
            // Keep any partial owned artifact for inspection; never replace an existing baseline or erase a workspace path.
            Console.WriteLine($"CFG_CHILD bundle-capture passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
    }

    private static async Task<int?> PublishPermissionChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-publish-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_PUBLISH_PERMISSION " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            CheckPublication((label, condition) => Check(condition, label));
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var initial = new AppSettings { AiDesire = 10 };
            new SettingsStore().Save(initial);
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, new SyntheticParticipant());
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-publisher", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            ConfigurationSaveRequest Request(int value) => new("cfg-0.3.2",
                new(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1")),
                new(header.Generation, clock.WallSeconds, Guid.NewGuid()),
                new(new BehaviorChanges { AiDesire = FieldEdit<int>.Set(value) }, null, null, null, null, null, null, null),
                ImmutableArray<SecretEdit>.Empty);
            var method = typeof(SettingsBox).GetMethod("ApplyConfiguration", BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(Func<AppSettings, (AppSettings Snapshot, ConfigurationSaveResult Result, bool PublishAllowed)>),
                    typeof(Func<AppSettings, ConfigurationSaveResult, ConfigurationSaveResult>) }, null)!;
            ConfigurationSaveResult Apply(SettingsBox box,
                Func<AppSettings, (AppSettings Snapshot, ConfigurationSaveResult Result, bool PublishAllowed)> persist,
                Func<AppSettings, ConfigurationSaveResult, ConfigurationSaveResult>? published = null)
                => (ConfigurationSaveResult)method.Invoke(box, new object?[] { persist, published })!;
            var box = new SettingsBox(initial);
            var callbacks = 0;
            ConfigurationSaveResult Published(AppSettings snapshot, ConfigurationSaveResult result)
            {
                callbacks++;
                var recorded = store.RecordApply(caller, result.Receipt!.Key,
                    ImmutableArray.Create(new ApplyStepReport(ApplyStep.PublishSnapshot, ApplyState.Applied, null)));
                return result with { Receipt = recorded.Receipt ?? result.Receipt };
            }
            var saved = await store.SaveAsync(caller, Request(17));
            Check(saved.Code == ConfigurationResultCode.Ok && saved.Receipt is { CommitState: CommitState.Committed }
                && ReferenceEquals(box.Current, initial), "real_commit_precedes_any_runtime_exchange");
            var firstClaim = store.TryStartApplyStep(caller, saved.Receipt!.Key, ApplyStep.PublishSnapshot);
            var existingClaim = store.TryStartApplyStep(caller, saved.Receipt.Key, ApplyStep.PublishSnapshot);
            var denied = Apply(box, candidate =>
            {
                candidate.AiDesire = 17;
                return (candidate, saved with { Receipt = existingClaim.Result.Receipt }, existingClaim.CanRun);
            }, Published);
            Check(firstClaim.CanRun && !existingClaim.CanRun && existingClaim.Result.Code == ConfigurationResultCode.Ok
                && denied.Code == ConfigurationResultCode.Ok && !denied.Replayed
                && denied.Receipt is { CommitState: CommitState.Committed, ApplyState: ApplyState.Unknown }
                && denied.Receipt.Key == saved.Receipt.Key && ReferenceEquals(box.Current, initial) && callbacks == 0,
                "existing_real_unknown_claim_returns_truth_without_exchange_or_fake_replay");
            var second = await store.SaveAsync(caller, Request(18));
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_publish_claim_fault BEFORE UPDATE ON configuration_operations
                BEGIN SELECT RAISE(ABORT,'synthetic_publish_claim_fault'); END;
                """));
            try
            {
                var failedClaim = store.TryStartApplyStep(caller, second.Receipt!.Key, ApplyStep.PublishSnapshot);
                var failed = Apply(box, candidate =>
                {
                    candidate.AiDesire = 18;
                    return (candidate, second with { Code = failedClaim.Result.Code }, failedClaim.CanRun);
                }, Published);
                Check(failedClaim.Result.Code == ConfigurationResultCode.StorageUnavailable && !failedClaim.CanRun
                    && failed.Code == ConfigurationResultCode.StorageUnavailable && !failed.Replayed
                    && failed.Receipt is { CommitState: CommitState.Committed } && failed.Receipt.Key == second.Receipt.Key
                    && ReferenceEquals(box.Current, initial) && callbacks == 0,
                    "actual_claim_write_fault_retains_committed_fact_and_never_exchanges");
            }
            finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_publish_claim_fault")); }
            var successRequest = Request(19);
            var success = Apply(box, candidate =>
            {
                var result = store.SaveAsync(caller, successRequest).GetAwaiter().GetResult();
                var claim = store.TryStartApplyStep(caller, result.Receipt!.Key, ApplyStep.PublishSnapshot);
                candidate.AiDesire = 19;
                return (candidate, result with { Receipt = claim.Result.Receipt }, claim.CanRun);
            }, Published);
            var durable = await store.FindAsync(caller, successRequest.Key);
            Check(success.Code == ConfigurationResultCode.Ok && box.Current.AiDesire == 19 && callbacks == 1
                && durable.Receipt!.Steps.Any(step => step.Step == ApplyStep.PublishSnapshot && step.State == ApplyState.Applied),
                "first_owned_claim_publishes_once_and_records_the_real_step");
            var publishedSnapshot = box.Current;
            var replay = Apply(box, candidate =>
            {
                candidate.AiDesire = 99;
                return (candidate, store.SaveAsync(caller, successRequest).GetAwaiter().GetResult(), true);
            }, Published);
            Check(replay.Replayed && ReferenceEquals(box.Current, publishedSnapshot) && callbacks == 1,
                "permission_does_not_override_existing_replay_guard");
            var a = Request(20); var b = Request(21);
            var results = await Task.WhenAll(new[] { a, b }.Select(request => Task.Run(() => Apply(box, candidate =>
            {
                var result = store.SaveAsync(caller, request).GetAwaiter().GetResult();
                if (result.Code != ConfigurationResultCode.Ok) return (candidate, result, true);
                var claim = store.TryStartApplyStep(caller, result.Receipt!.Key, ApplyStep.PublishSnapshot);
                request.Changes.Behavior!.AiDesire.TryGetValue(out var desired);
                candidate.AiDesire = desired;
                return (candidate, result with { Receipt = claim.Result.Receipt }, claim.CanRun);
            }, Published))));
            var winner = results.Single(result => result.Code == ConfigurationResultCode.Ok);
            var winnerValue = winner.Receipt!.Key == a.Key ? 20 : 21;
            Check(results.Count(result => result.Code == ConfigurationResultCode.VersionConflict) == 1
                && callbacks == 2 && box.Current.AiDesire == winnerValue,
                "same_box_lock_keeps_cas_winner_and_actual_publication_order_aligned");
            var superseded = await store.SaveAsync(caller, Request(22));
            await store.SaveAsync(caller, Request(23));
            var staleClaim = store.TryStartApplyStep(caller, superseded.Receipt!.Key, ApplyStep.PublishSnapshot);
            publishedSnapshot = box.Current;
            var stale = Apply(box, candidate => (candidate, superseded with { Receipt = staleClaim.Result.Receipt }, staleClaim.CanRun), Published);
            Check(!staleClaim.CanRun && staleClaim.Result.Code == ConfigurationResultCode.Ok && stale.Code == ConfigurationResultCode.Ok
                && !stale.Replayed && stale.Receipt!.Steps.Any(step => step.ErrorCode == "configuration_superseded")
                && ReferenceEquals(box.Current, publishedSnapshot) && callbacks == 2,
                "superseded_commit_keeps_real_receipt_without_exchange_or_invented_error");
            Console.WriteLine($"CFG_CHILD publish-permission passed={passed} failed=0"); return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"CFG_CHILD publish-permission passed={passed} failed=1 type={error.GetType().Name}"); return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-publish-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_publish_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> ConfigurationHttpChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-http-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root); Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0; Process? process = null; Task<string>? stdout = null; Task<string>? stderr = null;
        IDisposable? mock = null;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_HTTP " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BotAgent.slnx"))) directory = directory.Parent;
            var repository = directory?.FullName ?? throw new InvalidOperationException("synthetic_repository_missing");
            var headless = Path.Combine(repository, "src", "BotAgent.Headless", "bin", "Release", "net8.0", "BotAgent.Headless.dll");
            var harness = Assembly.LoadFrom(Path.Combine(repository, "tests", "BotAgent.IntegrationHarness", "bin", "Release", "net8.0", "BotAgent.IntegrationHarness.dll"));
            int Port()
            {
                using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            var modelPort = Port(); var panelPort = Port(); var bridgePort = Port();
            var mockType = harness.GetType("BotAgent.IntegrationHarness.MockOpenAi", throwOnError: true)!;
            mock = (IDisposable)Activator.CreateInstance(mockType, new object[] { modelPort, "127.0.0.1" })!;
            mockType.GetMethod("Start")!.Invoke(mock, null);
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage(); AppDatabase.Initialize();
            new SettingsStore().Save(new AppSettings
            {
                AiDesire = 10, IdleFallbackSeconds = 0, EnableStickers = false, EnableMusic = false,
                EnableVoice = false, EnablePoke = false, EnableWebSearch = false, HealthReportEnabled = false,
                OfficialEnabled = false, FeishuEnabled = false, FeishuVerificationToken = "synthetic-token-before"
            });
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = root, CreateNoWindow = true
            };
            start.ArgumentList.Add(headless);
            foreach (var name in start.Environment.Keys.ToArray())
                if (new[] { "BOTAGENT_", "QQCHAT_", "OPENAI_", "FEISHU_", "MINIMAX_" }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    || new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "HEALTH_PORT" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                    start.Environment.Remove(name);
            foreach (var pair in new Dictionary<string, string>
            {
                ["BOTAGENT_DATA_DIR"] = root, ["QQCHAT_DATA_DIR"] = root, ["QQCHAT_LOG_FILE"] = "0",
                ["QQCHAT_API_KEY"] = "synthetic-env-model-key", ["QQCHAT_BASE_URL"] = $"http://127.0.0.1:{modelPort}/v1",
                ["QQCHAT_MODEL"] = "synthetic-model", ["QQCHAT_UIN"] = "10001",
                ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket", ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{bridgePort}",
                ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1",
                ["QQCHAT_PANEL_TOKEN"] = "synthetic-http-panel-token", ["QQCHAT_PANEL_PASSWORD"] = "synthetic-http-password-123"
            }) start.Environment[pair.Key] = pair.Value;
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{panelPort}"), Timeout = TimeSpan.FromSeconds(15) };
            async Task Launch()
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("synthetic_host_start_failed");
                stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
                for (var i = 0; i < 160; i++)
                {
                    if (process.HasExited) throw new InvalidOperationException("synthetic_host_early_exit");
                    try { using var response = await http.GetAsync("/healthz"); if (response.IsSuccessStatusCode) return; }
                    catch (HttpRequestException) { }
                    await Task.Delay(100);
                }
                throw new TimeoutException("synthetic_host_not_ready");
            }
            async Task Stop()
            {
                if (process is null) return;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                // The fixture deliberately stops its own server; natural early exits are rejected in Launch.
                _ = process.ExitCode; if (stdout is not null) await stdout; if (stderr is not null) await stderr;
                process.Dispose(); process = null;
            }
            async Task<(int Status, JsonObject Body)> Post(string route, string json, string? key = null, bool auth = true,
                bool crossOrigin = false, bool chunked = false)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                if (auth) request.Headers.Add("X-Panel-Token", "synthetic-http-panel-token");
                if (key is not null) request.Headers.Add("X-BotAgent-Configuration-Key", key);
                if (crossOrigin) request.Headers.Add("Origin", "https://example.com");
                if (chunked) request.Headers.TransferEncodingChunked = true;
                using var response = await http.SendAsync(request);
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject
                    ?? throw new InvalidOperationException("synthetic_http_response_shape");
                return ((int)response.StatusCode, body);
            }
            Task<(int Status, JsonObject Body)> Read() => Post("/api/settings?configuration=read", "{\"kind\":1}");
            string Key(JsonObject read) => JsonSerializer.Serialize(new ConfigurationOperationKey(
                JsonSerializer.Deserialize<ConfigurationGeneration>(read["generation"]!.ToJsonString()),
                read["serverUnixSeconds"]!.GetValue<long>(), Guid.NewGuid())).Trim('"');
            string SaveBody(JsonObject read, string key, JsonObject? changes = null, JsonArray? secrets = null)
                => new JsonObject { ["contractRevision"] = "cfg-0.3.2", ["expectedVersion"] = read["version"]!.DeepClone(),
                    ["key"] = key, ["changes"] = changes ?? new JsonObject(), ["secrets"] = secrets ?? new JsonArray() }.ToJsonString();
            JsonObject Ai(int value) => new() { ["behavior"] = new JsonObject { ["aiDesire"] = new JsonObject { ["kind"] = 1, ["value"] = value } } };
            await Launch();
            Check((await Post("/api/settings?configuration=read", "{\"kind\":1}", auth: false)).Status == 401,
                "real_new_profile_cannot_bypass_existing_authentication");
            Check((await Post("/api/settings", "{\"aiDesire\":99}", crossOrigin: true)).Status == 403,
                "real_settings_writes_retain_same_origin_gate");
            var initial = await Read();
            Check(initial.Status == 200 && initial.Body["code"]!.GetValue<int>() == 0
                && initial.Body["fields"]!.AsArray().Count == 142 && initial.Body["version"] is JsonValue
                && initial.Body["view"]!["behavior"]!["aiDesire"]!["value"]!.GetValue<int>() == 10,
                "actual_host_adopts_store_and_returns_versioned_editable_projection");
            http.DefaultRequestHeaders.Add("X-Panel-Token", "synthetic-http-panel-token");
            var legacy = JsonNode.Parse(await http.GetStringAsync("/api/settings"))!.AsObject();
            Check(!legacy["runtime"]!.AsObject().ContainsKey("feishuVerificationToken")
                && !legacy.ToJsonString().Contains("synthetic-token-before", StringComparison.Ordinal)
                && !initial.Body.ToJsonString().Contains("synthetic-token-before", StringComparison.Ordinal),
                "legacy_and_strict_reads_never_return_verification_token_plaintext");
            http.DefaultRequestHeaders.Remove("X-Panel-Token");
            var operation = Key(initial.Body);
            var payload = SaveBody(initial.Body, operation, Ai(17), new JsonArray(
                new JsonObject { ["slot"] = 0, ["kind"] = 1, ["value"] = "synthetic-committed-model-key" },
                new JsonObject { ["slot"] = 5, ["kind"] = 1, ["value"] = "synthetic-token-after" }));
            var saved = await Post("/api/settings?configuration=save", payload);
            Check(saved.Status == 200 && saved.Body["code"]!.GetValue<int>() == 0
                && saved.Body["receipt"]!["commitState"]!.GetValue<int>() == 1
                && saved.Body["receipt"]!["steps"]!.AsArray().Count == 8
                && saved.Body["receipt"]!["key"]!.GetValue<string>() == operation
                && !saved.Body.ToJsonString().Contains("synthetic-token-after", StringComparison.Ordinal),
                "actual_http_commits_configuration_and_fixed_secrets_before_reported_runtime_steps");
            Check(saved.Body["receipt"]!["steps"]!.AsArray().Any(step => step!["step"]!.GetValue<int>() == (int)ApplyStep.ModelProjection
                && step["state"]!.GetValue<int>() == (int)ApplyState.RestartRequired)
                && saved.Body["receipt"]!["commitState"]!.GetValue<int>() == (int)CommitState.Committed,
                "actual_env_to_database_binding_commit_reports_model_restart_not_false_applied");
            var after = await Read();
            Check(after.Body["version"]!.ToJsonString() == saved.Body["currentVersion"]!.ToJsonString()
                && after.Body["view"]!["behavior"]!["aiDesire"]!["value"]!.GetValue<int>() == 17
                && new StorageSecrets().LoadApiKey() == "synthetic-committed-model-key"
                && new StorageSecrets().Load("feishuVerificationToken") == "synthetic-token-after",
                "persisted_fields_secrets_and_published_snapshot_agree_on_real_host_version");
            var replay = await Post("/api/settings?configuration=save", payload);
            Check(replay.Status == 200 && replay.Body["replayed"]!.GetValue<bool>()
                && replay.Body["receipt"]!["committedVersion"]!.ToJsonString() == saved.Body["currentVersion"]!.ToJsonString(),
                "same_http_key_returns_original_commit_without_new_version_or_apply");
            var altered = JsonNode.Parse(payload)!.AsObject(); altered["changes"] = Ai(18);
            var conflict = await Post("/api/settings?configuration=save", altered.ToJsonString());
            Check(conflict.Status == 409 && conflict.Body["code"]!.GetValue<int>() == (int)ConfigurationResultCode.IdempotencyConflict,
                "same_http_key_different_intent_is_conflict_not_overwrite");
            var stale = await Post("/api/settings?configuration=save", SaveBody(initial.Body, Key(after.Body), Ai(19)));
            Check(stale.Status == 409 && stale.Body["code"]!.GetValue<int>() == (int)ConfigurationResultCode.VersionConflict,
                "real_http_expected_version_conflict_preserves_newer_configuration");
            var unknown = await Post("/api/settings?configuration=save", SaveBody(after.Body, Key(after.Body),
                new JsonObject { ["behavior"] = new JsonObject { ["syntheticUnknown"] = new JsonObject { ["kind"] = 1, ["value"] = 99 } } }));
            Check(unknown.Status == 400 && !unknown.Body.ToJsonString().Contains("syntheticUnknown", StringComparison.Ordinal),
                "strict_unknown_fields_rejected_with_safe_error_not_input_echo");
            var duplicate = SaveBody(after.Body, Key(after.Body), Ai(20)).Replace(
                "\"aiDesire\":{\"kind\":1,\"value\":20}",
                "\"aiDesire\":{\"kind\":1,\"value\":20},\"aiDesire\":{\"kind\":1,\"value\":21}", StringComparison.Ordinal);
            Check((await Post("/api/settings?configuration=save", duplicate)).Status == 400,
                "strict_decoded_duplicate_is_rejected_before_any_configuration_write");
            var emptyToken = await Post("/api/settings",
                "{\"aiDesire\":99,\"apiKey\":\"synthetic-must-not-commit\",\"mood\":\"synthetic-no-side\",\"feishuVerificationToken\":\"   \"}");
            Check(emptyToken.Status == 409 && emptyToken.Body["error"]!.GetValue<string>() == "refresh_required"
                && emptyToken.Body["refreshRequired"]!.GetValue<bool>()
                && new StorageSecrets().LoadApiKey() == "synthetic-committed-model-key"
                && AppDatabase.Scalar<string>("SELECT text FROM mood WHERE id=1") is null
                && (await Read()).Body["version"]!.ToJsonString() == after.Body["version"]!.ToJsonString(),
                "legacy_empty_token_rejects_whole_batch_including_secret_configuration_and_mood");
            var official = await Post("/api/settings", "{\"aiDesire\":99,\"OFFICIALAPPSECRET\":null}");
            Check(official.Status == 400 && (await Read()).Body["version"]!.ToJsonString() == after.Body["version"]!.ToJsonString(),
                "official_secret_presence_rejected_before_all_legacy_side_effects");
            var plugin = after.Body["view"]!["registered"]!["plugins"]!.AsArray().First()!["key"]!.GetValue<string>();
            var missingEnabled = new JsonObject { ["registered"] = new JsonObject { ["plugins"] = new JsonArray(
                new JsonObject { ["key"] = plugin }) } };
            Check((await Post("/api/settings?configuration=save", SaveBody(after.Body, Key(after.Body), missingEnabled))).Status == 400
                && (await Read()).Body["version"]!.ToJsonString() == after.Body["version"]!.ToJsonString(),
                "ordinary_record_missing_required_bool_cannot_default_to_disabled_and_commit");
            var nullablePartitions = new JsonObject();
            foreach (var partition in new[] { "behavior", "model", "agent", "platform", "voice", "music", "reporting", "registered" })
                nullablePartitions[partition] = null;
            var kept = await Post("/api/settings?configuration=save", SaveBody(after.Body, Key(after.Body), nullablePartitions));
            Check(kept.Status == 200 && kept.Body["code"]!.GetValue<int>() == (int)ConfigurationResultCode.NoChanges
                && kept.Body["currentVersion"]!.ToJsonString() == after.Body["version"]!.ToJsonString(),
                "explicit_null_partitions_keep_existing_facts_without_override_or_version_change");
            var pluginPatch = new JsonObject { ["registered"] = new JsonObject { ["plugins"] = new JsonArray(
                new JsonObject { ["key"] = plugin, ["enabled"] = false }) } };
            var pluginSave = await Post("/api/settings?configuration=save", SaveBody(after.Body, Key(after.Body), pluginPatch));
            Check(pluginSave.Status == 200 && JsonNode.Parse(AppDatabase.GetMeta("plugins_state")!)![plugin]!.GetValue<bool>() == false,
                "trusted_registered_plugin_edit_persists_in_actual_configuration_transaction");
            after = await Read();
            var badPlugin = new JsonObject { ["behavior"] = Ai(40)["behavior"]!.DeepClone(),
                ["registered"] = new JsonObject { ["plugins"] = new JsonArray(new JsonObject { ["key"] = "synthetic-unregistered-plugin", ["enabled"] = true }) } };
            Check((await Post("/api/settings?configuration=save", SaveBody(after.Body, Key(after.Body), badPlugin))).Status == 400
                && (await Read()).Body["version"]!.ToJsonString() == after.Body["version"]!.ToJsonString(),
                "unregistered_plugin_and_valid_scalar_rejected_atomically");
            var deleted = await Post("/api/settings?configuration=save", SaveBody(after.Body, Key(after.Body), secrets:
                new JsonArray(new JsonObject { ["slot"] = 5, ["kind"] = 2 })));
            Check(deleted.Status == 200 && new StorageSecrets().Load("feishuVerificationToken") is null
                && AppDatabase.GetMeta("verification_token_initialized") == "deleted", "strict_explicit_token_delete_sets_authoritative_tombstone");
            after = await Read();
            var moodKey = Key(after.Body);
            var moodSaved = await Post("/api/settings", "{\"mood\":\"synthetic-http-mood\"}", moodKey);
            Check(moodSaved.Status == 200 && moodSaved.Body["configurationResult"]!["code"]!.GetValue<int>()
                == (int)ConfigurationResultCode.NoChanges && moodSaved.Body["legacyActionResults"]![0]!["state"]!.GetValue<int>()
                    == (int)StorageConfiguration.LegacyActionState.Applied
                && AppDatabase.Scalar<string>("SELECT text FROM mood WHERE id=1") == "synthetic-http-mood",
                "real_legacy_mood_no_configuration_changes_executes_independent_observed_action");
            var moodStamp = AppDatabase.Scalar<long>("SELECT updated_unix FROM mood WHERE id=1");
            var moodReplay = await Post("/api/settings", "{\"mood\":\"synthetic-http-mood\"}", moodKey);
            var found = await Post("/api/settings?configuration=find", new JsonObject { ["key"] = moodKey }.ToJsonString());
            Check(moodReplay.Status == 200 && moodReplay.Body["configurationResult"]!["replayed"]!.GetValue<bool>()
                && found.Status == 200 && found.Body["legacyActionResults"]![0]!["state"]!.GetValue<int>() == 1
                && AppDatabase.Scalar<long>("SELECT updated_unix FROM mood WHERE id=1") == moodStamp,
                "http_replay_and_find_return_action_fact_without_mood_redo");
            var oversized = "{\"syntheticUnknown\":\"" + new string('x', 1048576) + "\"}";
            Check((await Post("/api/settings?configuration=read", oversized, chunked: true)).Status == 413,
                "strict_chunked_body_enforces_cumulative_byte_limit_without_content_length");
            Check((await Post("/api/settings?configuration=read", "{\"kind\":" + new string('[', 17) + "0" + new string(']', 17) + "}")).Status == 400,
                "strict_depth_limit_rejects_nested_input_before_dispatch");
            var clients = new List<System.Net.Sockets.TcpClient>();
            try
            {
                var pending = new List<Task<string>>();
                for (var i = 0; i < 8; i++)
                {
                    var client = new System.Net.Sockets.TcpClient(); clients.Add(client);
                    await client.ConnectAsync(IPAddress.Loopback, panelPort);
                    var header = $"POST /api/settings?configuration=read HTTP/1.1\r\nHost: 127.0.0.1:{panelPort}\r\nX-Panel-Token: synthetic-http-panel-token\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{";
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(header));
                    pending.Add(new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true).ReadToEndAsync());
                }
                using var admissionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (pending.Count(task => task.IsCompletedSuccessfully) < 4)
                    await Task.Delay(20, admissionTimeout.Token);
                Check(pending.Where(task => task.IsCompletedSuccessfully).All(task => task.Result.StartsWith("HTTP/1.1 429", StringComparison.Ordinal)),
                    "four_admitted_partial_bodies_force_remaining_requests_to_safe_429");
                foreach (var client in clients)
                    try { await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("}")); } catch (IOException) { }
                var responses = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
                Check(responses.Count(value => value.StartsWith("HTTP/1.1 429", StringComparison.Ordinal)) == 4
                    && responses.Count(value => value.StartsWith("HTTP/1.1 400", StringComparison.Ordinal)) == 4,
                    "actual_http_parser_admits_exactly_four_and_releases_all_partial_body_slots");
            }
            finally { foreach (var client in clients) client.Dispose(); }
            var beforeRestart = (await Read()).Body;
            await Stop(); await Launch();
            var restored = await Read();
            Check(restored.Status == 200 && restored.Body["generation"]!.ToJsonString() != beforeRestart["generation"]!.ToJsonString()
                && restored.Body["version"]!.ToJsonString() == beforeRestart["version"]!.ToJsonString()
                && restored.Body["secrets"]!.AsArray().Single(slot => slot!["slot"]!.GetValue<int>() == 5)!["configured"]!.GetValue<bool>() == false
                && restored.Body["view"]!["registered"]!["plugins"]!.AsArray().Single(row => row!["key"]!.GetValue<string>() == plugin)!["desiredEnabled"]!.GetValue<bool>() == false,
                "fresh_real_host_restores_version_plugin_and_deleted_token_without_reseeding");
            var recovered = await Post("/api/settings?configuration=find", new JsonObject { ["key"] = moodKey }.ToJsonString());
            Check(recovered.Status == 200 && recovered.Body["legacyActionResults"]![0]!["state"]!.GetValue<int>() == 1
                && AppDatabase.Scalar<long>("SELECT updated_unix FROM mood WHERE id=1") == moodStamp,
                "fresh_real_host_finds_old_generation_mood_receipt_without_reexecution");
            Console.WriteLine($"CFG_CHILD configuration-http passed={passed} failed=0");
            await Stop(); return 0;
        }
        catch (Exception error)
        { Console.WriteLine($"CFG_CHILD configuration-http passed={passed} failed=1 type={error.GetType().Name}"); return 1; }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                _ = process.ExitCode; if (stdout is not null) await stdout; if (stderr is not null) await stderr;
                process.Dispose();
            }
            mock?.Dispose(); SqliteConnection.ClearAllPools(); var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-http-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_http_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> PlatformCompatibilityChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-platform-compatibility-" + Guid.NewGuid().ToString("N"));
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            new[] { "BOTAGENT_", "QQCHAT_", "OPENAI_", "FEISHU_" }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root); Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0; var failed = 0;
        void Check(bool condition, string label)
        {
            if (condition) passed++;
            else { failed++; Console.WriteLine("FAIL CFG_PLATFORM_COMPATIBILITY " + label); }
        }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage(); var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var declare = typeof(SettingsStore).Assembly.GetType("BotAgent.Host.CompositionRoot", throwOnError: true)!
                .GetMethod("DeclarePlatformCatalog", BindingFlags.NonPublic | BindingFlags.Static)!;
            void Declare(AppSettings settings) => declare.Invoke(null, new object[] { settings });
            var raw = new AppSettings { Model = "synthetic-model", ModelBaseUrl = "https://example.com/model", LocalChannelIds = "101",
                OfficialEnabled = false, OfficialChatEnabled = true, PrivateChatEnabled = true, FeishuEnabled = false,
                PlatformSwitchSchemaVersion = 0, PlatformPolicies = new()
                {
                    new() { PlatformId = "qq.private", AccountScope = "legacy", Enabled = true, ChatEnabled = false },
                    new() { PlatformId = "qq.official", AccountScope = "legacy", Enabled = true, ChatEnabled = true },
                    new() { PlatformId = "feishu", AccountScope = "default", Enabled = true, ChatEnabled = true }
                } };
            Declare(raw); new SettingsStore().Save(raw);
            new ModelProviderStore().UpsertMetadata(new("primary", 0, "synthetic-primary", raw.ModelBaseUrl, raw.Model, "secret:apiKey", false));
            var bootstrap = raw.Snapshot(); BotAgent.Services.Platforms.PlatformSwitchSettings.Normalize(bootstrap);
            var runtime = new SettingsBox(bootstrap);
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, CreateActualTokenAdapter(runtime));
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-platform-compatibility", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            ConfigurationOperationKey Key() => new(header.Generation, clock.Now.ToUnixTimeSeconds(), Guid.NewGuid());
            AppSettings Desired() { var value = new SettingsStore().Load(); Declare(value); return value; }
            (bool Enabled, bool ChatEnabled) Rules(AppSettings settings, string platform)
            {
                var rule = BotAgent.Services.Platforms.PlatformSwitchSettings.ResolvePolicyRules(settings,
                    new PolicyTarget.AccountDefault(platform, platform == "feishu" ? "default" : "legacy"), settings.DeclaredPlatformActions);
                return (rule.Enabled == ToggleRule.Enabled, rule.ChatEnabled == ToggleRule.Enabled);
            }
            string State() => JsonSerializer.Serialize(new { Json = AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1"),
                Meta = AppDatabase.GetMeta("configuration_platform_policies"), Version = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1"),
                Operations = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") });
            var initial = Desired();
            Check(Rules(initial, "qq.private") == (true, false) && Rules(initial, "qq.official") == (false, true)
                && Rules(initial, "feishu") == (false, false) && Rules(initial, "local") == (true, true), "pre_v1_and_migration_retains_real_typed_rules");
            Check(BotAgent.Services.Platforms.PlatformSwitchSettings.Read(initial, "qq.private", "legacy") == (true, false), "canonical_read_reports_typed_qq_preferences");
            Check(BotAgent.Services.Platforms.PlatformSwitchSettings.Read(initial, "local", "legacy") == (true, true), "canonical_read_preserves_existing_local_availability");
            var before = State(); var original = JsonSerializer.Serialize(initial);
            var editable = BotAgent.Services.Platforms.PlatformSwitchSettings.EditablePolicies(initial);
            Check(editable.Count == 4 && editable.Single(p => p.PlatformId == "local") is { Enabled: true, ChatEnabled: true }
                && editable.Single(p => p.PlatformId == "qq.private") is { Enabled: true, ChatEnabled: false }, "legacy_display_projects_all_four_from_typed_authority");
            Check(State() == before && JsonSerializer.Serialize(initial) == original, "compatibility_display_never_rewrites_sources_or_database");
            Check(BotAgent.Services.Platforms.PlatformSwitchSettings.Read(initial, "qq.private", "synthetic-unknown") == (false, false), "undeclared_account_read_is_fail_closed");
            var ambiguous = initial.Snapshot(); ambiguous.DeclaredPlatformDrivers = ambiguous.DeclaredPlatformDrivers.Add(new("local", "legacy", "synthetic-second"));
            Check(BotAgent.Services.Platforms.PlatformSwitchSettings.Read(ambiguous, "local", "legacy") == (false, false), "account_read_does_not_guess_between_declared_instances");
            var overridden = initial.Snapshot(); overridden.RuntimePlatformPolicies = overridden.RuntimePlatformPolicies!.Value.Add(new(
                new PolicyTarget.Instance(new("local", "legacy", "")), new(ToggleRule.Enabled, ToggleRule.Enabled, ListRule.Inherit(), ListRule.Inherit(),
                    new(ActionRuleKind.Inherit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty)));
            Check(BotAgent.Services.Platforms.PlatformSwitchSettings.Read(overridden, "local", "legacy") == (false, false), "account_read_does_not_override_exact_instance_authority");
            var resolver = new BotAgent.Services.Platforms.PlatformPolicyResolver(new SettingsBox(initial));
            Check(!resolver.Resolve(new PlatformContext("feishu", "default")).Enabled, "exact_resolver_still_rejects_missing_feishu_instance");
            var parse = typeof(BotAgent.Adapters.Panel.WebUiServer).GetMethod("ParseLegacyConfiguration", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(JsonObject), typeof(BotAgent.Platforms.PlatformOptions) }, null)!;
            ConfigurationPatch Parse(JsonObject body)
            {
                using var plan = (IDisposable)parse.Invoke(null, new object[] { body, Desired() })!;
                if ((ConfigurationResultCode)plan.GetType().GetProperty("Code")!.GetValue(plan)! != ConfigurationResultCode.Ok)
                    throw new InvalidOperationException("synthetic_legacy_plan_rejected");
                return (ConfigurationPatch)plan.GetType().GetProperty("Changes")!.GetValue(plan)!;
            }
            async Task Legacy(JsonObject body)
            {
                var result = await store.SaveLegacyAsync(caller, Key(), Parse(body), ImmutableArray<SecretEdit>.Empty);
                if (result.Code is not (ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges)) throw new InvalidOperationException("synthetic_legacy_save_rejected");
            }
            await Legacy(new() { ["feishuEnabled"] = true });
            Check(Rules(Desired(), "feishu") == (true, true), "legacy_feishu_toggle_updates_both_switches");
            await Legacy(new() { ["feishuEnabled"] = false });
            Check(Rules(Desired(), "feishu") == (false, false), "legacy_feishu_off_preserves_both_switches");
            JsonObject Policy(string platform, bool enabled, bool chat) => new() { ["PlatformId"] = platform,
                ["AccountScope"] = platform == "feishu" ? "default" : "legacy", ["Enabled"] = enabled, ["ChatEnabled"] = chat,
                ["GroupWhitelist"] = platform == "local" ? "101" : "" };
            await Legacy(new() { ["platformPolicies"] = new JsonArray(Policy("local", false, true)) });
            Check(Rules(Desired(), "local") == (false, true), "disabled_local_preserves_separate_chat_preference");
            await Legacy(new() { ["localChannelIds"] = "101" });
            Check(Rules(Desired(), "local") == (true, true), "legacy_roster_reenables_previously_disabled_policy");
            await Legacy(new() { ["localChannelIds"] = "" });
            Check(Rules(Desired(), "local") == (false, false), "legacy_empty_roster_disables_both_policy_switches");
            await Legacy(new() { ["localChannelIds"] = "101" });
            var whole = new JsonArray(new[] { "qq.private", "qq.official", "feishu", "local" }.Select(p => (JsonNode)Policy(p, true, true)).ToArray());
            await Legacy(new() { ["platformPolicies"] = whole, ["officialEnabled"] = false, ["officialChatEnabled"] = false,
                ["privateChatEnabled"] = false, ["feishuEnabled"] = false, ["localChannelIds"] = "" });
            Check(new[] { "qq.private", "qq.official", "feishu", "local" }.All(p => Rules(Desired(), p) == (true, true)), "whole_legacy_table_wins_over_conflicting_scalar_aliases");
            Check(Desired().LocalChannelIds == "101", "whole_local_policy_retains_its_requested_roster");
            ConfigurationSaveRequest Strict(ConfigurationPatch changes, ConfigurationOperationKey? key = null) => new("cfg-0.3.2",
                new(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1")), key ?? Key(), changes, ImmutableArray<SecretEdit>.Empty);
            var official = new PlatformPolicyEntry(new PolicyTarget.AccountDefault("qq.official", "legacy"), new(ToggleRule.Enabled, ToggleRule.Enabled,
                ListRule.Inherit(), ListRule.Inherit(), new(ActionRuleKind.Inherit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty));
            var strict = Strict(new(null, null, null, new PlatformChanges { OfficialEnabled = FieldEdit<bool>.Set(false),
                PlatformPolicies = FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Set(ImmutableArray.Create(official)) }, null, null, null, null));
            var saved = await store.SaveAsync(caller, strict);
            Check(saved.Code == ConfigurationResultCode.Ok && !Desired().OfficialEnabled && Rules(Desired(), "qq.official").Enabled,
                "strict_policy_never_flattens_explicit_override_into_scalar_baseline");
            var cleared = await store.SaveAsync(caller, Strict(new(null, null, null, new PlatformChanges
                { PlatformPolicies = FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Clear() }, null, null, null, null)));
            Check(cleared.Code == ConfigurationResultCode.Ok && !Rules(Desired(), "qq.official").Enabled,
                "strict_clear_recovers_original_scalar_inheritance_not_old_overlay");
            var replayKey = Key(); var patch = Parse(new() { ["feishuEnabled"] = true });
            await store.SaveLegacyAsync(caller, replayKey, patch, ImmutableArray<SecretEdit>.Empty); before = State();
            var replay = await store.SaveLegacyAsync(caller, replayKey, patch, ImmutableArray<SecretEdit>.Empty);
            Check(replay.Replayed && State() == before, "legacy_alias_replay_does_not_reapply_or_allocate_new_version");
            var final = Desired(); original = JsonSerializer.Serialize(final); before = State();
            _ = BotAgent.Services.Platforms.PlatformSwitchSettings.EditablePolicies(final);
            Check(State() == before && JsonSerializer.Serialize(final) == original, "post_save_compatibility_projection_is_still_pure");
            Console.WriteLine($"CFG_CHILD platform-compatibility passed={passed} failed={failed}"); return failed == 0 ? 0 : 1;
        }
        catch (Exception error)
        { Console.WriteLine($"CFG_CHILD platform-compatibility passed={passed} failed={failed + 1} type={error.GetType().Name}"); return 1; }
        finally
        {
            SqliteConnection.ClearAllPools(); var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-platform-compatibility-[0-9a-f]{32}$"))
                throw new InvalidOperationException("platform_compatibility_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static int FeishuWhitelistChild()
    {
        var passed = 0; var failed = 0;
        void Check(bool condition, string label)
        {
            if (condition) passed++;
            else { failed++; Console.WriteLine("FAIL CFG_FEISHU_WHITELIST " + label); }
        }
        try
        {
            ClockBindings.InitializePlatforms(); Clock.Use(new SyntheticClock());
            var context = new DriverKey(PlatformId.Feishu, AccountScope.Default, "feishu-main");
            var account = new PolicyTarget.AccountDefault(PlatformId.Feishu, AccountScope.Default);
            PlatformPolicyConfiguration Rules(ListRule group, ListRule? individual = null,
                ToggleRule enabled = ToggleRule.Enabled, ToggleRule chat = ToggleRule.Enabled)
                => new(enabled, chat, group, individual ?? ListRule.Explicit("synthetic-user"),
                    new(ActionRuleKind.Inherit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty);
            var allowed = Rules(ListRule.Explicit("synthetic-chat"));
            var box = new SettingsBox(new AppSettings { FeishuEnabled = true, FeishuAppId = "synthetic-app",
                FeishuAppSecret = "synthetic-secret", FeishuVerificationToken = "synthetic-token",
                DeclaredPlatformDrivers = ImmutableArray.Create(context),
                RuntimePlatformPolicies = ImmutableArray.Create(new PlatformPolicyEntry(account, allowed)) });
            var ids = new FeishuIdMap(); var fetcher = new RoutingFetcher();
            using var gateway = new BotAgent.Adapters.Platforms.Feishu.FeishuBotGateway(box, fetcher, null, ids);
            var group = ids.AliasFor("synthetic-app", "group", "synthetic-chat");
            var individual = ids.AliasFor("synthetic-app", "participant", "synthetic-user");
            var otherApp = ids.AliasFor("synthetic-other-app", "group", "synthetic-chat");
            var method = gateway.GetType().GetMethod("AllowsMappedTarget", new[] { typeof(bool), typeof(long) });
            var trusted = method is null ? new Func<bool, long, bool>((_, _) => false)
                : method.CreateDelegate<Func<bool, long, bool>>(gateway);
            BotAgent.Services.Qq.WhitelistGate Gate(bool loaded = true, bool mapped = true)
            {
                var registry = new BotAgent.Services.Platforms.PlatformRegistry(
                    loaded ? new IPlatformAdapter[] { gateway } : Array.Empty<IPlatformAdapter>(),
                    loaded ? new IPlatformMessenger[] { gateway } : Array.Empty<IPlatformMessenger>());
                var resolver = new BotAgent.Services.Platforms.PlatformPolicyResolver(box, registry, _ => ImmutableArray<ActionId>.Empty);
                var constructor = typeof(BotAgent.Services.Qq.WhitelistGate).GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(SettingsBox), typeof(BotAgent.Services.Platforms.PlatformPolicyResolver), typeof(Func<bool, long, bool>) }, null);
                // The old product really executes its old gate in the red phase; no substitute matcher.
                return !mapped || constructor is null ? new(box, resolver)
                    : (BotAgent.Services.Qq.WhitelistGate)constructor.Invoke(new object[] { box, resolver, trusted });
            }
            void Policy(PlatformPolicyConfiguration value, bool multiple = false, bool instanceOverride = false)
                => box.Apply(next =>
                {
                    next.DeclaredPlatformDrivers = multiple ? ImmutableArray.Create(context,
                        new DriverKey(PlatformId.Feishu, AccountScope.Default, "synthetic-second")) : ImmutableArray.Create(context);
                    var entries = ImmutableArray.Create(new PlatformPolicyEntry(account, value));
                    if (instanceOverride) entries = entries.Add(new(new PolicyTarget.Instance(context), allowed));
                    next.RuntimePlatformPolicies = entries;
                });
            bool Allows(BotAgent.Services.Qq.WhitelistGate gate, bool isGroup, long id)
                => gate.AllowsKey(Channels.Key(Channels.Feishu, isGroup, id));
            Check(group > 0 && Allows(Gate(), true, group), "mapped_raw_group_survives_real_canonical_reply_gate");
            Check(individual > 0 && Allows(Gate(), false, individual), "mapped_raw_private_target_survives_real_canonical_reply_gate");
            Policy(Rules(ListRule.Explicit("synthetic-other-chat")));
            Check(!Allows(Gate(), true, group), "changed_raw_allowlist_revokes_existing_alias");
            Policy(Rules(ListRule.Explicit("")));
            Check(!Allows(Gate(), true, group), "explicit_empty_never_becomes_allow_all");
            Policy(Rules(ListRule.AllowAll()));
            Check(Allows(Gate(), true, group), "allow_all_accepts_a_real_mapped_target");
            Check(!Allows(Gate(), true, group + 500_000), "allow_all_still_denies_unmapped_alias");
            Check(!Allows(Gate(), true, otherApp), "cross_app_alias_cannot_reuse_a_raw_allowlist");
            Check(!Allows(Gate(), true, individual), "participant_alias_cannot_be_used_as_group_identity");
            Policy(Rules(ListRule.AllowAll(), enabled: ToggleRule.Disabled));
            Check(!Allows(Gate(), true, group), "disabled_driver_does_not_bypass_canonical_gate");
            Policy(Rules(ListRule.AllowAll(), chat: ToggleRule.Disabled));
            Check(!Allows(Gate(), true, group), "disabled_chat_does_not_bypass_canonical_gate");
            Policy(allowed);
            Check(!Allows(Gate(loaded: false), true, group), "unloaded_adapter_is_fail_closed");
            Policy(Rules(ListRule.AllowAll()));
            Check(!Allows(Gate(mapped: false), true, group), "missing_trusted_mapper_is_fail_closed");
            Policy(allowed, multiple: true);
            Check(!Allows(Gate(), true, group), "legacy_key_cannot_guess_between_declared_instances");
            Policy(allowed, instanceOverride: true);
            Check(!Allows(Gate(), true, group), "legacy_key_cannot_bypass_exact_instance_override");
            Policy(allowed); box.Apply(next => next.FeishuAppSecret = "");
            Check(!Allows(Gate(), true, group), "unconfigured_gateway_is_fail_closed");
            Check(fetcher.Paths.Count == 0, "all_local_authorization_checks_perform_zero_network_io");
            Console.WriteLine($"CFG_CHILD feishu-whitelist passed={passed} failed={failed}");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception error)
        { Console.WriteLine($"CFG_CHILD feishu-whitelist passed={passed} failed={failed + 1} type={error.GetType().Name}"); return 1; }
    }

    private static async Task<int?> WholeRollbackChildAsync(string repository, string capture)
    {
        const string baselineHash = "3FC9BC4DC7E8B91BAAFFC1604B0E8B8A47E51F62003720B28494F02E37279542";
        const string preimageHash = "954F62E5721B3BA96EC1DBC377A2DEA1000DA589F5D3E3790DDF1717D448238D";
        const string preimageManifestHash = "E925741572580BE05DC685A1BF39C799B7B2636426D055622CCAB3AA1A54F955";
        var nonce = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-whole-rollback-" + nonce);
        var passed = 0; Process? process = null; Task<string>? stdout = null; Task<string>? stderr = null;
        IDisposable? mock = null; var processExits = new List<object>();
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_WHOLE_ROLLBACK " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        string Bound(string boundary, string relative)
        {
            boundary = Path.GetFullPath(boundary);
            if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is "" or "." or ".."))
                throw new InvalidOperationException("rollback_relative_path_invalid");
            var full = Path.GetFullPath(Path.Combine(boundary, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("rollback_path_escape");
            for (FileSystemInfo? info = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full);
                info is not null && info.FullName.StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
                info = info is FileInfo file ? file.Directory : ((DirectoryInfo)info).Parent)
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("rollback_reparse_denied");
            return full;
        }
        async Task Stop(string stage)
        {
            if (process is null) return;
            var forced = !process.HasExited;
            if (forced) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var exit = process.ExitCode;
            if (stdout is not null) await stdout; if (stderr is not null) await stderr;
            process.Dispose(); process = null;
            // Only our deliberately stopped test host may have a forced nonzero exit.
            Check(forced, "owned_host_remained_live_until_explicit_stop_" + stage);
            processExits.Add(new { Stage = stage, ForcedStop = forced, ExitCode = exit, ObservedAtUtc = DateTimeOffset.UtcNow });
        }
        try
        {
            repository = Path.GetFullPath(repository); capture = Path.GetFullPath(capture);
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BotAgent.slnx"))) directory = directory.Parent;
            Check(string.Equals(repository, directory?.FullName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(capture, Path.Combine(repository, ".git", "agent01-wave1-rollback-b3a84e32821f442cb72d0d7aee14d670"),
                    StringComparison.OrdinalIgnoreCase), "exact_repository_and_readonly_baseline_scope");
            var manifestPath = Bound(capture, "manifest.json"); var original = Bound(capture, "preimage.db");
            var preimageManifest = Bound(capture, "preimage-manifest.json");
            Check(Hash(manifestPath) == baselineHash && Hash(original) == preimageHash && Hash(preimageManifest) == preimageManifestHash,
                "three_original_evidence_hashes_match");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var assets = manifest.RootElement.GetProperty("Assets").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            var baseline = Bound(capture, "bundle");
            bool OriginalIntact() => assets.Count == 37 && assets.All(p => Hash(Bound(baseline, p.Key)) == p.Value)
                && Directory.EnumerateFiles(baseline, "*", SearchOption.AllDirectories).Count() == 37
                && Hash(manifestPath) == baselineHash && Hash(original) == preimageHash && Hash(preimageManifest) == preimageManifestHash;
            Check(OriginalIntact(), "whole_old_bundle_and_preimage_remain_intact");
            var source = Bound(repository, "src/BotAgent.Headless/bin/Release/net8.0");
            var candidateNames = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(source, path).Replace('\\', '/')).ToArray();
            // 候选闭包不再写死数量：从真实 deps.json 的 runtime/runtimeTargets 推导。
            // 这样新增工程/SDK 资产必须由实际依赖锁定解释，不能只改一个预期数字换绿。
            using (var runtimeDeps = JsonDocument.Parse(File.ReadAllText(Bound(source, "BotAgent.Headless.deps.json"))))
            {
                var target = runtimeDeps.RootElement.GetProperty("targets").EnumerateObject().First().Value;
                var declared = new HashSet<string>(StringComparer.Ordinal)
                {
                    "BotAgent.Headless.deps.json",
                    "BotAgent.Headless.runtimeconfig.json",
                    "BotAgent.Headless.exe",
                };
                foreach (var library in target.EnumerateObject())
                {
                    if (library.Value.TryGetProperty("runtime", out var runtimeAssets))
                        foreach (var asset in runtimeAssets.EnumerateObject())
                            declared.Add(Path.GetFileName(asset.Name.Replace('\\', '/')));

                    if (library.Value.TryGetProperty("runtimeTargets", out var runtimeTargets))
                        foreach (var asset in runtimeTargets.EnumerateObject())
                            declared.Add(asset.Name.Replace('\\', '/'));
                }

                Check(candidateNames.ToHashSet(StringComparer.Ordinal).SetEquals(declared),
                    "candidate_closure_matches_deps_declared_runtime_assets");
                Check(assets.Keys.All(declared.Contains),
                    "every_old_37_asset_is_still_part_of_the_current_closure");
                Check(new[] { "BotAgent.Model.dll", "BotAgent.Engine.dll", "BotAgent.Mcp.dll", "ModelContextProtocol.Core.dll" }
                        .All(declared.Contains),
                    "current_closure_contains_real_model_engine_mcp_and_official_sdk_assemblies");
            }
            var candidate = candidateNames.ToDictionary(name => name, name => Hash(Bound(source, name)), StringComparer.Ordinal);
            Check(candidate["BotAgent.Headless.dll"] != assets["BotAgent.Headless.dll"], "candidate_is_distinct_real_host_artifact");
            var proofPath = Bound(capture, "rollback-proof-" + nonce + ".json");
            Check(!Directory.Exists(root) && !File.Exists(root) && !File.Exists(proofPath) && !Directory.Exists(proofPath),
                "new_nonce_targets_never_overwrite_existing_evidence");
            Directory.CreateDirectory(root);
            var bundle = Bound(root, "bundle"); var runtime = Bound(root, "runtime");
            var database = Bound(root, "runtime/data/qqchat.db");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            void CopyBundle(string from, IReadOnlyDictionary<string, string> expected)
            {
                if (process is not null) throw new InvalidOperationException("rollback_host_still_running");
                // 只允许清理本夹具从当前候选产物复制进来的、旧包没有的资产；
                // 数量不是“放宽到 39”，每个候选专属资产都必须与候选哈希逐项一致。
                foreach (var retiredName in candidate.Keys.Where(name => !expected.ContainsKey(name)).ToArray())
                {
                    var retired = Bound(bundle, retiredName);
                    if (!File.Exists(retired))
                    {
                        continue;
                    }

                    if (Hash(retired) != candidate[retiredName])
                    {
                        throw new InvalidOperationException("rollback_owned_asset_drift");
                    }

                    File.Delete(retired);
                }
                foreach (var asset in expected)
                {
                    var input = Bound(from, asset.Key); var output = Bound(bundle, asset.Key);
                    if (Hash(input) != asset.Value) throw new InvalidOperationException("rollback_asset_input_drift");
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(input, output, overwrite: true);
                    if (Hash(output) != asset.Value) throw new InvalidOperationException("rollback_asset_copy_drift");
                }
                Check(Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories).Count() == expected.Count,
                    "exact_whole_bundle_count_after_copy");
            }
            void RestoreDatabase()
            {
                if (process is not null) throw new InvalidOperationException("rollback_host_still_running");
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var path = Bound(root, "runtime/data/qqchat.db" + suffix);
                    if (File.Exists(path)) { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } File.Delete(path); }
                }
                File.Copy(original, database, overwrite: false);
                Check(Hash(database) == preimageHash, "restored_exact_preimage_before_host_start");
            }
            (string Settings, string Slots, string Providers, long Version) Facts()
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
                string Digest(string sql)
                {
                    using var command = connection.CreateCommand(); command.CommandText = sql;
                    using var reader = command.ExecuteReader(); var rows = new List<string[]>();
                    while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount)
                        .Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!).ToArray());
                    return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows)));
                }
                var settings = Digest("SELECT json FROM settings WHERE id=1");
                var slots = Digest("SELECT name,value FROM secrets WHERE name IN ('apiKey','agentServerKey','ttsKey','feishuAppSecret','feishuEncryptKey','feishuVerificationToken') ORDER BY name");
                var providers = Digest("SELECT id,priority,name,base_url,model_name,secret_key_ref,is_enabled FROM model_providers ORDER BY id");
                using var version = connection.CreateCommand(); version.CommandText = "SELECT version FROM configuration_state WHERE id=1";
                var value = Convert.ToInt64(version.ExecuteScalar()); transaction.Commit(); return (settings, slots, providers, value);
            }
            int Port()
            {
                using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            var modelPort = Port(); var panelPort = Port(); var bridgePort = Port();
            var harness = Assembly.LoadFrom(Bound(repository, "tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll"));
            var mockType = harness.GetType("BotAgent.IntegrationHarness.MockOpenAi", throwOnError: true)!;
            mock = (IDisposable)Activator.CreateInstance(mockType, new object[] { modelPort, "127.0.0.1" })!;
            mockType.GetMethod("Start")!.Invoke(mock, null);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{panelPort}"), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("X-Panel-Token", "synthetic-rollback-panel-token");
            async Task Launch()
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, WorkingDirectory = runtime, CreateNoWindow = true };
                start.ArgumentList.Add(Bound(bundle, "BotAgent.Headless.dll"));
                foreach (var name in start.Environment.Keys.ToArray())
                    if (new[] { "BOTAGENT_", "QQCHAT_", "OPENAI_", "FEISHU_", "MINIMAX_", "TTS_" }.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        || new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "HEALTH_PORT" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                        start.Environment.Remove(name);
                foreach (var pair in new Dictionary<string, string>
                {
                    ["BOTAGENT_DATA_DIR"] = runtime, ["QQCHAT_DATA_DIR"] = runtime, ["QQCHAT_LOG_FILE"] = "0",
                    ["QQCHAT_API_KEY"] = "synthetic-env-key", ["QQCHAT_BASE_URL"] = $"http://127.0.0.1:{modelPort}/v1",
                    ["QQCHAT_MODEL"] = "synthetic-model", ["QQCHAT_UIN"] = "10001",
                    ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket", ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{bridgePort}",
                    ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1",
                    ["QQCHAT_PANEL_TOKEN"] = "synthetic-rollback-panel-token", ["QQCHAT_PANEL_PASSWORD"] = "synthetic-rollback-password-123"
                }) start.Environment[pair.Key] = pair.Value;
                process = Process.Start(start) ?? throw new InvalidOperationException("rollback_host_start_failed");
                stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
                for (var i = 0; i < 160; i++)
                {
                    if (process.HasExited) throw new InvalidOperationException("rollback_host_early_exit");
                    try { using var response = await http.GetAsync("/healthz"); if (response.IsSuccessStatusCode) return; }
                    catch (HttpRequestException) { }
                    await Task.Delay(100);
                }
                throw new TimeoutException("rollback_host_not_ready");
            }
            async Task<JsonObject> Post(string query, string json)
            {
                using var response = await http.PostAsync("/api/settings?configuration=" + query,
                    new StringContent(json, Encoding.UTF8, "application/json"));
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("rollback_http_status_" + (int)response.StatusCode);
                return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            }
            async Task<string> OldObservation()
            {
                var data = JsonNode.Parse(await http.GetStringAsync("/api/settings"))!.AsObject();
                var observed = data["runtime"]!.AsObject();
                Check(observed["aiDesire"]!.GetValue<int>() == 17 && observed["agentDevices"] is not null,
                    "actual_old_host_reads_legacy_configuration_and_device_table");
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new JsonObject
                { ["aiDesire"] = observed["aiDesire"]!.DeepClone(), ["agentDevices"] = observed["agentDevices"]!.DeepClone() }.ToJsonString())));
            }
            CopyBundle(baseline, assets); RestoreDatabase(); await Launch();
            var oldObservation = await OldObservation(); await Stop("old-baseline"); var oldFacts = Facts();
            Check(oldFacts.Version == 0, "old_binary_does_not_activate_new_configuration_writer");
            CopyBundle(source, candidate); await Launch();
            var read = await Post("read", "{\"kind\":1}");
            Check(read["code"]!.GetValue<int>() == 0 && read["view"]!["behavior"]!["aiDesire"]!["value"]!.GetValue<int>() == 17
                && read["view"]!["agent"]!["agentDevices"]!["value"]!.AsArray().Count == 1,
                "new_whole_host_migrates_same_legacy_database_and_device");
            var key = JsonSerializer.Serialize(new ConfigurationOperationKey(
                JsonSerializer.Deserialize<ConfigurationGeneration>(read["generation"]!.ToJsonString()),
                read["serverUnixSeconds"]!.GetValue<long>(), Guid.NewGuid())).Trim('"');
            var saved = await Post("save", new JsonObject { ["contractRevision"] = "cfg-0.3.2", ["expectedVersion"] = read["version"]!.DeepClone(),
                ["key"] = key, ["changes"] = new JsonObject { ["behavior"] = new JsonObject { ["aiDesire"] = new JsonObject { ["kind"] = 1, ["value"] = 23 } } },
                ["secrets"] = new JsonArray(new JsonObject { ["slot"] = 0, ["kind"] = 1, ["value"] = "synthetic-rollback-candidate-key" }) }.ToJsonString());
            Check(saved["code"]!.GetValue<int>() == 0 && saved["receipt"]!["commitState"]!.GetValue<int>() == 1,
                "new_actual_http_commits_configuration_and_secret_before_rollback");
            await Stop("new-candidate"); var candidateFacts = Facts();
            Check(candidateFacts.Version > oldFacts.Version && candidateFacts.Settings != oldFacts.Settings && candidateFacts.Slots != oldFacts.Slots,
                "candidate_has_real_durable_differences_not_a_noop_dry_run");
            CopyBundle(baseline, assets); RestoreDatabase();
            Check(assets.All(p => Hash(Bound(bundle, p.Key)) == p.Value) && OriginalIntact(), "all_old_assets_and_preimage_restored_before_restart");
            await Launch(); var restoredObservation = await OldObservation(); await Stop("old-restored");
            var restoredFacts = Facts();
            Check(restoredFacts == oldFacts && restoredObservation == oldObservation, "real_old_host_restores_settings_six_slots_provider_and_device_facts");
            Check(OriginalIntact() && assets.All(p => Hash(Bound(bundle, p.Key)) == p.Value)
                && candidate.All(p => Hash(Bound(source, p.Key)) == p.Value), "original_evidence_and_current_repo_artifacts_never_overwritten");
            var proof = JsonSerializer.Serialize(new { Format = "wave2-whole-rollback-1", VerifiedAtUtc = DateTimeOffset.UtcNow,
                BaselineManifestSha256 = baselineHash, PreimageSha256 = preimageHash, PreimageManifestSha256 = preimageManifestHash,
                BaselineHeadlessSha256 = assets["BotAgent.Headless.dll"], CandidateHeadlessSha256 = candidate["BotAgent.Headless.dll"],
                BaselineAssets = assets, CandidateAssets = candidate, WholeAssetCount = assets.Count, CandidateWholeAssetCount = candidate.Count,
                OldConfigurationVersion = oldFacts.Version, CandidateConfigurationVersion = candidateFacts.Version,
                RestoredConfigurationVersion = restoredFacts.Version, OldBinaryCompatibilityVerified = true, RollbackVerified = true,
                ProcessExits = processExits, SettingsSha256 = restoredFacts.Settings, FixedSlotsSha256 = restoredFacts.Slots,
                ProviderMetadataSha256 = restoredFacts.Providers, LegacyDeviceObservationSha256 = restoredObservation },
                new JsonSerializerOptions { WriteIndented = true });
            using (var file = new FileStream(proofPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(Encoding.UTF8.GetBytes(proof)); file.Flush(flushToDisk: true); }
            Check(File.ReadAllText(proofPath) == proof && OriginalIntact(), "new_durable_proof_does_not_rewrite_historical_false_flags");
            Console.WriteLine($"CFG_CHILD whole-rollback passed={passed} failed=0 assets={assets.Count} candidateAssets={candidate.Count} proof={Path.GetFileName(proofPath)} proofSha256={Hash(proofPath)}");
            return 0;
        }
        catch (Exception error)
        { Console.WriteLine($"CFG_CHILD whole-rollback passed={passed} failed=1 type={error.GetType().Name}"); return 1; }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); _ = process.ExitCode;
                if (stdout is not null) await stdout; if (stderr is not null) await stderr; process.Dispose();
            }
            mock?.Dispose(); SqliteConnection.ClearAllPools(); var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-whole-rollback-[0-9a-f]{32}$"))
                throw new InvalidOperationException("rollback_fixture_cleanup_scope");
            if (Directory.Exists(full))
            {
                if (Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories)
                    .Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new InvalidOperationException("rollback_cleanup_reparse_denied");
                Directory.Delete(full, recursive: true);
            }
        }
    }

    private static int WriteBoundariesChild()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-writers-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root); Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_WRITERS " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            ClockBindings.InitializeStorage(); var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var secrets = new StorageSecrets(); var plugins = new StorageConfiguration.PluginStore();
            bool RejectedWithSafeWarning(Func<bool> write, string code)
            {
                var previous = Console.Error; using var warning = new StringWriter();
                bool accepted;
                try { Console.SetError(warning); accepted = write(); }
                finally { Console.SetError(previous); }
                var lines = warning.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return !accepted && lines.Length == 1 && lines[0] ==
                    $"{Clock.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} [Secrets] WARN 密钥写入失败：{code}";
            }
            var names = new[] { "apiKey", "agentServerKey", "ttsKey", "feishuAppSecret", "feishuEncryptKey", "feishuVerificationToken" };
            foreach (var name in names) Check(secrets.Save(name, "synthetic-before"), "pre_activation_fixed_slot_writer_compatible");
            plugins.SetPluginEnabled("synthetic-plugin", false);
            var prior = plugins.LoadPluginStates();
            Check(prior.Count == 1 && !prior["synthetic-plugin"] && !prior.ContainsKey("SYNTHETIC-PLUGIN"),
                "plugin_stable_keys_remain_ordinal_and_do_not_alias_case");
            AppDatabase.Write(conn => StorageDatabase.SetMetaInTransaction(conn, "plugins_state",
                "{\"synthetic-plugin\":false,\"synthetic-old-unknown\":true}"));
            var original = AppDatabase.GetMeta("plugins_state");
            foreach (var invalid in new[] { "null", "[]", " ", "{\"synthetic-plugin\":1}",
                "{\"synthetic-plugin\":true,\"synthetic-plugin\":false}",
                "{\"synthetic-plugin\":true,\"synthetic-\\u0070lugin\":false}" })
            {
                AppDatabase.Write(conn => StorageDatabase.SetMetaInTransaction(conn, "plugins_state", invalid));
                var rejected = false;
                try { plugins.LoadPluginStates(); }
                catch (JsonException error) { rejected = error.Message == "plugin_state_needs_review"; }
                Check(rejected && AppDatabase.GetMeta("plugins_state") == invalid, "corrupt_plugin_state_rejected_without_silent_repair");
            }
            AppDatabase.Write(conn => StorageDatabase.SetMetaInTransaction(conn, "plugins_state", original!));
            try
            {
                AppDatabase.Write(conn =>
                {
                    var states = new Dictionary<string, bool>(StorageConfiguration.PluginStore.LoadPluginStatesInTransaction(conn), StringComparer.Ordinal)
                    { ["synthetic-plugin"] = true };
                    StorageConfiguration.PluginStore.SavePluginStatesInTransaction(conn, states);
                    StorageSecrets.SaveInTransaction(conn, WritableSecretSlot.ModelApiKey, "synthetic-rolled-back", clock.Now.ToUnixTimeSeconds());
                    throw new InvalidOperationException("synthetic_abort");
                });
            }
            catch (InvalidOperationException error) when (error.Message == "synthetic_abort") { }
            Check(AppDatabase.GetMeta("plugins_state") == original && secrets.LoadApiKey() == "synthetic-before",
                "same_connection_plugin_and_secret_primitives_share_actual_rollback");
            AppDatabase.Write(conn => AppDatabase.Exec(conn,
                "INSERT INTO secrets(name,value,updated_unix) VALUES('officialAppSecret','synthetic-historical',0)"));
            Check(secrets.LoadOfficialSecret() is null
                && RejectedWithSafeWarning(() => secrets.SaveOfficialSecret("synthetic-rejected"), "official_secret_environment_only")
                && AppDatabase.Scalar<string>("SELECT value FROM secrets WHERE name='officialAppSecret'") == "synthetic-historical",
                "historical_official_api_neither_reads_replaces_nor_deletes_legacy_row");
            new SettingsStore().Save(new AppSettings { AiDesire = 10 });
            StorageConfiguration.ConfigurationStorageBridge.Activate(clock, new SyntheticParticipant());
            foreach (var name in names) Check(RejectedWithSafeWarning(() => secrets.Save(name, "synthetic-after"), "configuration_writer_required")
                && secrets.Load(name) == "synthetic-before",
                "activated_public_fixed_slot_writer_cannot_bypass_configuration_versions");
            var threw = false;
            try { secrets.Save("apiKey", "synthetic-after", throwOnError: true); }
            catch (InvalidOperationException error) { threw = error.Message == "configuration_writer_required"; }
            Check(threw, "throwing_legacy_secret_overload_reports_writer_fence");
            threw = false;
            try { plugins.SetPluginEnabled("synthetic-plugin", true); }
            catch (InvalidOperationException error) { threw = error.Message == "configuration_writer_required"; }
            Check(threw && AppDatabase.GetMeta("plugins_state") == original, "activated_public_plugin_writer_is_fenced_inside_shared_writer");
            threw = false;
            var previousError = Console.Error; using var expectedError = new StringWriter();
            try
            {
                Console.SetError(expectedError);
                try { new SettingsStore().Save(new AppSettings { AiDesire = 99 }); }
                catch (InvalidOperationException error) { threw = error.Message == "configuration_writer_required"; }
            }
            finally { Console.SetError(previousError); }
            Check(threw && expectedError.ToString().Contains("configuration_writer_required", StringComparison.Ordinal)
                && JsonNode.Parse(AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1")!)!["AiDesire"]!.GetValue<int>() == 10,
                "activated_public_settings_writer_reports_fence_without_replacing_user_facts");
            Check(secrets.SaveNeteaseCookie("synthetic-cookie") && secrets.LoadNeteaseCookie() == "synthetic-cookie"
                && secrets.Save("panelPassword", "synthetic-password-record"), "non_configuration_cookie_and_panel_auth_writers_remain_available");
            AppDatabase.Write(conn =>
            {
                var states = new Dictionary<string, bool>(StorageConfiguration.PluginStore.LoadPluginStatesInTransaction(conn), StringComparer.Ordinal)
                { ["synthetic-plugin"] = true };
                StorageConfiguration.PluginStore.SavePluginStatesInTransaction(conn, states);
                StorageSecrets.SaveInTransaction(conn, WritableSecretSlot.ModelApiKey, "synthetic-controlled", clock.Now.ToUnixTimeSeconds());
                Check(StorageConfiguration.ConfigurationStorageBridge.ReadConfigurationVersion(conn).Value == 0,
                    "version_projection_uses_callers_real_connection");
            });
            Check(plugins.LoadPluginStates()["synthetic-plugin"] && plugins.LoadPluginStates()["synthetic-old-unknown"]
                && secrets.LoadApiKey() == "synthetic-controlled", "controlled_primitives_preserve_unlisted_plugin_keys_and_do_not_nest_writer");
            Console.WriteLine($"CFG_CHILD write-boundaries passed={passed} failed=0"); return 0;
        }
        catch (Exception error)
        { Console.WriteLine($"CFG_CHILD write-boundaries passed={passed} failed=1 type={error.GetType().Name}"); return 1; }
        finally
        {
            SqliteConnection.ClearAllPools(); var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-writers-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_writer_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> LegacyMoodChildAsync(bool audit = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-legacy-mood-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_LEGACY_MOOD " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            new SettingsStore().Save(new AppSettings { AiDesire = 10 });
            var participant = new SyntheticParticipant();
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(clock, participant);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-legacy-mood-owner", "bot",
                ConfigurationAccess.SaveConfiguration | ConfigurationAccess.ReadEditable);
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            ConfigurationOperationKey Key() => new(read.Generation, clock.Now.ToUnixTimeSeconds(), Guid.NewGuid());
            ConfigurationPatch Patch(int value) => new(new BehaviorChanges { AiDesire = FieldEdit<int>.Set(value) },
                null, null, null, null, null, null, null);
            ValueTask<StorageConfiguration.LegacySaveOutcome> Save(ConfigurationOperationKey key, int value, FieldEdit<string> edit)
                => StorageConfiguration.ConfigurationStorageBridge.SaveLegacyWithMoodAsync(store, caller, key, Patch(value),
                    ImmutableArray<SecretEdit>.Empty, edit);
            StorageConfiguration.LegacyActionResult Apply(ConfigurationOperationKey key, FieldEdit<string> edit,
                StorageModule::BotAgent.Adapters.Persistence.MoodStore instance)
                => StorageConfiguration.ConfigurationStorageBridge.ApplyLegacyMood(store, caller, key, edit, instance);
            var mood = new StorageModule::BotAgent.Adapters.Persistence.MoodStore();
            mood.Load(root); mood.RecordPoke(clock.Now);
            var firstKey = Key(); var edit = FieldEdit<string>.Set("synthetic-one");
            var first = await Save(firstKey, 11, edit);
            Check(first.ConfigurationResult.Code == ConfigurationResultCode.Ok && participant.Value == 11
                && first.LegacyActionResults.Single().State == StorageConfiguration.LegacyActionState.NotStarted
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") == 2 && mood.Text is null,
                "configuration_and_independent_reservation_commit_before_effect");
            var found = await StorageConfiguration.ConfigurationStorageBridge.FindLegacyAsync(store, caller, firstKey);
            Check(found.ConfigurationResult.Code == ConfigurationResultCode.Ok
                && found.LegacyActionResults.Single().State == StorageConfiguration.LegacyActionState.NotStarted && mood.Text is null,
                "find_never_executes_reserved_action");
            Check((await Save(firstKey, 99, FieldEdit<string>.Set("synthetic-other"))).ConfigurationResult.Code
                == ConfigurationResultCode.IdempotencyConflict && participant.Value == 11,
                "different_config_or_mood_rejected_before_any_write");
            Check((await Save(firstKey, 11, FieldEdit<string>.Set("synthetic-other"))).ConfigurationResult.Code
                == ConfigurationResultCode.IdempotencyConflict && participant.Value == 11,
                "same_configuration_different_mood_conflicts");
            Check((await store.SaveLegacyAsync(caller, firstKey, Patch(11), ImmutableArray<SecretEdit>.Empty)).Code
                == ConfigurationResultCode.IdempotencyConflict, "omitted_mood_cannot_bypass_existing_reservation");
            var replay = await Save(firstKey, 11, FieldEdit<string>.Set("  synthetic-one！ "));
            Check(replay.ConfigurationResult.Replayed && replay.LegacyActionResults.Single().State
                == StorageConfiguration.LegacyActionState.NotStarted && mood.Text is null,
                "canonical_replay_only_reads_not_started_reservation");
            Check(Apply(firstKey, edit, mood).State == StorageConfiguration.LegacyActionState.Applied
                && mood.Text == "synthetic-one" && mood.UpdatedAt == clock.Now, "first_execution_uses_real_observed_write_and_store_clock");
            if (audit)
            {
                var failures = 0;
                foreach (var malformed in new[] { "{\"action\":\"mood\",\"errorCode\":null}",
                    "{\"action\":\"mood\",\"state\":1,\"state\":0,\"errorCode\":null}" })
                {
                    AppDatabase.Write(conn =>
                    {
                        AppDatabase.Exec(conn, "UPDATE mood SET text='synthetic-external' WHERE id=1");
                        AppDatabase.Exec(conn, "UPDATE configuration_operations SET receipt=$json WHERE namespace='legacy-mood1'",
                            ("$json", malformed));
                    });
                    var result = Apply(firstKey, edit, mood);
                    if (result.State == StorageConfiguration.LegacyActionState.Unknown
                        && AppDatabase.Scalar<string>("SELECT text FROM mood WHERE id=1") == "synthetic-external") passed++;
                    else { failures++; Console.WriteLine("FAIL CFG_LEGACY_MOOD_AUDIT corrupt_receipt_cannot_reopen_terminal_action"); }
                }
                AppDatabase.Write(conn => AppDatabase.Exec(conn, "UPDATE configuration_operations SET receipt=$json WHERE namespace='legacy-mood1'",
                    ("$json", "{\"action\":\"mood\",\"state\":1,\"errorCode\":null}")));
                var version = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
                var rows = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations");
                var codec = AppDatabase.Scalar<string>("SELECT codec_revision FROM configuration_generations");
                AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                    CREATE TRIGGER synthetic_postwrite_codec_fault AFTER UPDATE OF version ON configuration_state
                    WHEN NEW.version<>OLD.version BEGIN
                      UPDATE configuration_generations SET codec_revision='synthetic-unknown-codec';
                    END;
                    """));
                var badCodec = await Save(Key(), 99, edit);
                AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_postwrite_codec_fault"));
                if (badCodec.ConfigurationResult.Code == ConfigurationResultCode.CommitFailed && participant.Value == 11
                    && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == version
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") == rows
                    && AppDatabase.Scalar<string>("SELECT codec_revision FROM configuration_generations") == codec) passed++;
                else { failures++; Console.WriteLine("FAIL CFG_LEGACY_MOOD_AUDIT postwrite_codec_failure_must_rollback_entire_transaction"); }
                Console.WriteLine($"CFG_CHILD legacy-mood-audit passed={passed} failed={failures}");
                return failures == 0 ? 0 : 1;
            }
            var stamp = mood.UpdatedAt;
            clock.WallSeconds += 4;
            Check(Apply(firstKey, edit, mood).State == StorageConfiguration.LegacyActionState.Applied && mood.UpdatedAt == stamp
                && (await Save(firstKey, 11, edit)).ConfigurationResult.Replayed, "terminal_receipt_does_not_repeat_mood_or_refresh_ttl");
            var clearKey = Key();
            var clear = await Save(clearKey, 11, FieldEdit<string>.Clear());
            Check(clear.ConfigurationResult.Code == ConfigurationResultCode.NoChanges
                && clear.LegacyActionResults.Single().State == StorageConfiguration.LegacyActionState.NotStarted
                && Apply(clearKey, FieldEdit<string>.Clear(), mood).State == StorageConfiguration.LegacyActionState.Applied
                && mood.Text is null && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM mood_pokes") == 1,
                "configuration_no_changes_still_reserves_and_clears_mood_without_losing_pokes");
            var plainKey = Key();
            await store.SaveLegacyAsync(caller, plainKey, Patch(12), ImmutableArray<SecretEdit>.Empty);
            Check((await Save(plainKey, 12, edit)).ConfigurationResult.Code == ConfigurationResultCode.IdempotencyConflict,
                "pure_configuration_replay_cannot_append_new_mood_action");
            var count = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations");
            Check((await Save(Key(), 98, FieldEdit<string>.Set(new string('x', 25)))).ConfigurationResult.Code
                == ConfigurationResultCode.ValidationFailed && participant.Value == 12
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") == count,
                "invalid_normalized_mood_rejects_entire_configuration_before_transaction");
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_mood_reservation_fault BEFORE INSERT ON configuration_operations
                WHEN NEW.namespace='legacy-mood1' BEGIN SELECT RAISE(ABORT,'synthetic_fault'); END;
                """));
            var failedReservation = await Save(Key(), 97, edit);
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_mood_reservation_fault"));
            Check(failedReservation.ConfigurationResult.Code == ConfigurationResultCode.CommitFailed && participant.Value == 12
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") == count,
                "reservation_insert_fault_rolls_back_configuration_version_and_receipt_together");
            mood.SetText("synthetic-steady", clock.Now);
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "UPDATE mood SET text='synthetic-external' WHERE id=1"));
            var steadyKey = Key(); var steady = FieldEdit<string>.Set("synthetic-steady");
            await Save(steadyKey, 12, steady);
            Check(Apply(steadyKey, steady, mood).State == StorageConfiguration.LegacyActionState.Applied
                && AppDatabase.Scalar<string>("SELECT text FROM mood WHERE id=1") == "synthetic-steady",
                "memory_equal_is_not_durable_evidence_real_legacy_consumer_repairs_mismatch");
            var failedKey = Key(); var failedEdit = FieldEdit<string>.Set("synthetic-fail");
            await Save(failedKey, 15, failedEdit);
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_mood_write_fault BEFORE INSERT ON mood
                BEGIN SELECT RAISE(ABORT,'synthetic_fault'); END;
                """));
            StorageConfiguration.LegacyActionResult failed;
            var priorError = Console.Error; using var expectedWarning = new StringWriter();
            try { Console.SetError(expectedWarning); failed = Apply(failedKey, failedEdit, mood); }
            finally
            {
                Console.SetError(priorError);
                AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_mood_write_fault"));
            }
            Check(expectedWarning.ToString().TrimEnd('\r', '\n') ==
                    $"{Clock.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} [Mood] WARN 心情文本持久化未确认（code=mood_text_write_failed,type=SqliteException）"
                && failed.State == StorageConfiguration.LegacyActionState.Failed && participant.Value == 15
                && mood.Text == "synthetic-steady" && (await store.FindLegacyAsync(caller, failedKey))
                    .LegacyActionResults.Single().State == StorageConfiguration.LegacyActionState.Failed,
                "mood_failure_is_independent_of_known_configuration_commit");
            var claimKey = Key(); await Save(claimKey, 16, edit);
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_mood_claim_fault BEFORE UPDATE ON configuration_operations
                WHEN OLD.namespace='legacy-mood1' BEGIN SELECT RAISE(ABORT,'synthetic_fault'); END;
                """));
            var deniedClaim = Apply(claimKey, edit, mood);
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_mood_claim_fault"));
            Check(deniedClaim.State == StorageConfiguration.LegacyActionState.Unknown && mood.Text == "synthetic-steady"
                && (await store.FindLegacyAsync(caller, claimKey)).LegacyActionResults.Single().State
                    == StorageConfiguration.LegacyActionState.NotStarted, "failed_durable_claim_prevents_any_mood_effect");
            var unknownKey = Key(); var unknownEdit = FieldEdit<string>.Set("synthetic-unknown");
            await Save(unknownKey, 17, unknownEdit);
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_mood_completion_fault BEFORE UPDATE ON configuration_operations
                WHEN OLD.namespace='legacy-mood1' AND json_extract(NEW.receipt,'$.state')=1
                BEGIN SELECT RAISE(ABORT,'synthetic_fault'); END;
                """));
            var uncertain = Apply(unknownKey, unknownEdit, mood);
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_mood_completion_fault"));
            stamp = mood.UpdatedAt; clock.WallSeconds += 4;
            Check(uncertain.State == StorageConfiguration.LegacyActionState.Unknown && mood.Text == "synthetic-unknown"
                && (await store.FindLegacyAsync(caller, unknownKey)).LegacyActionResults.Single().State
                    == StorageConfiguration.LegacyActionState.Unknown
                && Apply(unknownKey, unknownEdit, mood).State == StorageConfiguration.LegacyActionState.Unknown
                && mood.UpdatedAt == stamp, "effect_committed_but_result_unknown_never_automatically_reexecutes");
            participant.Allow = false;
            var forbidden = await store.FindLegacyAsync(caller, unknownKey);
            Check(forbidden.ConfigurationResult.Code == ConfigurationResultCode.Forbidden && forbidden.LegacyActionResults.IsEmpty
                && Apply(unknownKey, unknownEdit, mood).ErrorCode == "forbidden", "reauthorization_does_not_reveal_action_receipt");
            participant.Allow = true;
            var raceKey = Key(); var raceEdit = FieldEdit<string>.Set("synthetic-race"); await Save(raceKey, 18, raceEdit);
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TABLE synthetic_mood_writes(n INTEGER NOT NULL);
                INSERT INTO synthetic_mood_writes VALUES(0);
                CREATE TRIGGER synthetic_count_mood AFTER INSERT ON mood
                BEGIN UPDATE synthetic_mood_writes SET n=n+1; END;
                CREATE TRIGGER synthetic_count_mood_update AFTER UPDATE ON mood
                BEGIN UPDATE synthetic_mood_writes SET n=n+1; END;
                """));
            await Task.WhenAll(Task.Run(() => Apply(raceKey, raceEdit, mood)), Task.Run(() => Apply(raceKey, raceEdit, mood)));
            Check(AppDatabase.Scalar<long>("SELECT n FROM synthetic_mood_writes") == 1
                && (await store.FindLegacyAsync(caller, raceKey)).LegacyActionResults.Single().State
                    == StorageConfiguration.LegacyActionState.Applied, "concurrent_claims_execute_actual_mood_write_exactly_once");
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                WITH RECURSIVE slots(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM slots WHERE n<255)
                INSERT INTO configuration_operations(authority,subject,resource,operation_key,namespace,generation,
                    expires_unix,fingerprint,receipt,terminal)
                SELECT authority,subject,resource,'synthetic-fill-'||n,namespace,generation,expires_unix,fingerprint,receipt,terminal
                FROM configuration_operations CROSS JOIN slots WHERE operation_key=$key
                AND n<=255-(SELECT COUNT(*) FROM configuration_operations WHERE subject=$subject);
                """, ("$key", JsonSerializer.Serialize(firstKey).Trim('"')), ("$subject", caller.SubjectId)));
            Check((await Save(Key(), 96, edit)).ConfigurationResult.Code == ConfigurationResultCode.CapacityExceeded
                && participant.Value == 18 && (await store.FindLegacyAsync(caller, firstKey)).ConfigurationResult.Code
                    == ConfigurationResultCode.Ok, "two_receipt_capacity_reserved_before_user_write_find_still_works_at_limit");
            Check((await store.SaveLegacyAsync(caller, Key(), Patch(19), ImmutableArray<SecretEdit>.Empty)).Code
                == ConfigurationResultCode.Ok, "single_configuration_receipt_retains_last_available_slot");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(ConfigurationContractTests).Assembly.Location);
            start.ArgumentList.Add("--configuration-legacy-mood-reopen"); start.ArgumentList.Add(root);
            start.ArgumentList.Add(JsonSerializer.Serialize(unknownKey));
            using var child = Process.Start(start) ?? throw new InvalidOperationException("synthetic_mood_restart_start");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
            var text = await output;
            Check(child.ExitCode == 0 && text.Contains("CFG_CHILD legacy-mood-reopen passed=3 failed=0", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(await error), "fresh_process_reads_old_generation_actions_without_redo");
            Console.WriteLine($"CFG_CHILD legacy-mood passed={passed} failed=0"); return 0;
        }
        catch (Exception error)
        { Console.WriteLine($"CFG_CHILD legacy-mood passed={passed} failed=1 type={error.GetType().Name}"); return 1; }
        finally
        {
            SqliteConnection.ClearAllPools(); var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-legacy-mood-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_legacy_mood_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> LegacyMoodReopenChildAsync(string root, string encodedKey)
    {
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root); Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        try
        {
            ClockBindings.InitializeStorage(); var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(clock, new SyntheticParticipant());
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-legacy-mood-owner", "bot", ConfigurationAccess.SaveConfiguration);
            var key = JsonSerializer.Deserialize<ConfigurationOperationKey>(encodedKey);
            var found = await store.FindLegacyAsync(caller, key);
            if (found.ConfigurationResult.Code != ConfigurationResultCode.Ok || found.LegacyActionResults.Single().State
                != StorageConfiguration.LegacyActionState.Unknown) return 1;
            var current = await store.ReadAsync(caller with { Access = ConfigurationAccess.ReadEditable }, ConfigurationReadKind.Editable);
            if (current.Generation == key.Generation) return 1;
            var mood = new StorageModule::BotAgent.Adapters.Persistence.MoodStore(); mood.Load(root);
            var text = mood.Text; var stamp = mood.UpdatedAt;
            if (store.ApplyLegacyMood(caller, key, FieldEdit<string>.Set("synthetic-unknown"), mood).State
                != StorageConfiguration.LegacyActionState.Unknown || mood.Text != text || mood.UpdatedAt != stamp) return 1;
            Console.WriteLine("CFG_CHILD legacy-mood-reopen passed=3 failed=0"); return 0;
        }
        catch (Exception) { Console.WriteLine("CFG_CHILD legacy-mood-reopen failed=1"); return 1; }
    }

    private static async Task<int?> BridgeDeviceGateChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-bridge-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_BRIDGE_DEVICES " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var settings = new AppSettings { EnableAgentBridge = true, AgentTimeoutSeconds = 30 };
            new SettingsStore().Save(settings);
            var participant = new SyntheticParticipant { ManageDevices = true };
            participant.DeviceRuntimeAudit = (conn, _) => new AuditLogStore().AppendInTransaction(conn,
                new AuditEvent("device_activation", "synthetic-writer", "bot", "synthetic-device", "cfg-0.3.2"));
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-bridge-owner", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            ConfigurationSaveRequest Request(ImmutableArray<DeviceConfiguration> devices)
                => new("cfg-0.3.2", new(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1")),
                    new(header.Generation, clock.WallSeconds, Guid.NewGuid()),
                    new(null, null, new AgentChanges { AgentDevices = FieldEdit<ImmutableArray<DeviceConfiguration>>.Set(devices) },
                        null, null, null, null, null), ImmutableArray<SecretEdit>.Empty);
            var created = await store.SaveAsync(caller, Request(ImmutableArray.Create(
                new DeviceConfiguration(null, "synthetic-device-a", true, null, null, null, 30))));
            Check(created.Code == ConfigurationResultCode.Ok && created.Receipt!.DeviceKeyAssignments.Length == 1,
                "registered_device_committed_before_bridge_use");
            var key = created.Receipt!.DeviceKeyAssignments.Single().Key;
            var bridge = new BotAgent.Services.Agent.AgentBridgeServer(new SettingsBox(settings), _ => { });
            // Exercise the actual server after its existing HTTP authentication boundary, not a substitute auth protocol.
            await using var peer = await SyntheticBridgePeer.ConnectAsync(bridge, "synthetic-device-a");
            var limit = Stopwatch.StartNew();
            while (!bridge.IsDeviceOnline("synthetic-device-a") && limit.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10);
            Check(bridge.IsDeviceOnline("synthetic-device-a") && await bridge.RequestModelsAsync("synthetic-device-a")
                && (await peer.ReceiveAsync())?["type"]?.GetValue<string>() == "models", "real_bridge_control_positive_before_revocation");
            object Connection(string name)
            {
                var connections = bridge.GetType().GetField("_bridges", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bridge)!;
                return connections.GetType().GetProperty("Item")!.GetValue(connections, new object[] { name })!;
            }
            async Task<bool> Until(Func<bool> predicate)
            {
                var elapsed = Stopwatch.StartNew();
                while (!predicate() && elapsed.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
                return predicate();
            }
            Task<ConfigurationSaveResult> Enable(bool enabled) => store.SaveAsync(caller, Request(ImmutableArray.Create(
                new DeviceConfiguration(key, "synthetic-device-a", enabled, null, null, null, 30)))).AsTask();
            var originalConnection = Connection("synthetic-device-a");
            var disabled = await store.SaveAsync(caller, Request(ImmutableArray.Create(
                new DeviceConfiguration(key, "synthetic-device-a", false, null, null, null, 30))));
            Check(disabled.Code == ConfigurationResultCode.Ok && !bridge.IsDeviceOnline("synthetic-device-a")
                && !bridge.Connected && bridge.BridgeNames.Count == 0 && bridge.AnyBridge is null,
                "committed_disable_removes_real_bridge_from_all_selectors");
            Check(!await bridge.RequestPiSessionsAsync("synthetic-device-a") && await peer.ReceiveAsync() is null,
                "committed_disable_blocks_control_before_websocket_write");
            Check(originalConnection.GetType().GetProperty("BoundDeviceKey")!.GetValue(originalConnection) is DeviceKey bound && bound == key,
                "bridge_ignores_client_key_and_binds_server_owned_identity");
            await Enable(true);
            await using (var racePeer = await SyntheticBridgePeer.ConnectAsync(bridge, "synthetic-device-a"))
            {
                if (!await Until(() => bridge.IsDeviceOnline("synthetic-device-a"))) throw new InvalidOperationException("synthetic_bridge_not_ready");
                var connection = Connection("synthetic-device-a");
                var gate = (SemaphoreSlim)connection.GetType().GetProperty("SendGate")!.GetValue(connection)!;
                await gate.WaitAsync();
                var task = bridge.NewTask("group:10001", "synthetic-task", "synthetic-session", "synthetic-device-a");
                var queued = bridge.TryEnqueue(task);
                bool selected;
                ConfigurationSaveResult revoked;
                try
                {
                    selected = await Until(() => ReferenceEquals(bridge.Current, task));
                    revoked = await Enable(false);
                }
                finally { gate.Release(); }
                Check(queued && selected && revoked.Code == ConfigurationResultCode.Ok && await Until(() => task.Done)
                    && !task.Ok && await racePeer.ReceiveAsync() is null,
                    "task_revoked_while_waiting_send_gate_has_zero_task_frames");
            }
            await Enable(true);
            var late = bridge.NewTask("group:10001", "synthetic-late-task", "synthetic-session", "synthetic-device-a");
            await using (var latePeer = await SyntheticBridgePeer.ConnectAsync(bridge, "synthetic-device-a"))
            {
                if (!await Until(() => bridge.IsDeviceOnline("synthetic-device-a"))) throw new InvalidOperationException("synthetic_bridge_not_ready");
                var queued = bridge.TryEnqueue(late);
                var delivered = await latePeer.ReceiveAsync();
                await Enable(false);
                await latePeer.SendAsync(new JsonObject { ["type"] = "done", ["id"] = late.Id, ["text"] = "synthetic-result", ["exitCode"] = 0 });
                Check(queued && delivered?["type"]?.GetValue<string>() == "task" && await Until(() => late.Done) && !late.Ok,
                    "revoked_connection_late_success_cannot_settle_task_as_success");
            }
            await Enable(true);
            await using (var replacement = await SyntheticBridgePeer.ConnectAsync(bridge, "synthetic-device-a"))
            {
                if (!await Until(() => bridge.IsDeviceOnline("synthetic-device-a"))) throw new InvalidOperationException("synthetic_bridge_not_ready");
                // Model the legal replacement window: the old task is still current while the new socket is indexed.
                var stateLock = bridge.GetType().GetField("_stateLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bridge)!;
                int cancelled;
                lock (stateLock)
                {
                    bridge.GetType().GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bridge, late);
                    cancelled = bridge.Cancel(late.SourceKey);
                }
                Check(cancelled == 1 && await replacement.ReceiveAsync() is null,
                    "cancel_never_redirects_old_connection_id_to_same_name_replacement");
            }
            var deleted = await store.SaveAsync(caller, Request(ImmutableArray<DeviceConfiguration>.Empty));
            await using (var deletedPeer = await SyntheticBridgePeer.ConnectAsync(bridge, "synthetic-device-a"))
                Check(deleted.Code == ConfigurationResultCode.Ok && await Until(() => deletedPeer.Closed)
                    && !bridge.IsDeviceOnline("synthetic-device-a"), "tombstoned_name_cannot_rejoin_with_forged_client_key");
            var renameCreated = await store.SaveAsync(caller, Request(ImmutableArray.Create(
                new DeviceConfiguration(null, "synthetic-rename-old", true, null, null, null, 30))));
            var renameKey = renameCreated.Receipt!.DeviceKeyAssignments.Single().Key;
            await using (var oldPeer = await SyntheticBridgePeer.ConnectAsync(bridge, "synthetic-rename-old"))
            {
                if (!await Until(() => bridge.IsDeviceOnline("synthetic-rename-old"))) throw new InvalidOperationException("synthetic_bridge_not_ready");
                await store.SaveAsync(caller, Request(ImmutableArray.Create(
                    new DeviceConfiguration(renameKey, "synthetic-rename-new", true, null, null, null, 30))));
                var renameVersion = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
                var audits = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log WHERE event_type='device_activation'");
                await using var newPeer = await SyntheticBridgePeer.ConnectAsync(bridge, "synthetic-rename-new");
                Check(await Until(() => bridge.IsDeviceOnline("synthetic-rename-new")) && !bridge.IsDeviceOnline("synthetic-rename-old")
                    && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == renameVersion
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log WHERE event_type='device_activation'") == audits + 1,
                    "real_hello_confirms_pending_rename_with_audit_without_new_configuration_version");
            }
            await using (var unknown = await SyntheticBridgePeer.ConnectAsync(bridge, "synthetic-unregistered"))
            {
                Check(await Until(() => bridge.IsDeviceOnline("synthetic-unregistered")) && await bridge.RequestModelsAsync("synthetic-unregistered")
                    && (await unknown.ReceiveAsync())?["type"]?.GetValue<string>() == "models", "unknown_legacy_first_connection_compatibility_preserved");
                AppDatabase.Write(conn => AppDatabase.Exec(conn, "ALTER TABLE configuration_devices RENAME TO synthetic_unavailable_devices"));
                try
                {
                    Check(!await bridge.RequestModelsAsync("synthetic-unregistered") && !bridge.IsDeviceOnline("synthetic-unregistered")
                        && await unknown.ReceiveAsync() is null, "storage_read_failure_denies_even_unknown_legacy_control");
                }
                finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "ALTER TABLE synthetic_unavailable_devices RENAME TO configuration_devices")); }
            }
            Console.WriteLine($"CFG_CHILD bridge-devices passed={passed} failed=0");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"CFG_CHILD bridge-devices passed={passed} failed=1 type={error.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-bridge-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_bridge_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private sealed class SyntheticBridgePeer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpClient _clientTcp;
        private readonly System.Net.Sockets.TcpClient _serverTcp;
        private readonly System.Net.WebSockets.WebSocket _client;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _handler;

        private SyntheticBridgePeer(BotAgent.Services.Agent.AgentBridgeServer bridge,
            System.Net.Sockets.TcpClient client, System.Net.Sockets.TcpClient server)
        {
            _clientTcp = client; _serverTcp = server;
            _client = System.Net.WebSockets.WebSocket.CreateFromStream(client.GetStream(), false, null, Timeout.InfiniteTimeSpan);
            var socket = System.Net.WebSockets.WebSocket.CreateFromStream(server.GetStream(), true, null, Timeout.InfiniteTimeSpan);
            _handler = bridge.HandleAsync(socket, _stop.Token);
        }

        internal static async Task<SyntheticBridgePeer> ConnectAsync(BotAgent.Services.Agent.AgentBridgeServer bridge, string name)
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new System.Net.Sockets.TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var server = await listener.AcceptTcpClientAsync();
                var peer = new SyntheticBridgePeer(bridge, client, server);
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["type"] = "hello", ["host"] = name,
                        ["key"] = Guid.NewGuid().ToString("N") }.ToJsonString());
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await peer._client.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, timeout.Token);
                    return peer;
                }
                catch { await peer.DisposeAsync(); throw; }
            }
            catch { client.Dispose(); throw; }
        }

        internal async Task<JsonObject?> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                var bytes = new byte[4096];
                var result = await _client.ReceiveAsync(new ArraySegment<byte>(bytes), timeout.Token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return null;
                if (!result.EndOfMessage) throw new InvalidOperationException("synthetic_bridge_unexpected_frame_shape");
                return JsonNode.Parse(Encoding.UTF8.GetString(bytes, 0, result.Count))!.AsObject();
            }
            catch (Exception error) when (error is OperationCanceledException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
            { return null; }
        }

        internal bool Closed => _handler.IsCompleted;

        internal async Task SendAsync(JsonObject message)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _client.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _client.Abort();
            await _handler.WaitAsync(TimeSpan.FromSeconds(3));
            _client.Dispose(); _clientTcp.Dispose(); _serverTcp.Dispose(); _stop.Dispose();
        }
    }

    private static int LegacyConfigurationPlanChild()
    {
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_LEGACY_PLAN " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            var parse = typeof(BotAgent.Adapters.Panel.WebUiServer).GetMethod("ParseLegacyConfiguration",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(JsonObject), typeof(BotAgent.Platforms.PlatformOptions) }, null)!;
            var trusted = new AppSettings();
            typeof(SettingsStore).Assembly.GetType("BotAgent.Host.CompositionRoot", throwOnError: true)!
                .GetMethod("DeclarePlatformCatalog", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { trusted });
            T Get<T>(object plan, string name) => (T)plan.GetType().GetProperty(name)!.GetValue(plan)!;
            IDisposable Parse(JsonObject body) => (IDisposable)parse.Invoke(null, new object[] { body, trusted })!;
            var beforeTrusted = JsonSerializer.Serialize(trusted);
            var drivers = trusted.DeclaredPlatformDrivers;
            var actions = trusted.DeclaredPlatformActions;
            var body = new JsonObject
            {
                ["aiDesire"] = 11, ["model"] = "synthetic-model", ["enableVoice"] = false, ["enableMusic"] = false,
                ["healthReportEnabled"] = false, ["apiKey"] = " synthetic-plan-key ", ["mood"] = "synthetic-mood", ["ttsProvider"] = "synthetic-provider",
                ["agentDevices"] = """[{"name":"synthetic-device-a","enable":true,"model":"synthetic-model","workdir":"","tools":"","timeoutSec":120}]""",
                ["platformPolicies"] = new JsonArray(new JsonObject
                {
                    ["PlatformId"] = "qq.private", ["AccountScope"] = "legacy", ["Enabled"] = true, ["ChatEnabled"] = true,
                    ["InheritActionAllowlist"] = true, ["GroupWhitelist"] = "10001", ["PrivateWhitelist"] = "",
                    ["FeatureOverrides"] = new JsonObject(), ["AllowedActions"] = new JsonArray()
                })
            };
            var beforeBody = body.ToJsonString();
            ImmutableArray<SensitiveInput> owned;
            using (var plan = Parse(body))
            {
                var changes = Get<ConfigurationPatch>(plan, "Changes");
                Check(Get<ConfigurationResultCode>(plan, "Code") == ConfigurationResultCode.Ok && changes.Behavior is not null
                    && changes.Model is not null && changes.Agent is not null && changes.Platform is not null
                    && changes.Voice is not null && changes.Music is not null && changes.Reporting is not null,
                    "aggregate_assembles_all_seven_partitions");
                Check(changes.Behavior!.AiDesire.TryGetValue(out var desire) && desire == 11
                    && changes.Model!.Model.TryGetValue(out var model) && model == "synthetic-model"
                    && changes.Agent!.AgentDevices.TryGetValue(out var devices) && devices.Single().Key is null
                    && devices.Single().NameRaw == "synthetic-device-a"
                    && changes.Platform!.PlatformPolicies.TryGetValue(out var policies)
                    && policies.Single().Target == new PolicyTarget.AccountDefault("qq.private", "legacy"),
                    "aggregate_preserves_scalar_device_and_whole_policy_intent");
                Check(changes.Voice!.EnableVoice.TryGetValue(out var voice) && !voice
                    && changes.Music!.EnableMusic.TryGetValue(out var music) && !music
                    && changes.Reporting!.HealthReportEnabled.TryGetValue(out var reporting) && !reporting,
                    "aggregate_false_is_not_missing");
                var secrets = Get<ImmutableArray<SecretEdit>>(plan, "Secrets");
                owned = (ImmutableArray<SensitiveInput>)plan.GetType().GetField("_owned", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plan)!;
                Check(secrets.Length == 6 && secrets.Count(edit => edit.Kind == SecretEditKind.Replace) == 1
                    && owned.Length == 1 && owned[0].ByteLength == Encoding.UTF8.GetByteCount("synthetic-plan-key")
                    && Get<FieldEdit<string>>(plan, "Mood").TryGetValue(out var mood) && mood == "synthetic-mood"
                    && Get<bool>(plan, "TtsProjectionRequested") && plan.ToString() == "[redacted]",
                    "aggregate_reuses_one_secret_owner_and_separate_action_intents");
                Check(body.ToJsonString() == beforeBody && JsonSerializer.Serialize(trusted) == beforeTrusted
                    && trusted.DeclaredPlatformDrivers == drivers && trusted.DeclaredPlatformActions == actions
                    && trusted.RuntimePlatformPolicies is null, "aggregate_does_not_mutate_body_or_trusted_runtime");
            }
            Check(owned.All(input => Throws<ObjectDisposedException>(() => _ = input.ByteLength)), "aggregate_disposes_original_secret_owner");
            using (var plan = Parse(new JsonObject()))
            {
                var changes = Get<ConfigurationPatch>(plan, "Changes");
                var partitions = new object[] { changes.Behavior!, changes.Model!, changes.Agent!, changes.Platform!, changes.Voice!, changes.Music!, changes.Reporting! };
                Check(partitions.SelectMany(partition => partition.GetType().GetProperties().Select(property => property.GetValue(partition)!))
                    .All(edit => (EditKind)edit.GetType().GetProperty("Kind")!.GetValue(edit)! == EditKind.Keep)
                    && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").All(edit => edit.Kind == SecretEditKind.Keep),
                    "aggregate_missing_configuration_and_secrets_are_keep");
            }
            using (var plan = Parse(new JsonObject { ["platformPolicies"] = null }))
                Check(Get<ConfigurationPatch>(plan, "Changes").Platform!.PlatformPolicies.Kind == EditKind.Keep, "aggregate_legacy_null_table_stays_keep");
            using (var plan = Parse(new JsonObject { ["platformPolicies"] = new JsonArray() }))
                Check(Get<ConfigurationPatch>(plan, "Changes").Platform!.PlatformPolicies.TryGetValue(out var cleared) && cleared.IsEmpty,
                    "aggregate_explicit_empty_table_is_set_not_missing");
            foreach (var invalid in new[]
            {
                new JsonObject { ["agentDevices"] = "not-json", ["apiKey"] = "synthetic-plan-key" },
                new JsonObject { ["platformPolicies"] = new JsonArray(new JsonObject { ["PlatformId"] = "qq.private",
                    ["AccountScope"] = "synthetic-unknown", ["Enabled"] = true }), ["apiKey"] = "synthetic-plan-key" }
            })
            {
                using var plan = Parse(invalid);
                Check(Get<ConfigurationResultCode>(plan, "Code") != ConfigurationResultCode.Ok
                    && Get<ConfigurationPatch?>(plan, "Changes") is null && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").IsEmpty,
                    "aggregate_bad_composite_rejects_before_secret_allocation");
            }
            using (var plan = Parse(new JsonObject { ["officialAppSecret"] = null, ["agentDevices"] = "not-json" }))
                Check(Get<string>(plan, "ErrorCode") == "official_secret_environment_only"
                    && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").IsEmpty, "aggregate_official_presence_rejection_has_priority");
            using (var plan = Parse(new JsonObject { ["aiDesire"] = 11, ["feishuVerificationToken"] = " " }))
                Check(Get<ConfigurationResultCode>(plan, "Code") == ConfigurationResultCode.LegacyNeedsReview
                    && Get<ConfigurationPatch?>(plan, "Changes") is null, "aggregate_empty_token_rejects_entire_plan");
            Check(!StorageDatabase.IsReady, "aggregate_never_initializes_storage");
            Console.WriteLine($"CFG_CHILD legacy-plan passed={passed} failed=0");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"CFG_CHILD legacy-plan passed={passed} failed=1 type={error.GetType().Name}");
            return 1;
        }
    }

    private static int CaptureLegacyPreimage(string repository, string capture, string baselineManifestHash)
    {
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            repository = Path.GetFullPath(repository);
            capture = Path.GetFullPath(capture);
            if (!File.Exists(Path.Combine(repository, "src", "BotAgent.Headless", "BotAgent.Headless.csproj"))
                || !string.Equals(capture, Path.Combine(repository, ".git", "agent01-wave1-rollback-b3a84e32821f442cb72d0d7aee14d670"),
                    StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(baselineManifestHash, "^[0-9A-F]{64}$"))
                throw new InvalidOperationException("preimage_scope_invalid");
            for (var directory = new DirectoryInfo(capture); directory is not null
                && directory.FullName.StartsWith(repository, StringComparison.OrdinalIgnoreCase); directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("preimage_reparse_denied");
            var manifestPath = Path.Combine(capture, "manifest.json");
            Check(Hash(manifestPath) == baselineManifestHash, "preimage_baseline_manifest_drift");
            using var baseline = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var assets = baseline.RootElement.GetProperty("Assets");
            var bundle = Path.Combine(capture, "bundle");
            bool BundleIntact()
            {
                var count = 0;
                foreach (var asset in assets.EnumerateObject())
                {
                    if (Path.IsPathRooted(asset.Name) || asset.Name.Contains(':')
                        || asset.Name.Split('/', '\\').Any(part => part is "" or "." or "..")) return false;
                    var path = Path.GetFullPath(Path.Combine(bundle, asset.Name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || !File.Exists(path)) return false;
                    for (var info = new FileInfo(path) as FileSystemInfo; info is not null
                        && info.FullName.StartsWith(bundle, StringComparison.OrdinalIgnoreCase);
                        info = info is FileInfo file ? file.Directory : ((DirectoryInfo)info).Parent)
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if (Hash(path) != asset.Value.GetString()) return false;
                    count++;
                }
                return count == 37 && Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories).Count() == count;
            }
            Check(BundleIntact(), "preimage_old_whole_bundle_intact");
            var preimage = Path.Combine(capture, "preimage.db");
            var proof = Path.Combine(capture, "preimage-manifest.json");
            var staging = Path.Combine(capture, "backup-staging");
            if (File.Exists(preimage) || Directory.Exists(preimage) || File.Exists(proof) || Directory.Exists(proof)
                || File.Exists(staging) || Directory.Exists(staging)) throw new InvalidOperationException("preimage_target_exists");
            foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
                name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
                Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", staging);
            Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", staging);
            Directory.CreateDirectory(staging);
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            Clock.Use(new SyntheticClock());
            StorageDatabase.Initialize(Path.Combine(staging, "synthetic-source.db"));
            Check(!StorageConfiguration.ConfigurationStorageBridge.RequiresCoordinatedConfigurationWrites,
                "preimage_never_activates_new_configuration_writer");
            var settings = new AppSettings
            {
                AiDesire = 17, AiModeEnabled = false, Model = "synthetic-model", ModelBaseUrl = "https://example.com/model",
                EnableProactive = false, EnableProfileSummary = false, EnableVoice = false, EnableMusic = false,
                EnableHostAgent = false, EnableServerAgent = false, EnableAgentBridge = false, HealthReportEnabled = false,
                MessageWhitelist = "10001", LocalChannelIds = "1", PlatformSwitchSchemaVersion = 1,
                AgentDevices = """[{"name":"synthetic-device-a","enable":true,"model":"synthetic-model","workdir":"","tools":"","timeoutSec":120}]"""
            };
            new SettingsStore().Save(settings);
            var secrets = new StorageSecrets();
            foreach (var slot in Enum.GetValues<WritableSecretSlot>())
                Check(secrets.Save(StorageSecrets.SlotName(slot), "synthetic-value-" + (int)slot, throwOnError: true), "preimage_synthetic_fixed_slot_saved");
            new ModelProviderStore().UpsertMetadata(new("primary", 0, "synthetic-primary", settings.ModelBaseUrl,
                settings.Model, "secret:apiKey", false));
            Check(AppDatabase.GetMeta("configuration_platform_policies") is null
                && AppDatabase.GetMeta("configuration_provider_field_modes") is null
                && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == 0,
                "preimage_legacy_facts_not_new_typed_state");
            var backup = Path.GetFullPath(AppDatabase.VacuumIntoBackup(staging, maxRetained: int.MaxValue));
            Check(string.Equals(Path.GetDirectoryName(backup), staging, StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(backup), "^qqchat_daily_[0-9]{8}_[0-9]{6}_[0-9]{3}_[0-9a-f]{6}\\.db$")
                && File.Exists(backup) && (File.GetAttributes(backup) & FileAttributes.ReparsePoint) == 0,
                "preimage_real_backup_return_path_bounded");
            File.Move(backup, preimage, overwrite: false);
            long physicalVersion;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = preimage, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check";
                Check(command.ExecuteScalar() as string == "ok", "preimage_integrity_check");
                command.CommandText = "PRAGMA user_version";
                physicalVersion = Convert.ToInt64(command.ExecuteScalar());
                command.CommandText = "SELECT json FROM settings WHERE id=1";
                var restored = JsonSerializer.Deserialize<AppSettings>((string)command.ExecuteScalar()!)!;
                Check(restored.AiDesire == 17 && restored.AgentDevices == settings.AgentDevices
                    && restored.RuntimePlatformPolicies is null && restored.DeclaredPlatformDrivers.IsEmpty,
                    "preimage_legacy_settings_and_six_field_device_shape");
                command.CommandText = "SELECT COUNT(*) FROM secrets WHERE name='officialAppSecret'";
                Check(Convert.ToInt64(command.ExecuteScalar()) == 0, "preimage_official_secret_never_stored");
                command.CommandText = "SELECT COUNT(*) FROM secrets";
                Check(Convert.ToInt64(command.ExecuteScalar()) == 6, "preimage_fixed_secret_slot_count");
                command.CommandText = "SELECT COUNT(*) FROM model_providers WHERE id='primary' AND secret_key_ref='secret:apiKey'";
                Check(Convert.ToInt64(command.ExecuteScalar()) == 1, "preimage_legacy_primary_binding_shape");
            }
            Check(Hash(manifestPath) == baselineManifestHash && BundleIntact(), "preimage_preserves_original_bundle_and_manifest");
            var preimageHash = Hash(preimage);
            var manifest = JsonSerializer.Serialize(new
            {
                Format = "wave1-legacy-preimage-1", Purpose = "synthetic-rollback-preimage-only", CapturedAtUtc = DateTimeOffset.UtcNow,
                BaselineManifestSha256 = baselineManifestHash, BaselineHeadlessSha256 = baseline.RootElement.GetProperty("HeadlessSha256").GetString(),
                ContractRevision = "cfg-0.3.2", PreimageSha256 = preimageHash, PhysicalSchemaVersion = physicalVersion,
                ConfigurationVersion = 0, SettingsRows = 1, FixedSecretSlots = 6, ProviderRows = 1, LegacyDeviceCount = 1,
                PreimageCaptured = true, OldBinaryCompatibilityVerified = false, RollbackVerified = false
            }, new JsonSerializerOptions { WriteIndented = true });
            using (var file = new FileStream(proof, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.UTF8.GetBytes(manifest));
                file.Flush(flushToDisk: true);
            }
            Check(File.ReadAllText(proof) == manifest && Hash(preimage) == preimageHash, "preimage_manifest_durable");
            Console.WriteLine($"CFG_CHILD preimage-capture passed={passed} failed=0 preimageSha256={preimageHash} manifestSha256={Hash(proof)}");
            return 0;
        }
        catch (Exception error)
        {
            // Preserve every owned partial artifact; never erase an existing baseline or an unknown path.
            Console.WriteLine($"CFG_CHILD preimage-capture passed={passed} failed=1 type={error.GetType().Name}");
            return 1;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static async Task<int?> PlatformPoliciesChildAsync()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-policies-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_POLICIES " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var declared = ImmutableArray.Create(new DriverKey("qq.private", "legacy", ""), new DriverKey("qq.official", "legacy", ""),
                new DriverKey("local", "legacy", ""), new DriverKey("feishu", "default", "feishu-main"));
            var actions = BotAgent.Services.Agent.QqActionCatalog.All.Select(spec => new ActionId(spec.Name)).ToImmutableArray();
            var initial = new AppSettings { AiDesire = 10, Model = "synthetic-model", ModelBaseUrl = "https://example.com/model",
                MessageWhitelist = "10001", PrivateChatEnabled = true, OfficialEnabled = false, LocalChannelIds = "synthetic-local",
                PlatformPolicies = new() { new() { PlatformId = "qq.private", AccountScope = "legacy", ChatEnabled = true,
                    AllowedActions = new() { "like" }, GroupWhitelist = "10001" } } };
            var declare = typeof(SettingsStore).Assembly.GetType("BotAgent.Host.CompositionRoot", throwOnError: true)!
                .GetMethod("DeclarePlatformCatalog", BindingFlags.Static | BindingFlags.NonPublic)!;
            void Declare(AppSettings settings) => declare.Invoke(null, new object[] { settings });
            AppSettings Reload()
            {
                var loaded = new SettingsStore().Load();
                Declare(loaded);
                return loaded;
            }
            Declare(initial);
            var legacy = initial.Snapshot();
            legacy.PrivateChatEnabled = false;
            var intersection = BotAgent.Services.Platforms.PlatformSwitchSettings.ImportLegacyPolicies(legacy, declared, actions);
            legacy.PlatformSwitchSchemaVersion = 1;
            var overridden = BotAgent.Services.Platforms.PlatformSwitchSettings.ImportLegacyPolicies(legacy, declared, actions);
            Check(intersection.Code == ConfigurationResultCode.Ok && intersection.Entries.Single().Configuration.ChatEnabled == ToggleRule.Disabled
                && overridden.Code == ConfigurationResultCode.Ok && overridden.Entries.Single().Configuration.ChatEnabled == ToggleRule.Enabled,
                "legacy_schema_intersection_and_override_import_preserved");
            var ambiguous = declared.Add(new("qq.private", "LEGACY", ""));
            Check(BotAgent.Services.Platforms.PlatformSwitchSettings.ImportLegacyPolicies(legacy, ambiguous, actions).Code
                == ConfigurationResultCode.LegacyNeedsReview, "legacy_case_mapping_requires_unique_trusted_account");
            legacy.PlatformPolicies = new() { new() { PlatformId = "local", AccountScope = "legacy", GroupWhitelist = "*" } };
            legacy.LocalChannelIds = "1";
            var local = BotAgent.Services.Platforms.PlatformSwitchSettings.ImportLegacyPolicies(legacy, declared, actions);
            Check(local.Code == ConfigurationResultCode.Ok && local.Entries.Single().Configuration.GroupWhitelist.TryGetExplicitValue(out var localIds)
                && localIds == "", "legacy_local_star_not_promoted_to_allow_all_or_scalar_fallback");
            new SettingsStore().Save(initial);
            new ModelProviderStore().UpsertMetadata(new("primary", 0, "synthetic-primary", initial.ModelBaseUrl, initial.Model, "secret:apiKey", false));
            var runtime = new SettingsBox(initial);
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, CreateActualTokenAdapter(runtime));
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-policy-owner", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.ReadOverview | ConfigurationAccess.SaveConfiguration);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            long Version() => AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            string State() => JsonSerializer.Serialize(new { Version = Version(), Json = AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1"),
                Policy = AppDatabase.GetMeta("configuration_platform_policies"),
                Audits = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log"),
                Operations = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") });
            PlatformPolicyConfiguration Rules(ToggleRule enabled = ToggleRule.Inherit, ToggleRule chat = ToggleRule.Inherit,
                ListRule? group = null, ListRule? individual = null, ImmutableArray<FeatureOverride> features = default)
                => new(enabled, chat, group ?? ListRule.Inherit(), individual ?? ListRule.Inherit(),
                    new(ActionRuleKind.Inherit, ImmutableArray<ActionId>.Empty), features.IsDefault ? ImmutableArray<FeatureOverride>.Empty : features);
            var account = new PolicyTarget.AccountDefault("qq.private", "legacy");
            var instance = new PolicyTarget.Instance(declared[0]);
            ConfigurationSaveRequest Request(FieldEdit<ImmutableArray<PlatformPolicyEntry>> table = default, params PolicyEdit[] edits)
                => new("cfg-0.3.2", new(Version()), new(header.Generation, clock.WallSeconds, Guid.NewGuid()),
                    new(null, null, null, new PlatformChanges { PlatformPolicies = table }, null, null, null,
                        new(ImmutableArray<PluginEnabledEdit>.Empty, ImmutableArray<ProviderEdit>.Empty, edits.ToImmutableArray())), ImmutableArray<SecretEdit>.Empty);
            var entry = new PlatformPolicyEntry(account, Rules(ToggleRule.Enabled, ToggleRule.Enabled, ListRule.Explicit("10001"),
                ListRule.Explicit("10002"), ImmutableArray.Create(new FeatureOverride(PlatformFeatureId.Voice, false))));
            StorageConfiguration.ConfigurationPlatformPolicySnapshot migrated;
            using (var connection = AppDatabase.Open()) migrated = StorageConfiguration.ConfigurationStorageBridge.LoadPlatformPolicies(connection);
            Check(migrated.Stored && migrated.Entries.Length == 1 && migrated.ExplicitFields.Values.All(bits => bits == 0),
                "controlled_initialization_legacy_origin_not_fabricated_user");
            var migrationVersion = Version();
            Check((await store.SaveAsync(caller, Request(default, new PolicyEdit(account, migrated.Entries.Single().Configuration)))).Code
                == ConfigurationResultCode.Ok && Version() == migrationVersion + 1,
                "mode_only_policy_replace_is_a_real_versioned_change");
            var first = Request(FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Set(ImmutableArray.Create(entry)));
            var result = await store.SaveAsync(caller, first);
            Check(result.Code == ConfigurationResultCode.Ok && result.Receipt is { Decision: ConfigurationDecision.Changed, CommitState: CommitState.Committed }
                && AppDatabase.GetMeta("configuration_platform_policies") is not null, "whole_typed_policy_save_supported");
            Check(ReferenceEquals(runtime.Current, initial), "policy_persistence_not_false_runtime_application");
            var before = State();
            Check((await store.SaveAsync(caller, first)).Replayed && State() == before, "policy_replay_no_second_write");
            var version = Version();
            Check((await store.SaveAsync(caller, Request(FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Set(ImmutableArray.Create(entry))))).Code
                == ConfigurationResultCode.NoChanges && Version() == version, "equal_policy_no_version_increment");
            var unloadedBox = new SettingsBox(Reload());
            var unloadedRegistry = new BotAgent.Services.Platforms.PlatformRegistry(Array.Empty<IPlatformAdapter>(), Array.Empty<IPlatformMessenger>());
            var unloadedResolver = new BotAgent.Services.Platforms.PlatformPolicyResolver(unloadedBox, unloadedRegistry, _ => actions);
            Check(!new BotAgent.Services.Qq.WhitelistGate(unloadedBox, unloadedResolver).AllowsSource(true, 10001),
                "legacy_source_without_loaded_driver_cannot_bypass_canonical_gate");
            Check((await store.SaveAsync(caller, Request(default, new PolicyEdit(instance, Rules())))).Code == ConfigurationResultCode.Ok
                && AppDatabase.Scalar<long>("SELECT user_mode FROM configuration_field_modes WHERE field_id=102") == (long)UserValueKind.Explicit,
                "sparse_policy_target_tracks_correct_field_102");
            using (var payload = JsonDocument.Parse(AppDatabase.GetMeta("configuration_platform_policies")!))
                Check(payload.RootElement.GetProperty("version").GetInt32() == 1 && payload.RootElement.GetProperty("entries").GetArrayLength() == 2
                    && payload.RootElement.GetProperty("entries").EnumerateArray().All(item => item.GetProperty("explicitFields").GetInt32() == 63),
                    "sparse_target_preserves_others_and_full_replace_origin");
            var editable = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Check(editable.View!.Platform.PlatformPolicies.TryGetEditableValue(out var policyViews), "policy_authorized_editable_projection");
            var inherited = policyViews.Single(item => item.Target == instance).GroupWhitelist;
            Check(inherited.UserMode == UserValueKind.Inherited && inherited.EffectiveSource == ConfigurationValueSource.UserConfiguration
                && inherited.ApplyState != ApplyState.Applied && inherited.TryGetEditableValue(out var inheritedList)
                && inheritedList.TryGetExplicitValue(out var inheritedText) && inheritedText == "10001",
                "policy_inherited_editable_value_and_source_without_false_apply");
            var overview = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            Check(!overview.View!.Platform.PlatformPolicies.TryGetEditableValue(out _), "policy_overview_retains_no_raw_lists");
            before = State();
            Check((await store.SaveAsync(caller, Request(default, new PolicyEdit(new PolicyTarget.Instance(new("qq.private", "legacy", "synthetic-unknown")), Rules())))).Code
                == ConfigurationResultCode.ValidationFailed && State() == before, "request_cannot_self_register_instance");
            Check((await store.SaveAsync(caller, Request(default, new PolicyEdit(new PolicyTarget.AccountDefault("qq.private", "LEGACY"), Rules())))).Code
                == ConfigurationResultCode.ValidationFailed && State() == before, "account_scope_ordinal_not_case_merged");
            Check((await store.SaveAsync(caller, Request(default, new PolicyEdit(instance, Rules()),
                new PolicyEdit(new PolicyTarget.AccountDefault("synthetic-unknown", "legacy"), Rules())))).Code == ConfigurationResultCode.ValidationFailed
                && State() == before, "last_bad_policy_rejects_whole_batch");
            var resolve = typeof(BotAgent.Services.Platforms.PlatformSwitchSettings).GetMethod("ResolvePolicyRules", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(BotAgent.Platforms.PlatformOptions), typeof(PolicyTarget), typeof(ImmutableArray<ActionId>) }, null)!;
            var projected = Reload();
            PlatformPolicyConfiguration Resolve(PolicyTarget target) => (PlatformPolicyConfiguration)resolve.Invoke(null, new object[] { projected, target, actions })!;
            bool GateAllows(long id, PlatformContext? context = null)
            {
                var adapter = new RegistryEndpoint(new("qq.private", "legacy", ""));
                var registry = new BotAgent.Services.Platforms.PlatformRegistry(new IPlatformAdapter[] { adapter }, new IPlatformMessenger[] { adapter });
                var box = new SettingsBox(projected);
                var resolver = new BotAgent.Services.Platforms.PlatformPolicyResolver(box, registry, _ => actions);
                return new BotAgent.Services.Qq.WhitelistGate(box, resolver).AllowsSource(context ?? adapter.Context, true, id);
            }
            Check(Resolve(instance).Features.Any(feature => feature.Feature == PlatformFeatureId.Voice && !feature.Enabled), "empty_instance_features_inherit_parent_false");
            Check(GateAllows(10001) && !GateAllows(10003)
                && !GateAllows(10001, new("qq.private", "legacy", "synthetic-other")), "exact_whitelist_gate_uses_rules_and_source_instance");
            Check((await store.SaveAsync(caller, Request(default, new PolicyEdit(instance, Rules(group: ListRule.Explicit("")))))).Code == ConfigurationResultCode.Ok,
                "explicit_empty_policy_list_persists");
            projected = Reload();
            Check(Resolve(instance).GroupWhitelist.TryGetExplicitValue(out var empty) && empty == "", "explicit_empty_list_stops_inheritance");
            Check(!GateAllows(10001), "explicit_empty_enforced_by_real_whitelist_gate");
            Check((await store.SaveAsync(caller, Request(default, new PolicyEdit(instance, Rules(group: ListRule.AllowAll()))))).Code == ConfigurationResultCode.Ok,
                "allow_all_distinct_from_empty_or_inherit");
            projected = Reload();
            Check(Resolve(instance).GroupWhitelist.Kind == ListRuleKind.AllowAll, "allow_all_survives_restart_projection");
            Check(GateAllows(10003), "allow_all_enforced_by_real_whitelist_gate");
            var fetcher = new RoutingFetcher();
            projected.FeishuEnabled = true;
            projected.FeishuAppId = "synthetic-app";
            projected.FeishuAppSecret = "synthetic-secret";
            using (var gateway = new BotAgent.Adapters.Platforms.Feishu.FeishuBotGateway(new SettingsBox(projected), fetcher))
            {
                var message = new OutboundMessage(new("feishu", "default", BotAgent.Domain.Conversation.ConversationKind.GroupChat,
                    "synthetic-chat"), "synthetic-body");
                foreach (var wrong in new[] { new PlatformContext("feishu", "default", "synthetic-other"),
                    new PlatformContext("feishu", "DEFAULT", "feishu-main") })
                {
                    var rejected = await gateway.SendAsync(wrong, message);
                    Check(rejected.Status == DeliveryStatus.Rejected && rejected.ReasonCode == "context_mismatch"
                        && fetcher.Paths.Count == 0, "feishu_wrong_context_denied_before_token_or_http");
                }
            }
            before = State();
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_policy_fault BEFORE INSERT ON meta WHEN NEW.key='configuration_platform_policies'
                BEGIN SELECT RAISE(ABORT,'synthetic_policy_fault'); END;
                """));
            try
            {
                var fault = Request(default, new PolicyEdit(instance, Rules(ToggleRule.Disabled))) with
                    { Changes = Request().Changes with { Behavior = new BehaviorChanges { AiDesire = FieldEdit<int>.Set(55) },
                        Registered = new(ImmutableArray<PluginEnabledEdit>.Empty, ImmutableArray<ProviderEdit>.Empty,
                            ImmutableArray.Create(new PolicyEdit(instance, Rules(ToggleRule.Disabled)))) } };
                Check((await store.SaveAsync(caller, fault)).Code == ConfigurationResultCode.CommitFailed && State() == before
                    && runtime.Current.AiDesire == 10, "policy_meta_fault_rolls_back_scalar_version_audit_and_receipt");
            }
            finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_policy_fault")); }
            var retained = AppDatabase.GetMeta("configuration_platform_policies")!;
            string MutatePolicy(Action<JsonObject> mutate)
            {
                var value = JsonNode.Parse(retained)!.AsObject();
                mutate(value);
                return value.ToJsonString();
            }
            var malformed = new List<string> { "", "{\"version\":2,\"entries\":[]}", "{\"version\":1,\"entries\":[],\"extra\":0}",
                "{\"version\":1,\"version\":1,\"entries\":[]}", "{\"version\":1}", "{\"version\":1,\"entries\":[{\"policy\":null,\"explicitFields\":64}]}" };
            malformed.Add(MutatePolicy(value => value["entries"]![0]!["policy"]!["configuration"]!["extra"] = 0));
            malformed.Add(MutatePolicy(value => value["entries"]![0]!["policy"]!["configuration"]!.AsObject().Remove("enabled")));
            malformed.Add(MutatePolicy(value => value["entries"]![0]!["policy"]!["configuration"]!["enabled"] = 7));
            malformed.Add(MutatePolicy(value => value["entries"]!.AsArray().Add(value["entries"]![0]!.DeepClone())));
            malformed.Add(retained.Replace("\"enabled\":1", "\"enabled\":1,\"enabled\":1", StringComparison.Ordinal));
            malformed.Add(retained.Replace("\"instanceId\":\"\"", "\"instanceId\":\"\\ud800\"", StringComparison.Ordinal));
            malformed.Add(MutatePolicy(value => value["entries"]![0]!["policy"]!["configuration"]!["allowedActions"]!["actions"]!.AsArray().Add("send")));
            foreach (var invalid in malformed)
            {
                AppDatabase.SetMeta("configuration_platform_policies", invalid); before = State();
                Check((await store.ReadAsync(caller, ConfigurationReadKind.Editable)).Code == ConfigurationResultCode.LegacyNeedsReview
                    && State() == before, "corrupt_policy_read_no_legacy_fallback_or_write");
                Check((await store.SaveAsync(caller, Request(default, new PolicyEdit(instance, Rules())))).Code == ConfigurationResultCode.LegacyNeedsReview
                    && State() == before, "corrupt_policy_save_no_implicit_repair");
                try { new SettingsStore().Load(); Check(false, "boot_must_reject_corrupt_policy"); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                { Check(State() == before, "boot_corrupt_policy_cannot_default_grants"); }
            }
            AppDatabase.SetMeta("configuration_platform_policies", retained);
            Check((await store.SaveAsync(caller, Request(FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Clear()))).Code == ConfigurationResultCode.Ok,
                "policy_clear_authoritative_empty");
            projected = Reload();
            var runtimePolicies = typeof(BotAgent.Platforms.PlatformOptions).GetProperty("RuntimePlatformPolicies")!;
            Check(runtimePolicies.GetValue(projected) is ImmutableArray<PlatformPolicyEntry> policies && policies.IsEmpty,
                "clear_boot_projection_does_not_revive_old_dto");
            before = State();
            Check((await store.ReadAsync(caller, ConfigurationReadKind.Editable)).Code == ConfigurationResultCode.Ok && State() == before,
                "policy_read_is_observational");
            var firstConcurrent = Request(default, new PolicyEdit(account, Rules(ToggleRule.Enabled)));
            var secondConcurrent = Request(default, new PolicyEdit(account, Rules(ToggleRule.Disabled)));
            var concurrent = await Task.WhenAll(Task.Run(async () => await store.SaveAsync(caller, firstConcurrent)),
                Task.Run(async () => await store.SaveAsync(caller, secondConcurrent)));
            Check(concurrent.Count(item => item.Code == ConfigurationResultCode.Ok) == 1 && concurrent.Count(item => item.Code == ConfigurationResultCode.VersionConflict) == 1,
                "policy_concurrent_cas_serializes_without_lost_update");
            Console.WriteLine($"CFG_CHILD platform-policies passed={passed} failed=0"); return 0;
        }
        catch (Exception ex)
        { Console.WriteLine($"CFG_CHILD platform-policies passed={passed} failed=1 type={ex.GetType().Name}"); return 1; }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-policies-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_policy_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> MoodPersistenceChildAsync()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-mood-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_MOOD " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        T Quiet<T>(Func<T> action)
        {
            var original = Console.Error; using var captured = new StringWriter(); Console.SetError(captured);
            try { return action(); }
            finally
            {
                Console.SetError(original);
                var lines = captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Any(line => !line.Contains("[Mood] WARN ", StringComparison.Ordinal)
                    || !(line.Contains("synthetic_mood_", StringComparison.Ordinal) || line.Contains("mood_text_write_", StringComparison.Ordinal))))
                    throw new InvalidOperationException("synthetic_mood_unexpected_diagnostic");
            }
        }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, new SyntheticParticipant());
            var ttl = 120;
            var mood = new StorageModule::BotAgent.Adapters.Persistence.MoodStore(() => ttl);
            var now = clock.Now; mood.Load(root);
            string? DatabaseText() => AppDatabase.Scalar<string>("SELECT text FROM mood WHERE id=1");
            long? DatabaseUpdated() => AppDatabase.Scalar<long?>("SELECT updated_unix FROM mood WHERE id=1");
            long Version() => AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            Check(!mood.SetText(null, now) && !mood.SetText(new string('x', 25), now), "legacy_invalid_text_ignored");
            Check(mood.SetText("  synthetic-old!!  ", now) && mood.Text == "synthetic-old" && DatabaseText() == "synthetic-old",
                "legacy_cleaning_and_initial_commit");
            Check(!mood.SetText("synthetic-old", now.AddSeconds(1)) && mood.UpdatedAt == now && DatabaseUpdated() == now.ToUnixTimeSeconds(),
                "legacy_equal_text_does_not_refresh");
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_mood_text_fault BEFORE UPDATE ON mood
                BEGIN SELECT RAISE(ABORT,'synthetic_mood_text_fault'); END;
                """));
            try
            {
                Check(Quiet(() => mood.SetText("synthetic-new", now.AddSeconds(2))), "legacy_bool_preserves_accepted_intent_not_persist_success");
                Check(mood.Text == "synthetic-old" && mood.UpdatedAt == now && DatabaseText() == "synthetic-old"
                    && DatabaseUpdated() == now.ToUnixTimeSeconds(), "failed_text_write_never_publishes_memory");
            }
            finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_mood_text_fault")); }
            (string State, bool IntentChanged, string Summary) Observe(string? text, DateTimeOffset at, bool reset = false)
            {
                var method = mood.GetType().GetMethod(reset ? "ResetObserved" : "SetTextObserved", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var result = Quiet(() => method.Invoke(mood, reset ? new object[] { at } : new object?[] { text, at }))!;
                return (result.GetType().GetProperty("State")!.GetValue(result)!.ToString()!,
                    (bool)result.GetType().GetProperty("IntentChanged")!.GetValue(result)!, result.ToString()!);
            }
            Check(Observe(null, now) is { State: "Rejected", IntentChanged: false }, "observed_invalid_distinguished");
            Check(Observe("synthetic-old", now.AddSeconds(3)) is { State: "Unchanged", IntentChanged: false }
                && DatabaseUpdated() == now.ToUnixTimeSeconds(), "observed_nochange_distinguished");
            var committed = Observe("synthetic-new", now.AddSeconds(4));
            Check(committed is { State: "Committed", IntentChanged: true, Summary: "MoodTextWriteResult" }
                && mood.Text == DatabaseText() && mood.UpdatedAt?.ToUnixTimeSeconds() == DatabaseUpdated(), "observed_commit_and_safe_summary");
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_mood_poke_fault BEFORE INSERT ON mood_pokes
                BEGIN SELECT RAISE(ABORT,'synthetic_mood_poke_fault'); END;
                """));
            try
            {
                Quiet(() => { mood.RecordPoke(now.AddSeconds(5)); return true; });
                Check(mood.RecentPokes(now.AddSeconds(5)) == 1 && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM mood_pokes") == 0,
                    "failed_poke_retains_pending_memory_history");
                Check(Observe("synthetic-poke-fault", now.AddSeconds(6)) is { State: "Failed", IntentChanged: true }
                    && mood.Text == "synthetic-new" && DatabaseText() == "synthetic-new", "poke_failure_rolls_back_text_without_publish");
            }
            finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_mood_poke_fault")); }
            Check(Observe("synthetic-recovered", now.AddSeconds(7)).State == "Committed"
                && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM mood_pokes") == 1, "later_text_commit_flushes_pending_pokes");
            Check(Observe(null, now.AddSeconds(8), true).State == "Committed" && mood.Text is null
                && mood.UpdatedAt == now.AddSeconds(8) && mood.RecentPokes(now.AddSeconds(8)) == 1,
                "reset_refreshes_timestamp_without_clearing_pokes");
            Check(Observe(new string('x', 24), now.AddSeconds(9)).State == "Committed", "exact_24_character_boundary");
            ttl = 0;
            Check(mood.CurrentText(now.AddHours(3)) == new string('x', 24), "zero_ttl_never_expires_text");
            ttl = 120; Observe("synthetic-expiring", now.AddSeconds(10));
            Check(mood.CurrentText(now.AddSeconds(130)) == "synthetic-expiring", "ttl_exact_boundary_remains_visible");
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TRIGGER synthetic_mood_expire_fault BEFORE UPDATE ON mood
                BEGIN SELECT RAISE(ABORT,'synthetic_mood_expire_fault'); END;
                """));
            try
            {
                Check(Quiet(() => mood.CurrentText(now.AddSeconds(131))) is null
                    && mood.Text == "synthetic-expiring" && DatabaseText() == "synthetic-expiring", "expired_text_hidden_without_false_durable_clear");
                Check(!Quiet(() => mood.Describe(now.AddSeconds(131))).Contains("synthetic-expiring", StringComparison.Ordinal)
                    && mood.UpdatedAt == now.AddSeconds(10), "expired_description_hidden_and_timestamp_preserved");
            }
            finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_mood_expire_fault")); }
            Check(mood.CurrentText(now.AddSeconds(131)) is null && mood.Text is null && DatabaseText() is null
                && mood.UpdatedAt == now.AddSeconds(10) && DatabaseUpdated() == now.AddSeconds(10).ToUnixTimeSeconds(),
                "confirmed_expiration_clears_text_preserving_timestamp");
            Observe("synthetic-before-commit", now.AddSeconds(132));
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                CREATE TABLE synthetic_mood_parent(id INTEGER PRIMARY KEY);
                CREATE TABLE synthetic_mood_deferred(id INTEGER REFERENCES synthetic_mood_parent(id) DEFERRABLE INITIALLY DEFERRED);
                CREATE TRIGGER synthetic_mood_commit_fault AFTER UPDATE ON mood
                BEGIN INSERT INTO synthetic_mood_deferred(id) VALUES(1); END;
                """));
            try
            {
                Check(Observe("synthetic-commit-unknown", now.AddSeconds(133)) is { State: "Unknown", IntentChanged: true }
                    && mood.Text == "synthetic-before-commit" && DatabaseText() == "synthetic-before-commit",
                    "real_deferred_commit_failure_reports_unknown_not_committed");
            }
            finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_mood_commit_fault")); }
            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                var method = mood.GetType().GetMethod("SetTextObserved", BindingFlags.NonPublic | BindingFlags.Instance)!;
                return method.Invoke(mood, new object[] { "synthetic-concurrent-" + i, now.AddSeconds(140 + i) });
            })).ToArray();
            await Task.WhenAll(tasks);
            Check(tasks.All(task => task.Result!.GetType().GetProperty("State")!.GetValue(task.Result)!.ToString() == "Committed")
                && mood.Text == DatabaseText() && mood.UpdatedAt?.ToUnixTimeSeconds() == DatabaseUpdated(), "concurrent_text_writers_publish_in_commit_order");
            var restored = new StorageModule::BotAgent.Adapters.Persistence.MoodStore(() => ttl); restored.Load(root);
            Check(restored.Text == DatabaseText() && restored.UpdatedAt?.ToUnixTimeSeconds() == DatabaseUpdated()
                && restored.RecentPokes(now.AddSeconds(148)) == 1, "recovery_preserves_text_time_and_poke_history");
            Check(Version() == 0, "mood_writes_do_not_claim_configuration_version");
            Console.WriteLine($"CFG_CHILD mood-persistence passed={passed} failed=0"); return 0;
        }
        catch (Exception ex)
        { Console.WriteLine($"CFG_CHILD mood-persistence passed={passed} failed=1 type={ex.GetType().Name}"); return 1; }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-mood-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_mood_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static void SeedSyntheticProviderMetadata(ModelProviderDefinition row)
    {
        // Fixture corruption/catalog setup, never a production alternative to the activated coordinator.
        var write = typeof(ModelProviderStore).GetMethod("UpsertMetadataInTransaction", BindingFlags.NonPublic | BindingFlags.Static)!;
        AppDatabase.Write(conn => write.Invoke(null, new object[] { conn, row }));
    }

    private static int ProviderWriterFenceChild()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-provider-fence-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); passed++; }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var providers = new ModelProviderStore();
            var row = new ModelProviderDefinition("synthetic-fence", 10, "synthetic-original", "https://example.com/original",
                "synthetic-model", "secret:apiKey", false);
            providers.UpsertMetadata(row);
            const string originalModes = "{\"version\":1,\"entries\":[{\"key\":\"synthetic-fence\",\"explicitFields\":7}]}";
            AppDatabase.SetMeta("configuration_provider_field_modes", originalModes);
            providers.UpsertMetadata(row);
            Check(AppDatabase.GetMeta("configuration_provider_field_modes") == originalModes, "equal_legacy_writer_leaves_modes");
            providers.UpsertMetadata(row with { Name = "synthetic-renamed" });
            using (var modes = JsonDocument.Parse(AppDatabase.GetMeta("configuration_provider_field_modes")!))
                Check(modes.RootElement.GetProperty("entries")[0].GetProperty("explicitFields").GetInt32() == 6,
                    "legacy_changed_name_clears_only_name_origin");
            var fail = new SyntheticParticipant { Initialization = (_, _) =>
            {
                Check(StorageConfiguration.ConfigurationStorageBridge.RequiresCoordinatedConfigurationWrites, "activation_fence_raised");
                throw new InvalidOperationException("synthetic_activation_failure");
            } };
            try { StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, fail); Check(false, "activation_failure_expected"); }
            catch (InvalidOperationException ex) { Check(ex.Message == "synthetic_activation_failure", "activation_failure_propagated"); }
            Check(!StorageConfiguration.ConfigurationStorageBridge.RequiresCoordinatedConfigurationWrites, "failed_activation_releases_fence");
            providers.UpsertMetadata(row with { Name = "synthetic-after-failure" });
            Check(providers.Find(row.Id)!.Name == "synthetic-after-failure", "failed_activation_does_not_block_legacy_forever");
            using var queued = new ManualResetEventSlim();
            using var activationEntered = new ManualResetEventSlim();
            // An external caller must not inherit the transaction callback's AsyncLocal nested-write marker.
            var racedWriter = Task.Run(() =>
            {
                if (!activationEntered.Wait(TimeSpan.FromSeconds(5))) return "synthetic_activation_not_entered";
                var fenced = StorageConfiguration.ConfigurationStorageBridge.RequiresCoordinatedConfigurationWrites;
                queued.Set();
                try { providers.UpsertMetadata(row); return "synthetic_writer_bypassed"; }
                catch (InvalidOperationException ex) { return fenced ? ex.Message : "synthetic_fence_not_observed"; }
            });
            var participant = new SyntheticParticipant { Initialization = (_, _) =>
            {
                activationEntered.Set();
                Check(queued.Wait(TimeSpan.FromSeconds(5)), "parallel_writer_attempted_during_activation");
                return false;
            } };
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            Check(racedWriter.GetAwaiter().GetResult() == "configuration_writer_required", "activation_commit_gap_writer_denied");
            Check(StorageConfiguration.ConfigurationStorageBridge.RequiresCoordinatedConfigurationWrites, "committed_activation_fence_retained");
            try { providers.UpsertMetadata(row); Check(false, "activated_writer_must_reject"); }
            catch (InvalidOperationException ex) { Check(ex.Message == "configuration_writer_required", "activated_writer_denied"); }
            Check(ReferenceEquals(store, StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant)), "activation_idempotent");
            Console.WriteLine($"CFG_CHILD provider-writer-fence passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        { Console.WriteLine($"CFG_CHILD provider-writer-fence passed={passed} failed=1 type={ex.GetType().Name}"); return 1; }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-provider-fence-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_provider_fence_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> RegisteredProvidersChildAsync()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-providers-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_PROVIDERS " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var initial = new AppSettings { ApiKey = "synthetic-live-primary-key", Model = "synthetic-primary-model",
                ModelBaseUrl = "https://example.com/primary", AiDesire = 10, AgentMaskSensitive = true };
            new SettingsStore().Save(initial);
            var runtime = new SettingsBox(initial);
            var providers = new ModelProviderStore();
            providers.UpsertMetadata(new("primary", 0, "synthetic-primary", initial.ModelBaseUrl, initial.Model, "secret:apiKey", false));
            providers.UpsertMetadata(new("synthetic-secondary", 10, "synthetic-secondary-name", "https://example.com/secondary",
                "synthetic-secondary-model", "secret:apiKey", true));
            providers.UpsertMetadata(new("synthetic-legacy", 20, "synthetic-legacy", "https://example.com/registered",
                "synthetic-legacy-model", "synthetic-unknown-reference", false));
            providers.UpsertMetadata(new("synthetic-live", 30, "synthetic-live", "https://example.com/live",
                "synthetic-live-model", "secret:apiKey", true));
            providers.SaveCircuit(new("synthetic-secondary", BotAgent.Services.Resilience.ProviderCircuitState.Open, 3,
                clock.UtcNow.AddMinutes(1), false));
            new StorageSecrets().SaveApiKey("synthetic-database-key");
            var captured = providers.LoadAll().ToArray();
            var type = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)!;
            Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (conn, kind) =>
            {
                // The first red is an actual Save result, never a missing-member/reflection failure.
                var project = typeof(SettingsStore).GetMethod("ProjectRegisteredProviderViews", BindingFlags.NonPublic | BindingFlags.Static);
                if (project is null) return new(ImmutableArray<PluginConfigurationView>.Empty, ImmutableArray<ProviderConfigurationView>.Empty);
                try
                {
                    return new(ImmutableArray<PluginConfigurationView>.Empty,
                        (ImmutableArray<ProviderConfigurationView>)project.Invoke(null, new object[] { conn,
                            typeof(ModelProviderStore).GetMethod("LoadAllInTransaction", BindingFlags.NonPublic | BindingFlags.Static)!
                                .Invoke(null, new object[] { conn })!, captured, kind })!);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is JsonException)
                { throw new JsonException("synthetic_origin_codec"); }
            };
            Func<ConfigurationCaller, ConfigurationAccess, bool> authorization = (_, _) => true;
            Func<SqliteConnection, ImmutableArray<StorageConfiguration.SecretMutation>, ImmutableArray<SecretMetadata>> sources = (conn, edits) =>
                Enum.GetValues<WritableSecretSlot>().Select(slot =>
                {
                    var edit = edits.FirstOrDefault(x => x.Slot == slot);
                    var configured = edit.Kind == SecretEditKind.Replace || edit.Kind == SecretEditKind.Keep
                        && StorageSecrets.LoadInTransaction(conn, slot) is not null;
                    return new SecretMetadata(slot, configured, configured ? CredentialSource.Database : CredentialSource.None);
                }).ToImmutableArray();
            Func<ConfigurationFieldId, ConfigurationValueSource> provenance = _ => ConfigurationValueSource.LegacyCompatibility;
            Func<ConfigurationFieldId, FieldEffect> effect = _ => FieldEffect.Restart;
            var participant = (StorageConfiguration.ConfigurationStorageParticipant)type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single().Invoke(new object[] { runtime, authorization, sources, registered, provenance, effect, true });
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-provider-owner", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.ReadOverview | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            long Version() => AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            string State() => JsonSerializer.Serialize(new { Version = Version(), Providers = providers.LoadAll(),
                Modes = AppDatabase.GetMeta("configuration_provider_field_modes"), Key = new StorageSecrets().LoadApiKey(),
                Audits = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log"),
                Operations = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") });
            ProviderEdit Edit(string id = "synthetic-secondary", FieldEdit<string> name = default, FieldEdit<string> url = default,
                FieldEdit<string> model = default, FieldEdit<bool> enabled = default, FieldEdit<int> priority = default,
                CredentialBindingEdit? binding = null) => new(new(id), new(name, priority, url, model, enabled, binding ?? CredentialBindingEdit.KeepExisting()));
            ConfigurationSaveRequest Request(params ProviderEdit[] edits) => new("cfg-0.3.2", new(Version()),
                new(header.Generation, clock.WallSeconds, Guid.NewGuid()), new(null, null, null, null, null, null, null,
                    new(ImmutableArray<PluginEnabledEdit>.Empty, edits.ToImmutableArray(), ImmutableArray<PolicyEdit>.Empty)), ImmutableArray<SecretEdit>.Empty);
            var request = Request(Edit(name: FieldEdit<string>.Set("synthetic-edited-name")));
            var result = await store.SaveAsync(caller, request);
            Check(result.Code == ConfigurationResultCode.Ok && result.Receipt is { CommitState: CommitState.Committed }
                && providers.Find("synthetic-secondary")!.Name == "synthetic-edited-name", "registered_name_save_supported");
            Check(providers.Find("synthetic-secondary") is { CircuitState: BotAgent.Services.Resilience.ProviderCircuitState.Open,
                ConsecutiveHardFailures: 3 } && providers.Find("synthetic-secondary")!.CooldownUntil == clock.UtcNow.AddMinutes(1),
                "metadata_save_preserves_circuit_facts");
            Check(ReferenceEquals(runtime.Current, initial), "persist_does_not_publish_runtime");
            var before = State();
            Check((await store.SaveAsync(caller, request)).Replayed && State() == before, "provider_replay_no_second_write");
            Check(AppDatabase.GetMeta("configuration_provider_field_modes") is not null, "explicit_origin_persisted");
            before = State();
            var equalVersion = Version();
            Check((await store.SaveAsync(caller, Request(Edit(name: FieldEdit<string>.Set("synthetic-edited-name"))))).Code
                == ConfigurationResultCode.NoChanges && Version() == equalVersion && State() != before
                && providers.Find("synthetic-secondary")!.Name == "synthetic-edited-name",
                "equal_explicit_metadata_no_version_change");
            var version = Version();
            Check((await store.SaveAsync(caller, Request(Edit(model: FieldEdit<string>.Set("synthetic-secondary-model"))))).Code
                == ConfigurationResultCode.Ok && Version() == version + 1, "equal_legacy_value_explicit_origin_changes_version");
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            var secondary = read.View!.Registered.Providers.Single(x => x.Key.Value == "synthetic-secondary");
            Check(secondary.Name.Visibility == FieldVisibility.NameEditable && secondary.Name.TryGetEditableValue(out var raw)
                && raw == "synthetic-edited-name" && secondary.Name.Display != raw, "masked_display_and_name_raw");
            Check(secondary.ModelName.EffectiveSource == ConfigurationValueSource.UserConfiguration
                && secondary.BaseUrl.EffectiveSource == ConfigurationValueSource.LegacyCompatibility, "origin_per_field_not_row_exists");
            Check(secondary.RuntimeApplyState != ApplyState.Applied, "changed_metadata_not_applied");
            var primary = read.View.Registered.Providers.Single(x => x.Key.Value == "primary");
            Check(ReferenceEquals(primary.ModelName, read.View.Model.Model) && ReferenceEquals(primary.BaseUrl, read.View.Model.ModelBaseUrl),
                "primary_uses_canonical_read_fields");
            var serialized = JsonSerializer.Serialize(read);
            Check(!serialized.Contains("synthetic-database-key", StringComparison.Ordinal)
                && !serialized.Contains("synthetic-live-primary-key", StringComparison.Ordinal)
                && !serialized.Contains("secret:apiKey", StringComparison.Ordinal)
                && !serialized.Contains("synthetic-unknown-reference", StringComparison.Ordinal), "provider_read_nonsecret_metadata_only");
            var overview = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            Check(overview.View!.Registered.Providers.All(x => !x.Name.TryGetEditableValue(out _) && !x.BaseUrl.TryGetEditableValue(out _)
                && !x.ModelName.TryGetEditableValue(out _)), "overview_hides_editable_values");
            before = State();
            Check((await store.SaveAsync(caller, Request(Edit(name: FieldEdit<string>.Set("synthetic-should-rollback")),
                Edit("synthetic-missing", name: FieldEdit<string>.Set("synthetic-last-invalid"))))).Code == ConfigurationResultCode.ValidationFailed
                && State() == before, "last_invalid_provider_rejects_whole_batch");
            before = State();
            Check((await store.SaveAsync(caller, Request(Edit(url: FieldEdit<string>.Set("https://example.com/unregistered"))))).Code
                == ConfigurationResultCode.ValidationFailed && State() == before, "unknown_endpoint_not_self_authorized");
            Check((await store.SaveAsync(caller, Request(Edit(url: FieldEdit<string>.Set("https://example.com/registered"))))).Code
                == ConfigurationResultCode.Ok && providers.Find("synthetic-secondary")!.BaseUrl == "https://example.com/registered", "prior_registered_endpoint_allowed");
            before = State();
            Check((await store.SaveAsync(caller, Request(Edit(url: FieldEdit<string>.Clear())))).Code == ConfigurationResultCode.ValidationFailed
                && State() == before, "required_endpoint_clear_rejected");
            Check((await store.SaveAsync(caller, Request(Edit(name: FieldEdit<string>.Clear())))).Code == ConfigurationResultCode.Ok
                && providers.Find("synthetic-secondary")!.Name == "", "optional_name_clear_supported");
            Check((await store.SaveAsync(caller, Request(Edit(priority: FieldEdit<int>.Set(-5))))).Code == ConfigurationResultCode.Ok
                && providers.Find("synthetic-secondary")!.Priority == -5, "legacy_priority_has_no_invented_range");
            before = State();
            Check((await store.SaveAsync(caller, Request(Edit("primary", enabled: FieldEdit<bool>.Set(true))))).Code
                == ConfigurationResultCode.ValidationFailed && State() == before, "primary_enabled_read_only");
            Check((await store.SaveAsync(caller, Request(Edit(binding: CredentialBindingEdit.UsePrimaryModelCredential())))).Code
                == ConfigurationResultCode.Ok && providers.Find("synthetic-secondary")!.SecretKeyRef == "model:primary", "primary_binding_live_not_env_copy");
            before = State();
            Check((await store.SaveAsync(caller, Request(Edit(binding: CredentialBindingEdit.Unconfigured())))).Code
                == ConfigurationResultCode.ValidationFailed && State() == before, "enabled_unconfigured_whole_rejection");
            Check((await store.SaveAsync(caller, Request(Edit(enabled: FieldEdit<bool>.Set(false), binding: CredentialBindingEdit.Unconfigured())))).Code
                == ConfigurationResultCode.Ok && providers.Find("synthetic-secondary")!.SecretKeyRef == "model:unconfigured"
                && new StorageSecrets().LoadApiKey() == "synthetic-database-key", "disabled_unconfigured_keeps_shared_secret");
            before = State();
            Check((await store.SaveAsync(caller, Request(Edit(binding: CredentialBindingEdit.UseRegisteredModelCredential(new("synthetic-credential")))))).Code
                == ConfigurationResultCode.ValidationFailed && State() == before, "unsupported_credential_registry_rejected");
            Check((await store.SaveAsync(caller, Request(Edit("synthetic-legacy", name: FieldEdit<string>.Set("synthetic-legacy-retained"))))).Code
                == ConfigurationResultCode.Ok && providers.Find("synthetic-legacy")!.SecretKeyRef == "synthetic-unknown-reference", "disabled_unclassified_keep_retained");
            before = State();
            Check((await store.SaveAsync(caller, Request(Edit("synthetic-legacy", enabled: FieldEdit<bool>.Set(true))))).Code
                == ConfigurationResultCode.ValidationFailed && State() == before, "enabled_unclassified_no_credential_guess");
            Check((await store.SaveAsync(caller, Request(Edit("synthetic-legacy", binding: CredentialBindingEdit.Unconfigured())))).Code
                == ConfigurationResultCode.Ok, "explicit_binding_repairs_legacy_unknown");
            var modes = AppDatabase.GetMeta("configuration_provider_field_modes");
            foreach (var invalid in new[] { "{\"version\":2,\"entries\":[]}", "{\"version\":1,\"entries\":[],\"extra\":0}",
                "{\"version\":1,\"version\":1,\"entries\":[]}", "{\"version\":1,\"entries\":[{\"key\":\"synthetic-secondary\",\"explicitFields\":8}]}",
                "{\"version\":1,\"entries\":[{\"key\":\"synthetic-secondary\",\"explicitFields\":-1}]}",
                "{\"version\":1,\"entries\":[{\"key\":\"synthetic-secondary\",\"explicitFields\":1,\"extra\":0}]}",
                "{\"version\":1,\"entries\":[{\"key\":\"synthetic-secondary\",\"explicitFields\":1},{\"key\":\"synthetic-secondary\",\"explicitFields\":2}]}" })
            {
                AppDatabase.SetMeta("configuration_provider_field_modes", invalid); before = State();
                Check((await store.SaveAsync(caller, Request(Edit(name: FieldEdit<string>.Set("synthetic-codec-rejected"))))).Code
                    == ConfigurationResultCode.LegacyNeedsReview && State() == before, "invalid_origin_codec_not_repaired");
                Check((await store.ReadAsync(caller, ConfigurationReadKind.Editable)).Code == ConfigurationResultCode.LegacyNeedsReview
                    && State() == before, "invalid_origin_read_safe_rejection");
            }
            AppDatabase.SetMeta("configuration_provider_field_modes", modes!);
            foreach (var point in new[] { ("model_providers", "UPDATE"), ("meta", "INSERT"), ("secrets", "INSERT"),
                ("security_audit_log", "INSERT"), ("configuration_operations", "INSERT") })
            {
                before = State();
                AppDatabase.Write(conn => AppDatabase.Exec(conn, $"CREATE TRIGGER synthetic_provider_fault BEFORE {point.Item2} ON {point.Item1} "
                    + "BEGIN SELECT RAISE(ABORT,'synthetic_provider_fault'); END;"));
                try
                {
                    using var input = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-provider-fault-key"));
                    var faultRequest = Request(Edit(name: FieldEdit<string>.Set("synthetic-fault-name"), url: FieldEdit<string>.Set("https://example.com/registered")),
                        Edit("synthetic-legacy", model: FieldEdit<string>.Set("synthetic-legacy-model")))
                        with { Secrets = ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, input)) };
                    var fault = await store.SaveAsync(caller, faultRequest);
                    var unchanged = State() == before;
                    Console.WriteLine($"CFG_PROVIDER_FAULT table={point.Item1} code={fault.Code} unchanged={unchanged}");
                    Check(fault.Code == ConfigurationResultCode.CommitFailed
                        && unchanged && ReferenceEquals(runtime.Current, initial), "provider_fault_rolls_back_all_participants");
                }
                finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_provider_fault")); }
            }
            var firstConcurrent = Request(Edit(name: FieldEdit<string>.Set("synthetic-concurrent-first")));
            var secondConcurrent = Request(Edit(name: FieldEdit<string>.Set("synthetic-concurrent-second")));
            var concurrent = await Task.WhenAll(Task.Run(async () => await store.SaveAsync(caller, firstConcurrent)),
                Task.Run(async () => await store.SaveAsync(caller, secondConcurrent)));
            Check(concurrent.Count(x => x.Code == ConfigurationResultCode.Ok) == 1
                && concurrent.Count(x => x.Code == ConfigurationResultCode.VersionConflict) == 1
                && Version() == firstConcurrent.ExpectedVersion.Value + 1, "provider_concurrent_cas_never_overwrites_winner");
            before = State();
            try
            {
                var row = providers.Find("synthetic-secondary")!;
                providers.UpsertMetadata(new(row.Id, row.Priority, "synthetic-bypass", row.BaseUrl, row.ModelName, row.SecretKeyRef, row.IsEnabled));
                Check(false, "active_public_writer_must_reject");
            }
            catch (InvalidOperationException ex) { Check(ex.Message == "configuration_writer_required" && State() == before, "active_public_writer_rejects_bypass"); }
            providers.EnsurePrimary(new AppSettings { Model = "synthetic-no-startup-overwrite", ModelBaseUrl = "https://example.com/primary" });
            Check(State() == before, "existing_primary_startup_never_overwritten");
            var host = typeof(SettingsStore).Assembly.GetType("BotAgent.Host.CompositionRoot")!;
            var gate = host.GetMethod("TryBuildCurrentProviderRoute", BindingFlags.Static | BindingFlags.NonPublic)!;
            ModelProviderRoute? Route(PersistedModelProvider row) => (ModelProviderRoute?)gate.Invoke(null, new object[] { row, runtime.Current });
            Check(Route(captured.Single(x => x.Id == "synthetic-secondary")) is null, "captured_route_denied_after_desired_binding_endpoint_change");
            Check(Route(providers.Find("synthetic-secondary")!) is null, "unconfigured_never_routes");
            Check(Route(captured.Single(x => x.Id == "primary")) is null, "captured_disabled_never_routes");
            var live = captured.Single(x => x.Id == "synthetic-live");
            Check(Route(live)?.ApiKey == "synthetic-database-key", "coherent_unchanged_route_reads_committed_database");
            using (var rotation = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-rotated-database-key")))
            {
                var rotate = Request() with { Secrets = ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, rotation)) };
                Check((await store.SaveAsync(caller, rotate)).Code == ConfigurationResultCode.Ok
                    && Route(live)?.ApiKey == "synthetic-rotated-database-key" && ReferenceEquals(runtime.Current, initial),
                    "committed_database_rotation_live_without_fake_projection");
            }
            before = State();
            var delete = Request() with { Secrets = ImmutableArray.Create(SecretEdit.Delete(WritableSecretSlot.ModelApiKey)) };
            Check((await store.SaveAsync(caller, delete)).Code == ConfigurationResultCode.ValidationFailed && State() == before,
                "unedited_enabled_database_provider_validated_on_delete");
            Check((await store.SaveAsync(caller, Request(Edit("synthetic-live", enabled: FieldEdit<bool>.Set(false))))).Code
                == ConfigurationResultCode.Ok && Route(live) is null, "desired_disabled_immediately_denies_captured_route");
            before = State();
            var legacyRequest = Request(Edit("synthetic-missing", name: FieldEdit<string>.Set("synthetic-legacy-rejected")));
            var legacy = await StorageConfiguration.ConfigurationStorageBridge.SaveLegacyAsync(store, caller, legacyRequest.Key,
                legacyRequest.Changes with { Behavior = new BehaviorChanges { AiDesire = FieldEdit<int>.Set(55) } }, legacyRequest.Secrets);
            Check(legacy.Code == ConfigurationResultCode.ValidationFailed && State() == before
                && new SettingsStore().Load().AiDesire == 10, "legacy_registered_intent_not_silently_ignored");
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "DELETE FROM model_providers WHERE id='primary'"));
            before = State();
            var missingMarker = Request(Edit("primary"));
            var missingLegacy = await StorageConfiguration.ConfigurationStorageBridge.SaveLegacyAsync(store, caller, missingMarker.Key,
                missingMarker.Changes, missingMarker.Secrets);
            Check(missingLegacy.Code == ConfigurationResultCode.ValidationFailed && State() == before,
                "legacy_primary_marker_requires_registration");
            var safeRow = providers.Find("synthetic-live")!;
            SeedSyntheticProviderMetadata(new(safeRow.Id, safeRow.Priority, safeRow.Name,
                "https://synthetic:credential@example.com/live", safeRow.ModelName, safeRow.SecretKeyRef, true));
            var unsafeRead = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Check(unsafeRead.Code == ConfigurationResultCode.LegacyNeedsReview && unsafeRead.View is null
                && !JsonSerializer.Serialize(unsafeRead).Contains("synthetic:credential", StringComparison.Ordinal), "legacy_userinfo_never_exported_as_nonsecret");
            var unsafeProvider = providers.Find("synthetic-live")!;
            Check(Route(unsafeProvider) is null, "matching_legacy_userinfo_route_denied");
            var startup = host.GetMethod("TryBuildProviderRoute", BindingFlags.NonPublic | BindingFlags.Static)!;
            var startupSecrets = new ProviderRefSecrets { Value = "synthetic-startup-key" };
            ModelProviderRoute? Startup(PersistedModelProvider row) =>
                (ModelProviderRoute?)startup.Invoke(null, new object[] { row, initial, startupSecrets });
            Check(Startup(unsafeProvider) is null, "startup_userinfo_route_denied");
            Check(Startup(unsafeProvider with { BaseUrl = "ftp://example.com/live" }) is null, "startup_non_http_scheme_denied");
            Console.WriteLine($"CFG_CHILD registered-providers passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD registered-providers passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-providers-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_provider_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<int?> PrimaryFactsChildAsync()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-primary-facts-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) { Console.WriteLine("FAIL CFG_PRIMARY_FACTS " + label); throw new InvalidOperationException(label); }
            passed++;
        }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var initial = new AppSettings { AiDesire = 10, ApiKey = "synthetic-main-environment-key",
                Model = "synthetic-model", ModelBaseUrl = "https://example.com/registered" };
            var settings = new SettingsStore(); settings.Save(initial);
            var runtime = new SettingsBox(initial);
            var providers = new ModelProviderStore();
            providers.UpsertMetadata(new("primary", 0, "synthetic-primary", initial.ModelBaseUrl, initial.Model, "secret:apiKey", true));
            ImmutableArray<StorageConfiguration.SecretMutation> observed = default;
            Func<SqliteConnection, ImmutableArray<StorageConfiguration.SecretMutation>, ImmutableArray<SecretMetadata>> sources = (conn, mutations) =>
            {
                observed = mutations;
                return Enum.GetValues<WritableSecretSlot>().Select(slot =>
                {
                    var kind = mutations.FirstOrDefault(mutation => mutation.Slot == slot).Kind;
                    if (slot == WritableSecretSlot.ModelApiKey)
                    {
                        if (kind == SecretEditKind.Replace) return new SecretMetadata(slot, true, CredentialSource.Database);
                        var key = kind == SecretEditKind.Delete
                            ? (Environment.GetEnvironmentVariable("QQCHAT_API_KEY") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "").Trim()
                            : runtime.Current.ApiKey;
                        var configured = !string.IsNullOrWhiteSpace(key);
                        return new SecretMetadata(slot, configured, configured ? CredentialSource.Environment : CredentialSource.None);
                    }
                    var database = StorageSecrets.LoadInTransaction(conn, slot);
                    var hasValue = kind == SecretEditKind.Replace || kind == SecretEditKind.Keep && !string.IsNullOrWhiteSpace(database);
                    return new SecretMetadata(slot, hasValue, hasValue ? CredentialSource.Database : CredentialSource.None);
                }).ToImmutableArray();
            };
            Func<ConfigurationCaller, ConfigurationAccess, bool> authorization = (_, _) => true;
            Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) =>
                new(ImmutableArray<PluginConfigurationView>.Empty, ImmutableArray<ProviderConfigurationView>.Empty);
            Func<ConfigurationFieldId, ConfigurationValueSource> provenance = _ => ConfigurationValueSource.UserConfiguration;
            Func<ConfigurationFieldId, FieldEffect> effect = _ => FieldEffect.Restart;
            var type = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)!;
            var adapter = (StorageConfiguration.ConfigurationStorageParticipant)type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single().Invoke(new object[] { runtime, authorization, sources, registered, provenance, effect, true });
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, adapter);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-primary-facts-owner", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            long Version() => AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            long Audits() => AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log");
            string? DatabaseKey() => new StorageSecrets().LoadApiKey();
            void SetDatabaseKey(string? value) => AppDatabase.Write(conn =>
                StorageSecrets.SaveInTransaction(conn, WritableSecretSlot.ModelApiKey, value, clock.WallSeconds));
            var desire = 10;
            ValueTask<ConfigurationSaveResult> Save(SecretEdit? edit = null) => store.SaveAsync(caller,
                new("cfg-0.3.2", new(Version()), new(header.Generation, clock.WallSeconds, Guid.NewGuid()),
                    new(new BehaviorChanges { AiDesire = FieldEdit<int>.Set(++desire) }, null, null, null, null, null, null, null),
                    edit is null ? ImmutableArray<SecretEdit>.Empty : ImmutableArray.Create(edit)));
            (long Version, long Audits, string? Key, PersistedModelProvider? Provider, AppSettings Runtime) State()
                => (Version(), Audits(), DatabaseKey(), providers.Find("primary"), runtime.Current);
            bool Unchanged((long Version, long Audits, string? Key, PersistedModelProvider? Provider, AppSettings Runtime) before)
                => Version() == before.Version && Audits() == before.Audits && DatabaseKey() == before.Key
                    && providers.Find("primary") == before.Provider && ReferenceEquals(runtime.Current, before.Runtime);
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", "synthetic-qqchat-key");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "synthetic-openai-key");
            var before = State();
            Check((await Save()).Code == ConfigurationResultCode.ValidationFailed, "missing_database_not_main_environment"); // 1
            Check(Unchanged(before), "missing_database_rejection_atomic"); // 2
            SetDatabaseKey("synthetic-database-key"); before = State();
            Check((await Save()).Receipt is { Decision: ConfigurationDecision.Changed, CommitState: CommitState.Committed }
                && Version() == before.Version + 1 && DatabaseKey() == "synthetic-database-key", "database_keep_legal"); // 3
            Check(ReferenceEquals(runtime.Current, before.Runtime), "database_keep_not_published"); // 4
            SetDatabaseKey("https://example.com/synthetic-key"); before = State();
            Check((await Save()).Code == ConfigurationResultCode.ValidationFailed, "url_database_keep_unusable"); // 5
            Check(Unchanged(before), "url_database_keep_atomic"); // 6
            SetDatabaseKey("synthetic-database-key"); before = State();
            using (var input = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("  HTTPS://example.com/synthetic-key  ")))
                Check((await Save(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, input))).Code == ConfigurationResultCode.ValidationFailed,
                    "url_owned_replace_unusable"); // 7
            Check(Unchanged(before), "url_owned_replace_atomic"); // 8
            before = State();
            using (var input = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("  synthetic-replaced-key  ")))
                Check((await Save(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, input))).Code == ConfigurationResultCode.Ok
                    && DatabaseKey() == "synthetic-replaced-key", "owned_replace_legal"); // 9
            Check(Version() == before.Version + 1 && providers.Find("primary")?.SecretKeyRef == "secret:apiKey"
                && ReferenceEquals(runtime.Current, before.Runtime), "owned_replace_final_ref_not_published"); // 10
            Check((await Save(SecretEdit.Delete(WritableSecretSlot.ModelApiKey))).Code == ConfigurationResultCode.Ok,
                "delete_rebind_environment_legal"); // 11
            Check(DatabaseKey() is null && providers.Find("primary")?.SecretKeyRef == "env:QQCHAT_API_KEY"
                && ReferenceEquals(runtime.Current, before.Runtime), "delete_final_environment_not_published"); // 12
            runtime.Apply(next => next.ApiKey = "");
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", " ");
            Check((await Save()).Code == ConfigurationResultCode.Ok, "environment_reference_skips_blank_main_empty"); // 13
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", "HTTPS://example.com/synthetic-key"); before = State();
            Check((await Save()).Code == ConfigurationResultCode.ValidationFailed, "raw_environment_url_unusable"); // 14
            Check(Unchanged(before), "raw_environment_rejection_atomic"); // 15
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", " "); Environment.SetEnvironmentVariable("OPENAI_API_KEY", " ");
            runtime.Apply(next => next.ApiKey = "synthetic-current-fallback-key");
            Check((await Save()).Code == ConfigurationResultCode.Ok, "environment_reference_current_fallback"); // 16
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "synthetic-openai-key"); before = State();
            Check((await Save(SecretEdit.Delete(WritableSecretSlot.ModelApiKey))).Code == ConfigurationResultCode.ValidationFailed,
                "delete_main_blank_shadow_final_database_missing"); // 17
            Check(Unchanged(before), "delete_blank_shadow_atomic"); // 18
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", null);
            Check((await Save(SecretEdit.Delete(WritableSecretSlot.ModelApiKey))).Code == ConfigurationResultCode.Ok
                && providers.Find("primary")?.SecretKeyRef == "env:QQCHAT_API_KEY", "delete_main_absent_environment_legal"); // 19
            bool Fact(StorageConfiguration.SecretMutation mutation, string name) =>
                (bool)typeof(StorageConfiguration.SecretMutation).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mutation)!;
            var model = observed.Single(mutation => mutation.Slot == WritableSecretSlot.ModelApiKey);
            Check(model.Kind == SecretEditKind.Delete && Fact(model, "FactsKnown") && !Fact(model, "DatabaseConfigured")
                && !Fact(model, "DatabaseKeyUsable") && observed.Where(mutation => mutation.Slot != WritableSecretSlot.ModelApiKey)
                    .All(mutation => !Fact(mutation, "FactsKnown")), "model_only_no_value_facts"); // 20
            Console.WriteLine($"CFG_CHILD primary-facts passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD primary-facts passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-primary-facts-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static int ProviderRefsChild()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            passed++;
        }
        try
        {
            var type = typeof(SettingsStore).Assembly.GetType("BotAgent.Host.CompositionRoot", throwOnError: true)!;
            var resolve = type.GetMethod("ResolveProviderSecret", BindingFlags.NonPublic | BindingFlags.Static)!;
            var build = type.GetMethod("TryBuildProviderRoute", BindingFlags.NonPublic | BindingFlags.Static)!;
            var settings = new AppSettings { ApiKey = "synthetic-current-key", Model = "synthetic-current-model",
                ModelBaseUrl = "https://example.com/current" };
            var secrets = new ProviderRefSecrets();
            string? Resolve(string reference) => (string?)resolve.Invoke(null, new object[] { reference, settings, secrets });
            PersistedModelProvider Provider(string reference, string id = "primary") => new(id, 0, "synthetic-provider",
                "https://example.com/registered", "synthetic-registered-model", reference, true,
                BotAgent.Services.Resilience.ProviderCircuitState.Closed, 0, null,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            ModelProviderRoute? Build(PersistedModelProvider provider) =>
                (ModelProviderRoute?)build.Invoke(null, new object[] { provider, settings, secrets });
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", "synthetic-qqchat-key");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "synthetic-openai-key");
            Environment.SetEnvironmentVariable("BOTAGENT_API_KEY", "synthetic-botagent-key");
            Environment.SetEnvironmentVariable("QQCHAT_OFFICIAL_APP_SECRET", "synthetic-official-key");
            foreach (var reference in new[] { "secret:agentServerKey", "secret:officialAppSecret", "secret: apiKey",
                "SECRET:apiKey", "secret:apikey", "secret:", "env:QQCHAT_OFFICIAL_APP_SECRET", "env:BOTAGENT_API_KEY",
                "env:OPENAI_API_KEY", "ENV:QQCHAT_API_KEY", "env:QQCHAT_API_KEY ", "synthetic-unknown" })
            {
                var before = secrets.LoadNames.Count;
                Check(Resolve(reference) is null && Build(Provider(reference)) is null
                    && secrets.LoadNames.Count == before, "synthetic_unclassified_ref_read"); // 12
            }
            var loadCount = secrets.LoadNames.Count;
            Check(Resolve("secret:apiKey") == "synthetic-database-key" && secrets.LoadNames.Count == loadCount + 1
                && secrets.LoadNames[^1] == "apiKey", "synthetic_known_database_ref"); // 13
            secrets.Value = " ";
            Check(Resolve("secret:apiKey") == " " && Build(Provider("secret:apiKey")) is null,
                "synthetic_blank_database_no_environment_fallback"); // 14
            secrets.Value = null;
            Check(Resolve("secret:apiKey") is null && Build(Provider("secret:apiKey")) is null,
                "synthetic_missing_database_no_environment_fallback"); // 15
            loadCount = secrets.LoadNames.Count;
            Check(Resolve("env:QQCHAT_API_KEY") == "synthetic-qqchat-key" && secrets.LoadNames.Count == loadCount,
                "synthetic_fixed_environment_priority"); // 16
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", " ");
            Check(Resolve("env:QQCHAT_API_KEY") == "synthetic-openai-key", "synthetic_skip_blank_environment"); // 17
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", " ");
            Check(Resolve("env:QQCHAT_API_KEY") == "synthetic-current-key", "synthetic_current_snapshot_fallback"); // 18
            settings.ApiKey = "synthetic-next-key";
            Check(Resolve("env:QQCHAT_API_KEY") == "synthetic-next-key", "synthetic_not_startup_snapshot"); // 19
            settings.ApiKey = "";
            Check(Resolve("env:QQCHAT_API_KEY") == "" && Build(Provider("env:QQCHAT_API_KEY")) is null,
                "synthetic_empty_current_no_route"); // 20
            secrets.Value = "synthetic-database-key";
            var primary = Build(Provider("secret:apiKey"));
            Check(primary is { ProviderId: "primary", BaseUrl: "https://example.com/current", ModelName: "synthetic-current-model" }
                && primary.ApiKey == secrets.Value, "synthetic_primary_current_metadata"); // 21
            var secondary = Build(Provider("secret:apiKey", "synthetic-secondary"));
            Check(secondary is { ProviderId: "synthetic-secondary", BaseUrl: "https://example.com/registered", ModelName: "synthetic-registered-model" }
                && secondary.ApiKey == secrets.Value, "synthetic_secondary_registered_metadata"); // 22
            foreach (var unusable in new[] { "http://example.com/synthetic", "HTTPS://example.com/synthetic" })
            {
                secrets.Value = unusable;
                Check(Build(Provider("secret:apiKey")) is null, "synthetic_url_shaped_key_not_route_ready"); // 23-24
            }
            secrets.Value = "";
            Check(Build(Provider("secret:apiKey")) is null, "synthetic_empty_key_not_route_ready"); // 25
            secrets.Value = "synthetic-database-key";
            Check(Build(Provider("secret:apiKey") with { BaseUrl = " " }) is null, "synthetic_missing_provider_endpoint"); // 26
            Check(Build(Provider("secret:apiKey") with { ModelName = " " }) is null, "synthetic_missing_provider_model"); // 27
            settings.ModelBaseUrl = " ";
            Check(Build(Provider("secret:apiKey")) is null, "synthetic_missing_current_endpoint"); // 28
            settings.ModelBaseUrl = "https://example.com/current"; settings.Model = " ";
            Check(Build(Provider("secret:apiKey")) is null, "synthetic_missing_current_model"); // 29
            settings.Model = "synthetic-current-model";
            settings.ModelBaseUrl = "https://example.com/current";
            secrets.Value = "synthetic-uri-validation-key";
            Check(Build(Provider("secret:apiKey", "synthetic-secondary") with
                { BaseUrl = "https://synthetic:credential@example.com/registered" }) is null, "synthetic_startup_userinfo_denied"); // 30
            Check(Build(Provider("secret:apiKey", "synthetic-secondary") with
                { BaseUrl = "ftp://example.com/registered" }) is null, "synthetic_startup_scheme_denied"); // 31
            Check(Build(Provider("secret:apiKey", "synthetic-secondary") with
                { BaseUrl = "synthetic-not-absolute" }) is null, "synthetic_startup_absolute_uri_required"); // 32
            Check(Build(Provider("secret:apiKey", "synthetic-secondary") with
                { BaseUrl = "https://example.com/" + new string('x', 2048) }) is null, "synthetic_startup_endpoint_limit"); // 33
            settings.ModelBaseUrl = "https://synthetic:credential@example.com/current";
            Check(Build(Provider("secret:apiKey")) is null, "synthetic_primary_current_userinfo_denied"); // 34
            Console.WriteLine($"CFG_CHILD provider-refs passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD provider-refs passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            foreach (var name in new[] { "QQCHAT_API_KEY", "OPENAI_API_KEY", "BOTAGENT_API_KEY", "QQCHAT_OFFICIAL_APP_SECRET" })
                Environment.SetEnvironmentVariable(name, null);
        }
    }

    private sealed class ProviderRefSecrets : ISecretsRepository
    {
        internal string? Value { get; set; } = "synthetic-database-key";
        internal List<string> LoadNames { get; } = new();
        public string? Load(string name) { LoadNames.Add(name); return Value; }
        public bool Save(string name, string? value) => throw new InvalidOperationException("synthetic_unexpected_secret_write");
        public string? LoadApiKey() => throw new InvalidOperationException("synthetic_unexpected_specialized_secret_read");
        public string? LoadAgentServerKey() => throw new InvalidOperationException("synthetic_unexpected_specialized_secret_read");
        public string? LoadNeteaseCookie() => throw new InvalidOperationException("synthetic_unexpected_specialized_secret_read");
        public string? LoadTtsKey() => throw new InvalidOperationException("synthetic_unexpected_specialized_secret_read");
        public string? LoadOfficialSecret() => throw new InvalidOperationException("synthetic_unexpected_specialized_secret_read");
        public string? LoadFeishuSecret() => throw new InvalidOperationException("synthetic_unexpected_specialized_secret_read");
        public string? LoadFeishuEncryptKey() => throw new InvalidOperationException("synthetic_unexpected_specialized_secret_read");
        public bool SaveApiKey(string? value) => Save("apiKey", value);
        public bool SaveAgentServerKey(string? value) => Save("agentServerKey", value);
        public bool SaveNeteaseCookie(string? value) => Save("neteaseCookie", value);
        public bool SaveTtsKey(string? value) => Save("ttsKey", value);
        public bool SaveOfficialSecret(string? value) => Save("officialAppSecret", value);
        public bool SaveFeishuSecret(string? value) => Save("feishuSecret", value);
        public bool SaveFeishuEncryptKey(string? value) => Save("feishuEncryptKey", value);
    }

    private static async Task<int?> ProviderRoutingChildAsync()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-routing-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var passed = 0;
        var warnings = new List<string>();
        var previousError = Console.Error;
        using var consoleError = new StringWriter();
        Console.SetError(consoleError);
        try
        {
            ClockBindings.InitializePlatforms();
            var clock = new SyntheticClock(); Clock.Use(clock);
            ModelModule::BotAgent.Model.ModelClock.Initialize(ClockBindings.Source);
            ModelModule::BotAgent.Services.ModelLogging.WriteHandler = (_, _) => { };
            ModelModule::BotAgent.Services.ModelLogging.WarnHandler = (tag, message) => warnings.Add($"[{tag}] {message}");
            foreach (var module in new[] { false, true })
            foreach (var path in Enumerable.Range(0, 3))
            foreach (var mode in Enumerable.Range(0, 4))
            {
                var http = new RoutingFetcher(); var transport = new RoutingTransport(); var images = new RoutingImages();
                var ids = mode == 3 ? new[] { "synthetic-primary", "synthetic-secondary" } : new[] { "synthetic-primary" };
                var cached = new Dictionary<string, ModelProviderRoute>(StringComparer.Ordinal)
                {
                    ["synthetic-primary"] = new("synthetic-primary", "https://example.com/cache", "synthetic-model", "synthetic-cached-key")
                };
                var resolved = new List<string>();
                Func<string, ModelProviderRoute?>? resolver = mode == 1 ? null : id =>
                {
                    resolved.Add(id);
                    return mode switch
                    {
                        2 => new(id, "https://example.com/live", "synthetic-model", "synthetic-live-key"),
                        3 when id == "synthetic-secondary" => new(id, "https://example.com/live-other", "synthetic-model", "synthetic-other-key"),
                        _ => null
                    };
                };
                IModelClient client;
                if (module)
                {
                    var options = new ModelModule::BotAgent.Model.ModelOptions { Model = "synthetic-model", BaseUrl = "https://example.com/runtime",
                        ApiKey = "synthetic-runtime-key" };
                    var runner = new ModelModule::BotAgent.Services.Resilience.ProviderFailoverRunner(ids.Select((id, priority) =>
                        new ModelModule::BotAgent.Services.Resilience.ProviderCandidate(id, priority)), () => clock.UtcNow);
                    client = new ModelModule::BotAgent.Model.OpenAiClient(() => options, http, http, images, transport, runner, cached, resolver);
                }
                else
                {
                    var settings = new AppSettings { Model = "synthetic-model", ModelBaseUrl = "https://example.com/runtime", ApiKey = "synthetic-runtime-key" };
                    var runner = new BotAgent.Services.Resilience.ProviderFailoverRunner(ids.Select((id, priority) =>
                        new BotAgent.Services.Resilience.ProviderCandidate(id, priority)), () => clock.UtcNow);
                    client = new BotAgent.Services.Agent.OpenAiClient(new SettingsBox(settings), http, http, images, transport, runner, cached, resolver);
                }
                var available = false;
                try
                {
                    if (path == 0) available = (await client.CompleteAsync(new[] { new ChatMessage { Role = MessageRole.Peer, Text = "synthetic request" } })).Reply is not null;
                    else if (path == 1) available = await client.CompleteChatAsync("synthetic-model", "synthetic-system",
                        new[] { ("user", "synthetic request") }, 64, 0) is not null;
                    else available = await client.SummarizeSessionTitleAsync("synthetic digest", null) is not null;
                }
                catch (HttpRequestException) when (mode == 0 && path == 0) { }
                var paths = http.Paths.Concat(transport.Paths).ToArray();
                var expected = mode == 1 ? "/cache" : mode == 2 ? "/live" : "/live-other";
                var good = mode == 0 ? !available && paths.Length == 0 : available && paths.Length == 1
                    && paths.All(value => value == expected || value.StartsWith(expected + "/", StringComparison.Ordinal));
                good = good && resolved.SequenceEqual(mode == 1 ? Array.Empty<string>() : ids)
                    && transport.Paths.Count == (mode != 0 && path == 0 ? 1 : 0)
                    && http.Paths.Count == (mode != 0 && path != 0 ? 1 : 0);
                if (!good) throw new InvalidOperationException("synthetic_provider_route_assertion");
                passed++;
                Console.WriteLine($"PASS CFG_ROUTE {(module ? "model" : "host")} path={path} mode={mode} resolves={resolved.Count} sends={paths.Length}");
            }
            if (!warnings.Any(line => line.Contains("Provider 调用不可用，进入安全静默（reason=provider_secret_unavailable，hops=1）", StringComparison.Ordinal))
                || !warnings.Any(line => line.Contains("[会话标题] 没拿到标题，本次不改名", StringComparison.Ordinal)))
                throw new InvalidOperationException("synthetic_missing_provider_diagnostic");
            var consoleOutput = consoleError.ToString();
            var forbiddenDiagnosticSecrets = new[]
            {
                "synthetic-private-marker", "synthetic-cached-key", "synthetic-live-key",
                "synthetic-other-key", "synthetic-runtime-key", "https://example.com"
            };
            if (warnings.Any(line => forbiddenDiagnosticSecrets.Any(secret => line.Contains(secret, StringComparison.Ordinal)))
                || forbiddenDiagnosticSecrets.Any(secret => consoleOutput.Contains(secret, StringComparison.Ordinal)))
                throw new InvalidOperationException("synthetic_provider_diagnostic_secret_leak");
            passed++;
            Console.WriteLine($"CFG_CHILD provider-routing passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD provider-routing passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            Console.SetError(previousError);
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-routing-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private sealed class RoutingFetcher : BotAgent.Adapters.Net.IHttpFetcher, ModelModule::BotAgent.Model.Net.IModelHttpFetcher
    {
        internal List<string> Paths { get; } = new();
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"title\\\":\\\"synthetic\\\"}\"}}]}" ) });
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new InvalidOperationException("synthetic_unexpected_get");
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new InvalidOperationException("synthetic_unexpected_get");
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new InvalidOperationException("synthetic_unexpected_get");
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new InvalidOperationException("synthetic_unexpected_get");
        public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        { using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content }; return await SendAsync(request, ct); }
    }

    private sealed class RoutingTransport : IModelTransport
    {
        internal List<string> Paths { get; } = new();
        public Task<BuiltRequest> BuildAsync(IReadOnlyList<ChatMessage> window, string systemContent, IReadOnlyCollection<long> quotableIds,
            CancellationToken ct, BotAgent.Domain.Reply.SamplingProfile? sampling = null)
            => Task.FromResult(new BuiltRequest(new JsonObject { ["model"] = "synthetic-model" }, 0, new List<long>()));
        public Task<SendOutcome> SendAsync(JsonObject payload, int attachedImages, IReadOnlyList<long> attachedImageIds, CancellationToken ct)
            => throw new InvalidOperationException("synthetic_unexpected_unrouted_send");
        public Task<SendOutcome> SendAsync(JsonObject payload, int attachedImages, IReadOnlyList<long> attachedImageIds, ModelProviderRoute provider, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Paths.Add(new Uri(provider.BaseUrl).AbsolutePath);
            return Task.FromResult(new SendOutcome("{\"choices\":[{\"message\":{\"content\":\"{\\\"action\\\":\\\"reply\\\",\\\"suitability\\\":99,\\\"reply\\\":\\\"synthetic\\\"}\"}}]}", false));
        }
    }

    private sealed class RoutingImages : IImageDownloader
    {
        public Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshUrls { get; set; }
        public int CacheHits => 0;
        public int RefreshedCount => 0;
        public Task<(byte[] Data, string Mime, string Ext)?> DownloadBytesAsync(string url, CancellationToken ct, long? messageId = null)
            => throw new InvalidOperationException("synthetic_unexpected_image_request");
    }

    private static async Task<int?> AliasChildAsync(bool oldProtocol)
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name =>
            name.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray())
            Environment.SetEnvironmentVariable(name, null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-configuration-aliases-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("BOTAGENT_MODEL", "synthetic-env-model");
        Environment.SetEnvironmentVariable("BOTAGENT_BASE_URL", "https://example.com/fallback");
        var passed = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("synthetic_alias_assertion"); passed++; }
        try
        {
            ClockBindings.InitializePlatforms(); ClockBindings.InitializeStorage();
            var clock = new SyntheticClock(); Clock.Use(clock); AppDatabase.Initialize();
            var initial = new AppSettings { AiDesire = 10, Model = "synthetic-original", ModelOverride = "synthetic-original",
                ModelBaseUrl = "https://example.com/primary", ModelBaseUrlOverride = "https://example.com/primary" };
            var settings = new SettingsStore(); settings.Save(initial);
            var runtime = new SettingsBox(initial);
            var synthetic = new SyntheticParticipant();
            var providers = new ModelProviderStore();
            providers.UpsertMetadata(new("primary", 0, "synthetic-primary", "https://example.com/primary", "synthetic-original", "secret:apiKey", false));
            providers.UpsertMetadata(new("synthetic-fallback", 10, "synthetic-fallback", "https://example.com/fallback", "synthetic-env-model", "secret:apiKey", false));
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source,
                oldProtocol ? synthetic : CreateActualTokenAdapter(runtime));
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-alias-owner", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            long Version() => AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            long Audits() => AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log");
            ProviderEdit Alias(FieldEdit<string> model = default, FieldEdit<string> url = default) => new(new("primary"),
                new(default, default, url, model, default, CredentialBindingEdit.KeepExisting()));
            ConfigurationSaveRequest Request(ModelChanges? model = null, ProviderEdit? provider = null) => new("cfg-0.3.2", new(Version()),
                new(header.Generation, clock.WallSeconds, Guid.NewGuid()), new(null, model, null, null, null, null, null,
                    provider is null ? null : new(ImmutableArray<PluginEnabledEdit>.Empty, ImmutableArray.Create(provider), ImmutableArray<PolicyEdit>.Empty)),
                ImmutableArray<SecretEdit>.Empty);
            void SetCodec(string codec) => AppDatabase.Write(conn => AppDatabase.Exec(conn,
                "UPDATE configuration_generations SET codec_revision=$codec WHERE generation=$gen",
                ("$codec", codec), ("$gen", header.Generation.Value.ToString("N"))));
            if (oldProtocol)
            {
                // A synthetic retained-protocol fixture, not a claim that an old binary was rebuilt or restored.
                SetCodec("cfg-0.3.2");
                var raw = Request(new ModelChanges { Model = FieldEdit<string>.Set("synthetic-old-direct") },
                    Alias(FieldEdit<string>.Set("synthetic-old-provider")));
                var saved = await store.SaveAsync(caller, raw);
                Check(saved.Code == ConfigurationResultCode.Ok); // 1
                Check(StrictFingerprintMatches(caller, raw)); // 2: independent pre-alias byte reconstruction
                Check((await store.SaveAsync(caller, raw)).Replayed); // 3
                var writes = synthetic.Writes; var version = Version(); var audits = Audits();
                SetCodec("synthetic-future-codec");
                var unknown = await store.SaveAsync(caller, raw);
                Check(unknown.Code == ConfigurationResultCode.StorageUnavailable && !unknown.Replayed && unknown.Receipt is null); // 4
                Check((await store.FindAsync(caller, raw.Key)).Code == ConfigurationResultCode.StorageUnavailable); // 5
                Check(!store.TryStartApplyStep(caller, raw.Key, ApplyStep.ContextWindow).CanRun
                    && store.RecordApply(caller, raw.Key, ImmutableArray.Create(new ApplyStepReport(ApplyStep.ContextWindow, ApplyState.Applied, null))).Code
                        == ConfigurationResultCode.StorageUnavailable); // 6
                Check((await store.SaveAsync(caller, raw with { Key = raw.Key with { Nonce = Guid.NewGuid() } })).Code
                    == ConfigurationResultCode.StorageUnavailable && synthetic.Writes == writes && Version() == version && Audits() == audits); // 7
                SetCodec("cfg-0.3.2");
                Check((await store.SaveAsync(caller, raw)).Replayed && StrictFingerprintMatches(caller, raw)); // 8
                var legacy = Request(raw.Changes.Model, raw.Changes.Registered!.Providers.Single());
                var legacySaved = await store.SaveLegacyAsync(caller, legacy.Key, legacy.Changes, legacy.Secrets);
                Check(legacySaved.Code == ConfigurationResultCode.NoChanges
                    && (await store.SaveLegacyAsync(caller, legacy.Key, legacy.Changes, legacy.Secrets)).Replayed
                    && Version() == version && synthetic.Writes == writes); // 9
                Console.WriteLine($"CFG_CHILD alias-codecs passed={passed} failed=0");
                return 0;
            }
            Check(AppDatabase.Scalar<string>("SELECT codec_revision FROM configuration_generations WHERE generation=$gen",
                ("$gen", header.Generation.Value.ToString("N"))) == "cfg-0.3.2/alias-1"); // 1
            var direct = Request(new ModelChanges { Model = FieldEdit<string>.Set("synthetic-new-model") });
            Check((await store.SaveAsync(caller, direct)).Code == ConfigurationResultCode.Ok); // 2
            var alias = direct with { Changes = Request(provider: Alias(FieldEdit<string>.Set(" synthetic-new-model "))).Changes };
            Check((await store.SaveAsync(caller, alias)).Replayed); // 3
            var both = direct with { Changes = Request(direct.Changes.Model, Alias(FieldEdit<string>.Set("synthetic-new-model"))).Changes };
            Check((await store.SaveAsync(caller, both)).Replayed); // 4
            Check((await store.SaveAsync(caller, Request(provider: Alias(FieldEdit<string>.Set("synthetic-alias-model"))))).Code == ConfigurationResultCode.Ok
                && settings.Load().ModelOverride == "synthetic-alias-model" && providers.Find("primary")!.ModelName == "synthetic-alias-model"
                && AppDatabase.Scalar<long>("SELECT user_mode FROM configuration_field_modes WHERE field_id=75") == (int)UserValueKind.Explicit); // 5
            Check(AppDatabase.Scalar<string>("SELECT action_detail FROM security_audit_log WHERE event_type='configuration_decision' ORDER BY id DESC LIMIT 1")!
                .StartsWith("fields=75;", StringComparison.Ordinal)); // 6
            var before = Version(); var auditBefore = Audits();
            var conflict = await store.SaveAsync(caller, Request(new ModelChanges { Model = FieldEdit<string>.Set("synthetic-left") },
                Alias(FieldEdit<string>.Set("synthetic-right"))));
            Check(conflict.Code == ConfigurationResultCode.ValidationFailed && conflict.Errors.Any(error => error.Code == "alias_conflict")
                && Version() == before && Audits() == auditBefore && settings.Load().ModelOverride == "synthetic-alias-model"); // 7
            Check((await store.SaveAsync(caller, Request(new ModelChanges { Model = FieldEdit<string>.Clear() },
                Alias(FieldEdit<string>.Set("synthetic-env-model"))))).Code == ConfigurationResultCode.ValidationFailed && Version() == before); // 8
            Check((await store.SaveAsync(caller, Request(provider: Alias(FieldEdit<string>.Clear())))).Code == ConfigurationResultCode.Ok
                && settings.Load().ModelOverride is null && providers.Find("primary")!.ModelName == "synthetic-env-model"
                && AppDatabase.Scalar<long>("SELECT user_mode FROM configuration_field_modes WHERE field_id=75") == (int)UserValueKind.Inherited); // 9
            var url = Request(provider: Alias(url: FieldEdit<string>.Set(" https://example.com/fallback ")));
            Check((await store.SaveAsync(caller, url)).Code == ConfigurationResultCode.Ok
                && settings.Load().ModelBaseUrlOverride == "https://example.com/fallback" && providers.Find("primary")!.BaseUrl == "https://example.com/fallback"); // 10
            Check((await store.SaveAsync(caller, url with { Changes = Request(new ModelChanges { ModelBaseUrl = FieldEdit<string>.Set("https://example.com/fallback") }).Changes })).Replayed); // 11
            before = Version(); auditBefore = Audits();
            Check((await store.SaveAsync(caller, Request(new ModelChanges { ModelBaseUrl = FieldEdit<string>.Set("https://example.com/fallback") },
                Alias(url: FieldEdit<string>.Set("https://example.com/primary"))))).Code == ConfigurationResultCode.ValidationFailed && Version() == before); // 12
            Check((await store.SaveAsync(caller, Request(provider: Alias(url: FieldEdit<string>.Set("https://example.com/unregistered"))))).Code
                == ConfigurationResultCode.ValidationFailed && Version() == before); // 13
            Check((await store.SaveAsync(caller, Request(provider: Alias(FieldEdit<string>.Set(new string('x', 1025)))))).Code
                == ConfigurationResultCode.ValidationFailed && Version() == before); // 14
            var priority = Alias() with { Changes = Alias().Changes with { Priority = FieldEdit<int>.Set(1) } };
            Check((await store.SaveAsync(caller, Request(provider: priority))).Code == ConfigurationResultCode.ValidationFailed
                && providers.Find("primary")!.Priority == 0 && Version() == before && Audits() == auditBefore); // 15
            AppDatabase.Write(conn => AppDatabase.Exec(conn, "DELETE FROM model_providers WHERE id='primary'"));
            var missing = await store.SaveAsync(caller, Request(provider: Alias(FieldEdit<string>.Set("synthetic-missing-registration"))));
            Check(missing.Code == ConfigurationResultCode.ValidationFailed && missing.Errors.Any(error => error.Code == "registration_required")
                && Version() == before && Audits() == auditBefore); // 16
            Check(runtime.Current.Model == "synthetic-original" && new SecretsStore().LoadApiKey() is null); // 17: persistence is not publication
            Console.WriteLine($"CFG_CHILD primary-aliases passed={passed} failed=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CFG_CHILD {(oldProtocol ? "alias-codecs" : "primary-aliases")} passed={passed} failed=1 type={ex.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), "^botagent-configuration-aliases-[0-9a-f]{32}$"))
                throw new InvalidOperationException("configuration_fixture_cleanup_scope");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static void RegistryIsolation(Action<string, bool> check)
    {
        var a = new RegistryEndpoint(new("onebot", "legacy", "synthetic-a"));
        var b = new RegistryEndpoint(new("qq.private", "legacy", "synthetic-b"));
        var inputs = new List<IPlatformAdapter> { a, b };
        var registry = new BotAgent.Services.Platforms.PlatformRegistry(inputs, new IPlatformMessenger[] { a, b });
        IPlatformRegistry port = registry;
        check("registry exact adapter lookup preserves both instances", ReferenceEquals(port.GetAdapter(a.Context), a)
            && ReferenceEquals(port.GetAdapter(b.Context), b));
        check("registry exact messenger lookup preserves both instances", ReferenceEquals(port.GetMessenger(a.Context), a)
            && ReferenceEquals(port.GetMessenger(b.Context), b));
        check("registry platform aliases normalize without merging instance keys",
            ReferenceEquals(port.GetAdapter(a.Context with { PlatformId = " QQ.PRIVATE " }), a));
        check("registry legacy adapter lookup rejects ambiguous account", port.GetAdapter("qq.private", "legacy") is null);
        check("registry legacy messenger lookup rejects ambiguous account", port.GetMessenger("qq.private", "legacy") is null);
        check("registry exact missing instance never falls back to another", port.GetAdapter(a.Context with { InstanceId = "synthetic-missing" }) is null
            && port.GetMessenger(a.Context with { InstanceId = "synthetic-missing" }) is null);
        check("registry empty instance is not account default", port.GetAdapter(a.Context with { InstanceId = "" }) is null
            && port.GetMessenger(a.Context with { InstanceId = "" }) is null);
        check("registry snapshots retain real instance identities", registry.GetSnapshots().Select(x => x.InstanceId)
            .OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "synthetic-a", "synthetic-b" }));
        check("registry duplicate normalized adapter identity is rejected", Throws<ArgumentException>(() =>
            new BotAgent.Services.Platforms.PlatformRegistry(new IPlatformAdapter[] { a, new RegistryEndpoint(a.Context with { PlatformId = "qq.private" }) },
                Array.Empty<IPlatformMessenger>())));
        check("registry duplicate normalized messenger identity is rejected", Throws<ArgumentException>(() =>
            new BotAgent.Services.Platforms.PlatformRegistry(Array.Empty<IPlatformAdapter>(),
                new IPlatformMessenger[] { a, new RegistryEndpoint(a.Context with { PlatformId = "qq.private" }) })));
        var upperAccount = new RegistryEndpoint(a.Context with { AccountScope = "LEGACY" });
        var upperInstance = new RegistryEndpoint(a.Context with { InstanceId = "SYNTHETIC-A" });
        var ordinal = new BotAgent.Services.Platforms.PlatformRegistry(new IPlatformAdapter[] { a, upperAccount, upperInstance },
            new IPlatformMessenger[] { a, upperAccount, upperInstance });
        check("registry account scopes preserve Ordinal identity", ReferenceEquals(ordinal.GetAdapter(upperAccount.Context), upperAccount)
            && ReferenceEquals(ordinal.GetMessenger("qq.private", "LEGACY"), upperAccount));
        check("registry instance IDs preserve Ordinal identity", ReferenceEquals(ordinal.GetAdapter(upperInstance.Context), upperInstance)
            && ReferenceEquals(ordinal.GetMessenger(upperInstance.Context), upperInstance));
        check("registry incomplete exact contexts cannot normalize into grants", port.GetAdapter(a.Context with { PlatformId = "" }) is null
            && port.GetMessenger(a.Context with { AccountScope = "" }) is null
            && port.GetAdapter(a.Context with { InstanceId = " " }) is null);
        var legacy = new RegistryEndpoint(new("qq.private", "legacy", ""));
        var single = new BotAgent.Services.Platforms.PlatformRegistry(new IPlatformAdapter[] { legacy }, new IPlatformMessenger[] { legacy });
        check("registry unique legacy empty-instance callers remain compatible", ReferenceEquals(single.GetAdapter("qq", "legacy"), legacy)
            && ReferenceEquals(single.GetMessenger("onebot", "legacy"), legacy)
            && ReferenceEquals(single.GetAdapter(legacy.Context), legacy));
        inputs.Clear();
        check("registry caller input mutation does not change registered facts", registry.Adapters.Count == 2 && registry.GetSnapshots().Count == 2
            && ReferenceEquals(registry.GetAdapter(a.Context), a));
        var mutable = registry.Adapters as ICollection<IPlatformAdapter>;
        check("registry exposed adapter collection cannot change indexed facts", mutable is null || mutable.IsReadOnly
            && Throws<NotSupportedException>(() => mutable.Clear()) && registry.Adapters.Count == 2);
        IPlatformRegistry oldMulti = new LegacyRegistry(new IPlatformAdapter[] { a, b }, a);
        check("registry default interface preserves exact adapter and denies ambiguous messenger", ReferenceEquals(oldMulti.GetAdapter(b.Context), b)
            && oldMulti.GetMessenger(a.Context) is null);
        IPlatformRegistry wrongMessenger = new LegacyRegistry(new IPlatformAdapter[] { a }, b);
        check("registry default interface rejects a legacy messenger for another instance", wrongMessenger.GetMessenger(a.Context) is null);
        IPlatformRegistry oldSingle = new LegacyRegistry(new IPlatformAdapter[] { legacy }, legacy);
        check("registry default interface keeps unique legacy exact routing", ReferenceEquals(oldSingle.GetAdapter(legacy.Context), legacy)
            && ReferenceEquals(oldSingle.GetMessenger(legacy.Context), legacy));
        check("registry queries never perform outgoing IO", a.Sends == 0 && b.Sends == 0 && legacy.Sends == 0);
    }

    private sealed class RegistryEndpoint(PlatformContext context) : IPlatformAdapter, IPlatformMessenger
    {
        public PlatformContext Context { get; } = context;
        public string DisplayName => "synthetic-platform";
        public string Tag => "synthetic";
        public bool IsConnected => true;
        public PlatformCapabilities Capabilities { get; } = new();
        public string? LastErrorCode => null;
        public int Sends { get; private set; }
        public event Action<InboundMessage>? InboundReceived { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public Task<DeliveryResult> SendAsync(PlatformContext target, OutboundMessage message, CancellationToken ct = default)
        { Sends++; throw new InvalidOperationException("synthetic_registry_unexpected_io"); }
        public Task<(string? Text, string? SenderId)> GetQuotedMessageAsync(MessageRef message, CancellationToken ct = default)
            => throw new InvalidOperationException("synthetic_registry_unexpected_io");
    }

    private sealed class LegacyRegistry(IReadOnlyList<IPlatformAdapter> adapters, IPlatformMessenger messenger) : IPlatformRegistry
    {
        public IReadOnlyList<IPlatformAdapter> Adapters => adapters;
        public IPlatformAdapter? GetAdapter(string platformId, string accountScope = AccountScope.Default) => adapters.FirstOrDefault();
        public IPlatformMessenger? GetMessenger(string platformId, string accountScope = AccountScope.Default) => messenger;
        public IReadOnlyList<PlatformStatusSnapshot> GetSnapshots() => Array.Empty<PlatformStatusSnapshot>();
    }

    public static Task SurfaceAsync(Action<bool, string> report)
    {
        void check(string name, bool condition) => report(condition, name);
        RegistryIsolation(check);
        var ids = Enum.GetValues<ConfigurationFieldId>();
        check("configuration FieldIds are exactly 1..145",
            ids.Select(x => (int)x).SequenceEqual(Enumerable.Range(1, 145)));
        var partitions = new[] { typeof(AgentChanges), typeof(BehaviorChanges), typeof(ModelChanges),
            typeof(MusicChanges), typeof(PlatformChanges), typeof(ReportingChanges), typeof(VoiceChanges) };
        check("configuration seven partitions cover all 142 direct fields",
            partitions.Sum(t => t.GetProperties().Length) == 142);
        foreach (var type in partitions)
        {
            var view = type.Assembly.GetType(type.FullName!.Replace("Changes", "View"))!;
            check("configuration typed patch/view " + type.Name,
                type.GetProperties().Select(p => p.Name).SequenceEqual(view.GetProperties().Select(p => p.Name)));
            check("configuration init-only partition " + type.Name,
                type.GetProperties().All(IsInitOnly));
        }
        check("configuration port has only Read Save Find",
            typeof(IConfigurationChanges).GetMethods().Select(x => x.Name)
                .OrderBy(x => x).SequenceEqual(new[] { "FindAsync", "ReadAsync", "SaveAsync" }));
        check("configuration ports keep frozen namespaces",
            typeof(IConfigurationChanges).Namespace == "BotAgent.Domain.Configuration"
            && typeof(IManagedCapabilities).Namespace == "BotAgent.Domain.Management");
        check("configuration has no official secret slot", Enum.GetValues<WritableSecretSlot>().Length == 6
            && !Enum.GetNames<WritableSecretSlot>().Any(x => x.Contains("Official", StringComparison.Ordinal)));
        var keep = default(FieldEdit<int>);
        check("configuration default edit is Keep without value", keep.Kind == EditKind.Keep
            && !keep.TryGetValue(out _));
        check("configuration Set zero is not Keep", FieldEdit<int>.Set(0).TryGetValue(out var zero) && zero == 0);
        check("configuration Set false is not Keep", FieldEdit<bool>.Set(false).TryGetValue(out var flag) && !flag);
        check("configuration Clear has no value", FieldEdit<string>.Clear().Kind == EditKind.Clear
            && !FieldEdit<string>.Clear().TryGetValue(out _));
        check("configuration Set null rejected", Throws<ArgumentNullException>(() => FieldEdit<string>.Set(null!)));
        check("configuration Set undefined enum rejected",
            Throws<ArgumentException>(() => FieldEdit<ToggleRule>.Set((ToggleRule)99)));
        foreach (var wire in new[] { "{\"kind\":0,\"value\":null}", "{\"kind\":2,\"value\":0}",
            "{\"kind\":1}", "{\"kind\":1,\"value\":null}", "{\"kind\":3}", "{\"kind\":\"1\",\"value\":1}",
            "{\"Kind\":1,\"value\":1}", "{\"kind\":1,\"value\":1,\"unknown\":0}",
            "{\"kind\":1,\"k\\u0069nd\":1,\"value\":1}" })
            check("configuration closed edit rejects malformed presence/type/property",
                Throws<JsonException>(() => JsonSerializer.Deserialize<FieldEdit<int>>(wire)));
        check("configuration closed edit preserves explicit zero",
            JsonSerializer.Deserialize<FieldEdit<int>>("{\"kind\":1,\"value\":0}").TryGetValue(out var wireZero) && wireZero == 0);
        check("configuration closed edit rejects unpaired Unicode scalar", Throws<JsonException>(() =>
            JsonSerializer.Deserialize<FieldEdit<string>>("{\"kind\":1,\"value\":\"\\ud800\"}")));
        check("configuration explicit empty list differs from inheritance",
            ListRule.Explicit("").TryGetExplicitValue(out var empty) && empty == ""
            && !ListRule.Inherit().TryGetExplicitValue(out _) && ListRule.AllowAll().Kind == ListRuleKind.AllowAll);
        check("configuration binding branches preserve identity",
            CredentialBindingEdit.UseRegisteredModelCredential(new("model-a")).TryGetRegisteredKey(out var key)
            && key.Value == "model-a" && !CredentialBindingEdit.KeepExisting().TryGetRegisteredKey(out _));
        check("configuration registration uses immutable collections",
            typeof(RegisteredConfigurationChanges).GetProperty("Plugins")!.PropertyType
                == typeof(ImmutableArray<PluginEnabledEdit>));
        check("configuration device/policy views do not expose raw write DTOs",
            typeof(AgentView).GetProperty("AgentDevices")!.PropertyType
                == typeof(ReadField<ImmutableArray<DeviceConfigurationView>>)
            && typeof(PlatformView).GetProperty("PlatformPolicies")!.PropertyType
                == typeof(ReadField<ImmutableArray<PlatformPolicyEntryView>>));
        var bytes = Encoding.UTF8.GetBytes("synthetic-private-input");
        using var input = SensitiveInput.FromUtf8(bytes);
        var edit = SecretEdit.Replace(WritableSecretSlot.ModelApiKey, input);
        Array.Clear(bytes);
        check("configuration sensitive factory owns a copy", input.ByteLength > 0);
        check("configuration secret diagnostic strings redacted",
            input.ToString() == "[redacted]" && edit.ToString() == "[redacted]");
        check("configuration sensitive input has no public value/callback",
            typeof(SensitiveInput).GetProperties().Select(p => p.Name).SequenceEqual(new[] { "ByteLength" })
            && !typeof(SensitiveInput).GetMethods().Any(m => m.Name is "UseUtf8" or "CopyBytes" or "OwnCopy"));
        check("configuration sensitive JSON output forbidden", Throws<JsonException>(() => JsonSerializer.Serialize(input)));
        check("configuration secret-edit JSON output forbidden", Throws<JsonException>(() => JsonSerializer.Serialize(edit)));
        check("configuration device diagnostic does not print name",
            new DeviceConfiguration(null, "synthetic-name", true, null, null, null, 0).ToString()
                == "DeviceConfiguration");
        input.Dispose(); input.Dispose();
        check("configuration disposed input cannot expose bytes",
            Throws<ObjectDisposedException>(() => _ = input.ByteLength));
        check("configuration version wire preserves Int64", JsonSerializer.Serialize(new ConfigurationVersion(long.MaxValue))
            == "\"9223372036854775807\"");
        check("configuration version wire rejects leading zero",
            Throws<JsonException>(() => JsonSerializer.Deserialize<ConfigurationVersion>("\"01\"")));
        check("configuration version wire rejects numeric token",
            Throws<JsonException>(() => JsonSerializer.Deserialize<ConfigurationVersion>("1")));
        check("configuration version negative output rejected",
            Throws<JsonException>(() => JsonSerializer.Serialize(new ConfigurationVersion(-1))));
        var identity = Guid.NewGuid();
        check("configuration device key uses nonzero lowercase N scalar wire",
            JsonSerializer.Serialize(new DeviceKey(identity)) == "\"" + identity.ToString("N") + "\"");
        check("configuration zero/uppercase GUID wire rejected",
            Throws<JsonException>(() => JsonSerializer.Deserialize<DeviceKey>("\"00000000000000000000000000000000\""))
            && Throws<JsonException>(() => JsonSerializer.Deserialize<DeviceKey>("\"ABCDEFABCDEFABCDEFABCDEFABCDEFABCD\"")));
        var operation = new ConfigurationOperationKey(new(identity), 1900000000, Guid.NewGuid());
        var operationJson = JsonSerializer.Serialize(operation);
        check("configuration cfg1 key wire round-trips exact identity",
            JsonSerializer.Deserialize<ConfigurationOperationKey>(operationJson) == operation
            && operationJson.StartsWith("\"cfg1:", StringComparison.Ordinal));
        check("configuration and management key namespaces cannot be interchanged",
            Throws<JsonException>(() => JsonSerializer.Deserialize<ManagedOperationKey>(operationJson)));
        check("configuration canonical registry key preserves case and string identity",
            JsonSerializer.Deserialize<ProviderKey>("\"Provider-A\"").Value == "Provider-A"
            && JsonSerializer.Serialize(new PluginKey("Plugin-A")) == "\"Plugin-A\"");
        var rule = new FieldDescriptor(ConfigurationFieldId.BotPersona, FieldEffect.Restart, true, true, false);
        var hidden = Project(FieldVisibility.Hidden, "[masked]", rule, "synthetic-private-name");
        var hiddenJson = JsonSerializer.Serialize(hidden);
        check("configuration hidden field has neither value nor nameRaw", !hidden.TryGetEditableValue(out _)
            && !hiddenJson.Contains("synthetic-private-name", StringComparison.Ordinal)
            && !hiddenJson.Contains("\"value\"", StringComparison.Ordinal)
            && !hiddenJson.Contains("nameRaw", StringComparison.Ordinal));
        var named = Project(FieldVisibility.NameEditable, "[masked]", rule, "synthetic-private-name");
        var namedJson = JsonSerializer.Serialize(named);
        check("configuration authorized name editing uses nameRaw only",
            namedJson.Contains("\"nameRaw\":\"synthetic-private-name\"", StringComparison.Ordinal)
            && !namedJson.Contains("\"value\"", StringComparison.Ordinal)
            && named.ToString() == "ReadField");
        CheckPublication(check);
        CheckLegacySecretParser(check);
        CheckLegacyScalarParser(check);
        return Task.CompletedTask;
    }

    private static void CheckLegacyScalarParser(Action<string, bool> check)
    {
        var parser = typeof(BotAgent.Adapters.Panel.WebUiServer).GetMethod("ParseLegacyScalarFields",
            BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(JsonObject) }, null)
            ?? throw new InvalidOperationException("actual_legacy_scalar_parser_missing");
        ConfigurationPatch Parse(JsonObject body)
        {
            try { return (ConfigurationPatch)parser.Invoke(null, new object[] { body })!; }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
        IEnumerable<(string Name, object Edit)> Edits(ConfigurationPatch patch)
            => new object[] { patch.Behavior!, patch.Model!, patch.Agent!, patch.Platform!, patch.Voice!, patch.Music!, patch.Reporting! }
                .SelectMany(group => group.GetType().GetProperties().Select(p => (p.Name, p.GetValue(group)!)));
        EditKind Kind(object edit) => (EditKind)edit.GetType().GetProperty("Kind")!.GetValue(edit)!;
        object? Value(object edit)
        {
            var args = new object?[] { null };
            return (bool)edit.GetType().GetMethod("TryGetValue")!.Invoke(edit, args)! ? args[0] : null;
        }
        var empty = Parse(new JsonObject());
        var scalar = Edits(empty).Where(x => x.Name is not ("AgentDevices" or "PlatformPolicies")).ToArray();
        check("configuration legacy mapper exactly 140 scalar fields with missing Keep",
            scalar.Length == 140 && scalar.All(x => Kind(x.Edit) == EditKind.Keep)
                && Edits(empty).All(x => Kind(x.Edit) == EditKind.Keep));
        foreach (var field in scalar)
        {
            var key = char.ToLowerInvariant(field.Name[0]) + field.Name[1..];
            var type = field.Edit.GetType().GetGenericArguments().Single();
            JsonNode node = type == typeof(bool) ? JsonValue.Create(false)!
                : type == typeof(int) ? JsonValue.Create(1)!
                : type == typeof(double) ? JsonValue.Create(0.25)!
                : JsonValue.Create(field.Name switch {
                    "HealthReportTime" => "09:30", "AgentServerQqActions" => "*",
                    "AgentServerBaseUrl" or "ModelBaseUrl" => "https://example.com/synthetic",
                    _ => " synthetic-value " })!;
            var body = new JsonObject { [key] = node };
            var before = body.ToJsonString();
            var edits = Edits(Parse(body)).ToArray();
            check("configuration legacy scalar exact sparse Set " + field.Name,
                edits.Count(x => Kind(x.Edit) != EditKind.Keep) == 1
                    && Kind(edits.Single(x => x.Name == field.Name).Edit) == EditKind.Set
                    && body.ToJsonString() == before);
            var nulled = Edits(Parse(new JsonObject { [key] = null }));
            check("configuration legacy scalar null Keep " + field.Name, nulled.All(x => Kind(x.Edit) == EditKind.Keep));
            if (type == typeof(bool))
                check("configuration legacy explicit false preserved " + field.Name,
                    Value(edits.Single(x => x.Name == field.Name).Edit) is false);
        }
        var overrides = Parse(new JsonObject { ["model"] = " \t ", ["modelBaseUrl"] = "" });
        check("configuration legacy model aliases empty means Clear not empty Set",
            overrides.Model!.Model.Kind == EditKind.Clear && overrides.Model.ModelBaseUrl.Kind == EditKind.Clear);
        check("configuration legacy invalid model URL rejects before mutation", Throws<ArgumentException>(() =>
            Parse(new JsonObject { ["modelBaseUrl"] = "file:///synthetic" })));
        var agentUrl = Parse(new JsonObject { ["agentServerBaseUrl"] = "not-a-url" });
        check("configuration legacy invalid agent URL preserves old Keep", agentUrl.Agent!.AgentServerBaseUrl.Kind == EditKind.Keep);
        foreach (var key in new[] { "thinkingBudget", "thinkingCustomBudget", "fastThinkingBudget", "fastThinkingCustomBudget" })
        {
            check("configuration legacy original empty thinking text Keep", Edits(Parse(new JsonObject { [key] = "" }))
                .All(x => Kind(x.Edit) == EditKind.Keep));
            var whitespace = Edits(Parse(new JsonObject { [key] = " \t " })).Single(x => Kind(x.Edit) != EditKind.Keep).Edit;
            check("configuration legacy whitespace thinking text explicit empty", Kind(whitespace) == EditKind.Set && Value(whitespace) is "");
        }
        var clamped = Parse(new JsonObject { ["aiDesire"] = -1, ["defaultTopP"] = 99.0, ["maxTokens"] = 0,
            ["participationMaxConsecutiveReplies"] = 999, ["agentReasoningEffort"] = " DEFAULT ",
            ["agentPrompt"] = new string('a', 8001), ["agentReasoningLevels"] = new string('b', 1001) });
        check("configuration legacy old Clamp and reasoning normalization preserved",
            clamped.Behavior!.AiDesire.TryGetValue(out var desire) && desire == 0
                && clamped.Behavior.ParticipationMaxConsecutiveReplies.TryGetValue(out var replies) && replies == 10
                && clamped.Model!.DefaultTopP.TryGetValue(out var topP) && topP == 1
                && clamped.Model.MaxTokens.TryGetValue(out var tokens) && tokens == 64
                && clamped.Agent!.AgentReasoningEffort.TryGetValue(out var effort) && effort == "auto"
                && clamped.Agent.AgentPrompt.TryGetValue(out var prompt) && prompt.Length == 8000
                && clamped.Agent.AgentReasoningLevels.TryGetValue(out var levels) && levels.Length == 1000);
        var clock = Parse(new JsonObject { ["healthReportTime"] = 930, ["healthReportTargets"] = new JsonArray("10001", "10002") });
        check("configuration legacy nonqualifying clock Keep and historical ToString targets preserved",
            clock.Reporting!.HealthReportTime.Kind == EditKind.Keep
                && clock.Reporting.HealthReportTargets.TryGetValue(out var targets) && targets.StartsWith("[", StringComparison.Ordinal));
        var ignored = Parse(new JsonObject { ["apiKey"] = "synthetic-secret", ["feishuVerificationToken"] = "synthetic-token",
            ["agentDevices"] = "[]", ["platformPolicies"] = new JsonObject(), ["unknown"] = true });
        check("configuration legacy scalar mapper does not claim complex or secret processing",
            Edits(ignored).All(x => Kind(x.Edit) == EditKind.Keep));
        check("configuration legacy scalar bad type rejects before applying any Set", Throws<InvalidOperationException>(() =>
            Parse(new JsonObject { ["aiDesire"] = "bad-type", ["enableVoice"] = true })));
    }

    private static void CheckLegacySecretParser(Action<string, bool> check)
    {
        var parser = typeof(BotAgent.Adapters.Panel.WebUiServer).GetMethod("ParseLegacySecrets",
            BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(JsonObject) }, null)
            ?? throw new InvalidOperationException("actual_legacy_parser_missing");
        object Parse(JsonObject body) => parser.Invoke(null, new object[] { body })!;
        T Get<T>(object plan, string name) => (T)plan.GetType().GetProperty(name)!.GetValue(plan)!;
        void Verify(JsonObject body, string label, Action<object> assertions)
        {
            var before = body.ToJsonString();
            using var plan = (IDisposable)Parse(body);
            assertions(plan);
            check("configuration legacy pure input unchanged " + label, body.ToJsonString() == before);
        }
        var keys = new[] { "apiKey", "agentServerKey", "ttsApiKey", "feishuAppSecret", "feishuEncryptKey", "feishuVerificationToken" };
        Verify(new JsonObject(), "missing", plan =>
            check("configuration legacy fixed six missing slots Keep",
                Get<ConfigurationResultCode>(plan, "Code") == ConfigurationResultCode.Ok
                && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").Select(x => x.Slot).SequenceEqual(Enum.GetValues<WritableSecretSlot>())
                && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").All(x => x.Kind == SecretEditKind.Keep)
                && Get<FieldEdit<string>>(plan, "Mood").Kind == EditKind.Keep
                && !Get<bool>(plan, "TtsProjectionRequested")));
        foreach (var key in keys)
        {
            var index = Array.IndexOf(keys, key);
            Verify(new JsonObject { [key] = null }, "null slot " + index, plan =>
                check("configuration legacy null secret Keep " + index,
                    Get<ImmutableArray<SecretEdit>>(plan, "Secrets").All(x => x.Kind == SecretEditKind.Keep)));
            Verify(new JsonObject { [key] = " \t " }, "empty slot " + index, plan =>
            {
                if (index == 5)
                    check("configuration legacy empty Token whole plan requires refresh",
                        Get<ConfigurationResultCode>(plan, "Code") == ConfigurationResultCode.LegacyNeedsReview
                        && Get<string>(plan, "ErrorCode") == "verification_token_refresh_required"
                        && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").IsEmpty);
                else
                    check("configuration legacy empty slot preserves old delete or keep " + index,
                        Get<ImmutableArray<SecretEdit>>(plan, "Secrets")[index].Kind
                            == (index < 2 ? SecretEditKind.Delete : SecretEditKind.Keep));
            });
        }
        var replacements = new JsonObject();
        foreach (var key in keys) replacements[key] = " synthetic-parser-credential ";
        var ownedPlan = Parse(replacements);
        var owned = (ImmutableArray<SensitiveInput>)ownedPlan.GetType()
            .GetField("_owned", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ownedPlan)!;
        try
        {
            check("configuration legacy replacements own six trimmed inputs",
                Get<ImmutableArray<SecretEdit>>(ownedPlan, "Secrets").All(x => x.Kind == SecretEditKind.Replace)
                && owned.Length == 6 && owned.All(x => x.ByteLength == Encoding.UTF8.GetByteCount("synthetic-parser-credential")));
            replacements["apiKey"] = "different-synthetic-input";
            check("configuration legacy owned inputs independent of caller object", owned.All(x => x.ByteLength == 27));
            check("configuration legacy plan diagnostics redacted", ownedPlan.ToString() == "[redacted]");
            check("configuration legacy plan cannot serialize owned secrets",
                Throws<JsonException>(() => JsonSerializer.Serialize(ownedPlan, ownedPlan.GetType())));
        }
        finally { ((IDisposable)ownedPlan).Dispose(); }
        ((IDisposable)ownedPlan).Dispose();
        check("configuration legacy plan disposal invalidates all owned inputs",
            owned.All(x => Throws<ObjectDisposedException>(() => _ = x.ByteLength)));
        foreach (var pair in new[] { ("ttsApiKey", "clearTtsApiKey", 2), ("feishuAppSecret", "clearFeishuAppSecret", 3),
            ("feishuEncryptKey", "clearFeishuEncryptKey", 4) })
        {
            Verify(new JsonObject { [pair.Item1] = new JsonObject(), [pair.Item2] = true }, "clear slot " + pair.Item3, plan =>
                check("configuration legacy true clear precedes malformed replacement " + pair.Item3,
                    Get<ImmutableArray<SecretEdit>>(plan, "Secrets")[pair.Item3].Kind == SecretEditKind.Delete));
            foreach (var flag in new JsonNode?[] { null, JsonValue.Create(false), JsonValue.Create("true"), JsonValue.Create(1) })
                Verify(new JsonObject { [pair.Item1] = "synthetic-clear-fallback", [pair.Item2] = flag }, "tolerant clear slot " + pair.Item3, plan =>
                    check("configuration legacy nontrue clear preserves old tolerant replacement " + pair.Item3,
                        Get<ImmutableArray<SecretEdit>>(plan, "Secrets")[pair.Item3].Kind == SecretEditKind.Replace));
        }
        foreach (var key in new[] { "officialAppSecret", "OFFICIALAPPSECRET", "clearOfficialAppSecret", "ClearOfficialAppSecret" })
            Verify(new JsonObject { [key] = null, ["apiKey"] = "synthetic-before-rejection" }, "official presence", plan =>
                check("configuration legacy official presence rejected before allocating edits",
                    Get<ConfigurationResultCode>(plan, "Code") == ConfigurationResultCode.ValidationFailed
                    && Get<string>(plan, "ErrorCode") == "official_secret_environment_only"
                    && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").IsEmpty));
        foreach (var invalid in new[] { "synthetic\0invalid", "\ud800", new string('a', 16385), new string('\u00e9', 8193) })
            Verify(new JsonObject { ["apiKey"] = "synthetic-valid-earlier", ["feishuAppSecret"] = invalid }, "invalid UTF8 or budget", plan =>
                check("configuration legacy invalid secret rejects entire plan safely",
                    Get<ConfigurationResultCode>(plan, "Code") == ConfigurationResultCode.ValidationFailed
                    && Get<string>(plan, "ErrorCode") == "invalid_legacy_secret"
                    && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").IsEmpty));
        Verify(new JsonObject { ["apiKey"] = new string('\u00e9', 8192) }, "exact UTF8 budget", plan =>
            check("configuration legacy exact 16384 UTF8 bytes accepted",
                Get<ConfigurationResultCode>(plan, "Code") == ConfigurationResultCode.Ok
                && Get<ImmutableArray<SecretEdit>>(plan, "Secrets")[0].Kind == SecretEditKind.Replace));
        Verify(new JsonObject { ["feishuVerificationToken"] = false }, "invalid Token type", plan =>
            check("configuration legacy nonstring Token rejected safely",
                Get<ConfigurationResultCode>(plan, "Code") == ConfigurationResultCode.ValidationFailed));
        foreach (var value in new[] { "", " \t ", "synthetic-mood" })
            Verify(new JsonObject { ["mood"] = value, ["ttsKey"] = "ignored-synthetic-credential" }, "independent action", plan =>
                check("configuration legacy Mood intent independent from ignored ttsKey",
                    Get<FieldEdit<string>>(plan, "Mood").Kind == (string.IsNullOrWhiteSpace(value) ? EditKind.Clear : EditKind.Set)
                    && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").All(x => x.Kind == SecretEditKind.Keep)
                    && !Get<bool>(plan, "TtsProjectionRequested")));
        foreach (var field in new[] { "ttsProvider", "ttsApiBase", "ttsModel" })
            Verify(new JsonObject { [field] = "synthetic-projection" }, "TTS intent", plan =>
                check("configuration legacy TTS projection intent not a secret mutation",
                    Get<bool>(plan, "TtsProjectionRequested")
                    && Get<ImmutableArray<SecretEdit>>(plan, "Secrets").All(x => x.Kind == SecretEditKind.Keep)));
    }

    private static ReadField<string> Project(FieldVisibility visibility, string display, FieldDescriptor rule, string value)
        => StorageConfiguration.ConfigurationStorageBridge.ProjectField(visibility, display, rule,
            UserValueKind.Explicit, ConfigurationValueSource.UserConfiguration, ApplyState.NotStarted, value);

    private static void CheckPublication(Action<string, bool> check)
    {
        var key = new ConfigurationOperationKey(new(Guid.NewGuid()), 1, Guid.NewGuid());
        var receipt = new ConfigurationReceipt(key, "cfg-0.3.2", ConfigurationDecision.Changed,
            new(0), new ConfigurationVersion(1), CommitState.Committed, ApplyState.NotStarted,
            ImmutableArray<ApplyStepReport>.Empty, ImmutableArray<DeviceKeyAssignment>.Empty);
        var committed = new ConfigurationSaveResult(ConfigurationResultCode.Ok, false, receipt,
            new(1), ImmutableArray<FieldError>.Empty);
        var cases = new[]
        {
            committed with { Code = ConfigurationResultCode.ValidationFailed, Receipt = null },
            committed with { Code = ConfigurationResultCode.NoChanges, Receipt = receipt with
                { Decision = ConfigurationDecision.NoChanges, CommitState = CommitState.NotCommitted,
                    CommittedVersion = null } },
            committed with { Replayed = true },
            committed with { Code = ConfigurationResultCode.UnknownCommit, Receipt = receipt with
                { CommitState = CommitState.Unknown, CommittedVersion = null } }
        };
        foreach (var result in cases)
        {
            var original = new AppSettings { AiDesire = 10 };
            var box = new SettingsBox(original);
            var applied = 0;
            box.ApplyConfiguration(current => { current.AiDesire = 20; return (current, result); },
                (_, observed) => { applied++; return observed; });
            check("configuration structured noncommit/replay does not publish " + result.Code + "/" + result.Replayed,
                ReferenceEquals(original, box.Current) && original.AiDesire == 10 && applied == 0);
        }
        var successBox = new SettingsBox(new AppSettings { AiDesire = 10 });
        var callbacks = 0;
        successBox.ApplyConfiguration(current => { current.AiDesire = 20; return (current, committed); },
            (_, observed) => { callbacks++; return observed; });
        check("configuration first Changed Committed publishes exactly once",
            successBox.Current.AiDesire == 20 && callbacks == 1);
        var failed = successBox.ApplyConfiguration(current => { current.AiDesire = 30; return (current, committed); },
            (_, _) => throw new IOException("synthetic_application_failure"));
        check("configuration application failure preserves commit and published snapshot",
            successBox.Current.AiDesire == 30 && failed.Receipt is { ApplyState: ApplyState.Failed,
                CommitState: CommitState.Committed, CommittedVersion.Value: 1 }
            && failed.Receipt.Steps.Any(s => s.Step == ApplyStep.PublishSnapshot && s.State == ApplyState.Applied));
        var concurrent = new SettingsBox(new AppSettings { AiDesire = 0 });
        Parallel.For(0, 20, _ => concurrent.ApplyConfiguration(current =>
        { current.AiDesire++; return (current, committed); }));
        check("configuration structured saves share the existing writer gate", concurrent.Current.AiDesire == 20);
    }

    private static bool IsInitOnly(PropertyInfo property)
        => property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers()
            .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true;
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    public static async Task StorageAsync(Action<bool, string> report)
    {
        void check(string name, bool condition) => report(condition, name);
        var originalClock = Clock.Current;
        var clock = new SyntheticClock();
        Clock.Use(clock);
        try
        {
            var participant = new SyntheticParticipant();
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            check("configuration activation reuses the sole module", ReferenceEquals(store,
                StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant)));
            check("configuration activation rejects another participant", Throws<InvalidOperationException>(() =>
                StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, new SyntheticParticipant())));
            check("configuration module migration preserves physical schema 9", AppDatabase.Scalar<long>("PRAGMA user_version") == 9
                && AppDatabase.GetMeta("configuration_schema") == "1");
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-owner", "bot",
                ConfigurationAccess.ReadOverview | ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration
                    | ConfigurationAccess.EditAllowedSecrets | ConfigurationAccess.ValidateConfiguration);
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            var generation = read.Generation;
            ConfigurationSaveRequest Request(int? desire = null, ImmutableArray<SecretEdit> secrets = default)
                => new("cfg-0.3.2", new(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1")),
                    new(generation, clock.Now.ToUnixTimeSeconds(), Guid.NewGuid()),
                    new(desire is null ? null : new BehaviorChanges { AiDesire = FieldEdit<int>.Set(desire.Value) },
                        null, null, null, null, null, null, null),
                    secrets.IsDefault ? ImmutableArray<SecretEdit>.Empty : secrets);
            var firstRequest = Request(20);
            var first = await store.SaveAsync(caller, firstRequest);
            check("configuration synthetic participant commits version and user facts", first.Code == ConfigurationResultCode.Ok
                && first.Receipt is { CommitState: CommitState.Committed } && first.CurrentVersion.Value == 1
                && AppDatabase.Scalar<long>("SELECT user_mode FROM configuration_field_modes WHERE field_id=26") == 2);
            check("configuration retained strict fingerprint encoding remains unchanged", StrictFingerprintMatches(caller, firstRequest));
            var replay = await store.SaveAsync(caller, firstRequest);
            check("configuration same key replays before stale CAS", replay.Replayed && replay.Code == ConfigurationResultCode.Ok
                && replay.Receipt?.CommittedVersion == first.Receipt?.CommittedVersion && participant.Writes == 1);
            var conflict = await store.SaveAsync(caller, firstRequest with
                { Changes = firstRequest.Changes with { Behavior = new() { AiDesire = FieldEdit<int>.Set(21) } } });
            check("configuration same key different intent conflicts", conflict.Code == ConfigurationResultCode.IdempotencyConflict
                && participant.Writes == 1);
            var stale = await store.SaveAsync(caller, Request(21) with { ExpectedVersion = new(0) });
            check("configuration stale version does not write", stale.Code == ConfigurationResultCode.VersionConflict
                && participant.Writes == 1);
            var noChangesRequest = Request(20);
            var noChanges = await store.SaveAsync(caller, noChangesRequest);
            check("configuration no changes records a noncommit decision", noChanges.Code == ConfigurationResultCode.NoChanges
                && noChanges.Receipt is { Decision: ConfigurationDecision.NoChanges, CommitState: CommitState.NotCommitted,
                    CommittedVersion: null, ApplyState: ApplyState.NotStarted } && participant.Writes == 1);
            await store.SaveAsync(caller, Request(30));
            var originalDecision = await store.SaveAsync(caller, noChangesRequest);
            check("configuration replay preserves old no-change decision after another save", originalDecision.Replayed
                && originalDecision.Code == ConfigurationResultCode.NoChanges && participant.Value == 30);
            var found = await store.FindAsync(caller, firstRequest.Key);
            check("configuration Find recovers exact committed receipt without application", found.Code == ConfigurationResultCode.Ok
                && found.Receipt?.CommittedVersion == first.Receipt?.CommittedVersion && participant.Writes == 2);
            await CheckApplyReceipts(store, participant, caller, firstRequest, check);
            participant.Allow = false;
            var hidden = await store.FindAsync(caller, firstRequest.Key);
            var unauthorized = await store.SaveAsync(caller, Request(40));
            check("configuration trusted authorization overrides forged caller flags", hidden.Code == ConfigurationResultCode.Forbidden
                && hidden.Receipt is null && unauthorized.Code == ConfigurationResultCode.Forbidden);
            participant.Allow = true;
            using var secret = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes(" synthetic-credential "));
            var secretSave = await store.SaveAsync(caller, Request(null, ImmutableArray.Create(
                SecretEdit.Replace(WritableSecretSlot.ModelApiKey, secret))));
            check("configuration same transaction stores normalized allowed secret", secretSave.Code == ConfigurationResultCode.Ok
                && new SecretsStore().LoadApiKey() == "synthetic-credential");
            var beforeInvalid = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            foreach (var bytes in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes(" \t "), new byte[] { 0xc3, 0x28 } })
            {
                using var bad = SensitiveInput.FromUtf8(bytes);
                var rejected = await store.SaveAsync(caller, Request(41, ImmutableArray.Create(
                    SecretEdit.Replace(WritableSecretSlot.ModelApiKey, bad))));
                check("configuration invalid Replace cannot become delete or partial save", rejected.Code == ConfigurationResultCode.ValidationFailed
                    && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == beforeInvalid
                    && participant.Value == 30 && new SecretsStore().LoadApiKey() == "synthetic-credential");
            }
            var deleted = await store.SaveAsync(caller, Request(null, ImmutableArray.Create(SecretEdit.Delete(WritableSecretSlot.ModelApiKey))));
            check("configuration explicit delete removes only the database override", deleted.Code == ConfigurationResultCode.Ok
                && new SecretsStore().LoadApiKey() is null);
            var tokenDelete = await store.SaveAsync(caller, Request(null,
                ImmutableArray.Create(SecretEdit.Delete(WritableSecretSlot.FeishuVerificationToken))));
            check("configuration deleting uninitialized token persists deleted marker", tokenDelete.Code == ConfigurationResultCode.Ok
                && AppDatabase.GetMeta("verification_token_initialized") == "deleted");
            using (var ownedInput = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-owned-credential")))
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                participant.PrepareStarted = started; participant.PrepareRelease = release;
                var pendingRequest = Request(null, ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, ownedInput)));
                var pending = Task.Run(async () => await store.SaveAsync(caller, pendingRequest));
                check("configuration private ownership established before participant execution", started.Wait(TimeSpan.FromSeconds(10)));
                ownedInput.Dispose(); release.Set();
                var completed = await pending;
                participant.PrepareStarted = null; participant.PrepareRelease = null;
                check("configuration caller Dispose does not erase an admitted secret", completed.Code == ConfigurationResultCode.Ok
                    && new SecretsStore().LoadApiKey() == "synthetic-owned-credential");
            }
            await store.SaveAsync(caller, Request(null, ImmutableArray.Create(SecretEdit.Delete(WritableSecretSlot.ModelApiKey))));
            var array = new[] { new DeviceConfiguration(new(Guid.NewGuid()), "synthetic-device", true, null, null, null, 0) };
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                participant.PrepareStarted = started; participant.PrepareRelease = release;
                var pendingRequest = Request() with { Changes = new(null, null,
                    new AgentChanges { AgentDevices = FieldEdit<ImmutableArray<DeviceConfiguration>>.Set(
                        System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(array)) },
                    null, null, null, null, null) };
                var pending = Task.Run(async () => await store.SaveAsync(caller, pendingRequest));
                check("configuration array freeze precedes participant execution", started.Wait(TimeSpan.FromSeconds(10)));
                array[0] = array[0] with { NameRaw = "synthetic-replaced-device" }; release.Set();
                await pending; participant.PrepareStarted = null; participant.PrepareRelease = null;
                check("configuration caller array mutation cannot alter owned intent", participant.ObservedDeviceName == "synthetic-device");
            }
            await CheckTransactionFaults(store, participant, caller, Request, check);
            var raceBase = Request(50);
            var race = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
                await store.SaveAsync(caller, raceBase with { Key = raceBase.Key with { Nonce = Guid.NewGuid() },
                    Changes = raceBase.Changes with { Behavior = new() { AiDesire = FieldEdit<int>.Set(50 + i) } } }))));
            check("configuration same-version concurrent writers have one winner", race.Count(r => r.Code == ConfigurationResultCode.Ok) == 1
                && race.Count(r => r.Code == ConfigurationResultCode.VersionConflict) == 7);
            await CheckRestartRecovery(firstRequest, check);
            var expiryRequest = Request(60);
            await store.SaveAsync(caller, expiryRequest);
            var incompleteRestart = store.RecordApply(caller, expiryRequest.Key,
                ImmutableArray.Create(new ApplyStepReport(ApplyStep.PublishSnapshot, ApplyState.RestartRequired, "restart_required")));
            check("configuration incomplete restart remains nonterminal", incompleteRestart.Receipt?.ApplyState == ApplyState.RestartRequired
                && AppDatabase.Scalar<long>("SELECT terminal FROM configuration_operations WHERE operation_key=$key",
                    ("$key", JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(expiryRequest.Key)))) == 0);
            clock.AdvanceSeconds(86400);
            var expired = await store.SaveAsync(caller, expiryRequest);
            check("configuration exact 24-hour boundary expires even existing receipt", expired.Code == ConfigurationResultCode.KeyExpired);
            await store.SaveAsync(caller, Request(61));
            check("configuration expired incomplete restart survives terminal cleanup",
                AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations WHERE operation_key=$key",
                    ("$key", JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(expiryRequest.Key)))) == 1);
            var floorBeforeRead = AppDatabase.Scalar<long>("SELECT floor FROM configuration_state WHERE id=1");
            clock.AdvanceSeconds(1);
            await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            check("configuration Read never advances governance floor", AppDatabase.Scalar<long>("SELECT floor FROM configuration_state WHERE id=1") == floorBeforeRead);
            var rollbackRequest = Request(61);
            clock.WallSeconds -= 301;
            var rollback = await store.SaveAsync(caller, rollbackRequest);
            check("configuration wall-clock rollback fails closed", rollback.Code == ConfigurationResultCode.ClockRegression);
            clock.WallSeconds += 301;
            await CheckWriterBounds(store, participant, caller, clock, Request, check);
            await CheckLegacyProfile(store, participant, caller, Request, check);
        }
        finally { Clock.Use(originalClock); }
    }

    private static bool StrictFingerprintMatches(ConfigurationCaller caller, ConfigurationSaveRequest request)
    {
        // Reconstruct the pre-profile encoding against this owned synthetic database only.
        using var connection = AppDatabase.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hmac_key FROM configuration_generations WHERE generation=$g";
        command.Parameters.AddWithValue("$g", request.Key.Generation.Value.ToString("N"));
        var key = (byte[])command.ExecuteScalar()!;
        byte[]? protectedBytes = null;
        byte[]? expected = null;
        byte[]? actual = null;
        using var payload = new MemoryStream();
        try
        {
            var patch = new ConfigurationPatch(request.Changes.Behavior ?? new(), request.Changes.Model ?? new(),
                request.Changes.Agent ?? new(), request.Changes.Platform ?? new(), request.Changes.Voice ?? new(),
                request.Changes.Music ?? new(), request.Changes.Reporting ?? new(),
                request.Changes.Registered ?? new(ImmutableArray<PluginEnabledEdit>.Empty, ImmutableArray<ProviderEdit>.Empty, ImmutableArray<PolicyEdit>.Empty));
            using (var writer = new Utf8JsonWriter(payload))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(caller.AuthorityId); writer.WriteStringValue(caller.SubjectId);
                writer.WriteStringValue(caller.ResourceId); writer.WriteStringValue("cfg1");
                writer.WriteStringValue(request.ContractRevision); writer.WriteNumberValue(request.ExpectedVersion.Value);
                JsonSerializer.Serialize(writer, patch, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                foreach (var slot in Enum.GetValues<WritableSecretSlot>())
                { writer.WriteNumberValue((int)slot); writer.WriteNumberValue((int)SecretEditKind.Keep); writer.WriteNullValue(); }
                writer.WriteEndArray();
            }
            protectedBytes = payload.ToArray();
            expected = HMACSHA256.HashData(key, protectedBytes);
            command.Parameters.Clear();
            command.CommandText = "SELECT fingerprint FROM configuration_operations WHERE operation_key=$k";
            command.Parameters.AddWithValue("$k", JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(request.Key)));
            actual = (byte[])command.ExecuteScalar()!;
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
            if (payload.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
        }
    }

    private static async Task CheckLegacyProfile(StorageConfiguration.ConfigurationStore store,
        SyntheticParticipant participant, ConfigurationCaller caller,
        Func<int?, ImmutableArray<SecretEdit>, ConfigurationSaveRequest> requestFactory, Action<string, bool> check)
    {
        ValueTask<ConfigurationSaveResult> Legacy(ConfigurationSaveRequest request)
            => StorageConfiguration.ConfigurationStorageBridge.SaveLegacyAsync(store, caller, request.Key, request.Changes, request.Secrets);
        var staleIntent = requestFactory(64, ImmutableArray<SecretEdit>.Empty);
        var intervening = await store.SaveAsync(caller, requestFactory(65, ImmutableArray<SecretEdit>.Empty));
        check("configuration private profile does not change public strict CAS", (await store.SaveAsync(caller, staleIntent)).Code
            == ConfigurationResultCode.VersionConflict && participant.LastProfile == StorageConfiguration.ConfigurationStorageProfile.Strict);
        var legacy = await Legacy(staleIntent);
        check("configuration legacy uses same-transaction latest version without external CAS", legacy.Code == ConfigurationResultCode.Ok
            && legacy.Receipt?.ObservedVersion == intervening.CurrentVersion
            && legacy.Receipt?.CommittedVersion?.Value == intervening.CurrentVersion.Value + 1 && participant.Value == 64
            && participant.LastProfile == StorageConfiguration.ConfigurationStorageProfile.Legacy);
        var writes = participant.Writes;
        var replay = await Legacy(staleIntent);
        check("configuration private legacy replay does not persist again", replay.Replayed && replay.Code == ConfigurationResultCode.Ok
            && participant.Writes == writes && replay.Receipt?.CommittedVersion == legacy.Receipt?.CommittedVersion);
        var altered = await Legacy(staleIntent with
            { Changes = staleIntent.Changes with { Behavior = new() { AiDesire = FieldEdit<int>.Set(66) } } });
        check("configuration private legacy altered intent conflicts", altered.Code == ConfigurationResultCode.IdempotencyConflict);
        check("configuration strict cannot replay legacy profile key", (await store.SaveAsync(caller, staleIntent)).Code
            == ConfigurationResultCode.IdempotencyConflict);
        var strict = requestFactory(67, ImmutableArray<SecretEdit>.Empty);
        await store.SaveAsync(caller, strict);
        check("configuration legacy cannot replay strict profile key", (await Legacy(strict)).Code == ConfigurationResultCode.IdempotencyConflict);
        participant.Allow = false;
        try
        {
            check("configuration private legacy still reauthorizes caller", (await Legacy(requestFactory(68, ImmutableArray<SecretEdit>.Empty))).Code
                == ConfigurationResultCode.Forbidden);
        }
        finally { participant.Allow = true; }
        var version = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
        var value = participant.Value;
        AppDatabase.Write(conn => AppDatabase.Exec(conn, """
            CREATE TRIGGER synthetic_legacy_secret_fault BEFORE INSERT ON secrets
            BEGIN SELECT RAISE(ABORT,'synthetic_legacy_secret_fault'); END;
            """));
        try
        {
            using var input = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-legacy-credential"));
            var failure = await Legacy(requestFactory(68, ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, input))));
            check("configuration private legacy shares atomic settings-secret-version transaction", failure.Code == ConfigurationResultCode.CommitFailed
                && participant.Value == value && new SecretsStore().LoadApiKey() is null
                && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == version);
        }
        finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_legacy_secret_fault")); }
        var baseline = requestFactory(null, ImmutableArray<SecretEdit>.Empty);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () => await Legacy(baseline with
            { Key = baseline.Key with { Nonce = Guid.NewGuid() },
                Changes = baseline.Changes with { Behavior = new() { AiDesire = FieldEdit<int>.Set(70 + i) } } }))));
        check("configuration concurrent private legacy writers merge latest committed versions", results.All(r => r.Code == ConfigurationResultCode.Ok)
            && results.Select(r => r.Receipt!.ObservedVersion.Value).OrderBy(v => v).SequenceEqual(Enumerable.Range(0, 8).Select(i => version + i))
            && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == version + 8);
    }

    private static async Task CheckWriterBounds(StorageConfiguration.ConfigurationStore store,
        SyntheticParticipant participant, ConfigurationCaller caller, SyntheticClock clock,
        Func<int?, ImmutableArray<SecretEdit>, ConfigurationSaveRequest> requestFactory, Action<string, bool> check)
    {
        var request = requestFactory(participant.Value, ImmutableArray<SecretEdit>.Empty);
        var writes = participant.Writes;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await store.SaveAsync(caller, request, cancelled.Token);
        check("configuration already cancelled request does not persist", cancellation.Code == ConfigurationResultCode.CancelledBeforeCommit
            && participant.Writes == writes);
        var future = request with { Key = request.Key with { IssuedUnixSeconds = clock.WallSeconds + 301 } };
        check("configuration future key beyond 300 seconds rejected", (await store.SaveAsync(caller, future)).Code
            == ConfigurationResultCode.ValidationFailed);
        var boundary = future with { Key = future.Key with { IssuedUnixSeconds = clock.WallSeconds + 300 } };
        check("configuration future key at 300 seconds accepted", (await store.SaveAsync(caller, boundary)).Code
            == ConfigurationResultCode.NoChanges);
        var ticks = clock.Monotonic;
        clock.Monotonic = -1;
        try
        {
            check("configuration monotonic rollback rejects new request", (await store.SaveAsync(caller, request)).Code
                == ConfigurationResultCode.ClockRegression);
        }
        finally { clock.Monotonic = ticks; }
        var version = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
        AppDatabase.Write(conn => AppDatabase.Exec(conn, "UPDATE configuration_state SET version=$v WHERE id=1", ("$v", long.MaxValue)));
        try
        {
            var overflow = await store.SaveAsync(caller, requestFactory(participant.Value == 62 ? 63 : 62, ImmutableArray<SecretEdit>.Empty));
            check("configuration version overflow cannot wrap or partially write", overflow.Code == ConfigurationResultCode.ValidationFailed
                && participant.Writes == writes && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == long.MaxValue);
        }
        finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "UPDATE configuration_state SET version=$v WHERE id=1", ("$v", version))); }

        var subject = caller with { SubjectId = "synthetic-capacity-subject" };
        var first = request with { Key = request.Key with { Nonce = Guid.NewGuid() } };
        try
        {
            for (var i = 0; i < 256; i++)
            {
                var result = await store.SaveAsync(subject, first with { Key = i == 0 ? first.Key : first.Key with { Nonce = Guid.NewGuid() } });
                if (result.Code != ConfigurationResultCode.NoChanges) throw new InvalidOperationException("configuration_capacity_fixture_save");
            }
            check("configuration subject capacity permits exactly 256 receipts", AppDatabase.Scalar<long>(
                "SELECT COUNT(*) FROM configuration_operations WHERE subject=$s", ("$s", subject.SubjectId)) == 256);
            var denied = await store.SaveAsync(subject, first with { Key = first.Key with { Nonce = Guid.NewGuid() } });
            check("configuration subject capacity rejects new operation", denied.Code == ConfigurationResultCode.CapacityExceeded);
            var replay = await store.SaveAsync(subject, first with { ExpectedVersion = first.ExpectedVersion });
            check("configuration exact replay precedes subject capacity", replay.Replayed && replay.Code == ConfigurationResultCode.NoChanges);
            var conflict = await store.SaveAsync(subject, first with
                { Changes = first.Changes with { Behavior = new() { AiDesire = FieldEdit<int>.Set(participant.Value == 62 ? 63 : 62) } } });
            check("configuration altered replay conflicts before subject capacity", conflict.Code == ConfigurationResultCode.IdempotencyConflict);
            var other = await store.SaveAsync(caller with { SubjectId = "synthetic-capacity-other" },
                first with { Key = first.Key with { Nonce = Guid.NewGuid() } });
            check("configuration subject capacity does not block another subject", other.Code == ConfigurationResultCode.NoChanges
                && participant.Writes == writes && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == version);
        }
        finally
        {
            AppDatabase.Write(conn => AppDatabase.Exec(conn,
                "DELETE FROM configuration_operations WHERE authority=$a AND subject IN ($s,$other) AND resource=$r",
                ("$a", caller.AuthorityId), ("$s", subject.SubjectId), ("$other", "synthetic-capacity-other"), ("$r", caller.ResourceId)));
        }
    }

    private static async Task CheckRestartRecovery(ConfigurationSaveRequest request, Action<string, bool> check)
    {
        var version = new ConfigurationVersion(AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1"));
        var runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(AppDatabase.FilePath))!;
        var restoreRoot = Path.Combine(runtimeRoot, "configuration-restore");
        Directory.CreateDirectory(Path.Combine(restoreRoot, "data"));
        var backup = AppDatabase.VacuumIntoBackup();
        File.Copy(backup, Path.Combine(restoreRoot, "data", "qqchat.db"));
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(ConfigurationContractTests).Assembly.Location);
        start.ArgumentList.Add("--configuration-restart");
        start.ArgumentList.Add(restoreRoot);
        start.ArgumentList.Add(JsonSerializer.Serialize(request));
        start.ArgumentList.Add(JsonSerializer.Serialize(version));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("configuration_restart_child_start");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        var text = await output;
        Console.Write(text);
        check("configuration fresh process restores backup under new generation without redo",
            child.ExitCode == 0 && text.Contains("CFG_CHILD restart passed=8 failed=0", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(await error));
        check("configuration backup recovery does not replace live version",
            AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == version.Value);
    }

    private static async Task CheckApplyReceipts(StorageConfiguration.ConfigurationStore store,
        SyntheticParticipant participant, ConfigurationCaller caller, ConfigurationSaveRequest request, Action<string, bool> check)
    {
        var version = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
        var writes = participant.Writes;
        var update = ImmutableArray.Create(new ApplyStepReport(ApplyStep.PublishSnapshot, ApplyState.Applied, null));
        AppDatabase.Write(conn => AppDatabase.Exec(conn, """
            CREATE TRIGGER synthetic_apply_fault BEFORE UPDATE ON configuration_operations
            BEGIN SELECT RAISE(ABORT,'synthetic_apply_fault'); END;
            """));
        try
        {
            var failed = store.RecordApply(caller, request.Key, update);
            var original = await store.FindAsync(caller, request.Key);
            check("configuration apply-ledger fault cannot claim success", failed.Code == ConfigurationResultCode.StorageUnavailable
                && failed.Receipt is null && original.Receipt is { ApplyState: ApplyState.NotStarted, Steps.Length: 0 });
        }
        finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_apply_fault")); }
        check("configuration duplicate apply reports are rejected", store.RecordApply(caller, request.Key,
            update.Add(update[0])).Code == ConfigurationResultCode.ValidationFailed);
        check("configuration arbitrary apply errors are rejected", store.RecordApply(caller, request.Key,
            ImmutableArray.Create(update[0] with { ErrorCode = "synthetic-unapproved-detail" })).Code
                == ConfigurationResultCode.ValidationFailed);
        var partial = store.RecordApply(caller, request.Key, update);
        check("configuration one actual step cannot claim all applied", partial.Receipt is
            { ApplyState: ApplyState.PartiallyApplied, Steps.Length: 1 });
        var uncertain = store.RecordApply(caller, request.Key, ImmutableArray.Create(
            new ApplyStepReport(ApplyStep.ModelProjection, ApplyState.Unknown, "apply_unknown")));
        check("configuration unknown target remains unknown", uncertain.Receipt?.ApplyState == ApplyState.Unknown);
        var incomplete = store.RecordApply(caller, request.Key, ImmutableArray.Create(
            new ApplyStepReport(ApplyStep.ModelProjection, ApplyState.Failed, "apply_failed")));
        var key = JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(request.Key));
        check("configuration incomplete failure remains nonterminal", incomplete.Receipt?.ApplyState == ApplyState.Failed
            && AppDatabase.Scalar<long>("SELECT terminal FROM configuration_operations WHERE operation_key=$key", ("$key", key)) == 0);
        var complete = store.RecordApply(caller, request.Key, Enum.GetValues<ApplyStep>()
            .Select(step => new ApplyStepReport(step, step == ApplyStep.ModelProjection ? ApplyState.Failed : ApplyState.Applied,
                step == ApplyStep.ModelProjection ? "apply_failed" : null)).ToImmutableArray());
        check("configuration complete failed stages keep truthful aggregate", complete.Receipt is
            { ApplyState: ApplyState.Failed, Steps.Length: 8 }
            && AppDatabase.Scalar<long>("SELECT terminal FROM configuration_operations WHERE operation_key=$key", ("$key", key)) == 1);
        var replay = await store.SaveAsync(caller, request);
        check("configuration replay returns actual failed application without reexecution", replay.Replayed
            && replay.Receipt?.ApplyState == ApplyState.Failed && participant.Writes == writes
            && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == version);
    }

    private static async Task CheckTransactionFaults(StorageConfiguration.ConfigurationStore store,
        SyntheticParticipant participant, ConfigurationCaller caller,
        Func<int?, ImmutableArray<SecretEdit>, ConfigurationSaveRequest> requestFactory, Action<string, bool> check)
    {
        foreach (var point in new[] { ("settings", "UPDATE"), ("secrets", "INSERT"), ("configuration_field_modes", "INSERT"),
            ("configuration_state", "UPDATE"), ("configuration_operations", "INSERT"), ("security_audit_log", "INSERT") })
        {
            var version = AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1");
            var original = participant.Value;
            var ledger = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations");
            var audit = AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log");
            AppDatabase.Write(conn => AppDatabase.Exec(conn, $"CREATE TRIGGER synthetic_configuration_fault BEFORE {point.Item2} ON {point.Item1} " +
                "BEGIN SELECT RAISE(ABORT,'synthetic_configuration_fault'); END;"));
            try
            {
                using var input = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-fault-credential"));
                var request = requestFactory(45, ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.ModelApiKey, input)));
                var result = await store.SaveAsync(caller, request);
                check("configuration atomic rollback at " + point.Item1, result.Code == ConfigurationResultCode.CommitFailed
                    && AppDatabase.Scalar<long>("SELECT version FROM configuration_state WHERE id=1") == version
                    && participant.Value == original && new SecretsStore().LoadApiKey() is null
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM configuration_operations") == ledger
                    && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM security_audit_log") == audit);
            }
            finally { AppDatabase.Write(conn => AppDatabase.Exec(conn, "DROP TRIGGER synthetic_configuration_fault")); }
        }
    }

    private class SyntheticParticipant : StorageConfiguration.ConfigurationStorageParticipant
    {
        internal bool Allow = true;
        internal int Writes;
        internal ManualResetEventSlim? PrepareStarted;
        internal ManualResetEventSlim? PrepareRelease;
        internal string? ObservedDeviceName;
        internal StorageConfiguration.ConfigurationStorageProfile LastProfile;
        internal int InitializationCalls;
        internal Func<SqliteConnection, long, bool>? Initialization;
        internal Action<SqliteConnection, ConfigurationVersion, ConfigurationVersion>? InitializationAudit;
        internal bool ManageDevices;
        internal Action<SqliteConnection, DeviceKey>? DeviceRuntimeAudit;
        internal int Value => JsonSerializer.Deserialize<AppSettings>(AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id=1") ?? "{}")!.AiDesire;
        internal override bool Initialize(SqliteConnection connection, long serverUnixSeconds)
        {
            InitializationCalls++;
            return Initialization?.Invoke(connection, serverUnixSeconds) ?? false;
        }
        internal override void AppendInitializationAudit(SqliteConnection connection, ConfigurationVersion before, ConfigurationVersion after)
            => InitializationAudit?.Invoke(connection, before, after);
        internal override bool Reauthorize(ConfigurationCaller caller, ConfigurationAccess required) => Allow;
        internal override ImmutableArray<SecretMetadata> SecretSources(SqliteConnection connection,
            ImmutableArray<StorageConfiguration.SecretMutation> proposed)
            => Enum.GetValues<WritableSecretSlot>().Select(slot =>
            {
                var kind = proposed.FirstOrDefault(p => p.Slot == slot).Kind;
                var configured = kind == SecretEditKind.Replace || (kind == SecretEditKind.Keep
                    && StorageSecrets.LoadInTransaction(connection, slot) is not null);
                return new SecretMetadata(slot, configured, configured ? CredentialSource.Database : CredentialSource.None);
            }).ToImmutableArray();
        internal override StorageConfiguration.PreparedConfigurationChange Prepare(SqliteConnection connection,
            ConfigurationPatch intent, ImmutableArray<SecretMetadata> secrets,
            ImmutableArray<StorageConfiguration.SecretMutation> proposedMutations, StorageConfiguration.ConfigurationStorageProfile profile)
        {
            LastProfile = profile;
            PrepareStarted?.Set();
            if (PrepareRelease is not null && !PrepareRelease.Wait(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("synthetic_prepare_timeout");
            if (intent.Agent!.AgentDevices.TryGetValue(out var devices) && devices.Length != 0) ObservedDeviceName = devices[0].NameRaw;
            StorageConfiguration.PreparedDeviceChange? devicePlan = null;
            if (ManageDevices && intent.Agent.AgentDevices.Kind != EditKind.Keep)
            {
                devicePlan = StorageConfiguration.ConfigurationStorageBridge.PrepareDevices(connection,
                    intent.Agent.AgentDevices.Kind == EditKind.Clear ? ImmutableArray<DeviceConfiguration>.Empty : devices, profile);
                if (!devicePlan.Errors.IsEmpty)
                    return new(false, devicePlan.Errors, ImmutableArray<DeviceKeyAssignment>.Empty, _ => { }, devicePlan.Code);
            }
            using var command = connection.CreateCommand(); command.CommandText = "SELECT json FROM settings WHERE id=1";
            var baseline = JsonSerializer.Deserialize<AppSettings>(command.ExecuteScalar() as string ?? "{}")!;
            var old = baseline.AiDesire;
            if (intent.Behavior!.AiDesire.TryGetValue(out var value)) baseline.AiDesire = value;
            if (devicePlan is not null) baseline.AgentDevices = JsonSerializer.Serialize(devicePlan.DesiredWithKeys);
            return new(baseline.AiDesire != old || devicePlan is { Changed: true }, ImmutableArray<FieldError>.Empty,
                devicePlan?.Assignments ?? ImmutableArray<DeviceKeyAssignment>.Empty,
                conn =>
                {
                    devicePlan?.Persist(conn);
                    AppDatabase.Exec(conn, "INSERT INTO settings(id,json,updated_unix) VALUES(1,$json,0) " +
                        "ON CONFLICT(id) DO UPDATE SET json=excluded.json",
                        ("$json", JsonSerializer.Serialize(baseline)));
                    Interlocked.Increment(ref Writes);
                });
        }
        internal override ConfigurationReadResult Read(SqliteConnection connection, ConfigurationCaller caller,
            ConfigurationReadKind kind, ConfigurationVersion version, ConfigurationGeneration generation,
            long serverUnixSeconds, ApplyState latestApplyState)
            => new(ConfigurationResultCode.Ok, version, generation, "cfg-0.3.2", serverUnixSeconds, null,
                SecretSources(connection, ImmutableArray<StorageConfiguration.SecretMutation>.Empty),
                ImmutableArray<FieldDescriptor>.Empty, latestApplyState);
        internal override void AppendAudit(SqliteConnection connection, ConfigurationReceipt receipt,
            ImmutableArray<ConfigurationFieldId> fields)
            => new AuditLogStore().AppendInTransaction(connection,
                new AuditEvent("configuration_decision", "synthetic-writer", "bot", "synthetic_configuration", "cfg-0.3.2"));
        internal override void AppendDeviceRuntimeAudit(SqliteConnection connection, DeviceKey key)
            => DeviceRuntimeAudit?.Invoke(connection, key);
    }

    private sealed class SyntheticClock : IClock
    {
        internal long WallSeconds = 1900000000;
        internal long Monotonic;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(WallSeconds);
        public DateTimeOffset UtcNow => Now;
        public DateTime LocalDateTime => Now.DateTime;
        public long TickCount => Monotonic;
        internal void AdvanceSeconds(long seconds) { WallSeconds += seconds; Monotonic += seconds * 1000; }
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.Delay(millisecondsDelay, ct);
    }

    public static async Task QueryReuseAsync(Action<bool, string> report)
    {
        var jargon = new JargonStore();
        foreach (var entry in new[]
        {
            new JargonEntry { Scope = "synthetic:reuse", Phrase = "synthetic-one", Meaning = "synthetic-meaning", Status = JargonStatus.Confirmed },
            new JargonEntry { Scope = "synthetic:reuse", Phrase = "synthetic-two", Meaning = "synthetic-meaning", Status = JargonStatus.Pending },
            new JargonEntry { Scope = "global", Phrase = "synthetic-global", Meaning = "synthetic-meaning", Status = JargonStatus.Manual }
        }) await jargon.UpsertAsync(entry);
        report((await jargon.GetAsync("synthetic:reuse", "synthetic-one"))?.Status == JargonStatus.Confirmed,
            "configuration query reuse preserves Jargon exact lookup mapping");
        report((await jargon.ListByScopeAsync("synthetic:reuse")).Count == 2
            && (await jargon.ListByScopeAsync("synthetic:reuse", JargonStatus.Confirmed)).Count == 1,
            "configuration query reuse preserves Jargon scoped and status predicates");
        report((await jargon.ListConfirmedForPromptAsync("synthetic:reuse")).Count == 2
            && (await jargon.ListConfirmedForPromptAsync("synthetic:reuse", 1)).Count == 1,
            "configuration query reuse preserves Jargon global union and limits");
        var prompts = new PromptTemplateStore();
        var v1 = await prompts.SaveVersionAsync("synthetic-reuse-prompt", "synthetic-initial");
        var v2 = await prompts.SaveVersionAsync("synthetic-reuse-prompt", "synthetic-next");
        report((await prompts.ListVersionsAsync("synthetic-reuse-prompt")).Count == 2
            && (await prompts.GetVersionAsync("synthetic-reuse-prompt", v1))?.Content == "synthetic-initial"
            && (await prompts.GetActiveVersionAsync("synthetic-reuse-prompt"))?.VersionId == v2,
            "configuration query reuse preserves Prompt snapshots/version/active mapping");
        await prompts.ActivateVersionAsync("synthetic-reuse-prompt", v1);
        report((await prompts.GetActiveVersionAsync("synthetic-reuse-prompt"))?.VersionId == v1
            && (await prompts.ListVersionsAsync("synthetic-reuse-prompt")).Count(s => s.IsActive) == 1,
            "configuration query reuse preserves one active version per key");
        var episodes = new EpisodeStore();
        await episodes.InsertAsync(new EpisodeEntry { Scope = "synthetic:reuse", Title = "synthetic-title",
            Summary = "synthetic-summary", Importance = 3 });
        report((await episodes.ListRecentByScopeAsync("synthetic:reuse")).Count == 1
            && (await episodes.SearchAsync("synthetic:reuse", "synthetic-title")).Count == 1
            && (await episodes.SearchAsync("synthetic:other", "synthetic-title")).Count == 0,
            "configuration query reuse preserves Episode scope/search projection");
        var conversations = new ConversationStore();
        var record = new ConversationRecord { Id = "synthetic-reuse-conversation", SourceKey = "local:synthetic:reuse",
            Name = "synthetic-name", NextSeq = 2,
            Messages = new() { new() { Text = "synthetic-message", Seq = 1, TimeUnix = 1000 } } };
        conversations.RequestSave(new[] { record });
        conversations.DeleteMessages(record.SourceKey);
        report(AppDatabase.Scalar<long>("SELECT COUNT(*) FROM messages WHERE source_key='local:synthetic:reuse'") == 0
            && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM conversations WHERE source_key='local:synthetic:reuse'") == 1,
            "configuration query reuse preserves clear messages without deleting conversation");
        conversations.RequestSave(new[] { record }); conversations.DeleteConversation(record.SourceKey);
        report(AppDatabase.Scalar<long>("SELECT COUNT(*) FROM messages WHERE source_key='local:synthetic:reuse'") == 0
            && AppDatabase.Scalar<long>("SELECT COUNT(*) FROM conversations WHERE source_key='local:synthetic:reuse'") == 0,
            "configuration query reuse preserves complete conversation deletion");
        var profiles = new MemberProfileStore();
        var now = DateTimeOffset.FromUnixTimeSeconds(1900000000);
        for (var seq = 1; seq <= 3; seq++) profiles.Append("10001", "synthetic-name", "synthetic-line-" + seq,
            now, "synthetic-group", 10001, seq);
        profiles.ApplySummary("10001", "group:10001", "synthetic-summary", 2, 2);
        var candidates = profiles.FindSummarizable(1, 10);
        report(candidates.Any(c => c.Uid == "10001" && c.ThroughSeq == 3 && c.NewMessages.Count == 1),
            "configuration query reuse preserves summary folded boundary in candidate selection");
        var profile = profiles.GetProfileSummary("10001", 10001);
        report(profile.Contains("synthetic-line-3", StringComparison.Ordinal)
            && !profile.Contains("synthetic-line-1", StringComparison.Ordinal),
            "configuration query reuse preserves folded exclusion in profile projection");
        report(candidates.Any(c => c.Uid == "10001" && c.Scope == "group:10001"
                && c.ExistingSummary == "synthetic-summary" && c.FoldedCount == 3),
            "configuration row reuse preserves summary text and accumulated folded count");
        profiles.Append("10002", "synthetic-empty-summary", "synthetic-unfolded", now, "synthetic-group", 10002, 1);
        var absent = profiles.FindSummarizable(1, 20).Single(c => c.Uid == "10002");
        report(absent.ExistingSummary == "" && absent.FoldedCount == 1 && absent.ThroughSeq == 1,
            "configuration row reuse preserves absent-row defaults");
        profiles.Append("10001", "synthetic-name", "synthetic-other-line-1", now, "synthetic-other-group", 10002, 1);
        profiles.Append("10001", "synthetic-name", "synthetic-other-line-2", now, "synthetic-other-group", 10002, 2);
        profiles.ApplySummary("10001", "group:10002", "synthetic-other-summary", 1, 7);
        var other = profiles.FindSummarizable(1, 20).Single(c => c.Uid == "10001" && c.Scope == "group:10002");
        report(other.ExistingSummary == "synthetic-other-summary" && other.FoldedCount == 8
                && other.NewMessages.SequenceEqual(new[] { "synthetic-other-line-2" }),
            "configuration row reuse isolates summary facts by uid and scope");
        var scoped = profiles.GetProfileSummary("10001", 10001);
        var all = profiles.GetProfileSummary("10001", 10001, allScopes: true);
        report(!scoped.Contains("synthetic-other-summary", StringComparison.Ordinal)
                && all.Contains("synthetic-other-summary", StringComparison.Ordinal)
                && all.Contains("synthetic-summary", StringComparison.Ordinal),
            "configuration projection reuse preserves scoped versus all-scope predicates");
        profiles.SetProfileOverride("10001", "group:10001", "synthetic-override");
        var overridden = profiles.GetProfileSummary("10001", 10001, limit: 0);
        report(overridden.Contains("synthetic-override", StringComparison.Ordinal)
                && !overridden.Contains("synthetic-summary", StringComparison.Ordinal),
            "configuration projection reuse preserves manual override precedence");
        for (var seq = 1; seq <= 65; seq++)
            profiles.Append("10003", "synthetic-batch", "synthetic-batch-" + seq, now, "synthetic-group", 10003, seq);
        var limited = profiles.FindSummarizable(1, 20).Single(c => c.Uid == "10003");
        report(limited.NewMessages.Count == 60 && limited.ThroughSeq == 60 && limited.FoldedCount == 60
                && limited.NewMessages[0] == "synthetic-batch-1" && limited.NewMessages[^1] == "synthetic-batch-60",
            "configuration row reuse preserves oldest-first summary batch limit");
        profiles.Append("10004", "synthetic-backfill", "synthetic-backfill-old", now, "synthetic-group", 10004, -2);
        profiles.Append("10004", "synthetic-backfill", "synthetic-backfill-new", now, "synthetic-group", 10004, -1);
        var backfill = profiles.FindSummarizable(1, 20).Single(c => c.Uid == "10004");
        report(backfill.NewMessages.Count == 2 && backfill.ThroughSeq == -1,
            "configuration row reuse keeps long-min absent boundary for negative backfill sequences");
    }
}

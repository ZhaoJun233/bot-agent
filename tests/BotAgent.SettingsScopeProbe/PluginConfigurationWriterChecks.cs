extern alias StorageModule;

using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

internal static class PluginConfigurationWriterChecks
{
    internal static void RunStartup(Action<string, Action> check)
    {
        var fixture = new Fixture(activate: false);
        var store = new PluginSettingsStore();
        var manifest = Manifest("probe.configuration.startup");
        var values = JsonSerializer.SerializeToElement(new { enabled = false });
        _ = store.ReadManifest(manifest);
        var databaseType = typeof(StorageModule::BotAgent.Adapters.Persistence.AppDatabase);
        var configurationType = typeof(StorageModule::BotAgent.Storage.ConfigurationStore);
        var databaseGate = databaseType.GetField("WriteGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var activationGate = configurationType.GetField("ActivationGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        using var started = new ManualResetEventSlim(false);
        PluginSettingsReceipt? first = null;
        Exception? error = null;
        var writer = new Thread(() =>
        {
            started.Set();
            try { first = store.Save(manifest, 0, "startup-write", values, "synthetic-admin"); }
            catch (Exception exception) { error = exception; }
        }) { IsBackground = true };
        var activationOvertookWrite = false;
        Monitor.Enter(databaseGate);
        try
        {
            writer.Start();
            Require(started.Wait(TimeSpan.FromSeconds(5)), "bootstrap writer did not start");
            Require(SpinWait.SpinUntil(() => (writer.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)), "bootstrap writer did not reach the held database lock");
            // Do not wait for the activation gate while holding the database lock. An admitted
            // bootstrap writer must already own it; otherwise activation can overtake its commit.
            if (Monitor.TryEnter(activationGate))
            {
                try { fixture.Activate(); activationOvertookWrite = true; }
                finally { Monitor.Exit(activationGate); }
            }
        }
        finally
        {
            Monitor.Exit(databaseGate);
            Require(writer.Join(TimeSpan.FromSeconds(10)), "bootstrap writer did not finish");
        }
        if (error is not null) throw new InvalidOperationException("bootstrap save failed", error);
        if (!activationOvertookWrite) fixture.Activate();
        check("configuration activation cannot overtake an admitted bootstrap package write", () =>
            Require(!activationOvertookWrite, "activation overtook bootstrap admission; the package committed after activation without advancing its shared version"));
        check("bootstrap settings and original receipt survive configuration activation", () =>
        {
            var before = fixture.Read().Version;
            var beforeAudit = new AuditLogStore().Verify().CheckedCount;
            Require(first is { Revision: 1, RestartRequired: true }
                && store.ReadManifest(manifest).Revision == 1
                && !store.ReadManifest(manifest).Values["enabled"].GetBoolean(), "bootstrap settings were lost");
            Require(store.Save(manifest, 0, "startup-write", values, "synthetic-admin") == first
                && fixture.Read().Version == before && new AuditLogStore().Verify().CheckedCount == beforeAudit,
                "bootstrap receipt replay changed committed facts");
        });
        check("first active package update joins the shared version after bootstrap", () =>
        {
            var before = fixture.Read().Version;
            var receipt = store.Save(manifest, 1, "active-write",
                JsonSerializer.SerializeToElement(new { enabled = true }), "synthetic-admin");
            Require(receipt.Revision == 2 && fixture.Read().Version.Value == before.Value + 1,
                "active package update missed the shared configuration version");
        });
    }

    internal static void Run(Action<string, Action> check)
    {
        var fixture = new Fixture();
        var store = new PluginSettingsStore();
        var manifest = Manifest("probe.configuration.writer");
        var firstValues = JsonSerializer.SerializeToElement(new { enabled = false });
        var original = fixture.Read();
        var audit = new AuditLogStore();
        var auditBefore = audit.Verify().CheckedCount;
        var first = store.Save(manifest, 0, "first-write", firstValues, "synthetic-admin");

        check("plugin settings commit advances the shared configuration version once", () =>
        {
            Require(first.Revision == 1 && first.RestartRequired, "legacy package receipt changed");
            Require(fixture.Read().Version.Value == original.Version.Value + 1, "plugin commit bypassed configuration version");
            Require(audit.Verify() is { Valid: true } result && result.CheckedCount == auditBefore + 1,
                "plugin settings commit did not append one transactional audit");
        });
        check("plugin settings original-key replay does not advance version or audit", () =>
        {
            var before = fixture.Read().Version;
            var beforeAudit = audit.Verify().CheckedCount;
            var replay = store.Save(manifest, 0, "first-write", firstValues, "synthetic-admin");
            Require(replay == first, "original package receipt changed");
            Require(fixture.Read().Version == before && audit.Verify().CheckedCount == beforeAudit,
                "replay committed new configuration facts");
        });
        check("configuration caller with a version preceding plugin settings is rejected", () =>
        {
            var result = fixture.Save(original, 11);
            Require(result.Code == ConfigurationResultCode.VersionConflict, "stale cross-owner version was accepted");
        });
        check("invalid plugin settings have no value version or audit side effects", () =>
        {
            var before = fixture.Read().Version;
            var beforeAudit = audit.Verify().CheckedCount;
            Expect(() => store.Save(manifest, 1, "invalid-write",
                JsonSerializer.SerializeToElement(new { enabled = "invalid" }), "synthetic-admin"),
                PluginSettingsContract.InvalidValues);
            Require(store.ReadManifest(manifest).Revision == 1 && fixture.Read().Version == before
                && audit.Verify().CheckedCount == beforeAudit, "validation failure changed committed state");
        });
        check("plugin settings wait for configuration publication and derived-state completion", () =>
        {
            using var published = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var packageStarted = new ManualResetEventSlim(false);
            var rootWrite = Task.Run(() => fixture.Box.ApplyPersisted(next => next.GroupInterjectionProbability = 7,
                _ => Require(fixture.Save(fixture.Read(), 7).Code == ConfigurationResultCode.Ok, "root commit failed"),
                _ => { published.Set(); Require(release.Wait(TimeSpan.FromSeconds(10)), "publication release timed out"); }));
            Task<PluginSettingsReceipt>? packageWrite = null;
            try
            {
                Require(published.Wait(TimeSpan.FromSeconds(5)), "configuration publication did not start");
                packageWrite = Task.Run(() =>
                {
                    packageStarted.Set();
                    return store.Save(manifest, 1, "after-publication",
                        JsonSerializer.SerializeToElement(new { enabled = true }), "synthetic-admin");
                });
                Require(packageStarted.Wait(TimeSpan.FromSeconds(5)), "package writer did not start");
                Require(!packageWrite.Wait(TimeSpan.FromMilliseconds(500)), "package committed before prior publication finished");
            }
            finally
            {
                release.Set();
                Require(rootWrite.Wait(TimeSpan.FromSeconds(10)), "root writer did not finish");
                if (packageWrite is not null) Require(packageWrite.Wait(TimeSpan.FromSeconds(10)), "package writer did not finish");
            }
            Require(fixture.Box.Current.GroupInterjectionProbability == 7 && store.ReadManifest(manifest).Values["enabled"].GetBoolean(),
                "serialized commits lost a value");
        });
        check("old package receipt survives a newer committed revision", () =>
        {
            var before = fixture.Read().Version;
            var beforeAudit = audit.Verify().CheckedCount;
            Require(store.Save(manifest, 0, "first-write", firstValues, "synthetic-admin") == first,
                "old receipt was replaced after a new commit");
            Require(fixture.Read().Version == before && audit.Verify().CheckedCount == beforeAudit,
                "old receipt replay wrote a new version");
        });
        check("configuration and package settings maintain one committed version sequence", () =>
            Require(fixture.Read().Version.Value == original.Version.Value + 3,
                "root and package commits do not share a version sequence"));

        check("nested database settings writes reject before waiting for the publication gate", () =>
        {
            Task<string?>? nested = null;
            var before = fixture.Read().Version;
            try
            {
                fixture.Box.ApplyPersisted(_ => { }, _ => { }, _ =>
                {
                    nested = Task.Run(() =>
                    {
                        string? failure = null;
                        StorageModule::BotAgent.Adapters.Persistence.AppDatabase.Write(_ =>
                        {
                            try { store.Save(manifest, 2, "nested-write", firstValues, "synthetic-admin"); }
                            catch (PluginSettingsException exception) { failure = exception.Code; }
                        });
                        return failure;
                    });
                    Require(nested.Wait(TimeSpan.FromSeconds(2)), "nested write waited on the publication gate");
                    Require(nested.Result == "plugin_settings_unavailable", "nested write was not rejected");
                });
            }
            finally
            {
                if (nested is not null) Require(nested.Wait(TimeSpan.FromSeconds(5)), "nested writer did not exit after gate release");
            }
            Require(fixture.Read().Version == before && store.ReadManifest(manifest).Revision == 2,
                "nested write changed committed state");
        });
        check("package settings concurrent compare-and-save commits exactly one version", () =>
        {
            var target = Manifest("probe.configuration.concurrent");
            var before = fixture.Read().Version;
            var beforeAudit = audit.Verify().CheckedCount;
            using var start = new ManualResetEventSlim(false);
            var writers = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    store.Save(target, 0, "concurrent-" + index,
                        JsonSerializer.SerializeToElement(new { enabled = index == 0 }), "synthetic-admin");
                    return "committed";
                }
                catch (PluginSettingsException exception) { return exception.Code; }
            })).ToArray();
            start.Set();
            Require(Task.WaitAll(writers, TimeSpan.FromSeconds(10)), "concurrent package writers timed out");
            Require(writers.Count(writer => writer.Result == "committed") == 1
                && writers.Count(writer => writer.Result == "plugin_settings_revision_conflict") == 1,
                "concurrent CAS accepted or rejected both writers");
            Require(fixture.Read().Version.Value == before.Value + 1 && audit.Verify().CheckedCount == beforeAudit + 1,
                "concurrent save created duplicate versions or audit records");
        });
        check("package secret receipt version and audit roll back together after audit failure", () =>
        {
            var target = SecretManifest("probe.configuration.audit-rollback");
            var values = JsonSerializer.SerializeToElement(new { enabled = false, token = "synthetic-token" });
            var before = fixture.Read().Version;
            var beforeAudit = audit.Verify().CheckedCount;
            ExecuteSql("CREATE TRIGGER probe_package_audit_failure BEFORE INSERT ON security_audit_log "
                + "WHEN NEW.event_type='configuration_package_settings' BEGIN SELECT RAISE(ABORT, 'synthetic audit failure'); END;");
            try
            {
                Expect(() => store.Save(target, 0, "audit-rollback", values, "synthetic-admin"), "plugin_settings_unavailable");
                var retained = store.ReadManifest(target);
                Require(retained.Revision == 0 && retained.ConfiguredSecrets.Count == 0
                    && fixture.Read().Version == before && audit.Verify().CheckedCount == beforeAudit,
                    "audit failure retained a partial package commit");
            }
            finally { ExecuteSql("DROP TRIGGER probe_package_audit_failure;"); }
            var committed = store.Save(target, 0, "audit-rollback", values, "synthetic-admin");
            Require(committed.Revision == 1 && fixture.Read().Version.Value == before.Value + 1
                && store.ReadManifest(target).ConfiguredSecrets.SequenceEqual(["token"])
                && audit.Verify().CheckedCount == beforeAudit + 1, "original-key retry did not commit once after rollback");
        });
        check("exhausted global configuration version preserves package bytes secrets and original receipts", () =>
        {
            var target = SecretManifest("probe.configuration.version-rollback");
            var before = fixture.Read().Version;
            var beforeAudit = audit.Verify().CheckedCount;
            ExecuteSql("UPDATE configuration_state SET version=" + long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) + " WHERE id=1;");
            try
            {
                Expect(() => store.Save(target, 0, "version-rollback",
                    JsonSerializer.SerializeToElement(new { enabled = false, token = "synthetic-token" }), "synthetic-admin"),
                    "plugin_settings_revision_exhausted");
                Require(store.ReadManifest(target).Revision == 0 && store.ReadManifest(target).ConfiguredSecrets.Count == 0
                    && audit.Verify().CheckedCount == beforeAudit && fixture.Read().Version.Value == long.MaxValue,
                    "exhausted version partially committed settings");
                Require(store.Save(manifest, 0, "first-write", firstValues, "synthetic-admin") == first,
                    "global exhaustion blocked an already committed original receipt");
            }
            finally
            {
                ExecuteSql("UPDATE configuration_state SET version=" + before.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " WHERE id=1;");
            }
            Require(store.Save(target, 0, "version-rollback",
                JsonSerializer.SerializeToElement(new { enabled = false, token = "synthetic-token" }), "synthetic-admin").Revision == 1,
                "rollback left a phantom receipt or secret signing key");
        });
        check("new package settings commit invalidates a previously prepared adoption stamp", () =>
        {
            store.ResolveRuntimeValues(manifest, out var prepared);
            store.Save(manifest, prepared, "invalidate-preparation", firstValues, "synthetic-admin");
            var before = fixture.Read().Version;
            Require(!store.MarkApplied(manifest, prepared) && store.ReadManifest(manifest).RestartRequired,
                "stale preparation was marked applied");
            Require(fixture.Read().Version == before, "adoption acknowledgement changed the configuration version");
        });
    }

    private static PluginPackageManifest Manifest(string id) => new(id, "1.0.0", "probe.dll", "Probe.Plugin",
        new string('a', 64), "https://example.com/probe.zip",
        Settings: [new PluginSettingDefinition("enabled", "Enabled", "boolean", "true")]);

    private static PluginPackageManifest SecretManifest(string id) => Manifest(id) with
    {
        Settings = [new PluginSettingDefinition("enabled", "Enabled", "boolean", "true"),
            new PluginSettingDefinition("token", "Token", "secret")]
    };

    private static void ExecuteSql(string sql)
        => StorageModule::BotAgent.Adapters.Persistence.AppDatabase.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect(Action action, string code)
    {
        try { action(); }
        catch (PluginSettingsException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("expected plugin settings failure: " + code);
    }

    private sealed class Fixture
    {
        internal readonly SettingsBox Box;
        private IConfigurationChanges? _configuration;
        private readonly object _participant;
        private readonly ProbeClock _clock = new();
        private readonly ConfigurationCaller _caller = new("synthetic", "configuration-probe", "bot",
            ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);

        internal Fixture(bool activate = true)
        {
            foreach (var name in new[] { "BOTAGENT_MODEL", "QQCHAT_MODEL", "OPENAI_MODEL",
                "BOTAGENT_BASE_URL", "QQCHAT_BASE_URL", "OPENAI_BASE_URL" })
                Environment.SetEnvironmentVariable(name, null);
            var initial = new AppSettings { GroupInterjectionProbability = 0, Model = "synthetic-main", ModelBaseUrl = "https://example.com/v1" };
            new SettingsStore().Save(initial);
            Box = new(initial);
            var participant = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)!;
            var constructor = participant.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var sourceType = constructor.GetParameters()[2].ParameterType;
            var parameters = sourceType.GetMethod("Invoke")!.GetParameters()
                .Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
            var sources = Expression.Lambda(sourceType, Expression.Constant(Enum.GetValues<WritableSecretSlot>()
                .Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray()), parameters).Compile();
            Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) => new([], [], []);
            _participant = constructor.Invoke([Box,
                (Func<ConfigurationCaller, ConfigurationAccess, bool>)((caller, access) =>
                    caller.AuthorityId == "synthetic" && (caller.Access & access) == access),
                sources, registered, (Func<ConfigurationFieldId, ConfigurationValueSource>)(_ => ConfigurationValueSource.UserConfiguration),
                (Func<ConfigurationFieldId, FieldEffect>)(_ => FieldEffect.Hot), false]);
            if (activate) Activate();
        }

        internal void Activate() => _configuration = (IConfigurationChanges)typeof(StorageModule::BotAgent.Storage.ConfigurationStore)
            .GetMethod("Activate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [_clock, _participant])!;

        internal ConfigurationReadResult Read() => _configuration!.ReadAsync(_caller, ConfigurationReadKind.Editable)
            .AsTask().GetAwaiter().GetResult();

        internal ConfigurationSaveResult Save(ConfigurationReadResult header, int value)
        {
            var changes = new ConfigurationPatch(new BehaviorChanges { GroupInterjectionProbability = FieldEdit<int>.Set(value) },
                null, null, null, null, null, null, null);
            return _configuration!.SaveAsync(_caller, new(header.ContractRevision, header.Version,
                new(header.Generation, header.ServerUnixSeconds, Guid.NewGuid()), changes, [])).AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class ProbeClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public DateTimeOffset UtcNow => Now;
        public DateTime LocalDateTime => Now.LocalDateTime;
        public long TickCount => Environment.TickCount64;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.Delay(millisecondsDelay, ct);
    }
}

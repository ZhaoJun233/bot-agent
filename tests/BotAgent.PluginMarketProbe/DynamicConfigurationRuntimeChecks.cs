extern alias StorageModule;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Panel;
using BotAgent.Services.Net;
using BotAgent.Services.Plugins;
using BotAgent.Services.Resilience;
using Microsoft.Data.Sqlite;

internal partial class Program
{
    internal static async Task<int> RunDynamicConfigurationRuntimeChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        void Refuse(Action action, string code)
        {
            try { action(); }
            catch (PluginSettingsException error) when (error.Code == code) { checks++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        const string id = "preset.feature.websearch";
        var oldDirectory = Environment.GetEnvironmentVariable("BOTAGENT_IT_PREVIOUS_CONFIGURATION_RUNTIME")
            ?? throw new InvalidOperationException("previous_configuration_reader_required");
        var oldIndex = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(oldDirectory, "plugin-distribution", "index.json")))!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var oldManifest = oldIndex["releases"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == id)!
            .Deserialize<PluginPackageManifest>(options)!;
        var definition = ResearchSettings.Definition;
        var manifest = oldManifest with { Version = "1.1.0", Sha256 = new string('b', 64),
            DownloadUrl = "https://example.com/research.zip", Settings = definition.Settings, Configuration = definition.Configuration };
        var approvalBytes = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = new[] { oldManifest, manifest } }, options);
        var distribution = TrustedPluginDistribution.Read(approvalBytes, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(approvalBytes)));
        var store = new PluginSettingsStore(distribution);
        var empty = JsonSerializer.SerializeToElement(new { });
        var oldReceipt = store.Save(oldManifest, 0, "old-empty", empty, "synthetic-admin");
        var oldState = DynamicMetadata("plugin_settings:" + id)!;
        Check((oldManifest.Settings is null or { Count: 0 }) && oldManifest.Configuration is null,
            "real prior distribution has the empty owner schema");
        // Production captures preset intent before activating the configuration writer.
        typeof(StorageModule::BotAgent.Storage.PluginStore).GetMethod("PreservePresetIntent", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new[] { "music", "voice", "stickers", "poke", "vibes", "profiles", "websearch", "token-quota" }
                .ToDictionary(key => "preset.feature." + key, _ => true)]);
        var fixture = new DynamicConfigurationFixture();
        var before = store.ReadManifest(manifest);
        Check(before.Values["linkPreviewMax"].GetInt32() == 4 && DynamicMetadata("plugin_settings:" + id) == oldState,
            "empty legacy schema admission is read-only and uses prior configuration facts");
        var receipt = store.Save(manifest, before.Revision, "adopt-owner", JsonSerializer.SerializeToElement(new { linkPreviewMax = 2 }), "synthetic-admin");
        var adopted = DynamicMetadata("plugin_settings:" + id)!;
        Check(JsonDocument.Parse(adopted).RootElement.GetProperty("PreservedLegacyState").GetString() == oldState,
            "first owner commit preserves exact original empty metadata bytes");
        Check(store.Save(oldManifest, 0, "old-empty", empty, "synthetic-admin") == oldReceipt,
            "real prior manifest still replays its unchanged original receipt after adoption");
        Refuse(() => store.Save(oldManifest, oldReceipt.Revision, "old-new-write", empty, "synthetic-admin"), "plugin_settings_schema_mismatch");
        var stamp = store.ReadPreservationStamp(manifest, manifest with { Version = "1.2.0" });
        var legacy = fixture.Save(new BehaviorChanges { LinkPreviewMax = FieldEdit<int>.Set(5) });
        Check(legacy.Code == ConfigurationResultCode.Ok && store.ReadPreservationStamp(manifest, manifest with { Version = "1.2.0" }) != stamp,
            "legacy writes invalidate current and target lifecycle admission");
        Check(DynamicMetadata("plugin_settings:" + id) == adopted && store.ReadPreservationStamp(id).Length == 64,
            "opaque uninstall metadata stays independent of legacy-bound writes");
        var order = manifest with { Configuration = definition.Configuration with { Bindings = definition.Configuration.Bindings.Reverse().ToArray() } };
        Check(store.ReadPreservationStamp(manifest, manifest) == store.ReadPreservationStamp(order, order),
            "binding order is semantically equivalent during lifecycle admission");

        DynamicMetadata("plugin_settings:" + id, adopted.Replace("\"Values\":{}", "\"Values\":{\"synthetic\":1}"));
        Refuse(() => store.ReadManifest(manifest), "plugin_settings_corrupt");
        DynamicMetadata("plugin_settings:" + id, adopted);
        var oldNonempty = JsonSerializer.Serialize(new { Format = 1, Revision = 1, SchemaHash = new string('c', 64), Values = new { synthetic = 1 } });
        DynamicMetadata("plugin_settings:" + id, oldNonempty);
        Refuse(() => store.ReadManifest(manifest), "plugin_settings_schema_mismatch");
        Check(DynamicMetadata("plugin_settings:" + id) == oldNonempty, "nonempty old overrides are never silently migrated or discarded");
        DynamicMetadata("plugin_settings:" + id, adopted);

        var rollbackVersion = fixture.Read().Version;
        var rollbackSettings = new SettingsStore().Load().LinkPreviewMax;
        var rollbackAudit = new AuditLogStore().Verify().CheckedCount;
        DynamicSql("CREATE TRIGGER dynamic_owner_audit_failure BEFORE INSERT ON security_audit_log "
            + "WHEN NEW.event_type='configuration_package_settings' BEGIN SELECT RAISE(ABORT,'synthetic'); END;");
        try
        {
            Refuse(() => store.Save(manifest, rollbackVersion.Value, "rollback-key", JsonSerializer.SerializeToElement(new { linkPreviewMax = 1 }), "synthetic-admin"), "plugin_settings_unavailable");
            Check(fixture.Read().Version == rollbackVersion && DynamicMetadata("plugin_settings:" + id) == adopted
                && new SettingsStore().Load().LinkPreviewMax == rollbackSettings && fixture.Box.Current.LinkPreviewMax == 2
                && new AuditLogStore().Verify().CheckedCount == rollbackAudit, "audit failure rolls back carrier modes version receipt and publication together");
        }
        finally { DynamicSql("DROP TRIGGER dynamic_owner_audit_failure;"); }
        var rolled = store.Save(manifest, rollbackVersion.Value, "rollback-key", JsonSerializer.SerializeToElement(new { linkPreviewMax = 1 }), "synthetic-admin");
        Check(rolled.Revision == rollbackVersion.Value + 1 && fixture.Box.Current.LinkPreviewMax == 1,
            "same operation key commits once after a complete rollback");

        var concurrentVersion = store.ReadManifest(manifest).Revision;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            try { return store.Save(manifest, concurrentVersion, "concurrent-" + index,
                JsonSerializer.SerializeToElement(new { linkPreviewMax = index + 2 }), "synthetic-admin").Revision; }
            catch (PluginSettingsException error) when (error.Code == "plugin_settings_revision_conflict") { return -1L; }
        })));
        Check(outcomes.Count(value => value == concurrentVersion + 1) == 1 && outcomes.Count(value => value == -1) == 1,
            "concurrent bound writers share one version and only one wins");
        var trim = store.Save(manifest, store.ReadManifest(manifest).Revision, "trim-sources",
            JsonSerializer.SerializeToElement(new { webSearchSources = "  synthetic|https://example.com/search?q={q}  " }), "synthetic-admin");
        Check(new SettingsStore().Load().WebSearchSources == "synthetic|https://example.com/search?q={q}"
            && fixture.Box.Current.WebSearchSources == new SettingsStore().Load().WebSearchSources, "owner text trim is shared by runtime and legacy carrier");
        var reset = store.Save(manifest, trim.Revision, "reset-default",
            JsonSerializer.SerializeToElement(new { linkPreviewMax = (int?)null }), "synthetic-admin");
        Check(store.ReadManifest(manifest).Values["linkPreviewMax"].GetInt32() == ResearchSettings.Defaults.LinkPreviewMax
            && fixture.Box.Current.LinkPreviewMax == ResearchSettings.Defaults.LinkPreviewMax, "reset uses the owner default rather than first environment seed");

        // A malformed trusted runtime snapshot makes publication fail after the durable
        // transaction. Keep the old reference and prove original-key replay never applies it.
        var current = fixture.Box.Current;
        var failureVersion = reset.Revision;
        var failureValues = JsonSerializer.SerializeToElement(new { linkPreviewMax = 4 });
        var databaseGate = typeof(StorageModule::BotAgent.Adapters.Persistence.AppDatabase)
            .GetField("WriteGate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var started = new ManualResetEventSlim(false);
        Exception? applyError = null;
        var writer = new Thread(() =>
        {
            started.Set();
            try { store.Save(manifest, failureVersion, "apply-failure", failureValues, "synthetic-admin"); }
            catch (Exception error) { applyError = error; }
        }) { IsBackground = true };
        try
        {
            // Test-only corruption of the trusted participant; package code and real
            // application callbacks are not added to the storage commit boundary.
            var configurationType = typeof(StorageModule::BotAgent.Storage.ConfigurationStore);
            var active = configurationType.GetField("_active", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var participantField = configurationType.GetField("_participant", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var participant = participantField.GetValue(active)!;
            var runtimeField = participant.GetType().GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Monitor.Enter(databaseGate);
            try
            {
                writer.Start();
                Check(started.Wait(TimeSpan.FromSeconds(5)) && SpinWait.SpinUntil(
                    () => (writer.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)),
                    "application fault waits until the writer has entered the trusted coordinator");
                runtimeField.SetValue(participant, null);
            }
            finally
            {
                Monitor.Exit(databaseGate);
                try { Check(writer.Join(TimeSpan.FromSeconds(10)), "application fault writer finishes with a durable response"); }
                finally { runtimeField.SetValue(participant, fixture.Box); }
            }
            Check(applyError is PluginSettingsException { Code: "plugin_settings_application_failed" },
                "application failure returns a safe committed-failure code");
            Check(new SettingsStore().Load().LinkPreviewMax == 4 && fixture.Box.Current.LinkPreviewMax == current.LinkPreviewMax
                && store.ReadManifest(manifest).RestartRequired, "application failure keeps durable desired values distinct from runtime");
            var replay = store.Save(manifest, failureVersion, "apply-failure", failureValues, "synthetic-admin");
            Check(replay.Revision == failureVersion + 1 && !replay.RestartRequired
                && fixture.Box.Current.LinkPreviewMax == current.LinkPreviewMax, "committed original receipt replays without publishing a failed application");
        }
        finally { Check(ReferenceEquals(fixture.Box.Current, current), "failed publication retains the complete original runtime snapshot"); }

        checks += await RunDynamicDefaultReaderChecks(root, fixture, manifest, store);
        checks += await RunDynamicDeclarationReaderChecks(root, manifest, distribution, oldDirectory);
        Check(receipt.Revision > 0, "original owner receipt remains structurally valid");
        return checks;
    }

    private static string? DynamicMetadata(string key, string? write = null)
    {
        using var connection = StorageModule::BotAgent.Adapters.Persistence.AppDatabase.Open();
        var type = typeof(StorageModule::BotAgent.Adapters.Persistence.AppDatabase);
        if (write is not null) type.GetMethod("SetMetaInTransaction", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [connection, key, write]);
        return (string?)type.GetMethod("GetMetaInTransaction", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [connection, key]);
    }

    private static void DynamicSql(string sql)
    {
        using var connection = StorageModule::BotAgent.Adapters.Persistence.AppDatabase.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}

extern alias StorageModule;

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using Microsoft.Data.Sqlite;
using PluginStore = StorageModule::BotAgent.Storage.PluginStore;

internal partial class Program
{
    private static IReadOnlyDictionary<string, bool> PreserveIntent(IReadOnlyDictionary<string, bool> defaults)
    {
        try
        {
            return (IReadOnlyDictionary<string, bool>)typeof(PluginStore)
                .GetMethod("PreservePresetIntent", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [defaults])!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    internal static async Task<int> RunPresetIntentChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        const string stateKey = "plugins_state", intentKey = "plugins_preset_intent_v1";
        var priorState = AppDatabase.GetMeta(stateKey);
        var priorIntent = AppDatabase.GetMeta(intentKey);
        void Restore(string key, string? value)
        {
            if (value is not null) AppDatabase.SetMeta(key, value);
            else AppDatabase.Write(c => AppDatabase.Exec(c, "DELETE FROM meta WHERE key=$key", ("$key", key)));
        }
        void Reject(Action action, Func<Exception, bool> expected, string label)
        {
            try { action(); } catch (Exception error) when (expected(error)) { checks++; return; }
            throw new InvalidOperationException(label);
        }
        try
        {
            Restore(stateKey, null); Restore(intentKey, null);
            AppDatabase.SetMeta(stateKey, "{\"preset.explicit.on\":true,\"preset.explicit.off\":false,\"external.existing\":false}");
            var defaults = new Dictionary<string, bool>
            {
                ["preset.explicit.on"] = false, ["preset.explicit.off"] = true,
                ["preset.implicit.on"] = true, ["preset.implicit.off"] = false
            };
            var initial = AppDatabase.GetMeta(stateKey);
            AppDatabase.Write(c => AppDatabase.Exec(c, "CREATE TRIGGER reject_preset_migration BEFORE INSERT ON meta WHEN NEW.key='plugins_preset_intent_v1' BEGIN SELECT RAISE(ABORT, 'synthetic migration failure'); END"));
            Reject(() => PreserveIntent(defaults), e => e is SqliteException, "migration failure must be visible");
            Check(AppDatabase.GetMeta(stateKey) == initial && AppDatabase.GetMeta(intentKey) is null,
                "intent marker and explicit enablement roll back together");
            AppDatabase.Write(c => AppDatabase.Exec(c, "DROP TRIGGER reject_preset_migration"));
            var migrated = PreserveIntent(defaults);
            Check(migrated["preset.explicit.on"] && !migrated["preset.explicit.off"], "explicit true and false override old defaults");
            Check(migrated["preset.implicit.on"] && !migrated["preset.implicit.off"], "missing state captures old effective default");
            Check(!migrated["external.existing"] && migrated.Count == 5, "unrelated desired state retained");
            var state = AppDatabase.GetMeta(stateKey); var intent = AppDatabase.GetMeta(intentKey);
            _ = PreserveIntent(defaults);
            Check(AppDatabase.GetMeta(stateKey) == state && AppDatabase.GetMeta(intentKey) == intent, "restart migration is byte-stable");
            new PluginStore().SetPluginEnabled("preset.implicit.on", false);
            _ = PreserveIntent(new Dictionary<string, bool>());
            var reinstalled = PreserveIntent(defaults);
            Check(!reinstalled["preset.implicit.on"] && AppDatabase.GetMeta(intentKey) == intent,
                "missing package then reinstall does not re-enable or rewrite captured intent");
            var intact = AppDatabase.GetMeta(stateKey);
            var missing = new Dictionary<string, bool>(reinstalled); missing.Remove("preset.implicit.on");
            var damaged = JsonSerializer.Serialize(missing); AppDatabase.SetMeta(stateKey, damaged);
            Reject(() => PreserveIntent(defaults), e => e is JsonException { Message: "plugin_preset_intent_needs_review" }, "missing migrated state must not default on");
            Check(AppDatabase.GetMeta(stateKey) == damaged && AppDatabase.GetMeta(intentKey) == intent, "damaged intent is preserved for recovery");
            Restore(stateKey, intact); AppDatabase.SetMeta(intentKey, "{broken marker");
            Reject(() => PreserveIntent(defaults), e => e is JsonException, "broken migration record is not silently recreated");
            Check(AppDatabase.GetMeta(intentKey) == "{broken marker", "invalid marker bytes preserved");
            AppDatabase.SetMeta(stateKey, "{\"system.health-reporting\":true,\"system.conversation\":false}");
            var runtimeType = typeof(BotAgent.Adapters.Plugins.PluginPackageService).Assembly.GetType("BotAgent.Host.PluginRuntime")!;
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            await using (var runtime = (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
                .Invoke([Path.Combine(root, "preset-intent-recovery"), (Action<string>)(_ => { })]))
            {
                var registry = (BotAgent.Services.Plugins.PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
                Check(registry.GetAll().Count == 0, "no legacy preset registrations exist before bundle adoption");
                var descriptors = (System.Collections.Immutable.ImmutableArray<BotAgent.Domain.Management.ManagedDescriptor>)runtimeType
                    .GetMethod("Describe", hidden)!.Invoke(runtime, null)!;
                Check(descriptors.All(d => d.ErrorCode == "plugin_preset_intent_needs_review"),
                    "damaged marker stays observable without blocking control construction");
                await (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;
                Check(registry.IsEnabled("system.health-reporting") && !registry.IsEnabled("system.conversation")
                    && registry.GetAll().Count(p => p.IsEnabled) == 1, "damaged marker keeps explicit choices and disables absent choices");
                descriptors = (System.Collections.Immutable.ImmutableArray<BotAgent.Domain.Management.ManagedDescriptor>)runtimeType
                    .GetMethod("Describe", hidden)!.Invoke(runtime, null)!;
                Check(descriptors.All(d => d.ErrorCode == "plugin_preset_intent_needs_review"),
                    "package adoption cannot clear unresolved preset intent diagnostics");
            }
            Check(AppDatabase.GetMeta(intentKey) == "{broken marker", "runtime recovery preserves invalid migration record");
            Restore(stateKey, intact);
            Restore(intentKey, intent);
            Reject(() => PreserveIntent(new Dictionary<string, bool> { ["external.invalid"] = true }),
                e => e is ArgumentException, "only old preset defaults can migrate");
        }
        finally
        {
            AppDatabase.Write(c => AppDatabase.Exec(c, "DROP TRIGGER IF EXISTS reject_preset_migration"));
            Restore(stateKey, priorState); Restore(intentKey, priorIntent);
        }
        return checks;
    }

    internal static int RunPresetIntentWriterProtection()
    {
        var before = AppDatabase.GetMeta("plugins_state");
        var marker = AppDatabase.GetMeta("plugins_preset_intent_v1");
        try { PreserveIntent(new Dictionary<string, bool> { ["preset.after.activation"] = true }); }
        catch (InvalidOperationException error) when (error.Message == "configuration_writer_required")
        {
            if (before != AppDatabase.GetMeta("plugins_state") || marker != AppDatabase.GetMeta("plugins_preset_intent_v1"))
                throw new InvalidOperationException("late migration changed configuration");
            return 2;
        }
        throw new InvalidOperationException("late migration bypassed configuration writer");
    }
}

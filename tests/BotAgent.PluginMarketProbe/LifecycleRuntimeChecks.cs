using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;
using BotAgent.Services.Plugins;

internal partial class Program
{
    private sealed class LifecycleServices(Action<string> record) : IServiceProvider
    { public object? GetService(Type type) => type == typeof(Action<string>) ? record : null; }
    internal static async Task<int> RunLifecycleRuntimeChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var bytes = ReviewZip("probe.dll", await File.ReadAllBytesAsync(typeof(LifecycleRuntimePlugin).Assembly.Location));
        foreach (var enabled in new[] { false, true })
        {
            var packageRoot = Path.Combine(root, "runtime-lifecycle-" + enabled);
            var http = new ReviewPackageHttp { Bytes = bytes }; var packages = new PluginPackageService(packageRoot, http);
            var registry = new PluginManager(_ => { }); var initialized = new List<string>();
            var context = new PluginContext(new LifecycleServices(initialized.Add));
            var first = ReviewManifest(bytes, "probe.lifecycle.runtime", "1.0.0", "probe.dll", typeof(LifecycleRuntimePlugin).FullName!);
            var next = first with { Version = "2.0.0", EntryType = typeof(LifecycleRuntimeV2).FullName! };
            var bad = first with { Version = "3.0.0", EntryType = typeof(LifecycleRuntimeBroken).FullName! };
            var market = new PluginMarketService(packages, new PluginSettingsStore(), new PluginCatalogSource(http), () => DateTimeOffset.UnixEpoch,
                Path.Combine(packageRoot, "operations.json"), desiredStates: () => new Dictionary<string, bool> { [first.Id] = enabled },
                reload: (id, ct) => packages.ApplyAsync(id!, context, registry, enabled, ct: ct));
            async Task<ZbaPluginOperationResult> Switch(string kind, string key, PluginPackageManifest? target = null)
            {
                var revision = (await market.ReadInstalledAsync(default)).Revision;
                var preview = await market.PreviewLifecycleAsync(new(kind, first.Id, revision, target is null ? null : new("json", Manifest: target)), "runtime");
                Check(preview.CanApply, "runtime_preview_" + key);
                var request = new ZbaPluginMutationRequest(preview.TargetManifest!, revision, key, true) { PreviewId = preview.PreviewId };
                return kind == "update" ? await market.UpdateAsync(request, "runtime", default) : await market.RollbackAsync(request, "runtime", default);
            }
            try
            {
                await packages.StageInstallAsync(first); await packages.ApplyAsync(first.Id, context, registry, enabled);
                await Switch("update", "runtime-up-" + enabled, next);
                Check((await packages.GetInstalledAsync()).Single().Active?.Version == "2.0.0" && registry.GetAll().Single().IsEnabled == enabled, "runtime_upgrade_intent");
                await Switch("rollback", "runtime-back-" + enabled);
                Check((await packages.GetInstalledAsync()).Single().Active?.Version == "1.0.0" && registry.GetAll().Single().IsEnabled == enabled, "runtime_rollback_intent");
                Check(enabled ? initialized.SequenceEqual(new[] { "1.0.0", "2.0.0", "1.0.0" }) : initialized.Count == 0, "runtime_disabled_never_initializes");
                if (enabled)
                {
                    try { await Switch("update", "runtime-broken", bad); throw new InvalidOperationException("expected initialization rejection"); }
                    catch (ZbaBackendException ex) when (ex.Code == "plugin_reload_failed") { checks++; }
                    Check((await packages.GetInstalledAsync()).Single().Active?.Version == "1.0.0" && registry.Find(first.Id)!.Version == "1.0.0", "runtime_failed_init_keeps_active");
                    await Switch("rollback", "runtime-recover");
                    Check((await packages.GetInstalledAsync()).Single().Previous?.Version == "2.0.0", "runtime_recovery_preserves_previous");
                }
                await registry.StopAllAsync(default);
                var restarted = new PluginPackageService(packageRoot, http);
                var startup = await restarted.LoadAtStartupAsync(context, isEnabled: _ => enabled);
                Check(startup.Failures.Count == 0 && startup.Loaded.Count == (enabled ? 1 : 0), "runtime_restart_retains_intent");
                foreach (var plugin in startup.Loaded) await plugin.ShutdownAsync(default);
            }
            finally { await registry.StopAllAsync(default); }
        }
        return checks;
    }
}
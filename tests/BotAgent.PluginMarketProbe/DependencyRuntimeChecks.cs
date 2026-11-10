using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunDependencyRuntimeChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Refuse(Func<Task> action)
        {
            try { await action(); throw new InvalidOperationException("dependency_confirmation_should_refuse"); }
            catch (ZbaBackendException ex) when (ex.Code == "plugin_preview_refresh_required") { checks++; }
        }
        PluginPackageManifest Manifest(string id, int api)
        {
            var managed = ReviewManifest([], id, "1.0.0", "probe.dll", "UnusedSyntheticEntry");
            return api == 1 ? managed : managed with { ApiVersion = 2, EntryAssembly = null, EntryType = null,
                TargetFramework = null, Runtime = new("python", 1, "main.py", "3.11", "stdlib-only", ["echo"]) };
        }
        var interpreter = Environment.GetEnvironmentVariable("BOTAGENT_PYTHON_EXECUTABLE");
        Environment.SetEnvironmentVariable("BOTAGENT_PYTHON_EXECUTABLE", "");
        try
        {
            foreach (var (sourceApi, dependencyApi) in new[] { (1, 2), (2, 1), (1, 1), (2, 2) })
            {
                var caseRoot = Path.Combine(root, $"dependency-runtime-{sourceApi}-{dependencyApi}");
                var http = new ReviewPackageHttp();
                var packages = new PluginPackageService(Path.Combine(caseRoot, "packages"), http);
                var registered = new List<PluginInfo>();
                var receipt = Path.Combine(caseRoot, "operations.json");
                var market = new PluginMarketService(packages, new PluginSettingsStore(), new PluginCatalogSource(http, _ => { }),
                    () => DateTimeOffset.UnixEpoch, receipt, registrySnapshot: () => registered.ToArray());
                var dependency = Manifest("probe.runtime.dependency", dependencyApi);
                var source = Manifest("probe.runtime.source", sourceApi) with { Dependencies = [new(dependency.Id, dependency.Version)] };
                await LifecycleTestRequests.SaveState(packages, new(dependency.Id, Active: dependency));
                var expected = sourceApi == dependencyApi ? "passed" : "blocked";
                var preview = await market.PreviewAsync(new("json", (await market.ReadInstalledAsync(default)).Revision, Manifest: source), "synthetic-admin", default);
                Check(preview.Checks.Single(c => c.Code == "dependencies").Status == expected, $"fresh_runtime_{sourceApi}_{dependencyApi}");
                if (sourceApi == 1) Check(preview.CanInstall == (sourceApi == dependencyApi), "managed_fresh_overall_admission");
                if (sourceApi != dependencyApi)
                {
                    await Refuse(() => market.InstallAsync(new(source, preview.Revision, "cross-runtime-install", true)
                        { PreviewId = preview.PreviewId }, "synthetic-admin", default));
                    registered.Add(new(dependency.Id, "Synthetic preset", dependency.Version, "Synthetic", PluginCategory.Feature, true, true));
                    var registryPreview = await market.PreviewAsync(new("json", preview.Revision, Manifest: source), "synthetic-admin", default);
                    Check(registryPreview.Checks.Single(c => c.Code == "dependencies").Status == "blocked", "registry_cannot_override_package_runtime");
                }
                await LifecycleTestRequests.SaveState(packages, new(source.Id, Active: source with { Dependencies = [] }));
                var update = source with { Version = "2.0.0" };
                var lifecycle = await market.PreviewLifecycleAsync(new("update", source.Id, (await market.ReadInstalledAsync(default)).Revision,
                    new("json", Manifest: update)), "synthetic-admin");
                Check(lifecycle.Checks.Single(c => c.Code == "dependencies").Status == expected, $"update_runtime_{sourceApi}_{dependencyApi}");
                if (sourceApi == 1) Check(lifecycle.CanApply == (sourceApi == dependencyApi), "managed_update_overall_admission");
                if (sourceApi != dependencyApi)
                    await Refuse(() => market.UpdateAsync(new(update, lifecycle.Revision, "cross-runtime-update", true)
                        { PreviewId = lifecycle.PreviewId }, "synthetic-admin", default));
                Check(http.Calls == 0 && !File.Exists(receipt), "dependency_admission_never_downloads_or_reserves");
            }
            foreach (var api in new[] { 1, 2 })
            {
                var caseRoot = Path.Combine(root, $"builtin-dependency-runtime-{api}");
                var http = new ReviewPackageHttp();
                var packages = new PluginPackageService(Path.Combine(caseRoot, "packages"), http);
                var builtin = new PluginInfo("probe.builtin", "Synthetic preset", "1.0.0", "Synthetic", PluginCategory.Feature, true, true);
                var market = new PluginMarketService(packages, new PluginSettingsStore(), new PluginCatalogSource(http, _ => { }),
                    () => DateTimeOffset.UnixEpoch, Path.Combine(caseRoot, "operations.json"), registrySnapshot: () => [builtin]);
                var source = Manifest("probe.builtin.consumer", api) with { Dependencies = [new(builtin.Id, builtin.Version)] };
                var preview = await market.PreviewAsync(new("json", (await market.ReadInstalledAsync(default)).Revision, Manifest: source), "synthetic-admin", default);
                Check(preview.Checks.Single(c => c.Code == "dependencies").Status == (api == 1 ? "passed" : "blocked"), $"builtin_is_managed_{api}");
            }
        }
        finally { Environment.SetEnvironmentVariable("BOTAGENT_PYTHON_EXECUTABLE", interpreter); }
        return checks;
    }
}

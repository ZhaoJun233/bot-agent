using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

internal static class RuntimeDependencyChecks
{
    internal static async Task Run(string root, PluginPackageManifest original, SyntheticHttp http, Action<bool, string> check)
    {
        var service = new PluginPackageService(Path.Combine(root, "runtime-dependencies"), http);
        var registry = new PluginManager(_ => { });
        var provider = new Provider();
        var manifest = original with { Dependencies = [new(provider.Id, provider.Version)] };
        var context = new PluginContext(new EmptyServices());
        async Task Refuse()
        {
            try { await service.ApplyAsync(manifest.Id, context, registry, true); }
            catch (InvalidOperationException ex) when (ex.Message == "plugin_dependency_unavailable") { check(true, "dependency refusal"); return; }
            throw new InvalidOperationException("expected dependency refusal");
        }
        try
        {
            await service.StageInstallAsync(manifest);
            await Refuse();
            check(!service.PythonSources.Enabled && (await service.GetInstalledAsync()).Single().Pending is not null,
                "missing provider starts no Python contribution and keeps pending");
            await registry.ReplaceAsync(provider, true);
            await Refuse();
            await registry.ReplaceAsync(new Provider(), true, 2);
            await service.ApplyAsync(manifest.Id, context, registry, true);
            var tool = (await service.PythonSources.DiscoverAsync(default)).Single();
            check(service.PythonSources.IsToolCurrent(tool), "same-runtime dependency publishes Python tools");
            registry.SetEnabled(provider.Id, false);
            check(!service.PythonSources.Enabled && (await service.PythonSources.DiscoverAsync(default)).Count == 0,
                "unavailable provider hides dependent Python discovery");
            check(!service.PythonSources.IsToolCurrent(tool)
                && !(await service.PythonSources.CallWithLeaseAsync(tool, new JsonObject(), default)).Ok,
                "captured Python tool cannot bypass provider disable");
            registry.SetEnabled(provider.Id, true);
            check((await service.PythonSources.CallWithLeaseAsync(tool, new JsonObject { ["text"] = "synthetic" }, default)).Ok,
                "same live provider resumes a still-current Python generation");
            await registry.ReplaceAsync(new Provider(), true, 2);
            check(!service.PythonSources.IsToolCurrent(tool), "provider generation replacement revokes captured Python tool");
            await service.ApplyAsync(manifest.Id, context, registry, true);
            var rebound = (await service.PythonSources.DiscoverAsync(default)).Single();
            check(service.PythonSources.IsToolCurrent(rebound) && !service.PythonSources.IsToolCurrent(tool),
                "Python reinitialization binds new provider and creates new owner");
            var standalone = new PluginPackageService(Path.Combine(root, "standalone-dependencies"), http);
            await standalone.StageInstallAsync(manifest);
            var loaded = await standalone.LoadAtStartupAsync(context, isEnabled: _ => true);
            check(loaded.Loaded.Count == 0 && loaded.Failures.Count == 1 && !standalone.PythonSources.Enabled,
                "standalone compatibility loader cannot bypass registry dependency admission");
        }
        finally { await registry.StopAllAsync(default).WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    private sealed class Provider : IBotPlugin
    {
        public string Id => "test.python.provider";
        public string Name => "Synthetic provider";
        public string Version => "1.0.0";
        public string Description => "Synthetic Python API readiness fixture";
        public PluginCategory Category => PluginCategory.Feature;
        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

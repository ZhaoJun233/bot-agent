extern alias StorageModule;
using System.Reflection;
using System.Text.Json;
using BotAgent.Adapters.Plugins;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;
using BotAgent.Services.Net;
using BotAgent.Services.Plugins;
using BotAgent.Services.Resilience;

internal partial class Program
{
    private static async Task<int> RunDynamicDefaultReaderChecks(string root, DynamicConfigurationFixture fixture,
        PluginPackageManifest testManifest, PluginSettingsStore store)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        const string id = "preset.feature.websearch";
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = typeof(PluginPackageService).Assembly;
        var runtimeType = host.GetType("BotAgent.Host.PluginRuntime")!;
        var runtime = (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
            .Invoke([Path.Combine(root, "dynamic-default-reader"), (Action<string>)(_ => { })]);
        var model = new AdoptionModel(); var pages = new AdoptionPages();
        var serviceType = host.GetType("BotAgent.Host.ResearchPluginServices")!;
        var constructor = serviceType.GetConstructors(BindingFlags.Public | hidden)
            .Single(ctor => ctor.GetParameters().Length == 4);
        var services = (IServiceProvider)constructor.Invoke([model, pages, new ToolCircuitBreaker("synthetic-config"), Array.Empty<IDisposable>()]);
        runtimeType.GetMethod("BindServices", hidden)!.Invoke(runtime, [services]);
        Check(services.GetService(typeof(Func<ResearchOptions>)) is null, "actual production service constructor supplies no legacy options accessor");
        var flags = new StorageModule::BotAgent.Storage.PluginStore();
        var previous = flags.LoadPluginStates().GetValueOrDefault(id, true);
        Check(previous, "synthetic default owner intent was captured before writer activation");
        try
        {
            OfficialMarketplaceTestHelper.StageOfficialPackages(Path.Combine(root, "dynamic-default-reader"), [id]);
            store.Save(testManifest, store.ReadManifest(testManifest).Revision, "repair-application",
                JsonSerializer.SerializeToElement(new { enableWebSearch = true, linkPreviewMax = 4 }), "synthetic-admin");
            await (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;
            var registry = (PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
            var packages = (PluginPackageService)runtimeType.GetProperty("Packages", hidden)!.GetValue(runtime)!;
            var actualStore = (PluginSettingsStore)runtimeType.GetProperty("Settings", hidden)!.GetValue(runtime)!;
            var manifest = (await packages.GetInstalledAsync()).Single(item => item.Id == id).Active!;
            var facade = new ResearchUseCase(registry);
            Check(manifest.Configuration?.Bindings.Count == 10 && manifest.Settings?.Count == 9 && facade.IsReady,
                "generated default ZIP actually adopts ten owner bindings and nine editable fields");
            Check(model.Calls == 0 && pages.Calls == 0, "production configuration reader performs no calls during package preparation");
            Check((await facade.SearchAsync("synthetic:dynamic", "synthetic query"))?.HasContent == true && model.Calls == 1,
                "actual default search consumes the approved runtime configuration");
            var view = actualStore.ReadManifest(manifest);
            var saved = actualStore.Save(manifest, view.Revision, "default-source", JsonSerializer.SerializeToElement(new {
                enableWebSearch = false, webSearchUseModelSearch = false, webSearchSources = "synthetic|https://example.com/search?q={q}" }), "synthetic-admin");
            var calls = model.Calls;
            var source = await facade.SearchAsync("synthetic:source", "synthetic query");
            Check(!saved.RestartRequired && source?.HasContent == true && pages.Calls == 1 && model.Calls == calls,
                "package settings hot save changes the actual production search route without reconstructing its generation");
            // EnableWebSearch gates the host chat caller; direct diagnostics retain the
            // existing ability to test a disabled feature. Verify the shared flag below.
            fixture.Save(new BehaviorChanges { EnableWebSearch = FieldEdit<bool>.Set(true), WebSearchUseModelSearch = FieldEdit<bool>.Set(true) });
            fixture.Box.Apply(next => { next.EnableWebSearch = true; next.WebSearchUseModelSearch = true; });
            var legacy = await facade.SearchAsync("synthetic:legacy", "synthetic query");
            Check(legacy?.HasContent == true && pages.Calls == 1 && model.Calls == calls + 1,
                "legacy desired and runtime writes are cross-read by the same real owner instance");
            var configType = host.GetType("BotAgent.Adapters.Plugins.PluginActivationConfiguration")!;
            var business = (IPluginConfiguration)Activator.CreateInstance(configType, manifest, actualStore)!;
            try { business.Read(); throw new InvalidOperationException("preparation_read_accepted"); }
            catch (InvalidOperationException error) when (error.Message == "plugin_configuration_not_ready") { checks++; }
            var gate = registryTypeGate(registry, id);
            configType.GetMethod("Publish", hidden)!.Invoke(business, [gate]);
            var control = (IPluginConfiguration)configType.GetMethod("ControlView", hidden)!.Invoke(business, null)!;
            configType.GetMethod("Publish", hidden)!.Invoke(control, [gate.ForControl()]);
            Check(business.Read().Values.Count == 10 && control.Read().Values.Count == 10,
                "business and control capabilities read only the complete owner snapshot");
            Check(business.Read().Values["webSearchReadMaxChars"].GetInt32() == fixture.Box.Current.WebSearchReadMaxChars
                && !actualStore.ReadManifest(manifest).Values.ContainsKey("webSearchReadMaxChars"), "env-only read limit stays runtime-readable and operator-readonly");
            checks += RunWaitingConfigurationReadCheck(fixture.Box, business, gate,
                () => registry.SetEnabled(id, false), expectSnapshot: false);
            try { business.Read(); throw new InvalidOperationException("disabled_read_accepted"); }
            catch (InvalidOperationException error) when (error.Message == "plugin_configuration_not_ready") { checks++; }
            Check(control.Read().Values.Count == 10 && (await facade.TestReadPageAsync("https://example.com/page", default)).Text is { Length: > 40 },
                "disabled business still retains its separately approved configuration control lifetime");
            registry.SetEnabled(id, true);
            checks += RunWaitingConfigurationReadCheck(fixture.Box, control, gate.ForControl(),
                () => registry.SetEnabled(id, false), expectSnapshot: true);
            registry.SetEnabled(id, true);
            Task? removal = null;
            checks += RunWaitingConfigurationReadCheck(fixture.Box, control, gate.ForControl(), () =>
            {
                removal = registry.RemoveAsync(id);
                Check(!removal.IsCompleted && !gate.ForControl().IsReady,
                    "removal closes admission and waits for the already leased configuration read");
            }, expectSnapshot: false);
            await removal!;
            foreach (var handle in new[] { business, control })
            {
                try { handle.Read(); throw new InvalidOperationException("removed_read_accepted"); }
                catch (InvalidOperationException error) when (error.Message == "plugin_configuration_not_ready") { checks++; }
            }
            Check(!facade.IsReady, "removed default package closes all production configuration consumers");
            var scope = host.GetType("BotAgent.Adapters.Plugins.PluginActivationDataStore")!.GetMethod("Scope", BindingFlags.Static | BindingFlags.NonPublic)!;
            var dependencies = Activator.CreateInstance(host.GetType("BotAgent.Adapters.Plugins.PluginActivationDependencies")!)!;
            var scoped = (PluginContext)scope.Invoke(null, [new PluginContext(new AmbientConfiguration(control)), null, null, null, null, dependencies, null])!;
            Check(scoped.Services.GetService(typeof(IPluginConfiguration)) is null,
                "an undeclared package cannot inherit an ambient configuration capability");
            await packages.ApplyAtStartupAsync(id, new PluginContext(new AdoptionContext(services)), registry, true,
                resolveSettings: actualStore.ResolveRuntimeValues);
            foreach (var handle in new[] { business, control })
            {
                try { handle.Read(); throw new InvalidOperationException("old_generation_revived"); }
                catch (InvalidOperationException error) when (error.Message == "plugin_configuration_not_ready") { checks++; }
            }
            Check(facade.IsReady, "same-version reconstruction publishes a new owner while all old readers stay fenced");
        }
        finally { await runtime.DisposeAsync(); }
        return checks;
    }

    private static PluginManager.InvocationGate registryTypeGate(PluginManager registry, string id)
    {
        var entry = registry.GetType().GetField("_plugins", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        var value = entry.GetType().GetProperty("Item")!.GetValue(entry, [id])!;
        return (PluginManager.InvocationGate)typeof(PluginManager.InvocationGate).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single().Invoke([registry, value, false]);
    }

    private sealed class AmbientConfiguration(IPluginConfiguration value) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(IPluginConfiguration) ? value : null;
    }
}

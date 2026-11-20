extern alias StorageModule;

using System.Collections.Immutable;
using System.Reflection;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Management;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

internal partial class Program
{
    internal static async Task<int> RunRuntimeDependencyGenerationChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var bytes = ReviewZip("probe.dll", await File.ReadAllBytesAsync(typeof(DependencyProvider).Assembly.Location));
        var http = new ReviewPackageHttp { Bytes = bytes };
        var runtimeType = typeof(PluginPackageService).Assembly.GetType("BotAgent.Host.PluginRuntime")!;
        var events = new List<string>();
        await using var runtime = (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
            .Invoke([Path.Combine(root, "dependency-runtime"), (Action<string>)(value =>
                { if (value.StartsWith("dependency-init:", StringComparison.Ordinal)) events.Add(value); })]);
        BindResearchRuntime(runtime);
        var packages = (PluginPackageService)runtimeType.GetProperty("Packages", hidden)!.GetValue(runtime)!;
        typeof(PluginPackageService).GetField("_http", hidden)!.SetValue(packages, http);
        typeof(PluginGovernanceService).GetField("_source", hidden)!
            .SetValue(packages.Governance, new PluginCatalogSource(http));
        var registry = (PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
        Task Start() => (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;
        Task Reload(string id) => (Task)runtimeType.GetMethod("ReloadAsync", hidden)!.Invoke(runtime, [id, CancellationToken.None])!;
        ManagedDescriptor Descriptor(string id) => ((ImmutableArray<ManagedDescriptor>)runtimeType
            .GetMethod("Describe", hidden)!.Invoke(runtime, null)!).Single(p => p.Key == new ManagedObjectKey.Plugin(new(id)));
        PluginPackageManifest Manifest(DependencyProvider plugin, params PluginPackageDependency[] dependencies)
            => ReviewManifest(bytes, plugin.Id, plugin.Version, "probe.dll", plugin.GetType().FullName!) with { Dependencies = dependencies };
        var provider = Manifest(new DependencyProvider());
        var consumer = Manifest(new DependencyConsumer(), new PluginPackageDependency(provider.Id, provider.Version));
        var independent = Manifest(new DependencyIndependent());
        var broken = Manifest(new DependencyBroken());
        var blocked = Manifest(new DependencyBlocked(), new PluginPackageDependency(broken.Id, broken.Version));
        var cycleA = Manifest(new DependencyCycleA(), new PluginPackageDependency("test.dep.cycle-b", "1.0.0"));
        var cycleB = Manifest(new DependencyCycleB(), new PluginPackageDependency(cycleA.Id, cycleA.Version));
        foreach (var manifest in new[] { consumer, provider, independent, blocked, broken, cycleA, cycleB })
        {
            new StorageModule::BotAgent.Storage.PluginStore().SetPluginEnabled(manifest.Id, true);
            try { await packages.StageInstallAsync(manifest); }
            catch (Exception ex) { throw new InvalidOperationException("synthetic-stage:" + manifest.Id, ex); }
        }
        await Start();
        Check(events.IndexOf("dependency-init:" + provider.Id) >= 0
            && events.IndexOf("dependency-init:" + provider.Id) < events.IndexOf("dependency-init:" + consumer.Id),
            "startup follows dependencies even when consumer directory sorts first");
        Check(registry.IsReady(consumer.Id) && registry.IsReady(independent.Id), "independent valid chains reach Ready");
        Check(!events.Contains("dependency-init:" + blocked.Id) && !registry.IsReady(blocked.Id),
            "failed provider prevents consumer Initialize");
        Check(Descriptor(blocked.Id).RuntimeState == ManagedRuntimeState.NotLoaded
            && Descriptor(blocked.Id).ErrorCode == "plugin_dependency_unavailable", "runtime exposes blocked dependency truthfully");
        Check(!events.Contains("dependency-init:" + cycleA.Id) && !events.Contains("dependency-init:" + cycleB.Id),
            "cycle executes neither initializer");
        Check(Descriptor(cycleA.Id).ErrorCode == "plugin_dependency_cycle" && Descriptor(cycleB.Id).ErrorCode == "plugin_dependency_cycle",
            "cycle diagnostics reach every member");
        var pendingConsumer = Manifest(new DependencyConsumerV2(), new PluginPackageDependency(provider.Id, provider.Version));
        await packages.StageInstallAsync(pendingConsumer);
        var oldConsumer = registry.Find(consumer.Id);
        await packages.StageInstallAsync(provider);
        await Reload(provider.Id);
        var state = (await packages.GetInstalledAsync()).Single(p => p.Id == consumer.Id);
        Check(!ReferenceEquals(oldConsumer, registry.Find(consumer.Id)) && registry.IsReady(consumer.Id),
            "targeted provider replacement reinitializes existing consumers");
        Check(state.Active?.Version == "1.0.0" && state.Pending?.Version == "2.0.0"
            && registry.Find(consumer.Id)?.Version == "1.0.0", "targeted reload preserves unrelated pending consumer release");
        new StorageModule::BotAgent.Storage.PluginStore().SetPluginEnabled(provider.Id, false);
        var failed = false;
        try { await Reload(provider.Id); }
        catch (InvalidOperationException ex) when (ex.Message == "plugin_reload_failed") { failed = true; }
        Check(failed && !registry.IsReady(provider.Id) && !registry.IsReady(consumer.Id), "provider disable revokes dependent readiness");
        Check(Descriptor(consumer.Id).RuntimeState == ManagedRuntimeState.Faulted, "loaded but unavailable consumer is not reported running");
        Check(((IReplyPolicyPlugin)registry.Find(consumer.Id)!).BlockReason(new("synthetic", false, true)) == "plugin_dependency_unavailable",
            "disabled provider cannot be bypassed at invocation");
        new StorageModule::BotAgent.Storage.PluginStore().SetPluginEnabled(provider.Id, true);
        await Reload(provider.Id);
        Check(registry.IsReady(consumer.Id), "provider re-enable restores chain through fresh initialization");
        return checks;
    }
}

public class DependencyProvider : IReplyPolicyPlugin
{
    public virtual string Id => "test.dep.z-provider";
    public string Name => "Synthetic dependency fixture";
    public virtual string Version => "1.0.0";
    public string Description => "Synthetic runtime topology fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public virtual Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ((Action<string>?)context.Services.GetService(typeof(Action<string>)))?.Invoke("dependency-init:" + Id);
        return Task.CompletedTask;
    }
    public string? BlockReason(ReplyPolicyContext context) => null;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
public class DependencyConsumer : DependencyProvider { public override string Id => "test.dep.a-consumer"; }
public sealed class DependencyConsumerV2 : DependencyConsumer { public override string Version => "2.0.0"; }
public sealed class DependencyIndependent : DependencyProvider { public override string Id => "test.dep.independent"; }
public sealed class DependencyBroken : DependencyProvider
{
    public override string Id => "test.dep.broken";
    public override Task InitializeAsync(PluginContext context, CancellationToken ct) => throw new InvalidOperationException("synthetic_failure");
}
public sealed class DependencyBlocked : DependencyProvider { public override string Id => "test.dep.blocked"; }
public sealed class DependencyCycleA : DependencyProvider { public override string Id => "test.dep.cycle-a"; }
public sealed class DependencyCycleB : DependencyProvider { public override string Id => "test.dep.cycle-b"; }

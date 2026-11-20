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
    internal static async Task<int> RunMetadataRuntimeChecks(string root)
    {
        if (!Environment.GetCommandLineArgs().Contains("--metadata-runtime", StringComparer.Ordinal))
            return await RunIsolatedMetadataRuntimeChecks();
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var bytes = ReviewZip("probe.dll", await File.ReadAllBytesAsync(typeof(MetadataRuntimePlugin).Assembly.Location));
        var http = new ReviewPackageHttp { Bytes = bytes };
        var directory = Path.Combine(root, "metadata-runtime");
        // Run before the one-way configuration-writer activation used by ConfigurationInstallChecks.
        new StorageModule::BotAgent.Storage.PluginStore().SetPluginEnabled("probe.install.stale", true);
        var runtimeType = typeof(PluginPackageService).Assembly.GetType("BotAgent.Host.PluginRuntime")!;
        await using var runtime = (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
            .Invoke([directory, (Action<string>)(_ => { })]);
        // Production captures default intent before activating its configuration writer.
        // Keep this one-way activation inside the child process, away from bootstrap tests.
        _ = new DynamicConfigurationFixture();
        BindResearchRuntime(runtime);
        var packages = (PluginPackageService)runtimeType.GetProperty("Packages", hidden)!.GetValue(runtime)!;
        typeof(PluginPackageService).GetField("_http", hidden)!.SetValue(packages, http);
        typeof(PluginGovernanceService).GetField("_source", hidden)!
            .SetValue(packages.Governance, new PluginCatalogSource(http));
        Task Reload(string? id = null) => (Task)runtimeType.GetMethod("ReloadAsync", hidden)!.Invoke(runtime, [id, CancellationToken.None])!;
        var damagedDirectory = Path.Combine(directory, "probe.metadata.broken"); Directory.CreateDirectory(damagedDirectory);
        var damagedPath = Path.Combine(damagedDirectory, "installed.json");
        await File.WriteAllTextAsync(damagedPath, "{synthetic metadata fault");
        var original = File.ReadAllBytes(damagedPath);
        var manifest = ReviewManifest(bytes, "probe.install.stale", "1.0.0", "probe.dll", typeof(MetadataRuntimePlugin).FullName!);
        await packages.StageInstallAsync(manifest);
        await Reload();
        var registry = (PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
        ManagedDescriptor Descriptor(string id) => ((ImmutableArray<ManagedDescriptor>)runtimeType
            .GetMethod("Describe", hidden)!.Invoke(runtime, null)!).Single(p => p.Key == new ManagedObjectKey.Plugin(new(id)));
        var generation = registry.Find(manifest.Id);
        Check(generation is not null && Descriptor(manifest.Id).RuntimeState == ManagedRuntimeState.Running,
            "real runtime starts a readable neighbor despite damaged metadata");
        Check(Descriptor("probe.metadata.broken").RuntimeState == ManagedRuntimeState.NotLoaded
            && Descriptor("probe.metadata.broken").ErrorCode == "plugin_metadata_unreadable", "faulted record never becomes ready");
        Check(((IEnumerable<string>)runtimeType.GetMethod("DeclaredIds", hidden)!.Invoke(runtime, null)!).Contains("probe.metadata.broken"),
            "valid damaged identity remains visible to configuration and recovery");
        Check(File.ReadAllBytes(damagedPath).SequenceEqual(original), "startup preserves damaged metadata bytes");
        var activePath = Path.Combine(directory, manifest.Id, "installed.json");
        var active = File.ReadAllBytes(activePath);
        await File.WriteAllTextAsync(activePath, "{damaged while running");
        var damagedActive = File.ReadAllBytes(activePath);
        await Reload();
        Check(ReferenceEquals(generation, registry.Find(manifest.Id)) && Descriptor(manifest.Id).RuntimeState == ManagedRuntimeState.Running
            && Descriptor(manifest.Id).ErrorCode == "plugin_metadata_unreadable", "running generation remains truthful alongside metadata fault");
        var refused = false;
        try { await Reload(manifest.Id); }
        catch (InvalidOperationException ex) when (ex.Message == "plugin_reload_failed") { refused = true; }
        Check(refused && ReferenceEquals(generation, registry.Find(manifest.Id)), "targeted reload refuses damaged state without replacing generation");
        Check(File.ReadAllBytes(activePath).SequenceEqual(damagedActive), "reload refusal preserves active record bytes");
        await File.WriteAllBytesAsync(activePath, active);
        await Reload();
        Check(ReferenceEquals(generation, registry.Find(manifest.Id)) && Descriptor(manifest.Id).ErrorCode is null,
            "compatible record recovery clears error without restarting an unchanged generation");
        return checks;
    }

    private static async Task<int> RunIsolatedMetadataRuntimeChecks()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--metadata-runtime");
        using var child = System.Diagnostics.Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var stdout = await output;
        var stderr = await errors;
        if (child.ExitCode != 0 || !stdout.Contains("PASS metadata runtime: 8 checks", StringComparison.Ordinal))
            throw new InvalidOperationException("metadata_runtime_child_failed;exit=" + child.ExitCode + ";output=" + stdout + stderr);
        Console.Write(stdout);
        return 8;
    }
}

public sealed class MetadataRuntimePlugin : IBotPlugin
{
    public string Id => "probe.install.stale";
    public string Name => "Synthetic metadata runtime";
    public string Version => "1.0.0";
    public string Description => "Synthetic metadata isolation fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

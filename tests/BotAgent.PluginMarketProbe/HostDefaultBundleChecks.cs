extern alias StorageModule;

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services.Plugins;

internal partial class Program
{
    internal static async Task<int> RunHostDefaultBundleChecks(string root)
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        const string id = "preset.feature.token-quota";
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var runtimeType = typeof(PluginPackageService).Assembly.GetType("BotAgent.Host.PluginRuntime")!;
        var directory = Path.Combine(root, "host-default-bundle");
        var flags = new StorageModule::BotAgent.Storage.PluginStore();
        var enabled = flags.LoadPluginStates().GetValueOrDefault(id, true);
        flags.SetPluginEnabled(id, false);
        IAsyncDisposable NewRuntime(string? path = null) => (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
            .Invoke([path ?? directory, (Action<string>)(_ => { })]);
        PluginManager Registry(object runtime) => (PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
        PluginPackageService Packages(object runtime) => (PluginPackageService)runtimeType.GetProperty("Packages", hidden)!.GetValue(runtime)!;
        Task Start(object runtime) => (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;
        try
        {
            string fingerprint;
            await using (var runtime = NewRuntime())
            {
                var registry = Registry(runtime);
                Check(registry.GetAll().All(p => p.Id != id), "quota has no legacy preset registration before package adoption");
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == id);
                Check(state.Active is { Control: true } && state.Pending is null
                    && state.Active.DownloadUrl == "host-distribution://" + id + "/1.0.0", "host adopts actual default distribution ZIP");
                Check(!registry.IsReady(id) && registry.IsControlReady(id) && !flags.LoadPluginStates()[id], "explicit disabled intent survives first adoption");
                var control = registry.GetControlContribution<ITenantQuotaManagement>(id);
                Check(control.ReadManagement("synthetic:host-bundle").Quota.DailyTokenLimit == 50000, "disabled package supplies management");
                var market = (PluginMarketService)runtimeType.GetProperty("Market", hidden)!.GetValue(runtime)!;
                var installed = await market.ReadInstalledAsync(default);
                Check(installed.Plugins.Single(p => p.Manifest.Id == id).Metadata is
                    { Name: "Token 配额", Runtime: "dotnet", SourceKind: "official", Review.Status: "unreviewed" }
                    && installed.BuiltinPlugins.All(p => p.PluginId != id),
                    "the actual disabled default package retains offline presentation as an unreviewed official package");
                using var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, id, "installed.json")));
                Check(metadata.RootElement.GetProperty("minimumReaderVersion").GetInt32() == 12, "offline distribution source with packaged UI requires reader twelve");
                fingerprint = state.Active!.Sha256;
                try
                {
                    new PluginSettingsStore().ReadManifest(state.Active);
                    throw new InvalidOperationException("unapproved_settings_accepted");
                }
                catch (BotAgent.Domain.Plugins.PluginSettingsException) { checks++; }
                var settings = (PluginSettingsStore)runtimeType.GetProperty("Settings", hidden)!.GetValue(runtime)!;
                try
                {
                    settings.ReadManifest(state.Active with { Control = false });
                    throw new InvalidOperationException("changed_settings_manifest_accepted");
                }
                catch (BotAgent.Domain.Plugins.PluginSettingsException) { checks++; }
                Check(AssemblyLoadContext.Default.Assemblies.All(a => a.GetName().Name != "BotAgent.Package.TokenQuota"), "business implementation is not loaded into Default");
                await Packages(runtime).StageUninstallAsync(id);
            }
            await using (var runtime = NewRuntime())
            {
                await Start(runtime);
                var state = (await Packages(runtime).GetInstalledAsync()).Single(p => p.Id == id);
                Check(!Registry(runtime).IsControlReady(id) && state.Active is null && state.Previous?.Sha256 == fingerprint,
                    "restart adopts explicit uninstall and preserves previous release");
            }
            var packageDirectory = Path.GetFullPath(Path.Combine(directory, id));
            var preserved = Path.GetFullPath(Path.Combine(root, "host-default-preserved"));
            if (!packageDirectory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !preserved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("synthetic_path_escape");
            Directory.Move(packageDirectory, preserved);
            await using (var runtime = NewRuntime())
            {
                await Start(runtime);
                Check(!Directory.Exists(packageDirectory) && !Registry(runtime).IsControlReady(id), "default bundle does not resurrect physically removed adopted package");
            }
            if (OperatingSystem.IsWindows())
            {
                await using var runtime = NewRuntime(directory.ToUpperInvariant());
                await Start(runtime);
                Check(!Directory.Exists(packageDirectory), "path casing cannot resurrect a removed default package");
            }
            var alias = Path.Combine(root, "host-default-bundle-alias");
            if (OperatingSystem.IsWindows())
            {
                var command = new System.Diagnostics.ProcessStartInfo("pwsh")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
                foreach (var arg in new[] { "-NoProfile", "-Command", "New-Item -ItemType Junction -Path '"
                    + alias.Replace("'", "''") + "' -Target '" + directory.Replace("'", "''") + "' -ErrorAction Stop | Out-Null" })
                    command.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(command)!;
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(process.ExitCode == 0, "synthetic directory junction created");
            }
            else Directory.CreateSymbolicLink(alias, directory);
            try
            {
                await using var runtime = NewRuntime(alias);
                await Start(runtime);
                Check(!Directory.Exists(packageDirectory), "physical directory alias cannot resurrect a removed default package");
            }
            finally
            {
                var link = new DirectoryInfo(alias);
                var resolve = typeof(StorageModule::BotAgent.Adapters.Persistence.AppDatabase)
                    .GetMethod("ResolvePhysicalPath", BindingFlags.NonPublic | BindingFlags.Static)!;
                var expected = (string)resolve.Invoke(null, [directory])!;
                var actual = link.LinkTarget is null ? null : (string)resolve.Invoke(null, [link.ResolveLinkTarget(true)!.FullName])!;
                if (!string.Equals(actual, expected,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("synthetic_alias_changed");
                link.Delete();
            }
            var dependencies = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.ChangeExtension(typeof(PluginPackageService).Assembly.Location, ".deps.json")));
            Check(dependencies.RootElement.GetProperty("libraries").EnumerateObject().All(p =>
                !p.Name.StartsWith("BotAgent.Package.TokenQuota/", StringComparison.Ordinal)
                && !p.Name.StartsWith("BotAgent.PluginBundle/", StringComparison.Ordinal)), "host runtime dependencies exclude package and bundler");
        }
        finally { flags.SetPluginEnabled(id, enabled); }
        return checks;
    }
}

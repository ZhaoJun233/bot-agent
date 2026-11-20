extern alias StorageModule;

using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Management;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;
using BotAgent.Services.Plugins;

internal partial class Program
{
    internal static async Task<int> RunSharedContractRuntimeChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var fixtureRoot = Path.Combine(root, "shared-contract-runtime");
        var built = await SharedContractFixture.BuildAsync(Path.Combine(fixtureRoot, "build"));
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in new[] { built.Provider, built.Contract, built.ProviderPrivate })
            { using var entry = archive.CreateEntry(Path.GetFileName(file)).Open(); entry.Write(File.ReadAllBytes(file)); }
        var bytes = output.ToArray();
        const string id = "test.contract.provider";
        var manifest = ReviewManifest(bytes, id, "1.0.0", "Provider.dll", "Provider") with
        {
            SharedContracts = [new("Synthetic.Usage.Contracts", "1.0.0.0",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(built.Contract))), id)],
            Settings = [new("caption", "Synthetic caption", "string", "one")]
        };
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var runtimeType = typeof(PluginPackageService).Assembly.GetType("BotAgent.Host.PluginRuntime")!;
        IAsyncDisposable Create()
        {
            var result = (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
                .Invoke([Path.Combine(fixtureRoot, "installed"), (Action<string>)(_ => { })]);
            BindResearchRuntime(result);
            return result;
        }
        T Get<T>(IAsyncDisposable value, string property) => (T)runtimeType.GetProperty(property, hidden)!.GetValue(value)!;
        Task Start(IAsyncDisposable value) => (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(value, [CancellationToken.None])!;
        await using var runtime = Create();
        var packages = Get<PluginPackageService>(runtime, "Packages");
        var http = new ReviewPackageHttp { Bytes = bytes };
        typeof(PluginPackageService).GetField("_http", hidden)!.SetValue(packages, http);
        typeof(PluginGovernanceService).GetField("_source", hidden)!
            .SetValue(packages.Governance, new PluginCatalogSource(http));
        var desired = new StorageModule::BotAgent.Storage.PluginStore();
        desired.SetPluginEnabled(id, true);
        await packages.StageInstallAsync(manifest);
        await Start(runtime);
        var registry = Get<PluginManager>(runtime, "Registry");
        var original = registry.Find(id);
        var market = Get<PluginMarketService>(runtime, "Market");
        Check(original is not null && registry.IsReady(id), "real runtime cold start initializes contract owner");
        Check(!(await market.ReadInstalledAsync(default)).Plugins.Single(p => p.Manifest.Id == id).RestartRequired,
            "active contract package does not perpetually require restart");
        var settings = await market.ReadSettingsAsync(id, default);
        var saved = await market.SaveSettingsAsync(id, new(settings.Revision, "contract-settings",
            [new ZbaPluginSettingEdit("caption", 1, "two")]), "contract-runtime", default);
        Check(saved.RestartRequired && saved.Values["caption"] == "two" && ReferenceEquals(original, registry.Find(id)),
            "contract settings persist and request restart without replacing live generation");
        desired.SetPluginEnabled(id, false);
        var refused = false;
        try { await (Task)runtimeType.GetMethod("ReloadAsync", hidden)!.Invoke(runtime, [id, CancellationToken.None])!; }
        catch (InvalidOperationException exception) when (exception.Message == "plugin_contract_cold_start_required") { refused = true; }
        var descriptor = ((ImmutableArray<ManagedDescriptor>)runtimeType.GetMethod("Describe", hidden)!
            .Invoke(runtime, null)!).Single(p => p.Key == new ManagedObjectKey.Plugin(new(id)));
        Check(refused && registry.IsReady(id) && ReferenceEquals(original, registry.Find(id)),
            "configuration disable preserves live generation until restart");
        Check(descriptor.RuntimeState == ManagedRuntimeState.Running && descriptor.ErrorCode == "plugin_reload_pending",
            "cold configuration is not misreported as runtime failure");
        var preview = await market.PreviewLifecycleAsync(new("uninstall", id,
            (await market.ReadInstalledAsync(default)).Revision), "contract-runtime");
        Check(preview.CanApply, "contract uninstall remains an available cold operation");
        var result = await market.UninstallAsync(id, new(preview.Revision, "contract-uninstall")
            { PreviewId = preview.PreviewId, TrustAcknowledged = true }, "contract-runtime", default);
        var state = (await packages.GetInstalledAsync()).Single(p => p.Id == id);
        Check(result.Operation.RestartRequired && state.PendingUninstall && state.Active?.Version == "1.0.0"
            && ReferenceEquals(original, registry.Find(id)), "contract uninstall is staged while current generation remains live");
        await runtime.DisposeAsync();
        await using var restarted = Create();
        await Start(restarted);
        var removed = (await Get<PluginPackageService>(restarted, "Packages").GetInstalledAsync()).Single(p => p.Id == id);
        Check(Get<PluginManager>(restarted, "Registry").Find(id) is null
            && removed.Active is null && removed.Pending is null && !removed.PendingUninstall
            && removed.Previous?.Version == "1.0.0", "next cold start completes uninstall and retains previous release");
        return checks;
    }
}

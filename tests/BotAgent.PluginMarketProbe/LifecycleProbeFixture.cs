using System.Text;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    private sealed class LifecycleFixture
    {
        internal readonly string Root;
        internal readonly ReviewPackageHttp Http;
        internal readonly PluginPackageService Packages;
        internal readonly PluginSettingsStore Settings = new();
        internal readonly Dictionary<string, bool> Desired = new();
        internal readonly PluginMarketService Market;
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20);
        internal Func<string?, CancellationToken, Task>? Reload;
        internal readonly PluginPackageManifest First;
        internal LifecycleFixture(string root, string id)
        {
            Root = Path.Combine(root, id); Directory.CreateDirectory(Root);
            Http = new() { Bytes = ReviewZip("probe.dll", Encoding.UTF8.GetBytes("synthetic lifecycle archive")) };
            Packages = new(Path.Combine(Root, "packages"), Http);
            First = ReviewManifest(Http.Bytes, id, "1.0.0", "probe.dll", "SyntheticEntry");
            Market = new(Packages, Settings, new PluginCatalogSource(Http, _ => { }), () => Now,
                Path.Combine(Root, "operations.json"), desiredStates: () => Desired,
                reload: (pluginId, ct) => Reload is null ? LifecycleTestRequests.Settle(Packages, pluginId!) : Reload(pluginId, ct));
        }
        internal async Task Start(PluginPackageManifest? manifest = null)
        { await Packages.StageInstallAsync(manifest ?? First); await LifecycleTestRequests.Settle(Packages, First.Id); }
        internal async Task<ZbaPluginLifecyclePreview> Preview(string kind, PluginPackageManifest? target = null, string caller = "lifecycle")
            => await Market.PreviewLifecycleAsync(new(kind, First.Id, (await Market.ReadInstalledAsync(default)).Revision,
                target is null ? null : new("json", Manifest: target)), caller);
        internal Task<ZbaPluginOperationResult> Confirm(ZbaPluginLifecyclePreview p, string key, string caller = "lifecycle", CancellationToken ct = default)
            => p.Kind == "uninstall" ? Market.UninstallAsync(p.PluginId, new(p.Revision, key)
                { PreviewId = p.PreviewId, TrustAcknowledged = true }, caller, ct)
                : p.Kind == "update" ? Market.UpdateAsync(new(p.TargetManifest!, p.Revision, key, true) { PreviewId = p.PreviewId }, caller, ct)
                : Market.RollbackAsync(new(p.TargetManifest!, p.Revision, key, true) { PreviewId = p.PreviewId }, caller, ct);
        internal async Task<InstalledPluginPackage> State() => (await Packages.GetInstalledAsync()).Single(p => p.Id == First.Id);
    }
}
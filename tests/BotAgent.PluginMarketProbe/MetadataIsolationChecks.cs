using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunMetadataIsolationChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Refuse(Func<Task> action, string code)
        { try { await action(); throw new InvalidOperationException("expected_" + code); } catch (ZbaBackendException ex) when (ex.Code == code) { checks++; } }
        var f = new LifecycleFixture(root, "probe.metadata"); await f.Start();
        var completed = await f.Confirm(await f.Preview("update", f.First with { Version = "2.0.0" }), "metadata-original-key");
        var healthy = await f.Market.ReadInstalledAsync(default);
        var directory = Path.Combine(f.Root, "packages", "probe.unreadable"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "installed.json");
        await File.WriteAllTextAsync(path, "{synthetic truncated record");
        var raw = await File.ReadAllBytesAsync(path);
        var observed = await f.Market.ReadInstalledAsync(default);
        Check(observed.Plugins.Single().Manifest.Id == f.First.Id && observed.MetadataFailures.Single().Id == "probe.unreadable",
            "installed projection separates valid manifests and unreadable records");
        Check(observed.Revision != healthy.Revision, "fault identity changes directory revision");
        var catalogHttp = new ScriptedHttp { Text = JsonSerializer.Serialize(new { SchemaVersion = 1,
            Plugins = new[] { f.First, f.First with { Id = "probe.unreadable" } } }) };
        var catalogMarket = new PluginMarketService(f.Packages, f.Settings, new PluginCatalogSource(catalogHttp),
            () => f.Now, Path.Combine(f.Root, "catalog-operations.json"));
        var catalog = await catalogMarket.ReadCatalogAsync(null, null, default);
        Check(catalog.MetadataFailures.Single().Id == "probe.unreadable", "catalog stays readable with fault observation");
        Check(catalog.Plugins.Single(p => p.Manifest.Id == "probe.unreadable").Status == "metadata_unreadable",
            "catalog discovery never treats unreadable installed identity as available");
        Check(f.Market.FindOperation("metadata-original-key") == completed, "original operation receipt survives metadata failure");
        await Refuse(() => f.Market.ReadSettingsAsync("probe.unreadable", default), "plugin_metadata_unreadable");
        await Refuse(() => f.Market.PreviewLifecycleAsync(new("uninstall", "probe.unreadable", observed.Revision), "lifecycle"),
            "plugin_metadata_unreadable");
        var occupied = await f.Market.PreviewAsync(new("json", observed.Revision, Manifest: f.First with { Id = "probe.unreadable" }), "lifecycle", default);
        Check(!occupied.CanInstall, "unreadable record reserves its plugin identity");
        var independent = await f.Market.PreviewAsync(new("json", observed.Revision, Manifest: f.First with { Id = "probe.independent" }), "lifecycle", default);
        Check(independent.CanInstall, "independent fresh install can still be previewed");
        foreach (var kind in new[] { "update", "rollback", "uninstall" })
        {
            var preview = await f.Preview(kind, kind == "update" ? f.First with { Version = "3.0.0" } : null);
            Check(!preview.CanApply && preview.Checks.Any(c => c.Code == "readable_dependency_graph" && c.Status == "blocked"),
                "unknown reverse dependencies block " + kind);
        }
        Check(File.ReadAllBytes(path).SequenceEqual(raw), "all management observations preserve raw bytes");
        await File.WriteAllTextAsync(path, "{different synthetic truncated record");
        Check((await f.Market.ReadInstalledAsync(default)).Revision != observed.Revision, "changed corrupt bytes invalidate old previews");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new InstalledPluginPackage("probe.unreadable")));
        Check((await f.Market.ReadInstalledAsync(default)).MetadataFailures.Count == 0 && (await f.Preview("uninstall")).CanApply,
            "compatible record recovery restores lifecycle admission");
        return checks;
    }
}

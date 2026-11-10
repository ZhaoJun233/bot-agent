using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunLifecycleSourceChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Refuse(Func<Task> action)
        { try { await action(); throw new InvalidOperationException("expected_refresh"); } catch (ZbaBackendException ex) when (ex.Code == "plugin_preview_refresh_required") { checks++; } }
        var bytes = ReviewZip("probe.dll", Encoding.UTF8.GetBytes("synthetic repository lifecycle"));
        var first = ReviewManifest(bytes, "probe.lifecycle.source", "1.0.0", "probe.dll", "SyntheticEntry");
        var target = first with { Version = "2.0.0" }; var http = new ImportHttp { Archive = bytes };
        http.Descriptor = JsonSerializer.Serialize(new { SchemaVersion = 1, Plugins = new[] { new { Id = first.Id, Versions = new[] { new { Version = target.Version, Manifest = target } } } } });
        var packages = new PluginPackageService(Path.Combine(root, "lifecycle-source-packages"), http); await packages.StageInstallAsync(first); await LifecycleTestRequests.Settle(packages, first.Id);
        var market = new PluginMarketService(packages, new PluginSettingsStore(), new PluginCatalogSource(http), () => DateTimeOffset.UnixEpoch,
            Path.Combine(root, "lifecycle-source-receipts.json"));
        var discovery = await market.DiscoverSourceAsync("https://github.com/example/plugins", "lifecycle-source", default);
        var revision = (await market.ReadInstalledAsync(default)).Revision;
        var preview = await market.PreviewLifecycleAsync(new("update", first.Id, revision, new("repository", discovery.DiscoveryId, target.Version)), "lifecycle-source");
        var count = http.Downloads; http.Sha = new('b', 40);
        await Refuse(() => market.UpdateAsync(new(target, revision, "source-changed", true) { PreviewId = preview.PreviewId }, "lifecycle-source", default));
        Check(http.Downloads == count && (await packages.GetInstalledAsync()).Single().Pending is null, "source_drift_rejected_before_download");
        var cancelled = new LifecycleFixture(root, "probe.lifecycle.cancel"); await cancelled.Start();
        var cancelPreview = await cancelled.Preview("update", cancelled.First with { Version = "2.0.0" });
        using var cancellation = new CancellationTokenSource(); cancelled.Http.HoldNext();
        var operation = cancelled.Confirm(cancelPreview, "cancelled-lifecycle", ct: cancellation.Token);
        await cancelled.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel(); cancelled.Http.Release.TrySetResult();
        try { await operation; throw new InvalidOperationException("expected_cancelled"); } catch (OperationCanceledException) { checks++; }
        Check((await cancelled.State()).Pending is null, "cancelled_artifact_no_pending");
        try { cancelled.Market.FindOperation("cancelled-lifecycle"); throw new InvalidOperationException("expected_unknown"); }
        catch (ZbaBackendException ex) when (ex.Code == "plugin_operation_unknown") { checks++; }
        return checks;
    }
}
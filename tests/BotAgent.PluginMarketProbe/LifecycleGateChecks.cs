using System.Reflection;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    private static void SeedLifecycleReceipt(PluginMarketService market, string key, ZbaPluginOperation operation,
        string revision, string caller, string? fingerprint = null)
    {
        var store = typeof(PluginMarketService).GetField("_receipts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(market)!;
        var hash = (string)typeof(PluginMarketService).GetMethod("ReceiptKey", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [key])!;
        if (!(bool)store.GetType().GetMethod("TryAdd")!.Invoke(store, [hash, operation, revision, caller, fingerprint, null])!)
            throw new InvalidOperationException("synthetic_receipt_seed_failed");
    }
    internal static async Task<int> RunLifecycleGateChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var f = new LifecycleFixture(root, "probe.lifecycle.gate"); await f.Start();
        var preview = await f.Preview("update", f.First with { Version = "2.0.0" }); string? blockedPath = null;
        var observedArchive = false; var cleanupFailed = false;
        Func<IReadOnlyList<InstalledPluginPackage>, CancellationToken, Task> guard = (_, _) =>
        {
            var stage = Directory.GetDirectories(Path.Combine(f.Root, "packages"), ".stage-*").Single();
            observedArchive = File.Exists(Path.Combine(stage, "archive.tmp"));
            blockedPath = Path.Combine(stage, "readonly.tmp"); File.WriteAllText(blockedPath, "synthetic cleanup failure");
            File.SetAttributes(blockedPath, FileAttributes.ReadOnly);
            throw new InvalidOperationException("synthetic_final_guard_failure");
        };
        try
        {
            await (Task)typeof(PluginPackageService).GetMethod("StageLifecycleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(f.Packages, ["update", f.First.Id, preview.TargetManifest, preview.Revision, guard, CancellationToken.None, "none"])!;
            throw new InvalidOperationException("expected_guard_failure");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        { cleanupFailed = ex is UnauthorizedAccessException or IOException; }
        finally { if (blockedPath is not null && File.Exists(blockedPath)) File.SetAttributes(blockedPath, FileAttributes.Normal); }
        Check(observedArchive, "scratch_zip_written_before_final_guard");
        Check(!OperatingSystem.IsWindows() || cleanupFailed, "windows_cleanup_failure_injected");
        var snapshot = await f.Packages.GetInstalledAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(snapshot.Single().Pending is null, "cleanup_failure_releases_package_gate");
        // A later unknown receipt supersedes an otherwise valid terminal failure proof.
        f.Reload = (_, _) => throw new InvalidOperationException("synthetic failed apply");
        try { await f.Confirm(await f.Preview("update", f.First with { Version = "2.0.0" }), "gate-failed"); }
        catch (ZbaBackendException ex) when (ex.Code == "plugin_reload_failed") { checks++; }
        Check((await f.Preview("rollback")).CanApply, "terminal_failure_initially_recoverable");
        SeedLifecycleReceipt(f.Market, "newer-unknown", new("unknown", f.First.Id, "update", "failed", false, "plugin_operation_unknown"),
            (await f.Market.ReadInstalledAsync(default)).Revision, "lifecycle");
        Check(!(await f.Preview("rollback")).CanApply, "newer_unknown_supersedes_known_failure");
        // M2 persisted intent fingerprints had no preview ID. Exact complete replay must still win.
        foreach (var kind in new[] { "update", "rollback", "uninstall" })
        {
            var key = "legacy-" + kind; var manifest = kind == "uninstall" ? null : f.First;
            var trust = kind != "uninstall";
            var type = typeof(PluginPackageService).Assembly.GetType("BotAgent.Adapters.Plugins.PluginPackageRevision")!;
            var intent = (string)type.GetMethod("Intent", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [kind, f.First.Id, manifest, trust, "lifecycle"])!;
            var operation = new ZbaPluginOperation(key, f.First.Id, kind, "installed", false, null);
            SeedLifecycleReceipt(f.Market, key, operation, "legacy-revision", "lifecycle", intent);
            var result = kind == "uninstall" ? await f.Market.UninstallAsync(f.First.Id, new("stale", key), "lifecycle", default)
                : kind == "update" ? await f.Market.UpdateAsync(new(f.First, "stale", key, true), "lifecycle", default)
                : await f.Market.RollbackAsync(new(f.First, "stale", key, true), "lifecycle", default);
            Check(result.Operation == operation, "legacy_complete_replay_without_preview_" + kind);
        }
        return checks;
    }
}
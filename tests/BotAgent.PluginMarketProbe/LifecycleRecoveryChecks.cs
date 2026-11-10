using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunLifecycleRecoveryChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Refuse(Func<Task> action, string code)
        { try { await action(); throw new InvalidOperationException("expected_" + code); } catch (ZbaBackendException ex) when (ex.Code == code) { checks++; } }
        var f = new LifecycleFixture(root, "probe.lifecycle.recovery"); await f.Start();
        var previous = f.First with { Version = "0.9.0" };
        await LifecycleTestRequests.SaveState(f.Packages, (await f.State()) with { Previous = previous });
        f.Reload = (_, _) => throw new InvalidOperationException("synthetic lifecycle initialization failure");
        var update = await f.Preview("update", f.First with { Version = "2.0.0" });
        await Refuse(() => f.Confirm(update, "known-failure"), "plugin_reload_failed");
        var failed = await f.State();
        Check(failed.Active?.Version == "1.0.0" && failed.Pending?.Version == "2.0.0", "failed_update_keeps_active_fallback");
        var recovery = await f.Preview("rollback");
        Check(recovery.CanApply && recovery.Recovery == "failed-pending" && recovery.TargetManifest?.Version == "1.0.0" && recovery.PendingManifest?.Version == "2.0.0", "known_failed_recovery_targets_active");
        Check(recovery.Checks.Single(c => c.Code == "previous_release") is { Status: "passed", Message:
            "失败候选恢复需要当前活动版本的本地缓存 ZIP；上一版本历史保持不变。" },
            "failed_pending_discloses_active_cache_not_previous_target");
        Check(!(await f.Preview("rollback", caller: "other")).CanApply, "recovery_proof_caller_scoped");
        var downloads = f.Http.Calls; f.Reload = null; await f.Confirm(recovery, "recovery-apply");
        Check(f.Http.Calls == downloads && (await f.State()).Pending is null && (await f.State()).Previous?.Version == "0.9.0", "recovery_cached_preserves_previous");
        var noCache = await f.Preview("rollback");
        Check(!noCache.CanApply, "missing_previous_cache_blocks_preview");
        // A proof with a changed package state, or an expired proof, cannot clean up a pending candidate.
        await LifecycleTestRequests.SaveState(f.Packages, failed with { Pending = failed.Pending! with { Permissions = ["changed"] } });
        Check(!(await f.Preview("rollback")).CanApply, "changed_failed_state_blocks_recovery");
        await LifecycleTestRequests.SaveState(f.Packages, failed); f.Now += TimeSpan.FromDays(8);
        Check(!(await f.Preview("rollback")).CanApply, "expired_failed_receipt_blocks_recovery");
        var first = new LifecycleFixture(root, "probe.lifecycle.failed.install");
        first.Reload = (_, _) => throw new InvalidOperationException("synthetic first initialization failure");
        var revision = (await first.Market.ReadInstalledAsync(default)).Revision;
        await Refuse(() => first.Market.InstallWithJsonPreviewAsync(new(first.First, revision, "failed-first-install", true), "lifecycle", default), "plugin_reload_failed");
        var cleanup = await first.Preview("uninstall");
        Check(cleanup.CanApply && cleanup.Recovery == "failed-install" && cleanup.CurrentManifest.Version == cleanup.PendingManifest?.Version && cleanup.TargetManifest is null, "failed_first_install_explicit_cleanup");
        Check(cleanup.Checks.Single(c => c.Code == "active_package") is { Status: "passed", Message:
            "当前只有尚未生效的安装候选；卸载清理不要求存在活动版本，失败回执另行核对。" },
            "failed_install_discloses_pending_candidate_without_active_version");
        Check(!(await first.Preview("uninstall", caller: "other")).CanApply, "failed_first_install_cross_caller_blocked");
        first.Reload = null; await first.Confirm(cleanup, "failed-install-cleanup");
        Check((await first.State()).Pending is null && (await first.State()).Active is null, "failed_install_cleanup_removes_only_pending");
        var unknown = new LifecycleFixture(root, "probe.lifecycle.unknown"); await unknown.Packages.StageInstallAsync(unknown.First);
        Check(!(await unknown.Preview("uninstall")).CanApply, "unproven_pending_install_blocks_cleanup");
        var corrupt = new LifecycleFixture(root, "probe.lifecycle.corrupt"); await corrupt.Start();
        await corrupt.Confirm(await corrupt.Preview("update", corrupt.First with { Version = "2.0.0" }), "corrupt-upgrade");
        var back = await corrupt.Preview("rollback"); var before = await corrupt.State(); downloads = corrupt.Http.Calls;
        await File.WriteAllBytesAsync(Path.Combine(corrupt.Root, "packages", corrupt.First.Id, "releases", "1.0.0-" + corrupt.First.Sha256.ToLowerInvariant() + ".zip"), [1, 2, 3]);
        var rejected = await corrupt.Confirm(back, "corrupt-rollback");
        Check(rejected.Operation.Status == "failed" && corrupt.Http.Calls == downloads && (await corrupt.State()).Pending is null && (await corrupt.State()).Active?.Version == before.Active?.Version, "corrupt_cached_rollback_no_download_or_pending");
        return checks;
    }
}
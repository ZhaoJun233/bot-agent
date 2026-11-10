using System.Text.Json;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunLifecycleAdmissionChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Refuse(Func<Task> action, string code = "plugin_preview_refresh_required")
        { try { await action(); throw new InvalidOperationException("expected_" + code); } catch (ZbaBackendException ex) when (ex.Code == code) { checks++; } }
        var f = new LifecycleFixture(root, "probe.lifecycle.admission"); await f.Start();
        var next = f.First with { Version = "2.0.0", Permissions = ["message.send"], Settings = [new("note", "Note", "string", "default")] };
        var original = await f.State(); var downloads = f.Http.Calls;
        var preview = await f.Preview("update", next);
        Check(preview.CanApply && !preview.Enabled && preview.Preservation.Settings && preview.Preservation.UserData && preview.Preservation.EnabledIntent, "lifecycle_preservation_preview");
        Check(preview.Changes.Select(c => c.Field).SequenceEqual(new[] { "permissions", "settings", "version" }), "lifecycle_complete_sorted_diff");
        Check(preview.Checks.Any(c => c.Code == "archive_verification" && c.Status == "pending") && f.Http.Calls == downloads && await f.State() == original && !File.Exists(Path.Combine(f.Root, "operations.json")), "lifecycle_preview_no_side_effects");
        foreach (var kind in new[] { "update", "rollback", "uninstall" })
        {
            var revision = preview.Revision; var key = "missing-" + kind;
            await Refuse(() => kind == "uninstall" ? f.Market.UninstallAsync(f.First.Id, new(revision, key), "lifecycle", default)
                : kind == "update" ? f.Market.UpdateAsync(new(next, revision, key, false), "lifecycle", default)
                : f.Market.RollbackAsync(new(next, revision, key, false), "lifecycle", default), "plugin_preview_required");
            await Refuse(() => Task.FromResult(f.Market.FindOperation(key)), "plugin_operation_not_found");
        }
        await Refuse(() => f.Confirm(preview, "other-caller", "other"));
        await Refuse(() => f.Market.UpdateAsync(new(next with { Permissions = ["other"] }, preview.Revision, "tamper", true) { PreviewId = preview.PreviewId }, "lifecycle", default));
        f.Desired[f.First.Id] = true; await Refuse(() => f.Confirm(preview, "enable-drift")); f.Desired[f.First.Id] = false;
        f.Now += TimeSpan.FromMinutes(11); await Refuse(() => f.Confirm(preview, "expired"));
        foreach (var version in new[] { "1.0.0", "0.9.9", "2", "02.0.0", "2.0.0+build", "1.0.0-alpha" })
        {
            try { Check(!(await f.Preview("update", next with { Version = version })).CanApply, "upgrade_order_" + version); }
            catch (ZbaBackendException ex) when (ex.Code == "plugin_preview_invalid") { checks++; }
        }
        Check((await f.Preview("update", next with { Version = "1.0.1-alpha.2" })).CanApply, "valid_prerelease_upgrade");
        Check(!(await f.Preview("rollback")).CanApply, "missing_previous_blocks");
        var statePreview = await f.Preview("update", next);
        await LifecycleTestRequests.SaveState(f.Packages, original with { Active = original.Active! with { Permissions = ["changed"] } });
        await Refuse(() => f.Confirm(statePreview, "manifest-only-drift")); await LifecycleTestRequests.SaveState(f.Packages, original);
        var dep = f.First with { Id = "probe.lifecycle.dependent", Dependencies = [new(f.First.Id, f.First.Version)] };
        await LifecycleTestRequests.SaveState(f.Packages, new(dep.Id, Active: dep));
        Check(!(await f.Preview("uninstall")).CanApply && !(await f.Preview("update", next)).CanApply, "reverse_exact_dependency_blocks");
        await LifecycleTestRequests.SaveState(f.Packages, new(dep.Id));
        Check(!(await f.Preview("update", next with { Dependencies = [new("probe.absent", "1.0.0")] })).CanApply, "forward_exact_dependency_blocks");
        Check(f.Http.Calls == downloads && (await f.State()).Pending is null, "all_admission_refusals_before_download");
        return checks;
    }
}
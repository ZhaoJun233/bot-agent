using System.Text.Json;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunLifecyclePreservationChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Refuse(Func<Task> action, string code = "plugin_preview_refresh_required")
        { try { await action(); throw new InvalidOperationException("expected_" + code); } catch (ZbaBackendException ex) when (ex.Code == code) { checks++; } }
        foreach (var enabled in new[] { false, true })
        {
            var f = new LifecycleFixture(root, "probe.lifecycle.preserve." + enabled.ToString().ToLowerInvariant());
            var first = f.First with { Settings = [new("note", "Note", "string", "default"), new("token", "Token", "secret")] };
            await f.Start(first); f.Desired[first.Id] = enabled;
            f.Settings.Save(first, 0, "initial", JsonSerializer.SerializeToElement(new { note = "synthetic saved value", token = "synthetic-secret-only" }), "lifecycle");
            var before = JsonSerializer.Serialize(f.Settings.ReadManifest(first));
            var effective = JsonSerializer.Serialize(f.Settings.ResolveRuntimeValues(first));
            var data = Path.Combine(f.Root, "user-data.bin"); await File.WriteAllBytesAsync(data, [0, 1, 2, 3, 255]);
            var next = first with { Version = "2.0.0" }; var preview = await f.Preview("update", next);
            Check(preview.CanApply && preview.Enabled == enabled, "preserve_intent_preview");
            var result = await f.Confirm(preview, "preserve-up-" + enabled);
            Check(result.Operation.Status != "failed" && (await f.State()).Active?.Version == "2.0.0" && (await f.State()).Previous?.Version == "1.0.0", "upgrade_publishes_previous");
            Check(JsonSerializer.Serialize(f.Settings.ReadManifest(next)) == before && JsonSerializer.Serialize(f.Settings.ResolveRuntimeValues(next)) == effective, "upgrade_preserves_settings_and_secret");
            var rollback = await f.Preview("rollback"); var downloads = f.Http.Calls;
            Check(rollback.CanApply && rollback.TargetSource?.Origin == "installed" && rollback.TargetManifest?.Version == "1.0.0", "rollback_uses_persisted_previous");
            await f.Confirm(rollback, "preserve-back-" + enabled);
            Check(f.Http.Calls == downloads && (await f.State()).Active?.Version == "1.0.0" && f.Desired[first.Id] == enabled, "rollback_cached_and_enabled_unchanged");
            var remove = await f.Preview("uninstall"); await f.Confirm(remove, "preserve-remove-" + enabled);
            Check((await f.State()).Active is null && f.Desired[first.Id] == enabled && JsonSerializer.Serialize(f.Settings.ResolveRuntimeValues(first)) == effective && (await File.ReadAllBytesAsync(data)).SequenceEqual(new byte[] { 0, 1, 2, 3, 255 }), "uninstall_preserves_settings_secret_data_intent");
            f.Now += TimeSpan.FromHours(1);
            Check(await f.Confirm(preview, "preserve-up-" + enabled) == result, "completed_lifecycle_replay_after_expiry_and_state_change");
        }
        var drift = new LifecycleFixture(root, "probe.lifecycle.settings.drift");
        var configured = drift.First with { Settings = [new("note", "Note", "string", "default")] }; await drift.Start(configured);
        drift.Settings.Save(configured, 0, "drift-first", JsonSerializer.SerializeToElement(new { note = "one" }), "lifecycle");
        var target = configured with { Version = "2.0.0" }; var stable = await drift.Preview("update", target);
        var count = drift.Http.Calls;
        drift.Settings.Save(configured, 1, "drift-second", JsonSerializer.SerializeToElement(new { note = "two" }), "lifecycle");
        await Refuse(() => drift.Confirm(stable, "settings-drift"));
        Check(!(await drift.Preview("update", target with { Settings = [new("other", "Other", "string", "")] })).CanApply && drift.Http.Calls == count, "schema_drift_blocked_before_stage");
        var inflight = await drift.Preview("update", target); drift.Http.HoldNext();
        var running = drift.Confirm(inflight, "inflight-intent"); await drift.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        drift.Desired[configured.Id] = true; drift.Http.Release.TrySetResult();
        await Refuse(() => running); Check((await drift.State()).Pending is null, "post_artifact_enabled_guard_no_pending");
        var mismatched = (await drift.State()) with { Active = configured with { Settings = [new("different", "Different", "string", "")] } };
        await LifecycleTestRequests.SaveState(drift.Packages, mismatched);
        var cleanup = await drift.Preview("uninstall");
        Check(cleanup.CanApply, "incompatible_saved_schema_does_not_strand_uninstall");
        Check(cleanup.Checks.Single(c => c.Code == "preserved_settings") is { Status: "passed", Message:
            "卸载仅核对保存状态指纹并原样保留设置与秘密引用；不校验 schema 兼容性或必填值。" },
            "uninstall_discloses_opaque_preservation_without_schema_or_required_validation");
        drift.Settings.Save(configured, 2, "drift-third", JsonSerializer.SerializeToElement(new { note = "three" }), "lifecycle");
        await Refuse(() => drift.Confirm(cleanup, "opaque-state-drift"));
        var stamp = drift.Settings.ReadPreservationStamp(configured.Id);
        await drift.Confirm(await drift.Preview("uninstall"), "opaque-preserved-remove");
        Check(drift.Settings.ReadPreservationStamp(configured.Id) == stamp, "schema_mismatch_uninstall_byte_preserves_state");
        return checks;
    }
}
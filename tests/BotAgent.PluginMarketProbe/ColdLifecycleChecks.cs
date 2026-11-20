internal partial class Program
{
    internal static async Task<int> RunColdLifecycleChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label)
        { if (!value) throw new InvalidOperationException(label); checks++; }
        var fixture = new LifecycleFixture(root, "probe.lifecycle.cold") { CanReload = false };
        await fixture.Start();
        var reloads = 0;
        fixture.Reload = (_, _) => { reloads++; return Task.CompletedTask; };
        var preview = await fixture.Preview("update", fixture.First with { Version = "2.0.0" });
        Check(preview.CanApply, "cold_update_preview_available");
        const string key = "synthetic-cold-update";
        var result = await fixture.Confirm(preview, key);
        var staged = await fixture.State();
        Check(result.Operation.RestartRequired && result.Operation.Status == "restart_required",
            "cold_update_receipt_requires_restart");
        Check(staged.Active?.Version == "1.0.0" && staged.Pending?.Version == "2.0.0" && reloads == 0,
            "cold_update_only_stages_candidate");

        fixture.CanReload = true;
        var recovered = fixture.Market.FindOperation(key);
        Check(recovered == result, "business_attachment_preserves_original_receipt");
        var replay = await fixture.Confirm(preview, key);
        Check(replay == result, "cold_update_same_key_returns_original_receipt");
        var afterReplay = await fixture.State();
        Check(afterReplay.Active?.Version == "1.0.0" && afterReplay.Pending?.Version == "2.0.0" && reloads == 0,
            "business_attachment_never_replays_cold_update");
        return checks;
    }
}

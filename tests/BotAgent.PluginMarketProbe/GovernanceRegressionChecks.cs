using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunGovernanceRegressionChecks(string root)
    {
        var count = 0;
        void Check(bool pass, string label) { if (!pass) throw new InvalidOperationException("governance: " + label); count++; }
        async Task Deny(Func<Task> action)
        {
            try { await action(); throw new InvalidOperationException("governance: revoked acquisition accepted"); }
            catch (ZbaBackendException ex) when (ex.Code == "plugin_governance_revoked") { count++; }
        }
        var fixture = new GovernanceFixture(root, "probe.governance");
        var policy = fixture.Packages.Governance;
        await policy.RefreshAsync(default);
        Check(policy.Source is { Status: "fresh", GeneratedAt: not null, CheckedAt: not null, ErrorCode: null }, "fresh metadata");
        Check(policy.Evaluate(fixture.Manifest) is { Status: "active", Blocked: false, Match: "none" }, "fresh unlisted active");
        foreach (var warning in new[] { "deprecated", "withdrawn" })
        {
            fixture.Feed(warning); var preview = await fixture.Preview();
            Check(preview.CanInstall && preview.Governance is { Blocked: false } && preview.Governance.Status == warning,
                warning + " only warns");
        }
        await fixture.Start(); fixture.Desired[fixture.Manifest.Id] = true;
        var statePath = Path.Combine(fixture.PackagesRoot, fixture.Manifest.Id, "installed.json");
        var state = await File.ReadAllBytesAsync(statePath);
        fixture.Feed("revoked");
        var catalog = await fixture.Market.ReadCatalogAsync(null, null, default);
        Check(catalog.Plugins.Single().Governance is { Status: "revoked", Blocked: true } &&
            catalog.Plugins.Single().InstalledGovernance is { Status: "revoked", Blocked: true }, "catalog separates installed governance");
        var calls = fixture.Http.Calls;
        var installed = await fixture.Market.ReadInstalledAsync(default);
        Check(fixture.Http.Calls == calls && installed.GovernanceSource is not null, "installed read local only");
        var afterState = await File.ReadAllBytesAsync(statePath);
        Check(installed.Plugins.Single().Enabled && fixture.Desired[fixture.Manifest.Id] &&
            state.SequenceEqual(afterState), "revoked installed package untouched");
        var renamed = fixture.Manifest with { Id = "probe.renamed", DownloadUrl = fixture.Manifest.DownloadUrl.Replace("example/", "example-other/") };
        Check(policy.Evaluate(renamed) is { Status: "revoked", Match: "sha256" }, "global hash survives source/id rename");
        await Deny(() => fixture.Packages.StageInstallAsync(renamed));
        var downloads = fixture.Http.Downloads;
        await Deny(() => fixture.Packages.StageInstallAsync(fixture.Manifest, installed.Revision, default));
        Check(fixture.Http.Downloads == downloads, "deny before download");

        var valid = fixture.Http.Feed;
        var invalid = new List<string> { valid.Replace("schemaVersion", "version"), "{}", "[]", "null",
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            valid.Replace("Synthetic maintenance notice", new string('x', 501)),
            valid.Replace("generatedAt", "invalidGeneratedAt"), valid.Replace("revoked", "active"),
            valid.Replace(fixture.Manifest.Sha256.ToLowerInvariant(), fixture.Manifest.Sha256.ToUpperInvariant()) };
        var duplicate = JsonNode.Parse(valid)!;
        duplicate["entries"]!.AsArray().Add(duplicate["entries"]![0]!.DeepClone()); invalid.Add(duplicate.ToJsonString());
        var extra = JsonNode.Parse(valid)!; extra["unexpected"] = true; invalid.Add(extra.ToJsonString());
        var oversized = JsonNode.Parse(valid)!; var entry = oversized["entries"]![0]!.DeepClone();
        oversized["entries"] = new JsonArray(Enumerable.Range(0, 1025).Select(_ => entry.DeepClone()).ToArray());
        invalid.Add(oversized.ToJsonString());
        foreach (var bad in invalid)
        {
            fixture.Http.Feed = bad; await policy.RefreshAsync(default);
            Check(policy.Source is { Status: "cached", ErrorCode: not null } && policy.Evaluate(renamed).Blocked,
                "invalid feed retains deny");
        }
        fixture.Http.Feed = valid; fixture.Http.Unavailable = true;
        await policy.RefreshAsync(default);
        Check(policy.Source.Status == "cached" && policy.Evaluate(renamed).Blocked, "source failure retains deny");
        fixture.Http.Unavailable = false; fixture.Feed(); await policy.RefreshAsync(default);
        Check(policy.Source.Status == "fresh" && policy.Evaluate(renamed).Blocked, "feed omission cannot retract revoked union");
        await policy.RefreshAsync(default);
        Check(policy.Source.Status == "fresh" && policy.Evaluate(renamed).Blocked, "repeat omitted feed stays fresh");
        fixture.Http.Feed = valid; await policy.RefreshAsync(default);
        Check(policy.Source.ErrorCode == "plugin_governance_stale" && policy.Evaluate(renamed).Blocked, "stale feed retains deny");
        var restarted = new PluginPackageService(fixture.PackagesRoot, fixture.Http);
        Check(restarted.Governance.Source.Status == "cached" && restarted.Governance.Evaluate(renamed).Blocked, "restart retains union");
        fixture.Http.Unavailable = true;
        await Deny(() => restarted.StageInstallAsync(renamed));
        var noCache = new GovernanceFixture(root, "probe.gov-unavailable"); noCache.Http.Unavailable = true;
        var unknown = await noCache.Preview();
        Check(unknown.CanInstall && unknown.Governance is { Status: "unknown", Blocked: false } &&
            unknown.GovernanceSource?.Status == "unavailable", "unavailable is explicit not active");

        foreach (var origin in new[] { "json", "repository", "catalog" })
        {
            var gate = new GovernanceFixture(root, "probe.gov-" + origin);
            var before = await gate.Preview(origin); Check(before.CanInstall, origin + " baseline preview");
            gate.Feed("revoked");
            await Deny(() => gate.Install(before));
            var after = await gate.Preview(origin);
            Check(!after.CanInstall && after.Governance?.Blocked == true, origin + " preview blocked");
            Check((await gate.Packages.GetInstalledAsync()).Count == 0, origin + " confirmation leaves no installed state");
        }
        var race = new GovernanceFixture(root, "probe.gov-race");
        race.Http.Downloaded = () => race.Feed("revoked");
        await Deny(() => race.Packages.StageInstallAsync(race.Manifest));
        Check((await race.Packages.GetInstalledAsync()).Count == 0 && race.Http.Downloads == 1, "revocation during download blocks commit");

        var commitRace = new GovernanceFixture(root, "probe.gov-commit-race");
        var commitRevision = (await commitRace.Market.ReadInstalledAsync(default)).Revision;
        var configured = false;
        Func<CancellationToken, Task> configure = async ct =>
        {
            configured = true; commitRace.Feed("revoked");
            await commitRace.Packages.Governance.RefreshAsync(ct);
        };
        var freshMethod = typeof(PluginPackageService).GetMethod("StageFreshInstallAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await Deny(() => (Task)freshMethod.Invoke(commitRace.Packages,
            [commitRace.Manifest, commitRevision, configure, CancellationToken.None])!);
        Check(configured && (await commitRace.Packages.GetInstalledAsync()).Count == 0,
            "refresh during released configuration gate prevents commit without deadlock");

        var lifecycle = new GovernanceFixture(root, "probe.gov-lifecycle"); await lifecycle.Start();
        lifecycle.Http.Archive = ReviewZip("probe.dll", Encoding.UTF8.GetBytes("synthetic second governance package"));
        var second = ReviewManifest(lifecycle.Http.Archive, lifecycle.Manifest.Id, "2.0.0", "probe.dll", "SyntheticEntry");
        var update = await lifecycle.Lifecycle("update", second); Check(update.CanApply, "update preview baseline");
        lifecycle.Feed("revoked", second);
        await Deny(() => lifecycle.Market.UpdateAsync(new(second, update.Revision, "plugin:update:governance-deny", true) { PreviewId = update.PreviewId }, "governance", default));
        Check(!(await lifecycle.Lifecycle("update", second)).CanApply, "update preview revoked");
        // A separate fixture makes the previous release the revoked target, not the current install.
        var rollback = new GovernanceFixture(root, "probe.gov-rollback"); await rollback.Start();
        rollback.Http.Archive = ReviewZip("probe.dll", Encoding.UTF8.GetBytes("synthetic rollback current package"));
        var current = ReviewManifest(rollback.Http.Archive, rollback.Manifest.Id, "2.0.0", "probe.dll", "SyntheticEntry");
        await rollback.Packages.StageInstallAsync(current); await LifecycleTestRequests.Settle(rollback.Packages, current.Id);
        var rollbackPreview = await rollback.Lifecycle("rollback"); Check(rollbackPreview.CanApply, "rollback baseline");
        rollback.Feed("revoked");
        await Deny(() => rollback.Market.RollbackAsync(new(rollback.Manifest, rollbackPreview.Revision, "plugin:rollback:governance-deny", true) { PreviewId = rollbackPreview.PreviewId }, "governance", default));
        await Deny(() => GovernanceRollbackCore(rollback.Packages, rollback.Manifest, rollbackPreview.Revision));
        var revokedPrevious = await rollback.Lifecycle("rollback");
        Check(!revokedPrevious.CanApply && revokedPrevious.TargetGovernance?.Blocked == true &&
            revokedPrevious.CurrentGovernance?.Blocked == false, "rollback warns current and blocks previous independently");
        Check((await rollback.Packages.GetInstalledAsync()).Single().Active?.Version == "2.0.0", "denied rollback preserves current");
        var clockFixture = new GovernanceFixture(root, "probe.gov-clock");
        var now = DateTimeOffset.UtcNow;
        var clockPolicy = new PluginGovernanceService(Path.Combine(root, "governance-clock-policy"), clockFixture.Http, () => now);
        await clockPolicy.RefreshAsync(default); var noNetwork = clockFixture.Http.Calls;
        now = now.AddMinutes(15).AddTicks(-1);
        Check(clockPolicy.Source.Status == "fresh", "TTL before boundary");
        now = now.AddTicks(1);
        Check(clockPolicy.Source is { Status: "cached", ErrorCode: "plugin_governance_expired" } &&
            clockPolicy.Evaluate(clockFixture.Manifest).Status == "unknown" && clockFixture.Http.Calls == noNetwork,
            "TTL exact boundary no-network downgrade");
        clockFixture.Feed("revoked"); await clockPolicy.RefreshAsync(default);
        now = now.AddDays(8); await clockPolicy.RefreshAsync(default);
        Check(clockPolicy.Source is { Status: "cached", ErrorCode: "plugin_governance_expired" } &&
            clockPolicy.Evaluate(clockFixture.Manifest).Blocked, "ancient snapshot never fresh still denied");
        return count + await RunGovernanceBoundaryChecks(root);
    }

    private static Task GovernanceRollbackCore(PluginPackageService packages, PluginPackageManifest target, string revision)
    {
        var method = typeof(PluginPackageService).GetMethod("StageLifecycleAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Func<IReadOnlyList<InstalledPluginPackage>, CancellationToken, Task> guard = (_, _) => Task.CompletedTask;
        return (Task)method.Invoke(packages, ["rollback", target.Id, target, revision, guard, CancellationToken.None, "none"])!;
    }
}

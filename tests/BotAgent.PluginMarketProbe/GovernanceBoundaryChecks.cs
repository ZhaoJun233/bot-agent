using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;

internal partial class Program
{
    private static async Task<int> RunGovernanceBoundaryChecks(string root)
    {
        var count = 0;
        void Check(bool pass, string label) { if (!pass) throw new InvalidOperationException("governance boundary: " + label); count++; }
        foreach (var idLength in new[] { 65, 80 })
        foreach (var versionLength in new[] { 65, 96 })
        {
            var fixture = new GovernanceFixture(root, $"probe.boundary-{idLength}-{versionLength}");
            fixture.Feed("revoked");
            var feed = JsonNode.Parse(fixture.Http.Feed)!.AsObject();
            var longEntry = feed["entries"]![0]!.DeepClone();
            longEntry["id"] = new string('a', idLength);
            longEntry["version"] = "1.0.0-" + new string('a', versionLength - 6);
            longEntry["status"] = "deprecated";
            longEntry["sha256"] = new string('b', 64);
            feed["entries"]!.AsArray().Insert(0, longEntry);
            fixture.Http.Feed = feed.ToJsonString();
            await fixture.Packages.Governance.RefreshAsync(default);
            Check(fixture.Packages.Governance.Source.Status == "fresh", $"mixed id{idLength}/version{versionLength} accepted");
            Check(fixture.Packages.Governance.Evaluate(fixture.Manifest).Blocked, "unrelated revoked tuple survives long valid fields");
            var longManifest = fixture.Manifest with { Id = longEntry["id"]!.GetValue<string>(),
                Version = longEntry["version"]!.GetValue<string>(), Sha256 = new string('b', 64) };
            Check(fixture.Packages.Governance.Evaluate(longManifest) is { Status: "deprecated", Match: "identity" },
                "long tuple remains evaluable");
            var restarted = new PluginPackageService(fixture.PackagesRoot, fixture.Http);
            Check(restarted.Governance.Evaluate(fixture.Manifest).Blocked, "mixed snapshot persisted revoked record");
        }
        foreach (var reason in new[] { string.Concat(Enumerable.Repeat("😀", 250)), string.Concat(Enumerable.Repeat("😀", 251)), "synthetic\u0085reason" })
        {
            var fixture = new GovernanceFixture(root, "probe.reason-" + reason.Length);
            fixture.Feed("revoked");
            var feed = JsonNode.Parse(fixture.Http.Feed)!;
            feed["entries"]![0]!["reason"] = reason;
            fixture.Http.Feed = feed.ToJsonString();
            await fixture.Packages.Governance.RefreshAsync(default);
            Check(fixture.Packages.Governance.Source.Status == (reason.Length == 500 ? "fresh" : "unavailable"),
                "reason UTF16 and Cc boundary matches market");
        }
        return count;
    }
}

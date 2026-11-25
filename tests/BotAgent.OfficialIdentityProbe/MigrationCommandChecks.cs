using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Qq;

internal static class MigrationCommandChecks
{
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }
    private static (string Index, long Alias, OfficialIdentityKey Key) Fixture(SyntheticDirectory temp)
    {
        var alias = new OfficialIdMap(temp.Map).AliasFor("synthetic-native-private");
        var key = new OfficialIdentityKey(new("app-synthetic-private", OfficialIdentityKind.C2cUser), "synthetic-native-private");
        var index = Path.Combine(temp.Root, "index.json");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() } };
        File.WriteAllText(index, JsonSerializer.Serialize(new { version = 1, sourceSha256 = Hash(temp.Map), bindings = new[] { new { identity = key, alias } } }, options));
        return (index, alias, key);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        async Task Check(string name, Action body) => await test("migration CLI: " + name, () => { body(); return Task.CompletedTask; });
        await Check("preview is read-only and prints only status, counts and digests", () =>
        {
            using var temp = new SyntheticDirectory();
            var fixture = Fixture(temp);
            var before = Hash(temp.Map);
            var paths = Directory.GetFiles(temp.Root).Order().ToArray();
            using var output = new StringWriter();
            Require(MigrationCommand.Run(["preview", temp.Map, fixture.Index], output) == 0, "preview rejected valid index");
            var text = output.ToString();
            Require(Hash(temp.Map) == before && paths.SequenceEqual(Directory.GetFiles(temp.Root).Order()), "preview wrote files");
            Require(!text.Contains(fixture.Key.NativeId) && !text.Contains(fixture.Key.Scope.AppId) && !text.Contains(temp.Root)
                && JsonNode.Parse(text)!["attributed"]!.GetValue<int>() == 1, "preview exposed identity or wrong coverage");
        });
        await Check("migration creates a recoverable candidate without changing source", () =>
        {
            using var temp = new SyntheticDirectory();
            var fixture = Fixture(temp);
            var before = Hash(temp.Map);
            var candidate = Path.Combine(temp.Root, "candidate.json");
            using var output = new StringWriter();
            Require(MigrationCommand.Run(["migrate", temp.Map, fixture.Index, candidate], output) == 0, "migration failed");
            var ids = new OfficialIdentityMap(candidate);
            Require(Hash(temp.Map) == before && ids.AliasFor(fixture.Key) == fixture.Alias && ids.LegacyCount == 0, "source or alias changed");
            var rollback = Path.Combine(temp.Root, "rollback.json");
            Require(MigrationCommand.Run(["rollback", candidate, rollback], output) == 0
                && new OfficialIdMap(rollback).OriginalOf(fixture.Alias) == fixture.Key.NativeId, "rollback lost migrated alias");
            Require(!Directory.GetFiles(temp.Root, "*.tmp").Any(), "scratch files remain");
        });
        await Check("existing output and source paths are never overwritten", () =>
        {
            using var temp = new SyntheticDirectory();
            var fixture = Fixture(temp);
            var before = Hash(temp.Map);
            var outputPath = Path.Combine(temp.Root, "existing.json");
            File.WriteAllText(outputPath, "preserve-this-file");
            var outputHash = Hash(outputPath);
            using var output = new StringWriter();
            Require(MigrationCommand.Run(["migrate", temp.Map, fixture.Index, outputPath], output) == 3
                && MigrationCommand.Run(["migrate", temp.Map, fixture.Index, temp.Map], output) == 3, "existing path accepted");
            Require(Hash(temp.Map) == before && Hash(outputPath) == outputHash && !Directory.GetFiles(temp.Root, "*.tmp").Any(), "existing data changed");
        });
        foreach (var defect in new[] { "stale", "unknown", "duplicate", "wrong-alias", "null-binding", "missing-kind", "missing-app", "missing-native", "unknown-version" })
            await Check("invalid index " + defect + " leaves no candidate", () =>
            {
                using var temp = new SyntheticDirectory();
                var fixture = Fixture(temp);
                var doc = JsonNode.Parse(File.ReadAllText(fixture.Index))!.AsObject();
                if (defect == "stale") doc["sourceSha256"] = new string('0', 64);
                if (defect == "unknown") doc["unrecognized"] = true;
                if (defect == "wrong-alias") doc["bindings"]![0]!["alias"] = fixture.Alias + 1;
                if (defect == "null-binding") doc["bindings"]![0] = null;
                if (defect == "missing-kind") doc["bindings"]![0]!["identity"]!["scope"]!.AsObject().Remove("kind");
                if (defect == "missing-app") doc["bindings"]![0]!["identity"]!["scope"]!.AsObject().Remove("appId");
                if (defect == "missing-native") doc["bindings"]![0]!["identity"]!.AsObject().Remove("nativeId");
                if (defect == "unknown-version") doc["version"] = 2;
                var json = doc.ToJsonString();
                if (defect == "duplicate") json = json.Insert(1, "\"version\":1,");
                File.WriteAllText(fixture.Index, json);
                var before = Hash(temp.Map);
                var candidate = Path.Combine(temp.Root, "candidate.json");
                using var output = new StringWriter();
                Require(MigrationCommand.Run(["migrate", temp.Map, fixture.Index, candidate], output) == 2, "invalid index accepted");
                Require(Hash(temp.Map) == before && !File.Exists(candidate) && !Directory.GetFiles(temp.Root, "*.tmp").Any(), "invalid migration changed files");
            });
        await Check("ambiguous rollback has no output and retains typed source", () =>
        {
            using var temp = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(temp.Map);
            ids.AliasFor(new(new("app-A", OfficialIdentityKind.Group), "same"));
            ids.AliasFor(new(new("app-A", OfficialIdentityKind.C2cUser), "same"));
            var before = Hash(temp.Map);
            var candidate = Path.Combine(temp.Root, "rollback.json");
            using var output = new StringWriter();
            Require(MigrationCommand.Run(["rollback", temp.Map, candidate], output) == 2
                && !File.Exists(candidate) && Hash(temp.Map) == before, "ambiguous rollback changed files");
        });
    }
}

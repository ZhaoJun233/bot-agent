using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;

internal static class RecoveryEnvelopeChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "plugin-recovery-probe-" + Guid.NewGuid().ToString("N"));
        var current = typeof(PluginPackageService).Assembly.Location;
        var unmarked = typeof(IBotPlugin).Assembly.Location;
        const string id = "test.recovery";
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "installed.json");
        var manifest = new PluginPackageManifest(id, "1.0.0", "probe.dll", "SyntheticPlugin",
            new string('a', 64), "https://example.com/plugin.zip");
        var service = new PluginPackageService(root, new FakeHttp());
        try
        {
            var legacy = JsonSerializer.SerializeToUtf8Bytes(new InstalledPluginPackage(id, Active: manifest));
            await File.WriteAllBytesAsync(path, legacy);
            check((await service.GetInstalledAsync()).Single().Active == manifest, "legacy execution remains readable");
            var old = await PluginStateCompatibility.InspectAsync(root, unmarked);
            check(old.Compatible && old.CandidateReaderVersion == 0 && old.Entries.Single().Recovery?.FormatVersion == 0,
                "unmarked candidate is conservatively limited to legacy state");
            check(File.ReadAllBytes(path).SequenceEqual(legacy), "legacy directory and preflight reads do not migrate state");

            await service.StageUninstallAsync(id);
            var envelope = await File.ReadAllBytesAsync(path);
            var valid = JsonNode.Parse(envelope)!.AsObject();
            check(valid["formatVersion"]!.GetValue<int>() == 1 && valid["execution"]?["pendingUninstall"]?.GetValue<bool>() == true,
                "ordinary package mutation atomically writes the stable envelope");
            var compatible = await PluginStateCompatibility.InspectAsync(root, current);
            check(compatible.Compatible && compatible.CandidateReaderVersion == 12
                && compatible.CandidateSha256 == Digest(File.ReadAllBytes(current)), "preflight binds actual candidate bytes and reader capability");
            old = await PluginStateCompatibility.InspectAsync(root, unmarked);
            check(!old.Compatible && old.Entries.Single().Code == "plugin_state_reader_incompatible"
                && old.Entries.Single().Recovery?.Active?.Version == "1.0.0", "downgrade refused with safe release recovery still readable");
            check(File.ReadAllBytes(path).SequenceEqual(envelope), "downgrade refusal never strips new fields");
            await File.WriteAllTextAsync(Path.Combine(directory, "installed-interrupted.tmp"), "{incomplete");
            check((await service.GetInstalledAsync()).Single().PendingUninstall, "uncommitted temporary file cannot replace durable state");

            // A future reader may use a completely different execution shape. Do not decode it.
            var future = valid.DeepClone().AsObject();
            future["minimumReaderVersion"] = compatible.CandidateReaderVersion + 1;
            future["execution"] = "synthetic future execution";
            await Write(future, rehash: true);
            var bytesBefore = await File.ReadAllBytesAsync(path);
            var observed = (await service.GetInstalledAsync()).Single();
            check(observed.MetadataFailure?.Code == "plugin_state_reader_incompatible"
                && observed.MetadataFailure.Recovery?.Active?.Version == "1.0.0", "runtime isolates future execution while retaining recovery header");
            var inspected = await PluginStateCompatibility.InspectAsync(root, current);
            check(!inspected.Compatible && inspected.Entries.Single().Code == "plugin_state_reader_incompatible",
                "compatibility fails before future execution deserialization");
            var refused = false;
            try { await service.StageUninstallAsync(id); } catch (InvalidDataException) { refused = true; }
            check(refused && File.ReadAllBytes(path).SequenceEqual(bytesBefore), "mutation cannot rewrite unsupported state");
            var loaded = await service.LoadAtStartupAsync(new PluginContext(new EmptyServices()), isEnabled: _ => true);
            check(loaded.Loaded.Count == 0 && loaded.Failures.Count == 1, "unsupported execution is never initialized");

            var mutations = new (string Name, Action<JsonObject> Mutate, bool Rehash)[]
            {
                ("digest", node => node["executionSha256"] = new string('0', 64), false),
                ("projection", node => node["active"]!["version"] = "2.0.0", false),
                ("unknown execution", node => node["execution"]!["futurePolicy"] = true, true),
                ("unknown envelope", node => node["extra"] = true, false),
                ("header type", node => node["minimumReaderVersion"] = "one", false),
                ("null reference", node => node["active"] = JsonValue.Create(5), false),
                ("missing field", node => node.Remove("executionSha256"), false),
                ("unsupported envelope", node => node["formatVersion"] = 99, false)
            };
            var goodDirectory = Path.Combine(root, "test.neighbor");
            Directory.CreateDirectory(goodDirectory);
            await File.WriteAllTextAsync(Path.Combine(goodDirectory, "installed.json"),
                JsonSerializer.Serialize(new InstalledPluginPackage("test.neighbor")));
            foreach (var mutation in mutations)
            {
                var node = valid.DeepClone().AsObject();
                mutation.Mutate(node);
                await Write(node, mutation.Rehash);
                bytesBefore = await File.ReadAllBytesAsync(path);
                var entries = await service.GetInstalledAsync();
                inspected = await PluginStateCompatibility.InspectAsync(root, current);
                check(entries.Single(entry => entry.Id == id).MetadataFailure is not null
                    && entries.Single(entry => entry.Id == "test.neighbor").MetadataFailure is null
                    && !inspected.Compatible && File.ReadAllBytes(path).SequenceEqual(bytesBefore),
                    mutation.Name + " rejected without rewriting bytes or blocking neighbor");
            }
            await File.WriteAllBytesAsync(path, envelope);
            var raw = Encoding.UTF8.GetString(envelope);
            await File.WriteAllTextAsync(path, raw.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal));
            check((await service.GetInstalledAsync()).Single(entry => entry.Id == id).MetadataFailure is not null,
                "duplicate envelope fields rejected");
            await File.WriteAllBytesAsync(path, envelope);
            inspected = await PluginStateCompatibility.InspectAsync(root, current);
            check(inspected.Compatible && !JsonSerializer.Serialize(inspected).Contains("EntryType", StringComparison.OrdinalIgnoreCase),
                "recovery output excludes executable manifests");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                check(!(await PluginStateCompatibility.InspectAsync(root, current)).Compatible, "unreadable file is not mistaken for absent state");
            check((await service.GetInstalledAsync()).Single(entry => entry.Id == id).Active == manifest, "restored envelope is executable again");
        }
        finally { Directory.Delete(root, recursive: true); }

        async Task Write(JsonObject node, bool rehash)
        {
            if (rehash) node["executionSha256"] = Digest(Encoding.UTF8.GetBytes(node["execution"]!.ToJsonString()));
            await File.WriteAllTextAsync(path, node.ToJsonString());
        }
        static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

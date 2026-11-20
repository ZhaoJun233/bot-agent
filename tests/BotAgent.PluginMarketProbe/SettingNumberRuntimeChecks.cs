using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;

internal partial class Program
{
    internal static async Task<int> RunSettingNumberRuntimeChecks(string root)
    {
        var checks = 0;
        void Check(bool valid, string label)
        { if (!valid) throw new InvalidOperationException(label); checks++; }
        static JsonElement Values(string json)
        { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
        var field = new PluginSettingDefinition("limit", "Limit", "number", "5")
        { NumberConstraints = new(1, 10, true) };
        var manifest = new PluginPackageManifest("probe.number-runtime", "1.0.0", "probe.dll", "Synthetic.Plugin",
            new string('a', 64), "https://example.com/plugin.zip", Settings: [field]);
        var store = new PluginSettingsStore();
        var stamp = store.ReadPreservationStamp(manifest.Id);
        foreach (var json in new[] { "{\"limit\":0}", "{\"limit\":11}", "{\"limit\":1.5}", "{\"limit\":1.0}" })
        {
            try { store.Save(manifest, 0, "numeric-write", Values(json), "synthetic-admin"); }
            catch (PluginSettingsException error) when (error.Code == PluginSettingsContract.InvalidValues)
            {
                Check(store.ReadPreservationStamp(manifest.Id) == stamp && store.ReadManifest(manifest).Revision == 0,
                    "invalid number leaves state and revision unchanged");
                continue;
            }
            throw new InvalidOperationException("invalid_numeric_store_write_accepted");
        }
        var saved = store.Save(manifest, 0, "numeric-write", Values("{\"limit\":10}"), "synthetic-admin");
        Check(saved.Revision == 1 && saved.RestartRequired && store.ReadManifest(manifest).Values["limit"].GetInt32() == 10,
            "same-key correction commits the inclusive bound using existing restart semantics");
        Check(store.Save(manifest, 0, "numeric-write", Values("{\"limit\":10}"), "synthetic-admin") == saved,
            "existing numeric receipt returns the original revision");
        var preserved = store.ReadPreservationStamp(manifest.Id);
        var schemaRefused = false;
        try { store.ReadManifest(manifest with { Settings = [field with { NumberConstraints = new(1, 9, true) }] }); }
        catch (PluginSettingsException error) when (error.Code == "plugin_settings_schema_mismatch")
        { schemaRefused = true; }
        Check(schemaRefused && store.ReadPreservationStamp(manifest.Id) == preserved, "changed limits isolate old schema bytes");

        var stateRoot = Path.Combine(root, "number-reader");
        var packages = new PluginPackageService(stateRoot, new ReviewPackageHttp());
        var plain = manifest with { Settings = [new("limit", "Limit", "number", "5")] };
        var statePath = Path.Combine(stateRoot, manifest.Id, "installed.json");
        foreach (var state in new[] { new InstalledPluginPackage(manifest.Id, Active: manifest),
            new InstalledPluginPackage(manifest.Id, Active: plain, Previous: manifest),
            new InstalledPluginPackage(manifest.Id, Active: plain, Pending: manifest) })
        {
            await LifecycleTestRequests.SaveState(packages, state);
            var bytes = await File.ReadAllBytesAsync(statePath);
            var envelope = JsonNode.Parse(bytes)!;
            Check(envelope["minimumReaderVersion"]!.GetValue<int>() == 10,
                "durable active previous and pending bind reader ten");
            var current = await PluginStateCompatibility.InspectAsync(stateRoot, typeof(PluginPackageService).Assembly.Location);
            Check(current.Compatible && current.CandidateReaderVersion == 12, "current PE reads numeric rules");
            if (Environment.GetEnvironmentVariable("BOTAGENT_IT_PREVIOUS_NUMBER_RUNTIME") is { Length: > 0 } previous)
            {
                var old = await ExecutePreviousToolReader(previous, stateRoot);
                Check(old.GetProperty("CandidateReaderVersion").GetInt32() == 9 && !old.GetProperty("Compatible").GetBoolean()
                    && old.GetProperty("Entries")[0].GetProperty("Code").GetString() == "plugin_state_reader_incompatible"
                    && old.GetProperty("Entries")[0].GetProperty("Recovery").GetProperty("MinimumReaderVersion").GetInt32() == 10,
                    "actual reader nine refuses every numeric lifecycle position before execution");
                var opaqueEnvelope = envelope.DeepClone(); opaqueEnvelope["execution"] = "synthetic future execution";
                var opaque = System.Text.Encoding.UTF8.GetBytes(opaqueEnvelope.ToJsonString());
                await File.WriteAllBytesAsync(statePath, opaque);
                var rejected = await ExecutePreviousToolReader(previous, stateRoot);
                Check(rejected.GetProperty("Entries")[0].GetProperty("Code").GetString() == "plugin_state_reader_incompatible"
                    && (await File.ReadAllBytesAsync(statePath)).SequenceEqual(opaque),
                    "old reader rejects even undecodable execution without changing bytes");
                await File.WriteAllBytesAsync(statePath, bytes);
            }
            var lowered = envelope.DeepClone(); lowered["minimumReaderVersion"] = 9;
            await File.WriteAllTextAsync(statePath, lowered.ToJsonString());
            Check((await packages.GetInstalledAsync()).Single().MetadataFailure is not null,
                "lowered reader header cannot authorize numeric semantics");
            await File.WriteAllBytesAsync(statePath, bytes);
        }
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_NUMBER_EVIDENCE") is { Length: > 0 } evidence)
        {
            var target = Path.Combine(evidence, "reader-state"); Directory.CreateDirectory(target);
            await File.WriteAllBytesAsync(Path.Combine(target, "installed.json"), await File.ReadAllBytesAsync(statePath));
            await File.WriteAllTextAsync(Path.Combine(target, "facts.json"), JsonSerializer.Serialize(new
            { checks, readerVersion = 10, stateSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(statePath))) }));
        }
        Console.WriteLine($"PASS setting number runtime: {checks} checks");
        return checks;
    }
}

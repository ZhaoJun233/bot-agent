using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;

internal partial class Program
{
    private static async Task<int> RunDynamicDeclarationReaderChecks(string root, PluginPackageManifest manifest,
        TrustedPluginDistribution distribution, string previous)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(manifest, options);
        foreach (var (from, to) in new[]
        {
            ("\"apiVersion\":1,\"bindings\"", "\"apiVersion\":1,\"ApiVersion\":2,\"bindings\""),
            ("\"legacyFieldId\":138", "\"legacyFieldId\":138,\"LegacyFieldId\":139"),
            ("\"legacyFieldId\":138", "\"legacyFieldId\":138,\"\\u006cegacyFieldId\":139"),
            ("\"readOnly\":true", "\"readOnly\":true,\"readOnly\":false"),
            ("\"bindings\":[", "\"futurePolicy\":true,\"bindings\":[")
        })
        {
            var wire = json.Replace(from, to, StringComparison.Ordinal);
            Check(wire != json, "wire fixture actually mutates the owner declaration");
            try { JsonSerializer.Deserialize<PluginPackageManifest>(wire, options); throw new InvalidOperationException("ambiguous_declaration_accepted"); }
            catch (JsonException) { checks++; }
        }
        foreach (var changed in new[]
        {
            manifest with { Id = "probe.unapproved-owner" },
            manifest with { Configuration = manifest.Configuration! with { Bindings = [new("model", 49)] } },
            manifest with { Configuration = manifest.Configuration! with { Bindings = manifest.Configuration!.Bindings.Append(manifest.Configuration.Bindings[0]).ToArray() } }
        })
        {
            try { LegacyPluginConfigurationFields.Validate(changed); throw new InvalidOperationException("invalid_binding_accepted"); }
            catch (InvalidDataException) { checks++; }
        }
        var stateRoot = Path.Combine(root, "configuration-reader-matrix");
        var packages = new PluginPackageService(stateRoot, new ReviewPackageHttp(), distribution);
        var plain = manifest with { Configuration = null, Settings = [] };
        var path = Path.Combine(stateRoot, manifest.Id, "installed.json");
        foreach (var state in new[] { new InstalledPluginPackage(manifest.Id, Active: manifest),
            new InstalledPluginPackage(manifest.Id, Active: plain, Previous: manifest),
            new InstalledPluginPackage(manifest.Id, Active: plain, Pending: manifest) })
        {
            await LifecycleTestRequests.SaveState(packages, state);
            var bytes = await File.ReadAllBytesAsync(path);
            var envelope = JsonNode.Parse(bytes)!;
            Check(envelope["minimumReaderVersion"]!.GetValue<int>() == 11, "configuration in every lifecycle position requires reader eleven");
            var current = await PluginStateCompatibility.InspectAsync(stateRoot, typeof(PluginPackageService).Assembly.Location);
            Check(current.Compatible && current.CandidateReaderVersion == 12, "current PE reads the configuration declaration matrix");
            var old = await ExecutePreviousToolReader(previous, stateRoot);
            Check(old.GetProperty("CandidateReaderVersion").GetInt32() == 10 && !old.GetProperty("Compatible").GetBoolean()
                && old.GetProperty("Entries")[0].GetProperty("Code").GetString() == "plugin_state_reader_incompatible"
                && (await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "actual reader ten refuses configuration without changing bytes");
            var opaque = envelope.DeepClone(); opaque["execution"] = "synthetic future execution";
            var opaqueBytes = System.Text.Encoding.UTF8.GetBytes(opaque.ToJsonString());
            await File.WriteAllBytesAsync(path, opaqueBytes);
            var refusal = await ExecutePreviousToolReader(previous, stateRoot);
            Check(refusal.GetProperty("Entries")[0].GetProperty("Code").GetString() == "plugin_state_reader_incompatible"
                && (await File.ReadAllBytesAsync(path)).SequenceEqual(opaqueBytes), "reader ten refuses before hashing or decoding unknown execution");
            await File.WriteAllBytesAsync(path, bytes);
            var lowered = envelope.DeepClone(); lowered["minimumReaderVersion"] = 10;
            var lowBytes = System.Text.Encoding.UTF8.GetBytes(lowered.ToJsonString());
            await File.WriteAllBytesAsync(path, lowBytes);
            Check(!(await PluginStateCompatibility.InspectAsync(stateRoot, typeof(PluginPackageService).Assembly.Location)).Compatible
                && (await File.ReadAllBytesAsync(path)).SequenceEqual(lowBytes), "a lowered header cannot authorize newer configuration semantics");
            await File.WriteAllBytesAsync(path, bytes);
        }
        return checks;
    }
}

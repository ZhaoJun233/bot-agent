using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Net;

internal partial class Program
{
    internal static Task<int> RunNumberConstraintChecks()
    {
        var checks = 0;
        var failures = 0;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); checks++; }
            catch (Exception error)
            { Console.WriteLine("FAIL " + name + ": " + error.GetType().Name); failures++; }
        }
        static void Require(bool value)
        { if (!value) throw new InvalidOperationException("number_constraint_assertion"); }
        static JsonElement Number(string value) => JsonDocument.Parse(value).RootElement.Clone();
        static PluginPackageManifest Manifest(IReadOnlyList<PluginSettingDefinition> fields) =>
            new("probe.settings.number", "1.0.0", "probe.dll", "Synthetic.Plugin",
                new string('a', 64), "https://example.com/plugin.zip", Settings: fields);
        static void Reject(PluginSettingDefinition field, string value, string code = PluginSettingsContract.InvalidValues)
        {
            try { PluginSettingsContract.ValidateValue(field, Number(value), false); }
            catch (PluginSettingsException error) when (error.Code == code) { return; }
            throw new InvalidOperationException("number_constraint_was_not_enforced");
        }
        foreach (var field in ResearchSettings.Schema.Where(field => field.Type == "number"))
        {
            var legacyName = char.ToUpperInvariant(field.Key[0]) + field.Key[1..];
            Require(ResearchSettings.TryGetNumericRange(legacyName, out var range));
            Check(field.Key + " owns integer range declaration", () =>
            {
                var encoded = JsonSerializer.SerializeToNode(field, json)!;
                var bounds = encoded["numberConstraints"]!;
                Require(bounds["minimum"]!.GetValue<double>() == range.Min
                    && bounds["maximum"]!.GetValue<double>() == range.Max
                    && bounds["integer"]!.GetValue<bool>());
            });
            Check(field.Key + " rejects below minimum", () => Reject(field, (range.Min - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Check(field.Key + " rejects above maximum", () => Reject(field, (range.Max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Check(field.Key + " rejects fractional value", () => Reject(field, (range.Min + 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Check(field.Key + " accepts inclusive boundaries", () =>
            {
                PluginSettingsContract.ValidateValue(field, Number(range.Min.ToString(System.Globalization.CultureInfo.InvariantCulture)), false);
                PluginSettingsContract.ValidateValue(field, Number(range.Max.ToString(System.Globalization.CultureInfo.InvariantCulture)), false);
            });
            Check(field.Key + " rejects incompatible default", () =>
            {
                try { PluginSettingsContract.ReadSchema(Manifest([field with { DefaultValue = "999999" }])); }
                catch (PluginSettingsException error) when (error.Code == PluginSettingsContract.InvalidSchema) { return; }
                throw new InvalidOperationException("number_default_was_not_enforced");
            });
        }
        Check("unconstrained old number permits finite fraction", () =>
            PluginSettingsContract.ValidateValue(new("ratio", "比例", "number", "0.5"), Number("1.25"), false));
        var legacy = Manifest([new("ratio", "比例", "number", "0.5")]);
        Check("absent constraints keep old wire properties", () =>
            Require(JsonSerializer.SerializeToNode(legacy.Settings![0], json)!.AsObject().Count == 6));
        var descriptor = JsonSerializer.SerializeToNode(legacy, json)!;
        descriptor["settings"]![0]!["numberConstraints"] =
            JsonNode.Parse("{\"minimum\":0,\"maximum\":10,\"integer\":true}");
        PluginPackageManifest Parse(JsonNode node) => PluginPackageValidation.ParseManifest(node.ToJsonString());
        Check("valid numeric manifest round trips", () =>
        {
            var valid = descriptor.DeepClone();
            valid["settings"]![0]!["defaultValue"] = "5";
            var manifest = Parse(valid);
            var roundtrip = Parse(JsonSerializer.SerializeToNode(manifest, json)!);
            Require(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(manifest, json), JsonSerializer.SerializeToNode(roundtrip, json)));
        });
        foreach (var bounds in new[] { "{\"minimum\":10,\"maximum\":0}",
            "{\"integer\":\"true\"}", "{\"minimum\":0,\"unknown\":1}", "{\"minimum\":\"0\"}" })
            Check("invalid numeric declaration rejected " + bounds.Length, () =>
            {
                var invalid = descriptor.DeepClone();
                invalid["settings"]![0]!["numberConstraints"] = JsonNode.Parse(bounds);
                try { Parse(invalid); }
                catch (Exception error) when (error is JsonException or InvalidDataException) { return; }
                throw new InvalidOperationException("invalid_numeric_declaration_accepted");
            });
        Check("numeric declaration participates in all hashes and preview", () =>
        {
            var first = descriptor.DeepClone(); first["settings"]![0]!["defaultValue"] = "5";
            var changed = first.DeepClone(); changed["settings"]![0]!["numberConstraints"]!["maximum"] = 12;
            var a = Parse(first); var b = Parse(changed);
            const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static;
            var assembly = typeof(PluginPackageService).Assembly;
            var lifecycle = assembly.GetType("BotAgent.Adapters.Plugins.PluginLifecycleManifest")!;
            var revision = assembly.GetType("BotAgent.Adapters.Plugins.PluginPackageRevision")!;
            var schemaHash = typeof(PluginSettingsStore).GetMethod("SchemaHash", hidden)!;
            Require(!Equals(schemaHash.Invoke(null, [a.Settings]), schemaHash.Invoke(null, [b.Settings])));
            Require(!Equals(lifecycle.GetMethod("Fingerprint", hidden)!.Invoke(null, [a]), lifecycle.GetMethod("Fingerprint", hidden)!.Invoke(null, [b])));
            Require(!Equals(revision.GetMethod("Intent", hidden)!.Invoke(null, ["preview", a.Id, a, false, ""]),
                revision.GetMethod("Intent", hidden)!.Invoke(null, ["preview", b.Id, b, false, ""])));
            var changes = (System.Collections.IEnumerable)lifecycle.GetMethod("Changes", hidden)!.Invoke(null, [a, b])!;
            Require(changes.Cast<object>().Single().GetType().GetProperty("Field")!.GetValue(changes.Cast<object>().Single()) as string == "settings");
        });
        Check("numeric active previous and pending require reader ten", () =>
        {
            var valid = descriptor.DeepClone(); valid["settings"]![0]!["defaultValue"] = "5";
            var manifest = Parse(valid);
            var envelope = typeof(PluginPackageService).Assembly.GetType("BotAgent.Adapters.Plugins.PluginStateEnvelope")!;
            var required = envelope.GetMethod("RequiredReaderVersion", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var state in new[] { new InstalledPluginPackage(manifest.Id, Active: manifest),
                new InstalledPluginPackage(manifest.Id, Previous: manifest), new InstalledPluginPackage(manifest.Id, Pending: manifest) })
                Require((int)required.Invoke(null, [state])! == 10);
            Require((int)required.Invoke(null, [new InstalledPluginPackage(legacy.Id, Active: legacy)])! == 1);
        });
        var observations = Environment.GetEnvironmentVariable("BOTAGENT_IT_NUMBER_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(observations))
        {
            var legacyJson = JsonSerializer.Serialize(legacy.Settings);
            Directory.CreateDirectory(observations);
            File.WriteAllText(Path.Combine(observations, "observations.json"), JsonSerializer.Serialize(new
            { checks, failures, legacySchemaJson = legacyJson, legacySchemaSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyJson))) }));
        }
        if (failures != 0) throw new InvalidOperationException($"number constraints: {checks} passed / {failures} failed");
        return Task.FromResult(checks);
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunManifestSemanticChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "BotAgent.slnx"))) repository = repository.Parent;
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(repository?.FullName
            ?? throw new InvalidOperationException("manifest_fixture_root_missing"), "tests/contracts/plugin-manifest-semantics.json")))!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        var baselines = fixture["baselines"]!.AsObject();
        Check(Properties<PluginPackageManifest>().SequenceEqual(new[] { "adoption", "apiVersion", "configuration", "control", "data", "dependencies", "downloadUrl", "entryAssembly",
            "entryType", "exclusiveResources", "id", "minimumHostVersion", "permissions", "runtime", "settings", "sha256", "sharedContracts", "targetFramework", "toolApiVersion", "ui", "version" }),
            "manifest_field_inventory_must_expand_with_contract");
        Check(Properties<PluginUiDeclaration>().SequenceEqual(new[] { "allowedActions", "assets", "contractVersion", "entry", "kind", "title" }), "ui_field_inventory");
        Check(Properties<PluginUiAsset>().SequenceEqual(new[] { "contentType", "path", "sha256" }), "ui_asset_field_inventory");
        Check(Properties<PluginPythonRuntime>().SequenceEqual(new[] { "dependencyMode", "entryPoint", "kind", "minimumPythonVersion", "protocolVersion", "tools" }), "runtime_field_inventory");
        Check(Properties<PluginSettingDefinition>().SequenceEqual(new[] { "choices", "defaultValue", "key", "label", "numberConstraints", "required", "type" }), "setting_field_inventory");
        Check(Properties<PluginSettingNumberConstraints>().SequenceEqual(new[] { "integer", "maximum", "minimum" }), "setting_number_field_inventory");
        Check(Properties<PluginPackageDependency>().SequenceEqual(new[] { "id", "version" }), "dependency_field_inventory");
        Check(Properties<PluginSharedContract>().SequenceEqual(new[] { "name", "owner", "sha256", "version" }), "shared_contract_field_inventory");
        Check(Properties<PluginPackageData>().SequenceEqual(new[] { "audit", "receipt", "statePrefixes", "tables", "usage" }), "data_field_inventory");
        Check(Properties<PluginReceiptContract>().SequenceEqual(new[] { "contract", "version" }), "receipt_contract_field_inventory");
        Check(Properties<PluginConfigurationDeclaration>().SequenceEqual(new[] { "apiVersion", "bindings" }), "configuration_field_inventory");
        Check(Properties<PluginConfigurationBinding>().SequenceEqual(new[] { "key", "legacyFieldId", "readOnly", "trim" }), "configuration_binding_field_inventory");
        var assembly = typeof(PluginPackageService).Assembly;
        var revision = assembly.GetType("BotAgent.Adapters.Plugins.PluginPackageRevision")!;
        var lifecycle = assembly.GetType("BotAgent.Adapters.Plugins.PluginLifecycleManifest")!;
        const BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
        string Intent(PluginPackageManifest m) => (string)revision.GetMethod("Intent", hidden)!.Invoke(null, ["preview", m.Id, m, false, ""])!;
        string Lifecycle(PluginPackageManifest m) => (string)lifecycle.GetMethod("Fingerprint", hidden)!.Invoke(null, [m])!;
        var packages = new LifecycleFixture(root, "probe.manifest.contract").Packages;
        foreach (var test in fixture["cases"]!.AsArray())
        {
            var baseline = baselines[test!["base"]!.GetValue<string>()]!;
            var changed = baseline.DeepClone();
            var path = test["path"]!.GetValue<string>().Split('.');
            JsonNode parent = changed;
            foreach (var part in path[..^1]) parent = parent is JsonArray array ? array[int.Parse(part)]! : parent[part]!;
            parent[path[^1]] = test["value"]?.DeepClone();
            var before = baseline.Deserialize<PluginPackageManifest>(options)!;
            var after = changed.Deserialize<PluginPackageManifest>(options)!;
            var label = string.Join('.', path);
            void Validate(PluginPackageManifest manifest)
            {
                if (manifest.Id.StartsWith("preset.", StringComparison.Ordinal))
                    BotAgent.Domain.Configuration.LegacyPluginConfigurationFields.Validate(manifest);
                else PluginPackageValidation.Validate(manifest);
            }
            Check(Intent(before) != Intent(after), "approved_intent_binds_" + label);
            Check(Lifecycle(before) != Lifecycle(after), "lifecycle_fingerprint_binds_" + label);
            var changes = (IReadOnlyList<ZbaPluginManifestChange>)lifecycle.GetMethod("Changes", hidden)!.Invoke(null, [before, after])!;
            Check(changes.Count == 1 && changes[0].Field == path[0], "lifecycle_preview_exposes_" + label);
            if (test["valid"]!.GetValue<bool>())
            {
                Validate(after);
                var clone = JsonSerializer.Deserialize<PluginPackageManifest>(JsonSerializer.Serialize(after, options), options)!;
                Check(Intent(after) == Intent(clone) && Lifecycle(after) == Lifecycle(clone), "wire_roundtrip_preserves_" + label);
                var ownerPackages = packages;
                if (after.Id.StartsWith("preset.", StringComparison.Ordinal))
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, releases = new[] { after } }, options);
                    var distribution = TrustedPluginDistribution.Read(bytes, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
                    ownerPackages = new PluginPackageService(Path.Combine(root, "manifest-configuration-" + checks), new ReviewPackageHttp(), distribution);
                }
                await LifecycleTestRequests.SaveState(ownerPackages, new(after.Id, Active: after));
                var saved = (await ownerPackages.GetInstalledAsync()).Single(item => item.Id == after.Id);
                Check(saved.MetadataFailure is null && Intent(saved.Active!) == Intent(after), "disk_roundtrip_preserves_" + label);
            }
            else
            {
                try { Validate(after); throw new InvalidOperationException("accepted_invalid_" + label); }
                catch (InvalidDataException) { checks++; }
            }
        }
        var dotnet = baselines["dotnet"]!.Deserialize<PluginPackageManifest>(options)!;
        var python = baselines["python"]!.Deserialize<PluginPackageManifest>(options)!;
        Check(Intent(dotnet) == "E6D6FC28B095C6E68736028D7D89B4C8A0D062A68930559A6EB221A4330F2875"
            && Lifecycle(dotnet) == "4E8D0E57A16B2F608CFB7C39DF579B3618C30553FCD7CCB541FF84FE11270EBF", "legacy_dotnet_receipt_fingerprint");
        Check(Intent(python) == "19BF770C3492D34EA9E9CFE378A30B24D8C872BCA36959CF853ADFBF569A70FE"
            && Lifecycle(python) == "BBF6C40F82DD5DDDD2F9710048EEB0F3FA3B5C9A050BE1CA7E642409E101CE8A", "legacy_python_receipt_fingerprint");
        Check(Intent(dotnet) != Intent(python) && Lifecycle(dotnet) != Lifecycle(python), "runtime_discriminator_is_bound");
        foreach (var key in new[] { "dotnet", "python" })
        {
            var future = baselines[key]!.DeepClone(); future["futureContribution"] = "synthetic";
            try { future.Deserialize<PluginPackageManifest>(options); throw new InvalidOperationException("accepted_unknown_manifest_field"); }
            catch (JsonException) { checks++; }
        }
        var futureContract = baselines["shared"]!.DeepClone();
        futureContract["sharedContracts"]![0]!["futureContractPolicy"] = "synthetic";
        try { futureContract.Deserialize<PluginPackageManifest>(options); throw new InvalidOperationException("accepted_unknown_contract_field"); }
        catch (JsonException) { checks++; }
        foreach (var parent in new[] { "data", "receipt" })
        {
            var futureData = baselines["data"]!.DeepClone();
            (parent == "data" ? futureData["data"]! : futureData["data"]!["receipt"]!)["futureGrant"] = true;
            try { futureData.Deserialize<PluginPackageManifest>(options); throw new InvalidOperationException("accepted_unknown_data_field"); }
            catch (JsonException) { checks++; }
        }
        return checks;
    }

    private static string[] Properties<T>() => typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order(StringComparer.Ordinal).ToArray();
}

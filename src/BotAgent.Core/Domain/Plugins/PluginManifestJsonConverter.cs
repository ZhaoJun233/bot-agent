using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotAgent.Domain.Plugins;

/// <summary>The wire discriminator rejects cross-runtime fields, including explicit nulls.</summary>
public sealed class PluginManifestJsonConverter : JsonConverter<PluginPackageManifest>
{
    private static readonly string[] Common = ["Id", "Version", "Sha256", "DownloadUrl", "ApiVersion", "MinimumHostVersion", "Permissions", "Dependencies", "Settings"];
    public override PluginPackageManifest Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Manifest must be an object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value)) throw new JsonException("Duplicate manifest field.");

        T? Get<T>(string name, T? fallback = default) => fields.TryGetValue(name, out var value) ? value.Deserialize<T>(options) : fallback;
        var api = Get<int>("ApiVersion", 1);
        var allowed = Common.Concat(api == 1 ? ["EntryAssembly", "EntryType", "TargetFramework", "SharedContracts", "Data", "Control", "ToolApiVersion", "Configuration", "Ui", "Adoption", "ExclusiveResources"] : api == 2 ? ["Runtime", "Adoption", "ExclusiveResources"] : Array.Empty<string>());
        var wireNames = allowed.Select(name => options.PropertyNamingPolicy?.ConvertName(name) ?? name).ToArray();
        if (api is not (1 or 2) || fields.Keys.Any(key => !wireNames.Contains(key,
            options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)))
            throw new JsonException("Unsupported manifest discriminator or field.");
        return new PluginPackageManifest(Get<string>("Id")!, Get<string>("Version")!,
            Get<string>("EntryAssembly"), Get<string>("EntryType"), Get<string>("Sha256")!, Get<string>("DownloadUrl")!, api,
            api == 1 ? Get<string>("TargetFramework", PluginPackageManifest.CurrentTargetFramework) : null,
            Get<string>("MinimumHostVersion"), Get<IReadOnlyList<string>>("Permissions"),
            Get<IReadOnlyList<PluginPackageDependency>>("Dependencies"), Get<IReadOnlyList<PluginSettingDefinition>>("Settings"),
            Get<PluginPythonRuntime>("Runtime")) { SharedContracts = Get<IReadOnlyList<PluginSharedContract>>("SharedContracts"),
                Data = Get<PluginPackageData>("Data"), Control = Get<bool>("Control"), ToolApiVersion = Get<int?>("ToolApiVersion"),
                Configuration = Get<PluginConfigurationDeclaration>("Configuration"),
                Ui = Get<PluginUiDeclaration>("Ui"),
                Adoption = Get<string>("Adoption"),
                ExclusiveResources = Get<IReadOnlyList<string>>("ExclusiveResources") };
    }

    public override void Write(Utf8JsonWriter writer, PluginPackageManifest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        void Put<T>(string name, T item)
        {
            writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(name) ?? name);
            JsonSerializer.Serialize(writer, item, options);
        }
        Put("Id", value.Id); Put("Version", value.Version);
        if (value.ApiVersion == 1) { Put("EntryAssembly", value.EntryAssembly); Put("EntryType", value.EntryType); }
        Put("Sha256", value.Sha256); Put("DownloadUrl", value.DownloadUrl); Put("ApiVersion", value.ApiVersion);
        if (value.ApiVersion == 1) Put("TargetFramework", value.TargetFramework);
        Put("MinimumHostVersion", value.MinimumHostVersion); Put("Permissions", value.Permissions);
        Put("Dependencies", value.Dependencies); Put("Settings", value.Settings);
        // Omission preserves old package approval fingerprints and old-reader compatibility.
        if (value.SharedContracts is { Count: > 0 }) Put("SharedContracts", value.SharedContracts);
        if (value.Data is not null) Put("Data", value.Data);
        if (value.Control) Put("Control", true);
        if (value.ToolApiVersion is { } tools) Put("ToolApiVersion", tools);
        if (value.Configuration is not null) Put("Configuration", value.Configuration);
        if (value.Ui is not null) Put("Ui", value.Ui);
        if (value.Adoption is not null) Put("Adoption", value.Adoption);
        if (value.ExclusiveResources is { Count: > 0 }) Put("ExclusiveResources", value.ExclusiveResources);
        if (value.ApiVersion == 2) Put("Runtime", value.Runtime);
        writer.WriteEndObject();
    }
}

public sealed record PluginPythonRuntime(string Kind, int ProtocolVersion, string EntryPoint,
    string MinimumPythonVersion, string DependencyMode, IReadOnlyList<string> Tools);

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
        var allowed = Common.Concat(api == 1 ? ["EntryAssembly", "EntryType", "TargetFramework"] : api == 2 ? ["Runtime"] : Array.Empty<string>());
        var wireNames = allowed.Select(name => options.PropertyNamingPolicy?.ConvertName(name) ?? name).ToArray();
        if (api is not (1 or 2) || fields.Keys.Any(key => !wireNames.Contains(key,
            options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)))
            throw new JsonException("Unsupported manifest discriminator or field.");
        return new PluginPackageManifest(Get<string>("Id")!, Get<string>("Version")!,
            Get<string>("EntryAssembly"), Get<string>("EntryType"), Get<string>("Sha256")!, Get<string>("DownloadUrl")!, api,
            api == 1 ? Get<string>("TargetFramework", PluginPackageManifest.CurrentTargetFramework) : null,
            Get<string>("MinimumHostVersion"), Get<IReadOnlyList<string>>("Permissions"),
            Get<IReadOnlyList<PluginPackageDependency>>("Dependencies"), Get<IReadOnlyList<PluginSettingDefinition>>("Settings"),
            Get<PluginPythonRuntime>("Runtime"));
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
        if (value.ApiVersion == 2) Put("Runtime", value.Runtime);
        writer.WriteEndObject();
    }
}

public sealed record PluginPythonRuntime(string Kind, int ProtocolVersion, string EntryPoint,
    string MinimumPythonVersion, string DependencyMode, IReadOnlyList<string> Tools);

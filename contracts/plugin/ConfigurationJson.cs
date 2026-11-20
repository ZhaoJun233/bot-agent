using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotAgent.Domain.Plugins;

/// <summary>Reject ambiguous declarations before approval, including escaped and case-alias duplicate keys.</summary>
public sealed class PluginConfigurationDeclarationConverter : JsonConverter<PluginConfigurationDeclaration>
{
    public override PluginConfigurationDeclaration Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var fields = ConfigurationJson.Fields(ref reader, options, "ApiVersion", "Bindings");
        if (!fields.TryGetValue("ApiVersion", out var api) || api.ValueKind != JsonValueKind.Number || !api.TryGetInt32(out var version)
            || !fields.TryGetValue("Bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid configuration declaration.");
        return new(version, bindings.Deserialize<IReadOnlyList<PluginConfigurationBinding>>(options)!);
    }

    public override void Write(Utf8JsonWriter writer, PluginConfigurationDeclaration value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        ConfigurationJson.Write(writer, options, "ApiVersion", value.ApiVersion);
        ConfigurationJson.Write(writer, options, "Bindings", value.Bindings);
        writer.WriteEndObject();
    }
}

public sealed class PluginConfigurationBindingConverter : JsonConverter<PluginConfigurationBinding>
{
    public override PluginConfigurationBinding Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var fields = ConfigurationJson.Fields(ref reader, options, "Key", "LegacyFieldId", "ReadOnly", "Trim");
        if (!fields.TryGetValue("Key", out var key) || key.ValueKind != JsonValueKind.String)
            throw new JsonException("Invalid configuration binding.");
        var identity = fields.TryGetValue("LegacyFieldId", out var id) ? id.Deserialize<int?>(options) : null;
        var readOnly = fields.TryGetValue("ReadOnly", out var read) && read.Deserialize<bool>(options);
        var trim = fields.TryGetValue("Trim", out var text) && text.Deserialize<bool>(options);
        return new(key.GetString()!, identity, readOnly, trim);
    }

    public override void Write(Utf8JsonWriter writer, PluginConfigurationBinding value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        ConfigurationJson.Write(writer, options, "Key", value.Key);
        ConfigurationJson.Write(writer, options, "LegacyFieldId", value.LegacyFieldId);
        ConfigurationJson.Write(writer, options, "ReadOnly", value.ReadOnly);
        ConfigurationJson.Write(writer, options, "Trim", value.Trim);
        writer.WriteEndObject();
    }
}

internal static class ConfigurationJson
{
    internal static Dictionary<string, JsonElement> Fields(ref Utf8JsonReader reader, JsonSerializerOptions options,
        params string[] names)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid configuration object.");
        var allowed = names.Select(name => options.PropertyNamingPolicy?.ConvertName(name) ?? name).ToArray();
        var comparer = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, comparer)) throw new JsonException("Unknown configuration field.");
            if (!fields.TryAdd(property.Name, property.Value.Clone())) throw new JsonException("Duplicate configuration field.");
        }
        return fields;
    }

    internal static void Write<T>(Utf8JsonWriter writer, JsonSerializerOptions options, string name, T value)
    {
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(name) ?? name);
        JsonSerializer.Serialize(writer, value, options);
    }
}

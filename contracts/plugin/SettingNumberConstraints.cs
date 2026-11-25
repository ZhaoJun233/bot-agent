using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotAgent.Domain.Plugins;

/// <summary>Data only. Integer values are JSON integers within JavaScript's exact integer range.</summary>
[JsonConverter(typeof(PluginSettingNumberConstraintsConverter))]
public sealed record PluginSettingNumberConstraints(double? Minimum = null, double? Maximum = null, bool Integer = false);

/// <summary>Reject ambiguous or unknown rules before normalizing an approved declaration.</summary>
public sealed class PluginSettingNumberConstraintsConverter : JsonConverter<PluginSettingNumberConstraints>
{
    public override PluginSettingNumberConstraints Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid numeric constraints.");
        double? minimum = null, maximum = null;
        var integer = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new JsonException("Duplicate numeric constraint.");
            switch (property.Name.ToLowerInvariant())
            {
                case "minimum": minimum = Bound(property.Value); break;
                case "maximum": maximum = Bound(property.Value); break;
                case "integer":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new JsonException("Invalid integer constraint.");
                    integer = property.Value.GetBoolean(); break;
                default: throw new JsonException("Unknown numeric constraint.");
            }
        }
        return new(minimum, maximum, integer);
    }

    private static double? Bound(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException("Invalid numeric boundary.");
        return number;
    }

    public override void Write(Utf8JsonWriter writer, PluginSettingNumberConstraints value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("minimum");
        if (value.Minimum is { } minimum) writer.WriteNumberValue(minimum); else writer.WriteNullValue();
        writer.WritePropertyName("maximum");
        if (value.Maximum is { } maximum) writer.WriteNumberValue(maximum); else writer.WriteNullValue();
        writer.WriteBoolean("integer", value.Integer);
        writer.WriteEndObject();
    }
}

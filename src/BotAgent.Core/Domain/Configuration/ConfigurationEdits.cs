using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotAgent.Domain.Configuration;

/// <summary>Presence is explicit: default means Keep, not Set(default(T)).</summary>
[JsonConverter(typeof(FieldEditConverterFactory))]
public readonly struct FieldEdit<T>
{
    private readonly T? _value;
    public EditKind Kind { get; }

    private FieldEdit(EditKind kind, T? value) { Kind = kind; _value = value; }
    public static FieldEdit<T> Keep() => default;
    public static FieldEdit<T> Set(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is Enum enumeration && !Enum.IsDefined(enumeration.GetType(), enumeration))
            throw new ArgumentException("invalid_enum");
        return new(EditKind.Set, value);
    }
    public static FieldEdit<T> Clear() => new(EditKind.Clear, default);
    public bool TryGetValue(out T value)
    {
        value = Kind == EditKind.Set ? _value! : default!;
        return Kind == EditKind.Set;
    }
    public override string ToString() => "FieldEdit";
}

[JsonConverter(typeof(ListRuleConverter))]
public sealed class ListRule
{
    private readonly string? _value;
    public ListRuleKind Kind { get; }
    private ListRule(ListRuleKind kind, string? value) { Kind = kind; _value = value; }
    public static ListRule Inherit() => new(ListRuleKind.Inherit, null);
    public static ListRule Explicit(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(ListRuleKind.Explicit, value);
    }
    public static ListRule AllowAll() => new(ListRuleKind.AllowAll, null);
    public bool TryGetExplicitValue(out string value)
    {
        value = Kind == ListRuleKind.Explicit ? _value! : string.Empty;
        return Kind == ListRuleKind.Explicit;
    }
    public override string ToString() => "ListRule";
}

[JsonConverter(typeof(CredentialBindingEditConverter))]
public sealed class CredentialBindingEdit
{
    private readonly ModelCredentialKey _key;
    public CredentialBindingEditKind Kind { get; }
    private CredentialBindingEdit(CredentialBindingEditKind kind, ModelCredentialKey key = default)
    { Kind = kind; _key = key; }
    public static CredentialBindingEdit KeepExisting() => new(CredentialBindingEditKind.KeepExisting);
    public static CredentialBindingEdit UsePrimaryModelCredential()
        => new(CredentialBindingEditKind.UsePrimaryModelCredential);
    public static CredentialBindingEdit Unconfigured() => new(CredentialBindingEditKind.Unconfigured);
    public static CredentialBindingEdit UseRegisteredModelCredential(ModelCredentialKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Value);
        return new(CredentialBindingEditKind.UseRegisteredModelCredential, key);
    }
    public bool TryGetRegisteredKey(out ModelCredentialKey key)
    {
        key = Kind == CredentialBindingEditKind.UseRegisteredModelCredential ? _key : default;
        return Kind == CredentialBindingEditKind.UseRegisteredModelCredential;
    }
    public override string ToString() => "CredentialBindingEdit";
}

internal sealed class ConfigurationVersionConverter : JsonConverter<ConfigurationVersion>
{
    public override ConfigurationVersion Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("invalid_version");
        var text = reader.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > 19 || (text.Length > 1 && text[0] == '0')
            || text.Any(c => c < '0' || c > '9')
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new JsonException("invalid_version");
        return new(value);
    }
    public override void Write(Utf8JsonWriter writer, ConfigurationVersion value, JsonSerializerOptions options)
    {
        if (value.Value < 0) throw new JsonException("invalid_version");
        writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

internal static class ClosedEditJson
{
    internal static JsonElement Read(ref Utf8JsonReader reader, params string[] allowed)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        ValidateUnicode(root.GetRawText());
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("invalid_edit");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new JsonException("invalid_edit_property");
        return root.Clone();
    }
    internal static int Kind(JsonElement element)
    {
        if (!element.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.Number
            || !kind.TryGetInt32(out var value))
            throw new JsonException("invalid_edit_kind");
        return value;
    }
    internal static void ValidateUnicode(string json)
    {
        var quoted = false;
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] == '"') { quoted = !quoted; continue; }
            if (!quoted || json[i] != '\\') continue;
            if (++i >= json.Length) throw new JsonException("invalid_unicode");
            if (json[i] != 'u') continue;
            if (i + 4 >= json.Length || !ushort.TryParse(json.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var unit)) throw new JsonException("invalid_unicode");
            i += 4;
            if (char.IsLowSurrogate((char)unit)) throw new JsonException("invalid_unicode");
            if (!char.IsHighSurrogate((char)unit)) continue;
            if (i + 6 >= json.Length || json[i + 1] != '\\' || json[i + 2] != 'u'
                || !ushort.TryParse(json.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low)
                || !char.IsLowSurrogate((char)low)) throw new JsonException("invalid_unicode");
            i += 6;
        }
    }
}

internal sealed class FieldEditConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FieldEdit<>);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(FieldEditConverter<>).MakeGenericType(type.GetGenericArguments()))!;
}
internal sealed class FieldEditConverter<T> : JsonConverter<FieldEdit<T>>
{
    public override FieldEdit<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var element = ClosedEditJson.Read(ref reader, "kind", "value");
        var kind = ClosedEditJson.Kind(element);
        var hasValue = element.TryGetProperty("value", out var value);
        if (kind == 0 && !hasValue) return FieldEdit<T>.Keep();
        if (kind == 2 && !hasValue) return FieldEdit<T>.Clear();
        if (kind != 1 || !hasValue || value.ValueKind == JsonValueKind.Null)
            throw new JsonException("invalid_edit_value");
        try { return FieldEdit<T>.Set(value.Deserialize<T>(options)!); }
        catch (ArgumentException) { throw new JsonException("invalid_edit_value"); }
    }
    public override void Write(Utf8JsonWriter writer, FieldEdit<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("kind", (int)value.Kind);
        if (value.TryGetValue(out var set))
        { writer.WritePropertyName("value"); JsonSerializer.Serialize(writer, set, options); }
        writer.WriteEndObject();
    }
}

internal sealed class ListRuleConverter : JsonConverter<ListRule>
{
    public override ListRule Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var element = ClosedEditJson.Read(ref reader, "kind", "value");
        var kind = ClosedEditJson.Kind(element);
        var hasValue = element.TryGetProperty("value", out var value);
        if (kind == 0 && !hasValue) return ListRule.Inherit();
        if (kind == 2 && !hasValue) return ListRule.AllowAll();
        if (kind != 1 || !hasValue || value.ValueKind != JsonValueKind.String)
            throw new JsonException("invalid_list_rule");
        return ListRule.Explicit(value.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, ListRule value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("kind", (int)value.Kind);
        if (value.TryGetExplicitValue(out var list)) writer.WriteString("value", list);
        writer.WriteEndObject();
    }
}

internal sealed class CredentialBindingEditConverter : JsonConverter<CredentialBindingEdit>
{
    public override CredentialBindingEdit Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var element = ClosedEditJson.Read(ref reader, "kind", "key");
        var kind = ClosedEditJson.Kind(element);
        var hasKey = element.TryGetProperty("key", out var key);
        if (!hasKey)
            return kind switch
            {
                0 => CredentialBindingEdit.KeepExisting(),
                1 => CredentialBindingEdit.UsePrimaryModelCredential(),
                2 => CredentialBindingEdit.Unconfigured(),
                _ => throw new JsonException("invalid_credential_binding")
            };
        if (kind != 3 || key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()))
            throw new JsonException("invalid_credential_binding");
        return CredentialBindingEdit.UseRegisteredModelCredential(new(key.GetString()!));
    }
    public override void Write(Utf8JsonWriter writer, CredentialBindingEdit value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("kind", (int)value.Kind);
        if (value.TryGetRegisteredKey(out var key)) writer.WriteString("key", key.Value);
        writer.WriteEndObject();
    }
}

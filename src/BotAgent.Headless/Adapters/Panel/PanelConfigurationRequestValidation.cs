using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Settings;
using BotAgent.Adapters.Persistence;
using System.Collections.Immutable;
using BotAgent.Domain.Configuration;

namespace BotAgent.Adapters.Panel;

internal static class PanelConfigurationRequestValidation
{
    internal static (ConfigurationSaveRequest Request, LegacySecretPlan Owner) ParseConfigurationSave(JsonElement root)
    {
        ConfigurationMembers(root, "contractRevision", "expectedVersion", "key", "changes", "secrets");
        var revision = root.GetProperty("contractRevision").GetString() ?? throw new JsonException();
        var version = JsonSerializer.Deserialize<ConfigurationVersion>(root.GetProperty("expectedVersion"), ConfigurationJson);
        var key = JsonSerializer.Deserialize<ConfigurationOperationKey>(root.GetProperty("key"), ConfigurationJson);
        var patchNode = JsonNode.Parse(root.GetProperty("changes").GetRawText())?.AsObject() ?? throw new JsonException();
        if (patchNode["registered"] is JsonObject registered)
            foreach (var name in new[] { "plugins", "providers", "policies" })
                if (!registered.ContainsKey(name)) registered[name] = new JsonArray();
        using var normalized = JsonDocument.Parse(patchNode.ToJsonString());
        ValidateConfigurationShape(normalized.RootElement, typeof(ConfigurationPatch));
        var changes = JsonSerializer.Deserialize<ConfigurationPatch>(normalized.RootElement, ConfigurationJson) ?? throw new JsonException();
        var secretsNode = root.GetProperty("secrets");
        if (secretsNode.ValueKind != JsonValueKind.Array || secretsNode.GetArrayLength() > 6) throw new JsonException();
        var owned = ImmutableArray.CreateBuilder<SensitiveInput>(); var edits = ImmutableArray.CreateBuilder<SecretEdit>();
        var slots = new HashSet<WritableSecretSlot>();
        try
        {
            foreach (var edit in secretsNode.EnumerateArray())
            {
                if (edit.ValueKind != JsonValueKind.Object || !edit.TryGetProperty("kind", out var kindNode)
                    || !kindNode.TryGetInt32(out var kind) || kind is < 0 or > 2) throw new JsonException();
                ConfigurationMembers(edit, kind == 1 ? new[] { "slot", "kind", "value" } : new[] { "slot", "kind" });
                if (!edit.GetProperty("slot").TryGetInt32(out var slotNumber) || slotNumber is < 0 or > 5) throw new JsonException();
                var slot = (WritableSecretSlot)slotNumber;
                if (!slots.Add(slot)) throw new JsonException();
                edits.Add(kind switch { 0 => SecretEdit.Keep(slot), 2 => SecretEdit.Delete(slot),
                    _ => PanelLegacySecrets.OwnedLegacySecret(slot, edit.GetProperty("value").GetString() ?? throw new JsonException(), owned) });
            }
            var owner = new LegacySecretPlan(ConfigurationResultCode.Ok, null, edits.ToImmutable(), owned.ToImmutable());
            return (new(revision, version, key, changes, owner.Secrets), owner);
        }
        catch { foreach (var input in owned) input.Dispose(); throw; }
    }

    internal static void ConfigurationMembers(JsonElement node, params string[] expected)
    {
        if (node.ValueKind != JsonValueKind.Object) throw new JsonException();
        var names = node.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Length || names.Any(name => !expected.Contains(name, StringComparer.Ordinal))) throw new JsonException();
    }

    internal static void ValidateConfigurationDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new JsonException(); ValidateConfigurationDuplicates(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) ValidateConfigurationDuplicates(item);
    }

    internal static void ValidateConfigurationShape(JsonElement node, Type declared)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (node.ValueKind == JsonValueKind.Null) { if (Nullable.GetUnderlyingType(declared) is null) throw new JsonException(); return; }
        if (type == typeof(string)) { if (node.ValueKind != JsonValueKind.String || Encoding.UTF8.GetByteCount(node.GetString()!) > 32768) throw new JsonException(); return; }
        if (type == typeof(bool)) { if (node.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException(); return; }
        if (type == typeof(int) || type.IsEnum)
        { if (!node.TryGetInt32(out var value) || type.IsEnum && !Enum.IsDefined(type, value)) throw new JsonException(); return; }
        if (type == typeof(double)) { if (!node.TryGetDouble(out var value) || !double.IsFinite(value)) throw new JsonException(); return; }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        { if (node.ValueKind != JsonValueKind.Array) throw new JsonException(); foreach (var item in node.EnumerateArray()) ValidateConfigurationShape(item, type.GetGenericArguments()[0]); return; }
        if (type.GetCustomAttribute<System.Text.Json.Serialization.JsonConverterAttribute>() is not null)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FieldEdit<>))
            {
                if (!node.TryGetProperty("kind", out var k) || !k.TryGetInt32(out var kind) || kind is < 0 or > 2) throw new JsonException();
                ConfigurationMembers(node, kind == 1 ? new[] { "kind", "value" } : new[] { "kind" });
                if (kind == 1) ValidateConfigurationShape(node.GetProperty("value"), type.GetGenericArguments()[0]);
            }
            // The registered Core converter enforces the other closed scalar/rule encodings.
            JsonSerializer.Deserialize(node, type, ConfigurationJson); return;
        }
        if (type == typeof(PolicyTarget))
        {
            var kind = node.GetProperty("kind").GetString();
            if (kind == "accountDefault") { ConfigurationMembers(node, "kind", "platformId", "accountScope"); }
            else if (kind == "instance") { ConfigurationMembers(node, "kind", "key"); ValidateConfigurationShape(node.GetProperty("key"), typeof(DriverKey)); }
            else throw new JsonException();
            JsonSerializer.Deserialize<PolicyTarget>(node, ConfigurationJson); return;
        }
        if (node.ValueKind != JsonValueKind.Object) throw new JsonException();
        // Reflection is confined to these declared Core DTO types, never a body-provided type or a settings setter.
        if (type.Namespace != typeof(ConfigurationPatch).Namespace) throw new JsonException();
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).ToDictionary(
            property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), StringComparer.Ordinal);
        foreach (var property in node.EnumerateObject())
        {
            if (!properties.TryGetValue(property.Name, out var member)) throw new JsonException();
            if (property.Value.ValueKind == JsonValueKind.Null && (Nullable.GetUnderlyingType(member.PropertyType) is not null
                || new NullabilityInfoContext().Create(member).ReadState == NullabilityState.Nullable)) continue;
            ValidateConfigurationShape(property.Value, member.PropertyType);
        }
        var constructor = type.GetConstructors().OrderByDescending(value => value.GetParameters().Length).FirstOrDefault();
        foreach (var parameter in constructor?.GetParameters() ?? Array.Empty<ParameterInfo>())
        {
            if (parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition() == typeof(FieldEdit<>)) continue;
            if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null || new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable) continue;
            if (!node.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!), out _)) throw new JsonException();
        }
    }

    internal static void ValidateConfigurationUnicode(string json)
    {
        var quoted = false;
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] == '"') { quoted = !quoted; continue; }
            if (!quoted || json[i] != '\\') continue;
            if (++i >= json.Length) throw new JsonException();
            if (json[i] != 'u') continue;
            if (i + 4 >= json.Length || !ushort.TryParse(json.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var unit)) throw new JsonException();
            i += 4;
            if (char.IsLowSurrogate((char)unit)) throw new JsonException();
            if (!char.IsHighSurrogate((char)unit)) continue;
            if (i + 6 >= json.Length || json[i + 1] != '\\' || json[i + 2] != 'u'
                || !ushort.TryParse(json.AsSpan(i + 3, 4), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var low) || !char.IsLowSurrogate((char)low)) throw new JsonException();
            i += 6;
        }
    }

    internal static readonly JsonSerializerOptions ConfigurationJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };
}

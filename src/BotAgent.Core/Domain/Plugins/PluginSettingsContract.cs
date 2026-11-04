using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotAgent.Domain.Plugins;

/// <summary>Operator view: secret values are never part of Values or Schema defaults.</summary>
public sealed record PluginSettingsView(long Revision, IReadOnlyList<PluginSettingDefinition> Schema,
    IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<string> ConfiguredSecrets, bool RestartRequired);

/// <summary>Stable, non-echoing acknowledgement; repeated requests return this same receipt.</summary>
public sealed record PluginSettingsReceipt(long Revision, string ReceiptId, bool RestartRequired);

/// <summary>Safe machine-readable failure; never includes submitted values or database diagnostics.</summary>
public sealed class PluginSettingsException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Pure validation and typed defaults for declarative plugin settings.</summary>
public static class PluginSettingsContract
{
    public const int MaximumValueLength = 4096;
    public const string InvalidSchema = "plugin_settings_invalid_schema";
    public const string InvalidValues = "plugin_settings_invalid_values";
    public const string RequiredMissing = "plugin_settings_required_missing";

    public static IReadOnlyList<PluginSettingDefinition> ReadSchema(PluginPackageManifest manifest)
    {
        if (manifest is null || manifest.Settings?.Count > 128) throw new PluginSettingsException(InvalidSchema);
        var source = manifest.Settings ?? [];
        var result = new List<PluginSettingDefinition>(source.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var size = 0;
        foreach (var field in source)
        {
            var key = field?.Key;
            var label = field?.Label;
            if (field is null || key is null || label is null ||
                !Regex.IsMatch(key, @"^[A-Za-z][A-Za-z0-9_.-]{0,79}$") ||
                !keys.Add(key) || string.IsNullOrWhiteSpace(label) || label.Length > 200 ||
                field.Type is not ("string" or "boolean" or "number" or "select" or "secret") ||
                field.DefaultValue?.Length > MaximumValueLength || field.Choices?.Count > 100 ||
                (field.Type == "secret" && field.DefaultValue is not null))
                throw new PluginSettingsException(InvalidSchema);
            var choices = field.Choices?.ToArray();
            if (choices is not null && (choices.Any(v => v is null || v.Length > MaximumValueLength) ||
                choices.Distinct(StringComparer.Ordinal).Count() != choices.Length))
                throw new PluginSettingsException(InvalidSchema);
            if (field.Type == "select" && (choices is null || choices.Length == 0))
                throw new PluginSettingsException(InvalidSchema);
            size += key.Length + label.Length + (field.DefaultValue?.Length ?? 0) +
                (choices?.Sum(v => v.Length) ?? 0);
            if (size > 32768) throw new PluginSettingsException(InvalidSchema);
            var copy = field with { Choices = choices is null ? null : Array.AsReadOnly(choices) };
            if (copy.DefaultValue is not null)
            {
                var value = DefaultValue(copy);
                ValidateValue(copy, value, allowMissing: false, InvalidSchema);
            }
            result.Add(copy);
        }
        return result.AsReadOnly();
    }

    public static JsonElement DefaultValue(PluginSettingDefinition field)
    {
        if (field.DefaultValue is null) return JsonSerializer.SerializeToElement<string?>(null);
        if (field.Type is "string" or "select") return JsonSerializer.SerializeToElement(field.DefaultValue);
        if (field.Type == "secret") throw new PluginSettingsException(InvalidSchema);
        try
        {
            using var document = JsonDocument.Parse(field.DefaultValue);
            var value = document.RootElement.Clone();
            if (value.ValueKind == JsonValueKind.Null) throw new PluginSettingsException(InvalidSchema);
            ValidateValue(field, value, allowMissing: false, InvalidSchema);
            return value;
        }
        catch (JsonException) { throw new PluginSettingsException(InvalidSchema); }
    }

    public static void ValidateValue(PluginSettingDefinition field, JsonElement value, bool allowMissing,
        string invalidCode = InvalidValues)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!allowMissing && field.Required) throw new PluginSettingsException(RequiredMissing);
            return;
        }
        var valid = field.Type switch
        {
            "string" or "secret" or "select" => value.ValueKind == JsonValueKind.String &&
                value.GetString()!.Length <= MaximumValueLength,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            _ => false
        };
        if (!valid) throw new PluginSettingsException(invalidCode);
        if (field.Type == "select" && !field.Choices!.Contains(value.GetString(), StringComparer.Ordinal))
            throw new PluginSettingsException(invalidCode);
        if (value.ValueKind == JsonValueKind.String && (field.Required || field.Type == "secret") &&
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new PluginSettingsException(invalidCode);
    }
}

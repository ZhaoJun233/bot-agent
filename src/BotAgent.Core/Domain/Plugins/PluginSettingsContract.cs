using System.Text.Json;

namespace BotAgent.Domain.Plugins;

/// <summary>Operator view: secret values are never part of Values or Schema defaults.</summary>
public sealed record PluginSettingsView(long Revision, IReadOnlyList<PluginSettingDefinition> Schema,
    IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<string> ConfiguredSecrets, bool RestartRequired);

/// <summary>Stable, non-echoing acknowledgement; repeated requests return this same receipt.</summary>
public sealed record PluginSettingsReceipt(long Revision, string ReceiptId, bool RestartRequired);

/// <summary>Compatibility facade for manifest-bound callers. Rules are owned by the independent Plugin SDK.</summary>
public static class PluginSettingsContract
{
    public const int MaximumValueLength = PluginSettingValidation.MaximumValueLength;
    public const string InvalidSchema = PluginSettingValidation.InvalidSchema;
    public const string InvalidValues = PluginSettingValidation.InvalidValues;
    public const string RequiredMissing = PluginSettingValidation.RequiredMissing;

    public static IReadOnlyList<PluginSettingDefinition> ReadSchema(PluginPackageManifest manifest)
        => manifest is null ? throw new PluginSettingsException(InvalidSchema) : PluginSettingValidation.ReadSchema(manifest.Settings);
    public static JsonElement DefaultValue(PluginSettingDefinition field) => PluginSettingValidation.DefaultValue(field);
    public static void ValidateValue(PluginSettingDefinition field, JsonElement value, bool allowMissing,
        string invalidCode = InvalidValues) => PluginSettingValidation.ValidateValue(field, value, allowMissing, invalidCode);
}

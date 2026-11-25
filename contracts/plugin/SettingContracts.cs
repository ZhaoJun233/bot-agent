namespace BotAgent.Domain.Plugins;

/// <summary>Declarative fields only; never executable UI or validation scripts.</summary>
public sealed record PluginSettingDefinition(string Key, string Label, string Type,
    string? DefaultValue = null, bool Required = false, IReadOnlyList<string>? Choices = null)
{
    /// <summary>Optional numeric rules. Omission preserves the existing wire and schema hash.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PluginSettingNumberConstraints? NumberConstraints { get; init; }
}

using System.Text.Json.Serialization;

namespace BotAgent.Domain.Configuration;

/// <summary>Read-only evidence about the saved value, separate from any individual save receipt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationRuntimeState>))]
public enum ConfigurationRuntimeState
{
    [JsonStringEnumMemberName("current")] Current,
    [JsonStringEnumMemberName("restartRequired")] RestartRequired,
    [JsonStringEnumMemberName("applyFailed")] ApplyFailed,
    [JsonStringEnumMemberName("unknown")] Unknown,
}
[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationRestartTarget>))]
public enum ConfigurationRestartTarget
{
    [JsonStringEnumMemberName("botAgent")] BotAgent,
}

public sealed record ConfigurationRuntimeEffect(
    FieldAddress Field, ConfigurationRuntimeState State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ConfigurationRestartTarget? RestartTarget = null)
{
    // Do not format addresses or configuration values into logs.
    public override string ToString() => "ConfigurationRuntimeEffect";
}

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotAgent.Domain.Configuration;

[JsonConverter(typeof(ConfigurationKeyConverterFactory))]
public readonly record struct DeviceKey(Guid Value);
public sealed record DeviceConfiguration(
    DeviceKey? Key, string NameRaw, bool Enable, string? Model,
    string? WorkDir, string? Tools, int TimeoutSeconds)
{
    public override string ToString() => "DeviceConfiguration";
}
public sealed record DeviceConfigurationView(
    DeviceKey Key, ReadField<string> Name,
    ReadField<bool> Enable, ReadField<string> Model,
    ReadField<string> WorkDir, ReadField<string> Tools,
    ReadField<int> TimeoutSeconds, ApplyState RenameState)
{
    public override string ToString() => "DeviceConfigurationView";
}
public readonly record struct DriverKey(
    string PlatformId, string AccountScope, string InstanceId);
[JsonConverter(typeof(ConfigurationKeyConverterFactory))]
public readonly record struct ProviderKey(string Value);
[JsonConverter(typeof(ConfigurationKeyConverterFactory))]
public readonly record struct PluginKey(string Value);
[JsonConverter(typeof(ConfigurationKeyConverterFactory))]
public readonly record struct ModelCredentialKey(string Value);
[JsonConverter(typeof(ConfigurationKeyConverterFactory))]
public readonly record struct ActionId(string Value);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PolicyTarget.AccountDefault), "accountDefault")]
[JsonDerivedType(typeof(PolicyTarget.Instance), "instance")]
public abstract record PolicyTarget
{
    private PolicyTarget() { }
    public sealed record AccountDefault(
        string PlatformId, string AccountScope) : PolicyTarget
    {
        public override string ToString() => "AccountDefault";
    }
    public sealed record Instance(DriverKey Key) : PolicyTarget
    {
        public override string ToString() => "Instance";
    }
}
public enum ToggleRule { Inherit = 0, Enabled = 1, Disabled = 2 }
public enum ListRuleKind { Inherit = 0, Explicit = 1, AllowAll = 2 }
public enum ActionRuleKind { Inherit = 0, Explicit = 1 }
public sealed record ActionRule(
    ActionRuleKind Kind, ImmutableArray<ActionId> Actions)
{
    public override string ToString() => "ActionRule";
}
public enum PlatformFeatureId
{
    Text = 0, Image = 1, Voice = 2, Quote = 3, Recall = 4,
    Thread = 5, Stickers = 6, Music = 7, Poke = 8,
    LinkPreview = 9, WebSearch = 10
}
public sealed record FeatureOverride(PlatformFeatureId Feature, bool Enabled)
{
    public override string ToString() => "FeatureOverride";
}
public sealed record PlatformPolicyConfiguration(
    ToggleRule Enabled, ToggleRule ChatEnabled,
    ListRule GroupWhitelist, ListRule PrivateWhitelist,
    ActionRule AllowedActions, ImmutableArray<FeatureOverride> Features)
{
    public override string ToString() => "PlatformPolicyConfiguration";
}
public sealed record PlatformPolicyEntry(
    PolicyTarget Target, PlatformPolicyConfiguration Configuration)
{
    public override string ToString() => "PlatformPolicyEntry";
}
public sealed record PlatformPolicyEntryView(
    PolicyTarget Target, ReadField<ToggleRule> Enabled,
    ReadField<ToggleRule> ChatEnabled,
    ReadField<ListRule> GroupWhitelist, ReadField<ListRule> PrivateWhitelist,
    ReadField<ActionRule> AllowedActions,
    ReadField<ImmutableArray<FeatureOverride>> Features)
{
    public override string ToString() => "PlatformPolicyEntryView";
}
public sealed record PolicyEdit(
    PolicyTarget Target, PlatformPolicyConfiguration Configuration)
{
    public override string ToString() => "PolicyEdit";
}
public sealed record PluginEnabledEdit(PluginKey Key, bool Enabled)
{
    public override string ToString() => "PluginEnabledEdit";
}
public enum CredentialBindingEditKind
{
    KeepExisting = 0, UsePrimaryModelCredential = 1,
    Unconfigured = 2, UseRegisteredModelCredential = 3
}
public sealed record ProviderMetadataPatch(
    FieldEdit<string> NameRaw, FieldEdit<int> Priority,
    FieldEdit<string> BaseUrl, FieldEdit<string> ModelName,
    FieldEdit<bool> Enabled, CredentialBindingEdit Credential)
{
    public override string ToString() => "ProviderMetadataPatch";
}
public sealed record ProviderEdit(ProviderKey Key, ProviderMetadataPatch Changes)
{
    public override string ToString() => "ProviderEdit";
}
public sealed record RegisteredConfigurationChanges(
    ImmutableArray<PluginEnabledEdit> Plugins,
    ImmutableArray<ProviderEdit> Providers, ImmutableArray<PolicyEdit> Policies)
{
    public override string ToString() => "RegisteredConfigurationChanges";
}
public enum CredentialBindingState
{
    Unconfigured = 0, PrimaryModel = 1, RegisteredModel = 2, Unclassified = 3
}
public sealed record PluginConfigurationView(PluginKey Key, bool DesiredEnabled)
{
    public override string ToString() => "PluginConfigurationView";
}
public sealed record ProviderConfigurationView(
    ProviderKey Key, ReadField<string> Name,
    ReadField<string> BaseUrl, ReadField<string> ModelName,
    int Priority, bool DesiredEnabled, CredentialBindingState Credential,
    ApplyState RuntimeApplyState)
{
    public override string ToString() => "ProviderConfigurationView";
}
public sealed record RegisteredConfigurationView(
    ImmutableArray<PluginConfigurationView> Plugins,
    ImmutableArray<ProviderConfigurationView> Providers)
{
    public override string ToString() => "RegisteredConfigurationView";
}

/// <summary>A closed scalar identity codec, never a type-name-driven deserialization registry.</summary>
internal sealed class ConfigurationKeyConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type == typeof(DeviceKey) || type == typeof(ProviderKey)
        || type == typeof(PluginKey) || type == typeof(ModelCredentialKey) || type == typeof(ActionId)
        || type == typeof(ConfigurationGeneration) || type == typeof(ConfigurationOperationKey)
        || type == typeof(BotAgent.Domain.Management.ManagedOperationKey);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
    {
        if (!CanConvert(type)) throw new JsonException("invalid_key_type");
        return (JsonConverter)Activator.CreateInstance(typeof(ConfigurationKeyConverter<>).MakeGenericType(type))!;
    }
}

internal sealed class ConfigurationKeyConverter<T> : JsonConverter<T> where T : struct
{
    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("invalid_identity");
        using var document = JsonDocument.ParseValue(ref reader);
        ClosedEditJson.ValidateUnicode(document.RootElement.GetRawText());
        return (T)FromString(document.RootElement.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToStringValue(value));
    private static object FromString(string text)
    {
        if (typeof(T) == typeof(DeviceKey)) return new DeviceKey(ParseGuid(text));
        if (typeof(T) == typeof(ConfigurationGeneration)) return new ConfigurationGeneration(ParseGuid(text));
        if (typeof(T) == typeof(ConfigurationOperationKey))
        {
            var parts = ParseOperation(text, "cfg1");
            return new ConfigurationOperationKey(new(parts.Generation), parts.Issued, parts.Nonce);
        }
        if (typeof(T) == typeof(BotAgent.Domain.Management.ManagedOperationKey))
        {
            var parts = ParseOperation(text, "mg1");
            return new BotAgent.Domain.Management.ManagedOperationKey(new(parts.Generation), parts.Issued, parts.Nonce);
        }
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128) throw new JsonException("invalid_identity");
        if (typeof(T) == typeof(ProviderKey)) return new ProviderKey(text);
        if (typeof(T) == typeof(PluginKey)) return new PluginKey(text);
        if (typeof(T) == typeof(ModelCredentialKey)) return new ModelCredentialKey(text);
        if (typeof(T) == typeof(ActionId)) return new ActionId(text);
        throw new JsonException("invalid_key_type");
    }
    private static Guid ParseGuid(string text)
    {
        if (text.Length != 32 || text.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !Guid.TryParseExact(text, "N", out var value) || value == Guid.Empty) throw new JsonException("invalid_guid_identity");
        return value;
    }
    private static (Guid Generation, long Issued, Guid Nonce) ParseOperation(string text, string prefix)
    {
        var parts = text.Split(':');
        if (text.Length > 96 || parts.Length != 4 || parts[0] != prefix || parts[2].Length == 0
            || parts[2].Any(c => c < '0' || c > '9') || (parts[2].Length > 1 && parts[2][0] == '0')
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var issued)
            || issued > long.MaxValue - 86400) throw new JsonException("invalid_operation_key");
        return (ParseGuid(parts[1]), issued, ParseGuid(parts[3]));
    }
    private static string ToStringValue(object value)
    {
        var text = value switch
        {
            DeviceKey key => key.Value.ToString("N"),
            ConfigurationGeneration key => key.Value.ToString("N"),
            ProviderKey key => key.Value, PluginKey key => key.Value,
            ModelCredentialKey key => key.Value, ActionId key => key.Value,
            ConfigurationOperationKey key => $"cfg1:{key.Generation.Value:N}:{key.IssuedUnixSeconds.ToString(CultureInfo.InvariantCulture)}:{key.Nonce:N}",
            BotAgent.Domain.Management.ManagedOperationKey key => $"mg1:{key.Generation.Value:N}:{key.IssuedUnixSeconds.ToString(CultureInfo.InvariantCulture)}:{key.Nonce:N}",
            _ => throw new JsonException("invalid_key_type")
        };
        FromString(text); return text;
    }
}

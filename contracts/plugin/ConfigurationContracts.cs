using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotAgent.Domain.Plugins;

/// <summary>Explicit adoption of existing non-secret fields. Version one binds every editable setting.</summary>
[JsonConverter(typeof(PluginConfigurationDeclarationConverter))]
public sealed record PluginConfigurationDeclaration(int ApiVersion, IReadOnlyList<PluginConfigurationBinding> Bindings);

[JsonConverter(typeof(PluginConfigurationBindingConverter))]
public sealed record PluginConfigurationBinding(string Key, int? LegacyFieldId = null, bool ReadOnly = false, bool Trim = false);

/// <summary>A complete immutable view of this package's declared values at one configuration version.</summary>
public sealed record PluginConfigurationSnapshot(long Revision, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>Acquired during preparation, readable only while the owning runtime generation is Ready.</summary>
public interface IPluginConfiguration
{
    PluginConfigurationSnapshot Read();
}

/// <summary>Trusted build input; never initialized while collecting the owner's declarations.</summary>
public sealed record PluginConfigurationDefinition(IReadOnlyList<PluginSettingDefinition> Settings,
    PluginConfigurationDeclaration Configuration);

public interface IPluginConfigurationDefinition
{
    PluginConfigurationDefinition ConfigurationDefinition { get; }
}

using System.Text.Json.Serialization;

namespace BotAgent.Domain.Plugins;

/// <summary>Requested resources. A declaration alone never grants access.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginPackageData(
    IReadOnlyList<string> Tables, IReadOnlyList<string> StatePrefixes,
    bool Usage = false, bool Audit = false, PluginReceiptContract? Receipt = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginReceiptContract(string Contract, string Version);

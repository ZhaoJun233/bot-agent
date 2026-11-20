using System.Text.Json.Serialization;

namespace BotAgent.Domain.Plugins;

/// <summary>Hash-pinned UI asset packaged inside a plugin ZIP under <c>web/</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginUiAsset(string Path, string Sha256, string ContentType);

/// <summary>
/// Declarative runtime UI bundle metadata bound to manifest approval and package generation.
/// Assets are served read-only by exact release SHA-256 and mounted inside a sandboxed iframe.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginUiDeclaration(
    int ContractVersion,
    string Entry,
    IReadOnlyList<PluginUiAsset> Assets,
    IReadOnlyList<string>? AllowedActions = null,
    string? Kind = null,
    string? Title = null)
{
    public const int CurrentContractVersion = 1;
}

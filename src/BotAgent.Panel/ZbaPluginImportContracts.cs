using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

public sealed record ZbaPluginDiscoveryRequest([property: JsonRequired] string SourceUrl);
public sealed record ZbaPluginDiscoveryCandidate(string PluginId, string Version, ZbaPluginMetadata? Metadata);
public sealed record ZbaPluginDiscovery(string DiscoveryId, string SourceUrl, string SourceRevision,
    string ExpiresAt, IReadOnlyList<ZbaPluginDiscoveryCandidate> Candidates);
public sealed record ZbaPluginPreviewRequest([property: JsonRequired] string Origin, [property: JsonRequired] string ExpectedRevision,
    string? DiscoveryId = null, string? PluginId = null, string? Version = null, PluginPackageManifest? Manifest = null);
public sealed record ZbaPluginPreviewSource(string Kind, string? Url = null, string? Revision = null);
public sealed record ZbaPluginPreviewCheck(string Code, string Status, string Message);
public sealed record ZbaPluginPreview(string PreviewId, string ExpiresAt, string Revision,
    PluginPackageManifest Manifest, ZbaPluginPreviewSource Source, ZbaPluginMetadata? Metadata,
    string? InstalledVersion, bool EnabledAfterInstall, bool CanInstall,
    IReadOnlyList<ZbaPluginPreviewCheck> Checks, string TrustNotice)
{
    public ZbaPluginGovernance? Governance { get; init; }
    public ZbaPluginGovernanceSource? GovernanceSource { get; init; }
}
using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

public sealed record ZbaPluginLifecycleCandidate(string Origin, string? DiscoveryId = null,
    string? Version = null, PluginPackageManifest? Manifest = null);
public sealed record ZbaPluginLifecyclePreviewRequest(string Kind, string PluginId, string ExpectedRevision,
    ZbaPluginLifecycleCandidate? Candidate = null);
public sealed record ZbaPluginLifecycleSource(string Origin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Url,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Revision);
public sealed record ZbaPluginManifestChange(string Field,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Before,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? After);
public sealed record ZbaPluginPreservation(bool Settings, bool UserData, bool EnabledIntent);
public sealed record ZbaPluginLifecyclePreview(string PreviewId, string ExpiresAt, string Revision,
    string Kind, string PluginId, string Recovery, PluginPackageManifest CurrentManifest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PluginPackageManifest? PendingManifest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PluginPackageManifest? TargetManifest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ZbaPluginLifecycleSource? TargetSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ZbaPluginMetadata? Metadata,
    bool Enabled, string CurrentStatus, bool RestartRequired, bool CanApply,
    IReadOnlyList<ZbaPluginManifestChange> Changes, IReadOnlyList<ZbaPluginPreviewCheck> Checks,
    ZbaPluginPreservation Preservation, string TrustNotice)
{
    public ZbaPluginGovernance? CurrentGovernance { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ZbaPluginGovernance? TargetGovernance { get; init; }
    public ZbaPluginGovernanceSource? GovernanceSource { get; init; }
}
using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;

namespace BotAgent.Panel;

public sealed record ZbaPluginUiSession(
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] string PluginId,
    [property: JsonRequired] string Version,
    [property: JsonRequired] string ReleaseSha256,
    [property: JsonRequired] string GenerationId,
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string EntryUrl,
    [property: JsonRequired] IReadOnlyList<string> AllowedActions,
    [property: JsonRequired] bool BackendReady,
    [property: JsonRequired] bool VersionMatched,
    [property: JsonRequired] string AdoptionMode,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string Title,
    [property: JsonRequired] DateTimeOffset ExpiresAt)
{
    public override string ToString() => "ZbaPluginUiSession";
}

public sealed record ZbaPluginUiSessionClosed(
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] string PluginId,
    [property: JsonRequired] bool Closed)
{
    public override string ToString() => "ZbaPluginUiSessionClosed";
}

public sealed record ZbaPluginUiAssetResult(
    int Status,
    string ContentType,
    byte[] Bytes,
    string? ErrorCode,
    string? ContentSecurityPolicy,
    string CacheControl);

public sealed record ZbaPluginBundlePreviewRequest(
    [property: JsonRequired] IReadOnlyList<PluginPackageManifest> Manifests)
{
    public override string ToString() => "ZbaPluginBundlePreviewRequest";
}

public sealed record ZbaPluginBundlePreview(
    [property: JsonRequired] string PreviewId,
    [property: JsonRequired] string ExpiresAt,
    [property: JsonRequired] string Revision,
    [property: JsonRequired] bool CanApply,
    [property: JsonRequired] string AdoptionMode,
    [property: JsonRequired] IReadOnlyList<string> AffectedIds,
    [property: JsonRequired] IReadOnlyList<ZbaPluginPreviewCheck> Checks)
{
    public override string ToString() => "ZbaPluginBundlePreview";
}

public sealed record ZbaPluginBundleApplyRequest(
    [property: JsonRequired] IReadOnlyList<PluginPackageManifest> Manifests,
    [property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] bool TrustAcknowledged,
    string? PreviewId = null)
{
    public override string ToString() => "ZbaPluginBundleApplyRequest";
}

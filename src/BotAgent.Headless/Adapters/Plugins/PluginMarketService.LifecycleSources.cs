using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginMarketService
{
    private sealed record LifecycleTarget(PluginPackageManifest Manifest, ZbaPluginLifecycleSource Source,
        ZbaPluginMetadata? Metadata, string? SourceFingerprint);

    private async Task<LifecycleTarget> LifecycleTargetAsync(string id, ZbaPluginLifecycleCandidate candidate,
        string caller, CancellationToken ct)
    {
        try
        {
            if (candidate.Origin == "repository")
            {
                if (candidate.Manifest is not null || candidate.DiscoveryId is null || candidate.Version is null)
                    throw InvalidLifecycle();
                var discovery = _imports.Get<DiscoverySnapshot>(candidate.DiscoveryId, caller, "discovery");
                var selected = discovery.Candidates.SingleOrDefault(c => c.Manifest.Id == id && c.Manifest.Version == candidate.Version)
                    ?? throw InvalidLifecycle();
                if (await _repositories.ResolveAsync(discovery.Url, ct).ConfigureAwait(false) != discovery.Revision)
                    throw PluginImportSnapshots.Refresh();
                return new(selected.Manifest, new("repository", discovery.Url, discovery.Revision), selected.Metadata, null);
            }
            if (candidate.Origin is not ("catalog" or "json") || candidate.Manifest is null || candidate.DiscoveryId is not null ||
                candidate.Origin == "json" && candidate.Version is not null ||
                candidate.Version is not null && candidate.Version != candidate.Manifest.Version)
                throw InvalidLifecycle();
            var manifest = CloneManifest(candidate.Manifest);
            if (manifest.Id != id) throw InvalidLifecycle();
            PluginPackageValidation.Validate(manifest, checkCompatibility: false);
            PluginImportPolicy.Validate(manifest);
            if (candidate.Origin == "json") return new(manifest, new("json", null, null), null, null);
            var catalog = await ReadCatalogSnapshotAsync(ct).ConfigureAwait(false);
            manifest = catalog.Manifests.FirstOrDefault(m => ManifestFingerprint(m) == ManifestFingerprint(manifest))
                ?? throw PluginImportSnapshots.Refresh();
            return new(manifest, new("catalog", catalog.Url, null), catalog.Presentation.ForPackage(manifest), catalog.Fingerprint);
        }
        catch (Exception ex) when (SourceFailure(ex, ct))
        { throw new ZbaBackendException(422, "plugin_preview_invalid", "清单或来源不可用，请核对后重新预览。"); }
    }

    private async Task ValidateLifecycleSourceAsync(LifecycleSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            if (snapshot.Source?.Origin == "repository" &&
                await _repositories.ResolveAsync(snapshot.Source.Url!, ct).ConfigureAwait(false) != snapshot.Source.Revision)
                throw PluginImportSnapshots.Refresh();
            if (snapshot.Source?.Origin == "catalog")
            {
                var catalog = await ReadCatalogSnapshotAsync(ct).ConfigureAwait(false);
                if (catalog.Url != snapshot.Source.Url || catalog.Fingerprint != snapshot.SourceFingerprint)
                    throw PluginImportSnapshots.Refresh();
            }
        }
        catch (Exception ex) when (SourceFailure(ex, ct)) { throw PluginImportSnapshots.Refresh(); }
    }
    private static ZbaBackendException InvalidLifecycle()
        => new(400, "invalid_plugin_lifecycle_preview", "操作、插件身份或候选来源无效，字段不可混用。");
}
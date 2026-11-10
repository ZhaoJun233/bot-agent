using System.Security.Cryptography;
using System.Text.Json;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginMarketService
{
    private readonly PluginImportSnapshots _imports;
    private readonly PluginRepositorySource _repositories;
    private sealed record Candidate(PluginPackageManifest Manifest, ZbaPluginMetadata? Metadata);
    private sealed record DiscoverySnapshot(string Url, string Revision, IReadOnlyList<Candidate> Candidates);
    private sealed record PreviewSnapshot(PluginPackageManifest Manifest, ZbaPluginPreviewSource Source,
        string Revision, string Fingerprint, string? SourceFingerprint, bool CanInstall);
    private sealed record CatalogSnapshot(string Url, string Fingerprint, IReadOnlyList<PluginPackageManifest> Manifests,
        PluginPresentationCatalog Presentation);

    public async Task<ZbaPluginDiscovery> DiscoverSourceAsync(string sourceUrl, string caller, CancellationToken ct)
    {
        ValidateImportCaller(caller);
        try
        {
            var source = await _repositories.DiscoverAsync(sourceUrl, ct).ConfigureAwait(false);
            var candidates = source.Manifests.Select(m => new Candidate(m, Community(source.Presentation.ForPackage(m), m))).ToArray();
            var snapshot = new DiscoverySnapshot(source.Url, source.Revision, candidates);
            var stored = _imports.Add(caller, "discovery", snapshot);
            return new(stored.Id, source.Url, source.Revision, stored.ExpiresAt,
                candidates.Select(c => new ZbaPluginDiscoveryCandidate(c.Manifest.Id, c.Manifest.Version, c.Metadata)).ToArray());
        }
        catch (Exception ex) when (SourceFailure(ex, ct))
        { throw new ZbaBackendException(422, "plugin_source_unavailable", "来源解析失败：请检查公开仓库、固定 tag 和 repository.json。"); }
    }

    public async Task<ZbaPluginPreview> PreviewAsync(ZbaPluginPreviewRequest request, string caller, CancellationToken ct)
    {
        ValidateImportCaller(caller);
        if (request.Origin is not ("repository" or "catalog" or "json")) throw InvalidPreview();
        PluginPackageManifest manifest;
        ZbaPluginMetadata? metadata = null;
        ZbaPluginPreviewSource source;
        string? sourceFingerprint = null;
        try
        {
            if (request.Origin == "repository")
            {
                if (request.Manifest is not null || request.PluginId is null || request.Version is null) throw InvalidPreview();
                var discovery = _imports.Get<DiscoverySnapshot>(request.DiscoveryId, caller, "discovery");
                var candidate = discovery.Candidates.SingleOrDefault(c => c.Manifest.Id == request.PluginId && c.Manifest.Version == request.Version)
                    ?? throw InvalidPreview();
                if (await _repositories.ResolveAsync(discovery.Url, ct).ConfigureAwait(false) != discovery.Revision) throw PluginImportSnapshots.Refresh();
                manifest = candidate.Manifest;
                metadata = candidate.Metadata;
                source = new("repository", discovery.Url, discovery.Revision);
            }
            else
            {
                if (request.Manifest is null || request.DiscoveryId is not null || request.PluginId is not null || request.Version is not null)
                    throw InvalidPreview();
                manifest = CloneManifest(request.Manifest);
                PluginPackageValidation.Validate(manifest, checkCompatibility: false);
                PluginImportPolicy.Validate(manifest);
                source = new(request.Origin);
                if (request.Origin == "catalog")
                {
                    var catalog = await ReadCatalogSnapshotAsync(ct).ConfigureAwait(false);
                    var fingerprint = ManifestFingerprint(manifest);
                    manifest = catalog.Manifests.FirstOrDefault(m => ManifestFingerprint(m) == fingerprint) ?? throw PluginImportSnapshots.Refresh();
                    metadata = catalog.Presentation.ForPackage(manifest);
                    source = new("catalog", catalog.Url);
                    sourceFingerprint = catalog.Fingerprint;
                }
            }
            PluginPackageValidation.Validate(manifest, checkCompatibility: false);
            PluginImportPolicy.Validate(manifest);
        }
        catch (Exception ex) when (SourceFailure(ex, ct))
        { throw new ZbaBackendException(422, "plugin_preview_invalid", "清单或来源不可用，请核对后重新预览。"); }

        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var revision = PluginPackageRevision.Of(installed);
        if (revision != request.ExpectedRevision) throw PluginImportSnapshots.Refresh();
        await _packages.Governance.RefreshAsync(ct).ConfigureAwait(false);
        var checks = await WithPythonCheckAsync(PreviewChecks(manifest, installed), manifest, ct).ConfigureAwait(false);
        var canInstall = checks.All(c => c.Status != "blocked");
        var stored = _imports.Add(caller, "preview", new PreviewSnapshot(manifest, source, revision,
            ManifestFingerprint(manifest), sourceFingerprint, canInstall));
        var existing = installed.FirstOrDefault(p => p.Id == manifest.Id);
        return new(stored.Id, stored.ExpiresAt, revision, CloneManifest(manifest), source, metadata,
            (existing?.Active ?? existing?.Pending)?.Version, false, canInstall, checks, TrustNotice)
            { Governance = _packages.Governance.Evaluate(manifest), GovernanceSource = _packages.Governance.Source };
    }

    private async Task ValidatePreviewAsync(ZbaPluginMutationRequest request, string caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PreviewId))
            throw new ZbaBackendException(409, "plugin_preview_required", "安装前必须先预览并确认风险。");
        await _packages.Governance.RefreshAsync(ct).ConfigureAwait(false);
        _packages.Governance.EnsureAllowed(request.Manifest);
        var preview = _imports.Get<PreviewSnapshot>(request.PreviewId, caller, "preview");
        if (!preview.CanInstall || !request.TrustAcknowledged || preview.Revision != request.ExpectedRevision
            || preview.Fingerprint != ManifestFingerprint(request.Manifest)) throw PluginImportSnapshots.Refresh();
        try
        {
            if (preview.Source.Kind == "repository" && await _repositories.ResolveAsync(preview.Source.Url!, ct).ConfigureAwait(false) != preview.Source.Revision)
                throw PluginImportSnapshots.Refresh();
            if (preview.Source.Kind == "catalog")
            {
                var catalog = await ReadCatalogSnapshotAsync(ct).ConfigureAwait(false);
                if (catalog.Url != preview.Source.Url || catalog.Fingerprint != preview.SourceFingerprint) throw PluginImportSnapshots.Refresh();
            }
        }
        catch (Exception ex) when (SourceFailure(ex, ct)) { throw PluginImportSnapshots.Refresh(); }
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        if (PluginPackageRevision.Of(installed) != preview.Revision || (await WithPythonCheckAsync(PreviewChecks(preview.Manifest, installed), preview.Manifest, ct).ConfigureAwait(false)).Any(c => c.Status == "blocked"))
            throw PluginImportSnapshots.Refresh();
        // Network checks may consume the remaining lifetime; never admit an expired snapshot afterward.
        _imports.Get<PreviewSnapshot>(request.PreviewId, caller, "preview");
    }

    private IReadOnlyList<ZbaPluginPreviewCheck> PreviewChecks(PluginPackageManifest manifest, IReadOnlyList<InstalledPluginPackage> installed)
    {
        var existing = installed.FirstOrDefault(p => p.Id == manifest.Id);
        var registered = _registrySnapshot?.Invoke() ?? [];
        var occupied = existing is { Active: not null } or { Pending: not null } or { PendingUninstall: true }
            || registered.Any(p => p.Id == manifest.Id);
        var dependencies = PluginDependencyPolicy.AreSatisfied(manifest, installed, registered);
        return [
            GovernanceCheck(manifest),
            new("manifest", "passed", "固定身份、SHA-256、入口与声明形状有效；不代表实际包已核验。"),
            new("new_install", occupied ? "blocked" : "passed", occupied ? "此插件已安装或存在待处理状态；本入口只接受全新安装。" : "此 ID 当前没有已安装或待处理包。"),
            new("compatibility", PluginPackageValidation.IsCompatible(manifest) ? "passed" : "blocked", "检查 API、目标框架及最低宿主版本。"),
            new("dependencies", dependencies ? "passed" : "blocked", dependencies ? "同运行时的固定版本依赖已具备。" : "缺少依赖、版本或运行时不符，或依赖有待处理变更。"),
            new("artifact", "pending", "确认后才下载 ZIP 并核验 SHA-256、归档路径与入口；现在尚未核验。"),
            new("execution", "pending", "安装/加载仍可能执行第三方构造器等代码，不是沙箱。业务能力默认停用，配置后可另行启用。")
        ];
    }

    private async Task<CatalogSnapshot> ReadCatalogSnapshotAsync(CancellationToken ct)
    {
        var url = PluginCatalogSource.ConfiguredUrl();
        var index = await _catalog.FetchAsync(url, ct).ConfigureAwait(false);
        var managed = PluginCatalogSource.Parse(index);
        var pythonBytes = await _catalog.ReadPythonBytesAsync(url, "python-repository.json", ct).ConfigureAwait(false);
        var python = pythonBytes is null ? [] : PluginPythonRepository.Parse(pythonBytes);
        if (managed.Any(m => python.Any(p => p.Id == m.Id))) throw new PluginCatalogException("plugin_runtime_id_collision");
        IReadOnlyList<PluginPackageManifest> manifests = managed.Concat(python).ToArray();
        var presentationBytes = await _catalog.ReadOptionalJsonAsync(new Uri(new Uri(url), "presentation.json").AbsoluteUri, ct).ConfigureAwait(false);
        var presentation = PluginPresentationCatalog.Unavailable();
        if (presentationBytes is not null)
            try { presentation = PluginPresentationCatalog.Parse(presentationBytes); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        var pythonPresentation = await _catalog.ReadPythonBytesAsync(url, "python-presentation.json", ct).ConfigureAwait(false);
        if (pythonPresentation is not null)
            try { presentation = presentation.Merge(PluginPresentationCatalog.Parse(pythonPresentation)); } catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        static string Digest(byte[]? data) => data is null ? "missing" : Convert.ToHexString(SHA256.HashData(data));
        var fingerprint = Hash(url + ":" + Digest(index) + ":" + Digest(presentationBytes) + ":" + Digest(pythonBytes) + ":" + Digest(pythonPresentation));
        return new(url, fingerprint, manifests, presentation);
    }

    private static ZbaPluginMetadata? Community(ZbaPluginMetadata? metadata, PluginPackageManifest manifest)
        => metadata is null ? null : metadata with { SourceKind = "community", Review = new("unreviewed", manifest.Version, manifest.Sha256.ToLowerInvariant()) };
    private static string ManifestFingerprint(PluginPackageManifest manifest)
        => PluginPackageRevision.Intent("preview", manifest.Id, manifest, false, "");
    private static PluginPackageManifest CloneManifest(PluginPackageManifest manifest)
        => JsonSerializer.Deserialize<PluginPackageManifest>(JsonSerializer.SerializeToUtf8Bytes(manifest)) ?? throw InvalidPreview();
    private static ZbaBackendException InvalidPreview() => new(400, "invalid_plugin_preview", "请选择发现候选，或提交完整清单；来源字段不可混用。");
    private static void ValidateImportCaller(string caller)
    {
        if (string.IsNullOrWhiteSpace(caller) || caller.Length > 256 || caller.Any(char.IsControl)) throw InvalidPreview();
    }
    private static bool SourceFailure(Exception ex, CancellationToken ct) => ex is PluginCatalogException or JsonException or InvalidDataException or HttpRequestException or IOException
        || ex is OperationCanceledException && !ct.IsCancellationRequested;
}

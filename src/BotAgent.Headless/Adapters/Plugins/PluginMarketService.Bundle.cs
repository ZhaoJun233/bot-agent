using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginMarketService
{
    public async Task<ZbaPluginBundlePreview> PreviewBundleAsync(
        ZbaPluginBundlePreviewRequest request, string caller, CancellationToken ct)
    {
        if (request.Manifests is null || request.Manifests.Count is 0 or > 16)
            throw new ZbaBackendException(400, "plugin_bundle_invalid", "组合变更必须包含 1 到 16 个插件清单。");

        var targets = new List<PluginPackageManifest>(request.Manifests.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in request.Manifests)
        {
            var manifest = CloneManifest(raw);
            if (!seen.Add(manifest.Id))
                throw new ZbaBackendException(400, "plugin_bundle_duplicate_id", "组合变更中存在重复的插件 ID。");
            try { PluginPackageValidation.Validate(manifest, checkCompatibility: true); }
            catch (InvalidDataException)
            { throw new ZbaBackendException(400, "invalid_plugin_manifest", "组合中包含无效或不兼容的插件清单。"); }
            targets.Add(manifest);
        }

        await _packages.Governance.RefreshAsync(ct).ConfigureAwait(false);
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var revision = PluginPackageRevision.Of(installed);
        var checks = InspectBundle(targets, installed);
        var canApply = checks.All(check => check.Status != "blocked");
        var adoptionMode = targets.Any(t =>
                PluginUiValidation.RequiresColdStart(t)
                || installed.Any(p => p.Id == t.Id && (PluginUiValidation.RequiresColdStart(p.Active)
                    || PluginUiValidation.RequiresColdStart(p.Pending))))
            ? PluginAdoptionMode.Cold : PluginAdoptionMode.Online;
        var binding = BundleBinding(targets, revision);
        var (previewId, expiresAt) = _imports.Add(caller, "bundle",
            new BundleSnapshot(targets.ToArray(), revision, binding, canApply, adoptionMode));

        return new ZbaPluginBundlePreview(
            previewId,
            expiresAt,
            revision,
            canApply,
            adoptionMode,
            targets.Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            checks);
    }

    public Task<ZbaPluginOperationResult> ApplyBundleAsync(
        ZbaPluginBundleApplyRequest request, string caller, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.PreviewId))
            throw new ZbaBackendException(409, "plugin_preview_required", "请先预览组合变更影响，再确认执行。");
        if (!request.TrustAcknowledged)
            throw new ZbaBackendException(400, "trust_not_acknowledged", "请先确认信任声明。");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 256 || request.IdempotencyKey.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(caller) || caller.Length > 256 || caller.Any(char.IsControl))
            throw new ZbaBackendException(400, "invalid_plugin_operation", "插件操作键或调用方无效。");

        var targets = (request.Manifests ?? []).Select(CloneManifest).ToArray();
        if (targets.Length is 0 or > 16)
            throw new ZbaBackendException(400, "plugin_bundle_invalid", "组合变更清单无效。");

        var receiptKey = ReceiptKey(request.IdempotencyKey);
        var requestedFingerprint = BundleBinding(targets, request.ExpectedRevision);
        var primaryId = "bundle:" + targets.Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal).First();
        var reload = SupportsRuntimeReload && targets.All(t => CanReloadPackage(t.Id, t));
        var operation = new ZbaPluginOperation(
            "po-" + Hash(request.IdempotencyKey)[..16],
            primaryId,
            "bundle",
            reload ? StatusInstalled : StatusRestartRequired,
            !reload,
            null);

        return _operations.ExecuteAsync(
            _receipts,
            receiptKey,
            Hash(requestedFingerprint + ":" + request.PreviewId),
            caller,
            operation,
            () => RevisionAsync(CancellationToken.None),
            request.ExpectedRevision,
            async () =>
            {
                await _packages.StageBundleAsync(targets, request.ExpectedRevision, (lockedInstalled, _) =>
                {
                    var checks = InspectBundle(targets, lockedInstalled);
                    if (checks.Any(c => c.Status == "blocked")) throw PluginImportSnapshots.Refresh();
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);

                if (reload && _reload is not null)
                {
                    foreach (var target in targets.OrderBy(t => t.Id, StringComparer.Ordinal))
                    {
                        try { await _reload(target.Id, CancellationToken.None).ConfigureAwait(false); }
                        catch (BotAgent.Services.Plugins.PluginRestartRequiredException) { throw; }
                        catch (Exception)
                        {
                            throw new ZbaBackendException(503, "plugin_bundle_reload_failed",
                                "组合变更已暂存，但热重载失败；旧实例保持不变，请修正后用新的操作键重试。");
                        }
                    }
                }
            },
            ct,
            admit: () =>
            {
                var snapshot = _imports.Get<BundleSnapshot>(request.PreviewId, caller, "bundle");
                if (!snapshot.CanApply || snapshot.Revision != request.ExpectedRevision || snapshot.Binding != requestedFingerprint)
                    throw PluginImportSnapshots.Refresh();
                return Task.CompletedTask;
            });
    }

    private IReadOnlyList<ZbaPluginPreviewCheck> InspectBundle(
        IReadOnlyList<PluginPackageManifest> targets, IReadOnlyList<InstalledPluginPackage> installed)
    {
        var checks = new List<ZbaPluginPreviewCheck>();
        var targetMap = targets.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var registered = _registrySnapshot?.Invoke() ?? [];

        foreach (var target in targets.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var gov = _packages.Governance.Evaluate(target);
            if (gov.Blocked)
                checks.Add(new("governance", "blocked", $"插件 {target.Id} 被治理策略阻止（{gov.Reason}）。"));
            var existing = installed.FirstOrDefault(p => p.Id == target.Id);
            if (existing?.MetadataFailure is not null)
                checks.Add(new("metadata", "blocked", $"插件 {target.Id} 元数据损坏（{existing.MetadataFailure.Code}）。"));
            if (existing?.Pending is not null || existing?.PendingUninstall == true)
                checks.Add(new("pending", "blocked", $"插件 {target.Id} 已有待应用变更，无法并入组合。"));
        }

        var simulatedActive = new Dictionary<string, PluginPackageManifest>(StringComparer.Ordinal);
        foreach (var pkg in installed)
        {
            if (pkg.MetadataFailure is not null)
            {
                checks.Add(new("metadata", "blocked", $"目录存在损坏元数据 {pkg.Id}，无法证明反向依赖完整。"));
                continue;
            }
            if (pkg.Active is not null && !pkg.PendingUninstall) simulatedActive[pkg.Id] = pkg.Active;
        }
        foreach (var target in targets) simulatedActive[target.Id] = target;

        foreach (var target in targets.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            foreach (var dep in target.Dependencies ?? [])
            {
                var ok = simulatedActive.TryGetValue(dep.Id, out var provider)
                    ? provider.Version == dep.Version && provider.ApiVersion == target.ApiVersion
                    : target.ApiVersion == 1 && registered.Any(r => r.IsPreset && r.Id == dep.Id && r.Version == dep.Version);
                if (!ok)
                    checks.Add(new("dependency", "blocked", $"插件 {target.Id} 依赖 {dep.Id}@{dep.Version} 未在组合或当前环境中满足。"));
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool HasCycle(string id)
        {
            if (!visited.Add(id)) return false;
            if (!visiting.Add(id)) return true;
            if (simulatedActive.TryGetValue(id, out var m))
                foreach (var dep in m.Dependencies ?? [])
                    if (visiting.Contains(dep.Id) || (!visited.Contains(dep.Id) && HasCycle(dep.Id))) return true;
            visiting.Remove(id);
            return false;
        }
        foreach (var id in targetMap.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            visiting.Clear(); visited.Clear();
            if (HasCycle(id)) checks.Add(new("cycle", "blocked", $"目标组合中存在循环依赖（涉及 {id}）。"));
        }

        foreach (var pkg in installed.Where(p => !targetMap.ContainsKey(p.Id) && !p.PendingUninstall))
        {
            foreach (var manifest in new[] { pkg.Active, pkg.Pending }.OfType<PluginPackageManifest>())
            {
                foreach (var dep in manifest.Dependencies ?? [])
                {
                    if (!targetMap.TryGetValue(dep.Id, out var updatedProvider)) continue;
                    if (updatedProvider.Version != dep.Version || updatedProvider.ApiVersion != manifest.ApiVersion)
                        checks.Add(new("reverse_dependency", "blocked",
                            $"外部已安装插件 {pkg.Id} 依赖 {dep.Id}@{dep.Version}，未纳入本次组合升级。"));
                }
            }
        }

        if (checks.Count == 0)
            checks.Add(new("bundle", "ok", $"组合内 {targets.Count} 个插件的依赖闭包与反向依赖校验通过。"));
        return checks;
    }

    private static string BundleBinding(IReadOnlyList<PluginPackageManifest> targets, string revision)
        => PluginPackageRevision.Hash(string.Join("|",
            new[] { revision }.Concat(targets.OrderBy(t => t.Id, StringComparer.Ordinal)
                .Select(t => t.Id + ":" + PluginLifecycleManifest.Fingerprint(t)))));

    private sealed record BundleSnapshot(
        IReadOnlyList<PluginPackageManifest> Targets,
        string Revision,
        string Binding,
        bool CanApply,
        string AdoptionMode);
}

using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginMarketService
{
    public Task<ZbaPluginOperationResult> RollbackAsync(ZbaPluginMutationRequest request, string caller, CancellationToken ct)
        => LifecycleMutationAsync("rollback", request.IdempotencyKey, request.ExpectedRevision, request.Manifest.Id,
            CloneManifest(request.Manifest), request.PreviewId, request.TrustAcknowledged, caller, ct);

    private Task<ZbaPluginOperationResult> LifecycleMutationAsync(string kind, string key, string revision,
        string id, PluginPackageManifest? target, string? previewId, bool trust, string caller, CancellationToken ct)
    {
        if (target is not null) PluginPackageValidation.Validate(target, checkCompatibility: false);
        LifecycleSnapshot? admitted = null;
        return MutateAsync(key, id, kind, target, trust, caller, revision,
            async () => await _packages.StageLifecycleAsync(kind, id, target, revision,
                async (installed, token) => { await ValidateLifecycleAsync(kind, id, target, revision, previewId,
                    trust, caller, token, installed, ReceiptKey(key)).ConfigureAwait(false); }, ct,
                admitted!.Recovery).ConfigureAwait(false), ct,
            async () => { admitted = await ValidateLifecycleAsync(kind, id, target, revision, previewId,
                trust, caller, ct).ConfigureAwait(false); }, previewId);
    }

    private async Task<LifecycleSnapshot> ValidateLifecycleAsync(string kind, string id, PluginPackageManifest? target,
        string revision, string? previewId, bool trust, string caller, CancellationToken ct,
        IReadOnlyList<InstalledPluginPackage>? lockedInstalled = null, string? excludingKey = null)
    {
        if (string.IsNullOrEmpty(previewId))
            throw new ZbaBackendException(409, "plugin_preview_required", "请先预览操作影响，再确认执行。");
        if (target is not null)
        {
            await _packages.Governance.RefreshAsync(ct).ConfigureAwait(false);
            _packages.Governance.EnsureAllowed(target);
        }
        var snapshot = _imports.Get<LifecycleSnapshot>(previewId, caller, "lifecycle");
        if (!trust || !snapshot.CanApply || snapshot.Kind != kind || snapshot.Id != id || snapshot.Revision != revision ||
            PluginLifecycleManifest.Fingerprint(snapshot.Target) != PluginLifecycleManifest.Fingerprint(target))
            throw PluginImportSnapshots.Refresh();
        await ValidateLifecycleSourceAsync(snapshot, ct).ConfigureAwait(false);
        var installed = lockedInstalled ?? await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        if (PluginPackageRevision.Of(installed) != revision) throw PluginImportSnapshots.Refresh();
        var current = installed.FirstOrDefault(p => p.Id == id && (p.Active is not null || snapshot.Recovery == "failed-install" && p.Pending is not null)) ?? throw PluginImportSnapshots.Refresh();
        var inspection = InspectLifecycle(kind, current, target, installed, caller, snapshot.Recovery, excludingKey);
        inspection = inspection with { Checks = await WithPythonCheckAsync(inspection.Checks, target, ct).ConfigureAwait(false) };
        if (inspection.Binding != snapshot.Binding || inspection.Checks.Any(c => c.Status == "blocked"))
            throw PluginImportSnapshots.Refresh();
        _ = _imports.Get<LifecycleSnapshot>(previewId, caller, "lifecycle");
        ct.ThrowIfCancellationRequested();
        return snapshot;
    }
}
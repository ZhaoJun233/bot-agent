using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginMarketService
{
    private sealed record LifecycleSnapshot(string Kind, string Id, string Recovery, string Revision, PluginPackageManifest Current,
        PluginPackageManifest? Target, ZbaPluginLifecycleSource? Source, string? SourceFingerprint, string Binding, bool CanApply);

    public async Task<ZbaPluginLifecyclePreview> PreviewLifecycleAsync(ZbaPluginLifecyclePreviewRequest request,
        string caller, CancellationToken ct = default)
    {
        ValidateImportCaller(caller);
        if (request.Kind is not ("update" or "rollback" or "uninstall") ||
            (request.Kind == "update") != (request.Candidate is not null)) throw InvalidLifecycle();
        PluginPackageValidation.ValidateId(request.PluginId);
        LifecycleTarget? selected = request.Candidate is null ? null :
            await LifecycleTargetAsync(request.PluginId, request.Candidate, caller, ct).ConfigureAwait(false);
        var installed = await _packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var revision = PluginPackageRevision.Of(installed);
        if (revision != request.ExpectedRevision) throw PluginImportSnapshots.Refresh();
        var current = installed.FirstOrDefault(p => p.Id == request.PluginId && (p.Active is not null || request.Kind == "uninstall" && p.Pending is not null))
            ?? throw new ZbaBackendException(404, "plugin_not_installed", "插件没有活动安装版本。");
        var recovery = request.Kind == "uninstall" && current.Active is null ? "failed-install" : request.Kind == "rollback" && current.Pending is not null && !current.PendingUninstall ? "failed-pending" : "none";
        var target = request.Kind == "rollback" ? recovery == "failed-pending" ? current.Active : current.Previous : selected?.Manifest;
        var source = request.Kind == "rollback" && target is not null ? new ZbaPluginLifecycleSource("installed", target.DownloadUrl, null) : selected?.Source;
        await _packages.Governance.RefreshAsync(ct).ConfigureAwait(false);
        var inspection = InspectLifecycle(request.Kind, current, target, installed, caller, recovery);
        inspection = inspection with { Checks = await WithPythonCheckAsync(inspection.Checks, target, ct).ConfigureAwait(false) };
        var canApply = inspection.Checks.All(c => c.Status != "blocked");
        var snapshot = new LifecycleSnapshot(request.Kind, current.Id, recovery, revision, (current.Active ?? current.Pending)!, target, source,
            selected?.SourceFingerprint, inspection.Binding, canApply);
        var stored = _imports.Add(caller, "lifecycle", snapshot);
        return new(stored.Id, stored.ExpiresAt, revision, request.Kind, current.Id, recovery, (current.Active ?? current.Pending)!, current.Pending, target, source,
            selected?.Metadata, inspection.Enabled, inspection.Status, inspection.RestartRequired, canApply,
            PluginLifecycleManifest.Changes((current.Active ?? current.Pending)!, target),
            inspection.Checks, new(true, true, true), TrustNotice)
            { CurrentGovernance = _packages.Governance.Evaluate((current.Active ?? current.Pending)!),
                TargetGovernance = target is null ? null : _packages.Governance.Evaluate(target),
                GovernanceSource = _packages.Governance.Source };
    }
}
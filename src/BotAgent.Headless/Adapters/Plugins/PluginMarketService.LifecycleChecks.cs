using System.Text.Json;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginMarketService
{
    private sealed record LifecycleInspection(string Binding, bool Enabled, string Status, bool RestartRequired,
        IReadOnlyList<ZbaPluginPreviewCheck> Checks);

    private LifecycleInspection InspectLifecycle(string kind, InstalledPluginPackage current,
        PluginPackageManifest? target, IReadOnlyList<InstalledPluginPackage> installed, string caller, string recovery = "none", string? excludingKey = null)
    {
        var checks = new List<ZbaPluginPreviewCheck>();
        void Check(string code, bool pass, string message) => checks.Add(new(code, pass ? "passed" : "blocked", message));
        var desired = _desiredStates?.Invoke() ?? new Dictionary<string, bool>();
        var currentManifest = (current.Active ?? current.Pending)!;
        var row = Project(currentManifest, installed, desired);
        var registered = _registrySnapshot?.Invoke() ?? [];
        Check("active_package", current.Active is not null || recovery == "failed-install" && kind == "uninstall" && current.Pending is not null, recovery == "failed-install"
            ? "当前只有尚未生效的安装候选；卸载清理不要求存在活动版本，失败回执另行核对。" : "只允许管理已安装的活动包。");
        Check("package_not_builtin", !registered.Any(p => p.Id == current.Id && p.IsPreset), "内置插件不支持包生命周期操作。");
        var proof = recovery != "none" ? _receipts.RecoveryProof(current.Id, PluginLifecycleManifest.PackageState(current), caller, excludingKey) : null;
        Check("no_pending_change", (current.Pending is null || recovery != "none" && proof is not null) && !current.PendingUninstall, "只允许无待办变更或有已知失败回执的显式恢复；未知状态需人工核对。");
        if (recovery == "failed-pending") Check("failed_pending_recovery", proof is not null && target is not null &&
            PluginLifecycleManifest.Fingerprint(target) == PluginLifecycleManifest.Fingerprint(current.Active), "恢复丢弃已知失败候选并重新应用当前活动版本；保留上一版本历史。");
        Check("enabled_intent", true, "保留当前启用意图，不自动启用或停用。");
        if (kind == "rollback") Check("previous_release", target is not null && _packages.HasCachedArchive(target), recovery == "failed-pending"
            ? "失败候选恢复需要当前活动版本的本地缓存 ZIP；上一版本历史保持不变。" : "回滚需要已保存的上一版本及本地缓存 ZIP。");
        if (kind == "update") Check("newer_version", target is not null && PluginLifecycleManifest.IsUpgrade(current.Active!.Version, target.Version), "更新必须是可明确排序的更高语义版本。");
        if (target is not null)
        {
            Check("runtime", current.Active is null || current.Active.ApiVersion == target.ApiVersion, "Updates cannot change runtime family.");
            checks.Add(GovernanceCheck(target));
            Check("host_compatibility", PluginPackageValidation.IsCompatible(target), "目标版本必须兼容宿主。");
            Check("fixed_release", new[] { current.Active, current.Previous }.All(m => m?.Version != target.Version ||
                PluginLifecycleManifest.Fingerprint(m) == PluginLifecycleManifest.Fingerprint(target)), "同一固定版本不可替换为不同清单。");
            Check("dependencies", (target.Dependencies ?? []).All(d => registered.Any(p => p.IsPreset && p.Id == d.Id && p.Version == d.Version) || installed.Any(p => p.Id == d.Id &&
                !p.PendingUninstall && p.Pending is null && p.Active?.Version == d.Version)), "依赖必须已安装且精确匹配声明版本。");
        }
        Check("reverse_dependencies", installed.Where(p => p.Id != current.Id)
            .SelectMany(p => new[] { p.Active, p.Pending }).Where(m => m is not null)
            .All(m => (m!.Dependencies ?? []).Where(d => d.Id == current.Id).All(d => target?.Version == d.Version)),
            "现有插件的精确版本依赖必须仍然满足。");
        string? settingsStamp = null;
        try
        {
            settingsStamp = _settings.ReadPreservationStamp(current.Id);
            if (kind != "uninstall")
            {
                _ = _settings.ReadManifest(currentManifest);
                if (target is not null) _settings.ValidatePreserved(target);
            }
            Check("preserved_settings", true, kind == "uninstall"
                ? "卸载仅核对保存状态指纹并原样保留设置与秘密引用；不校验 schema 兼容性或必填值。"
                : "保存的设置与秘密引用可原样保留，必填项已满足。");
        }
        catch (PluginSettingsException)
        { Check("preserved_settings", false, "保存的设置与目标 schema 不兼容或缺少必填值；不会重置或迁移。"); }
        Check("source_trust", true, "来源和清单已固定；这不是签名或安全背书。");
        if (target is not null) checks.Add(new("archive_verification", "pending", "确认后校验 ZIP 与 SHA-256；预览不读取或执行归档。"));
        var state = installed.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => new
        { p.Id, Active = PluginLifecycleManifest.Fingerprint(p.Active), Previous = PluginLifecycleManifest.Fingerprint(p.Previous),
            Pending = PluginLifecycleManifest.Fingerprint(p.Pending), p.PendingUninstall });
        var binding = PluginPackageRevision.Hash(JsonSerializer.Serialize(new { state, row.Enabled, row.Status, row.RestartRequired, settingsStamp, proof }));
        return new(binding, row.Enabled, row.Status, row.RestartRequired, checks);
    }
}
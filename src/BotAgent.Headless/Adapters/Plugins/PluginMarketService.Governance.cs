using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginMarketService
{
    private ZbaPluginPreviewCheck GovernanceCheck(PluginPackageManifest manifest)
    {
        var state = _packages.Governance.Evaluate(manifest);
        return new("governance", state.Blocked ? "blocked" : state.Status == "active" ? "passed" : "warning",
            state.Blocked ? "官方已安全撤回此包，禁止获取；现有安装不会被自动移除或停用。" :
            state.Status is "deprecated" or "withdrawn" ? "此版本已弃用或普通撤下；允许显式获取，请核对维护说明。" :
            state.Status == "unknown" ? "治理源未完成新鲜核验；继续执行已缓存的撤回规则，不代表安全背书。" :
            "新鲜治理快照没有命中撤回记录；不代表安全背书。");
    }
}

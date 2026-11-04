using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Tools;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Tools;

/// <summary>
/// **唯一**回答“这个工具现在能不能给模型用/能不能执行”的地方（#115，聊天路径）。
///
/// 它把以前散在调用点附近的四类判断收成一条链（顺序即语义，改顺序等于改权限）：
///   ① 来源租约 / 停止标志（<see cref="IsCurrent" />）—— 换代或停机后立刻失效；
///   ② 插件门禁（<see cref="PluginGate.AllowsTool" />）—— 未知插件 id 的 fail-open 语义留在 PluginGate 内部，
///      这里只消费它的答案（#115 不合并两种相反方向，只合并入口）；
///   ③ 面板开关（<see cref="ToolSpec.FeatureKey" /> + <see cref="ToolSwitches" />）—— 未知/空开关名 Fail-Closed；
///   ④ 场景策略快照（本轮固化的 <see cref="EffectivePlatformPolicy" />）与实时平台策略解析器 ——
///      前者防“这一轮被临时放宽”，后者防“热重载空窗里旧快照还开着”。
///
/// **不做**的事：不做 ToolGate 的预算/审批判定（那是 <see cref="ToolGate.Evaluate" /> 的职责），
/// 也不缓存结果 —— 租约与策略都可能在两次动作之间变化，续跑必须在每个动作前重算（工具可见性是有时效的）。
/// </summary>
public sealed class ToolVisibility(
    PluginGate? plugins = null,
    IPlatformPolicyResolver? policies = null,
    Func<bool>? isDisposed = null,
    Func<bool>? webReady = null)
{
    /// <summary>
    /// 这条租约现在还成立吗？false = 换代/停机后**所有**工具都不可见（不是逐个工具判断）。
    /// </summary>
    public bool IsCurrent => isDisposed?.Invoke() != true && PlatformSourceExecution.Current is not { IsCurrent: false };

    /// <summary>这个工具在这一轮、这个策略快照下是否可见（含插件门禁、面板开关、场景/平台策略）。</summary>
    public bool Allows(AppSettings snapshot, EffectivePlatformPolicy? pinned, string toolId)
    {
        if (!IsCurrent || plugins?.AllowsTool(toolId) == false) return false;
        var spec = ChatToolSpecs.Find(toolId);
        if (spec is null) return false;   // 没登记的工具一律不可见（Fail-Closed，与改造前一致）

        var switches = new ToolSwitchState(
            snapshot.EnableWebSearch && webReady?.Invoke() != false,
            snapshot.EnableMusic,
            snapshot.EnableVoice,
            snapshot.EnableStickers,
            snapshot.EnablePoke,
            snapshot.EnableApprovals,
            snapshot.EnableQuestions);
        // 平台策略里的“能力名”与面板开关名不是同一套：审批/提问/回复都属于 text 这条能力，
        // 而开关（ToolSwitches）里它们各有自己的键。两张表分开，是为了不改平台策略的既有口径。
        var feature = PlatformFeature(spec.FeatureKey);
        var enabled = ToolSwitches.Enabled(spec, switches);
        if (!enabled || pinned?.Feature(feature, enabled).Enabled == false) return false;
        var context = PlatformSourceExecution.Current?.Context ?? pinned?.Context;
        return policies is null || context is not null && policies.Resolve(context).Feature(feature, enabled).Enabled;
    }

    /// <summary>面板开关名 → 平台能力名（平台策略只认 text/image/voice/… 这套）。</summary>
    private static string PlatformFeature(string featureKey) => featureKey switch
    {
        ToolFeature.Approval or ToolFeature.Question or ToolFeature.Text => "text",
        _ => featureKey,
    };

    /// <summary>本轮可见工具按目录顺序（给提示词渲染用；不可见的不进清单）。</summary>
    public IReadOnlyList<ToolSpec> Visible(AppSettings snapshot, EffectivePlatformPolicy? pinned)
        => ChatToolSpecs.All.Where(spec => spec.PromptVisible && Allows(snapshot, pinned, spec.Id)).ToArray();
}

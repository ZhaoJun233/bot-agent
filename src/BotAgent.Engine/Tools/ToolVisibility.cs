using BotAgent.Domain.Configuration;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Tools;
using BotAgent.Services.Memory;
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
    Func<bool>? webReady = null,
    Func<ChatToolCatalog>? catalog = null)
{
    /// <summary>
    /// 这条租约现在还成立吗？false = 换代/停机后**所有**工具都不可见（不是逐个工具判断）。
    /// </summary>
    public bool IsCurrent => isDisposed?.Invoke() != true && PlatformSourceExecution.Current is not { IsCurrent: false };

    /// <summary>这个工具在这一轮、这个策略快照下是否可见（含插件门禁、面板开关、场景/平台/会话策略）。</summary>
    public bool Allows(AppSettings snapshot, EffectivePlatformPolicy? pinned, string toolId,
        ChatToolCatalog? pinnedTools = null, EffectiveConversationSettings? conversation = null)
    {
        if (!IsCurrent || plugins?.AllowsTool(toolId) == false) return false;
        var tools = pinnedTools ?? catalog?.Invoke() ?? ChatToolCatalog.Builtin;
        var spec = tools.Find(toolId);
        if (spec is null) return false;   // 没登记的工具一律不可见（Fail-Closed，与改造前一致）
        if (tools.FindBinding(toolId) is { IsReady: false }) return false;
        if (conversation is not null && !AllowsByToolTier(spec, conversation.EffectiveToolTier)) return false;
        if (MemoryUseScope.HasMemory && !AllowsByMemoryTaint(spec, snapshot.MaxAgentSteps)) return false;

        var webEnabled = (conversation is null ? snapshot.EnableWebSearch : conversation.EffectiveFeatureDefaults.GetValueOrDefault("websearch", snapshot.EnableWebSearch))
            && webReady?.Invoke() != false;
        var docsEnabled = tools.FindBinding("doc.read") is { IsReady: true }
            && (conversation is null || (conversation.EffectiveFeatureDefaults.GetValueOrDefault("documents", true) && conversation.AllowsReadOnlyTools));
        var digestEnabled = tools.FindBinding("chat.digest") is { IsReady: true }
            && (conversation is null || (conversation.EffectiveFeatureDefaults.GetValueOrDefault("digest", true) && conversation.AllowsReadOnlyTools));
        var knowledgeEnabled = tools.FindBinding("kb.search") is { IsReady: true }
            && (conversation is null || (conversation.EffectiveFeatureDefaults.GetValueOrDefault("knowledge", true) && conversation.AllowsReadOnlyTools));
        var schedulerEnabled = tools.FindBinding("reminder.list") is { IsReady: true }
            && (conversation is null || (conversation.EffectiveFeatureDefaults.GetValueOrDefault("scheduler", true) && conversation.AllowsReadOnlyTools));
        var personalTodoEnabled = tools.FindBinding("todo.list") is { IsReady: true }
            && (conversation is null || (conversation.EffectiveFeatureDefaults.GetValueOrDefault("personal_todo", true) && conversation.AllowsReadOnlyTools));
        var switches = new ToolSwitchState(
            webEnabled,
            conversation is null ? snapshot.EnableMusic : conversation.EffectiveFeatureDefaults.GetValueOrDefault("music", snapshot.EnableMusic),
            conversation is null ? snapshot.EnableVoice : conversation.EffectiveFeatureDefaults.GetValueOrDefault("voice", snapshot.EnableVoice),
            conversation is null ? snapshot.EnableStickers : conversation.EffectiveFeatureDefaults.GetValueOrDefault("stickers", snapshot.EnableStickers),
            conversation is null ? snapshot.EnablePoke : conversation.EffectiveFeatureDefaults.GetValueOrDefault("poke", snapshot.EnablePoke),
            snapshot.EnableApprovals && (conversation?.AllowsToolRequest ?? true),
            snapshot.EnableQuestions && (conversation?.AllowsAsk ?? true),
            Documents: docsEnabled,
            Digest: digestEnabled,
            Knowledge: knowledgeEnabled,
            Scheduler: schedulerEnabled,
            PersonalTodo: personalTodoEnabled);
        // 平台策略里的“能力名”与面板开关名不是同一套：审批/提问/回复都属于 text 这条能力，
        // 而开关（ToolSwitches）里它们各有自己的键。两张表分开，是为了不改平台策略的既有口径。
        var feature = PlatformFeature(spec.FeatureKey);
        var enabled = ToolSwitches.Enabled(spec, switches);
        var effectivePinned = conversation?.EffectivePolicy ?? pinned;
        if (!enabled || effectivePinned?.Feature(feature, enabled).Enabled == false) return false;
        var context = PlatformSourceExecution.Current?.Context ?? effectivePinned?.Context;
        return policies is null || context is not null && policies.Resolve(context).Feature(feature, enabled).Enabled;
    }

    private static bool AllowsByMemoryTaint(ToolSpec spec, int maxAgentSteps)
    {
        if (spec.FeatureKey is ToolFeature.Approval or ToolFeature.Question)
            return MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalSideEffect);
        if (spec.Id == "music.share" && MemoryUseScope.MaxTier == MemorySensitivityTier.SameScopeProfile)
            return true;
        var egress = MemoryTaintPolicy.ClassifyTool(spec.Id, spec.ReadOnly, spec.Origin.ToString());
        if (maxAgentSteps <= 1 && egress is ToolEgressClass.SameScopeRead or ToolEgressClass.ExternalReadEgress)
            return false;
        return MemoryUseScope.AllowsEgress(egress);
    }

    private static bool AllowsByToolTier(ToolSpec spec, ConversationToolTier tier)
    {
        if (tier is ConversationToolTier.Inherit or ConversationToolTier.ManagedTools) return true;
        var isBasicMedia = spec.Origin != ToolOrigin.Mcp && spec.FeatureKey is ToolFeature.Text or ToolFeature.Music or ToolFeature.Voice
            or ToolFeature.Stickers or ToolFeature.Poke;
        if (tier == ConversationToolTier.ChatOnly) return isBasicMedia;
        // ReadOnlyTools：放行基础媒体、只读工具与提问，阻断审批写请求与非媒体写工具。
        if (spec.FeatureKey == ToolFeature.Approval) return false;
        return isBasicMedia || spec.FeatureKey is ToolFeature.WebSearch or ToolFeature.Question || spec.ReadOnly;
    }

    /// <summary>面板开关名 → 平台能力名（平台策略只认 text/image/voice/… 这套）。</summary>
    private static string PlatformFeature(string? featureKey) => featureKey switch
    {
        null or "" or ToolFeature.Approval or ToolFeature.Question or ToolFeature.Text => "text",
        _ => featureKey,
    };

    /// <summary>本轮可见工具按目录顺序（给提示词渲染用；不可见的不进清单）。</summary>
    public IReadOnlyList<ToolSpec> Visible(AppSettings snapshot, EffectivePlatformPolicy? pinned,
        EffectiveConversationSettings? conversation = null)
    {
        var tools = catalog?.Invoke() ?? ChatToolCatalog.Builtin;
        return tools.Specs.Where(spec => spec.PromptVisible && Allows(snapshot, pinned, spec.Id, tools, conversation)).ToArray();
    }
}

using BotAgent.Services.Plugins;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Services.Memory;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Tools;
using System.Text.Json.Nodes;
using BotAgent.Services.Net;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Tools;

namespace BotAgent.Services.Reply;

/// <summary>
/// 批次 E 的"**当场**做掉只读工具"那一步：模型点名的 search / read 不等下一轮，直接做完再喂回去。
///
/// 为什么单独一个类：它与"留给下一轮"那条老路（<c>ReplyPipeline.QueueWebSearchAsync</c> / <c>QueuePageReadAsync</c>）
/// 是**同一个动作的两种时间点**，判定与冷却必须**完全一致**才行 —— 放在一个地方，改一处就够，
/// 也顺手把回复主链让出来的行数还给棘轮（§14.3 登记的 +14）。
///
/// 三条纪律：
///   · **判定照旧**：先过能力闸门（<see cref="ApprovalUseCase.AllowCapability" />，同时记账单轮预算）；
///   · **冷却照旧**：搜索仍受同会话冷却（<see cref="IResearchUseCase.TryBeginSearch" />），拒绝就不做；
///   · **只做只读**：只接 search / read。语音/表情/戳/分享一律不在这里做 ——
///     那些会"打扰别人"，仍走发送阶段统一裁决（§8：两条路的授权边界不许合一）。
/// </summary>
public sealed class InlineTurnTools
{
    private readonly IResearchUseCase _research;
    private readonly ApprovalUseCase _approvals;
    private readonly ParticipationUseCase _participation;
    private readonly Action<string> _log;
    private readonly PluginGate? _plugins;
    private readonly ReplyToolAvailability _tools;

    public InlineTurnTools(
        IResearchUseCase research,
        ApprovalUseCase approvals,
        ParticipationUseCase participation,
        Action<string> log,
        PluginGate? plugins = null,
        IPlatformPolicyResolver? policies = null,
        Func<bool>? isDisposed = null)
    {
        _research = research;
        _approvals = approvals;
        _participation = participation;
        _log = log;
        _plugins = plugins;
        _tools = new ReplyToolAvailability(plugins, policies, isDisposed, () => research.IsReady, () => approvals.Capabilities.Catalog);
    }

    /// <summary>这一轮有没有"当场做工具"的可能（没开循环 / 无可用只读插件工具 / 插件、记忆污点或会话档案禁用 = 没有）。</summary>
    public bool Enabled(AppSettings snapshot, EffectiveConversationSettings? conversationSettings = null)
    {
        if (snapshot.MaxAgentSteps <= 1 || !_tools.IsCurrent) return false;
        if (MemoryUseScope.HasMemory && !MemoryUseScope.AllowsEgress(ToolEgressClass.SameScopeRead)) return false;
        if (conversationSettings is not null && !conversationSettings.AllowsReadOnlyTools) return false;
        var webAllowed = (!MemoryUseScope.HasMemory || MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress))
            && (conversationSettings is null ? snapshot.EnableWebSearch : conversationSettings.FeatureEnabled("websearch"))
            && _research.IsReady && (_plugins?.AllowsTool("web.search") ?? true);
        if (webAllowed) return true;
        var catalog = _approvals.Capabilities.Catalog;
        return catalog.Specs.Any(spec => spec.ReadOnly && spec.Origin is ToolOrigin.Plugin or ToolOrigin.Mcp
            && spec.Id is not ("web.search" or "web.read")
            && catalog.FindBinding(spec.Id) is { IsReady: true }
            && (!MemoryUseScope.HasMemory || MemoryUseScope.AllowsEgress(MemoryTaintPolicy.ClassifyTool(spec.Id, spec.ReadOnly, spec.Origin.ToString())))
            && (conversationSettings is null
                ? (spec.Origin == ToolOrigin.Mcp || (_plugins?.AllowsTool(spec.Id) ?? true))
                : _tools.Allows(snapshot, conversationSettings.EffectivePolicy, spec.Id, catalog, conversationSettings)));
    }

    /// <summary>
    /// 做掉这一步点名的工具，返回**喂回给模型**的那段文本；null/空 = 没有可当场做的事（循环据此收工）。
    /// </summary>
    public async Task<string?> RunAsync(
        BotConversation conversation, AppSettings snapshot, ChatCapabilitySet caps, CompletionResult result,
        EffectivePlatformPolicy? platformPolicy = null, CancellationToken ct = default,
        EffectiveConversationSettings? conversationSettings = null)
    {
        if (ct.IsCancellationRequested || !Enabled(snapshot, conversationSettings)) return null;
        var search = !string.IsNullOrWhiteSpace(result.Search);
        var hasRead = !search && !string.IsNullOrWhiteSpace(result.Read);
        if (!search && !hasRead)
        {
            return result.Call is { } inlineCall
                ? await RunPluginCallAsync(conversation, snapshot, caps, inlineCall, platformPolicy, ct, conversationSettings)
                : null;
        }
        var id = search ? "web.search" : "web.read";
        if (MemoryUseScope.HasMemory && (!MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress) || !MemoryUseScope.CanUse))
            return null;
        var value = search ? result.Search! : result.Read!;
        var call = new ToolCall(id, new JsonObject { [search ? "query" : "url"] = value });
        var binding = caps.Catalog.FindBinding(id);
        if (binding is null || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)) return null;
        var verdict = ToolArguments.Validate(binding.Definition, call.Arguments);
        if (!verdict.Ok)
        {
            _log($"[循环] 工具参数不合契约（{verdict.ReasonCode}）→ 不执行");
            return null;
        }
        if (!_approvals.AllowCapability(conversation, id, value, out _, pinned: caps)) return null;
        if (!search) _log("[循环] 当场读页面");
        await ReplyCommitBoundary.BeforeEffectAsync();
        if (ct.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)) return null;
        if (MemoryUseScope.HasMemory && (!MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress) || !MemoryUseScope.CanUse))
            return null;
        try
        {
            if (search && !binding.GetContribution<IResearchUseCase>()
                .TryBeginSearch(conversation.SourceKey, snapshot.WebSearchCooldownSeconds, out _)) return null;
            if (search) _log("[循环] 当场搜索");
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, PlatformSourceExecution.Current?.Lifetime ?? default);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)) return null;
            var outcome = await ExecuteWithMemoryNoteCleanupAsync(conversation.SourceKey, binding, call, lifetime.Token);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)) return null;
            if (MemoryUseScope.HasMemory && !MemoryUseScope.CanUse) return null;
            if (search && !outcome.Ok) _participation.Observe(conversation.SourceKey, ParticipationEvent.ToolFailure);
            return outcome.Ok ? outcome.ModelText : null;
        }
        catch (InvalidOperationException error) when (error.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable")
        {
            return null;
        }
    }

    private async Task<string?> RunPluginCallAsync(
        BotConversation conversation, AppSettings snapshot, ChatCapabilitySet caps, InlineToolCall inlineCall,
        EffectivePlatformPolicy? platformPolicy, CancellationToken ct, EffectiveConversationSettings? conversationSettings)
    {
        var id = inlineCall.ToolId;
        var binding = caps.Catalog.FindBinding(id);
        if (binding is null || !binding.Definition.Spec.ReadOnly || binding.Definition.Spec.Origin is not (ToolOrigin.Plugin or ToolOrigin.Mcp))
            return null;
        var egress = MemoryTaintPolicy.ClassifyTool(id, binding.Definition.Spec.ReadOnly, binding.Definition.Spec.Origin.ToString());
        if (MemoryUseScope.HasMemory && (!MemoryUseScope.AllowsEgress(egress) || !MemoryUseScope.CanUse))
            return null;
        if (!_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings))
            return null;
        var args = new JsonObject();
        foreach (var pair in inlineCall.Arguments)
            args[pair.Key] = pair.Value;
        var call = new ToolCall(id, args);
        if (binding.Definition.Spec.Origin != ToolOrigin.Mcp)
        {
            var verdict = ToolArguments.Validate(binding.Definition, call.Arguments);
            if (!verdict.Ok)
            {
                _log($"[循环] 工具参数不合契约（{verdict.ReasonCode}）→ 不执行");
                return null;
            }
        }
        if (!_approvals.AllowCapability(conversation, id, id, out _, pinned: caps)) return null;
        _log($"[循环] 当场调用只读工具 {id}");
        await ReplyCommitBoundary.BeforeEffectAsync();
        if (ct.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)) return null;
        if (MemoryUseScope.HasMemory && (!MemoryUseScope.AllowsEgress(egress) || !MemoryUseScope.CanUse)) return null;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, PlatformSourceExecution.Current?.Lifetime ?? default);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)) return null;
            var outcome = await binding.ExecuteAsync(conversation.SourceKey, call, lifetime.Token);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)) return null;
            if (MemoryUseScope.HasMemory && !MemoryUseScope.CanUse) return null;
            if (!outcome.Ok) _participation.Observe(conversation.SourceKey, ParticipationEvent.ToolFailure);
            return outcome.Ok ? outcome.ModelText : null;
        }
        catch (InvalidOperationException error) when (error.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable")
        {
            return null;
        }
    }

    private async Task<ToolOutcome> ExecuteWithMemoryNoteCleanupAsync(
        string sourceKey, IToolBinding binding, ToolCall call, CancellationToken ct)
    {
        try
        {
            return await binding.ExecuteAsync(sourceKey, call, ct);
        }
        finally
        {
            if (MemoryUseScope.HasMemory)
            {
                // L1 记忆回合禁止将外部搜索成果留存进跨轮研究缓存（GlobalStateWrite）。
                _ = _research.TakeNote(sourceKey);
            }
        }
    }
}

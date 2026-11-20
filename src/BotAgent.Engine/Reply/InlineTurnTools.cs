using BotAgent.Services.Plugins;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
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

    /// <summary>这一轮有没有"当场做工具"的可能（没开循环 / 没有搜索服务 / 插件禁用 = 没有）。</summary>
    public bool Enabled(AppSettings snapshot)
        => snapshot.MaxAgentSteps > 1 && snapshot.EnableWebSearch && _research.IsReady && _tools.IsCurrent
            && (_plugins?.AllowsTool("web.search") ?? true);

    /// <summary>
    /// 做掉这一步点名的工具，返回**喂回给模型**的那段文本；null/空 = 没有可当场做的事（循环据此收工）。
    /// </summary>
    public async Task<string?> RunAsync(
        BotConversation conversation, AppSettings snapshot, ChatCapabilitySet caps, CompletionResult result,
        EffectivePlatformPolicy? platformPolicy = null, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || !Enabled(snapshot)) return null;
        var search = !string.IsNullOrWhiteSpace(result.Search);
        if (!search && string.IsNullOrWhiteSpace(result.Read)) return null;
        var id = search ? "web.search" : "web.read";
        var value = search ? result.Search! : result.Read!;
        var call = new ToolCall(id, new JsonObject { [search ? "query" : "url"] = value });
        var binding = caps.Catalog.FindBinding(id);
        if (binding is null || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog)) return null;
        var verdict = ToolArguments.Validate(binding.Definition, call.Arguments);
        if (!verdict.Ok)
        {
            _log($"[循环] 工具参数不合契约（{verdict.ReasonCode}）→ 不执行");
            return null;
        }
        if (!_approvals.AllowCapability(conversation, id, value, out _, pinned: caps)) return null;
        if (!search) _log("[循环] 当场读页面");
        await ReplyCommitBoundary.BeforeEffectAsync();
        if (ct.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog)) return null;
        try
        {
            if (search && !binding.GetContribution<IResearchUseCase>()
                .TryBeginSearch(conversation.SourceKey, snapshot.WebSearchCooldownSeconds, out _)) return null;
            if (search) _log("[循环] 当场搜索");
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, PlatformSourceExecution.Current?.Lifetime ?? default);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog)) return null;
            var outcome = await binding.ExecuteAsync(conversation.SourceKey, call, lifetime.Token);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog)) return null;
            if (search && !outcome.Ok) _participation.Observe(conversation.SourceKey, ParticipationEvent.ToolFailure);
            return outcome.Ok ? outcome.ModelText : null;
        }
        catch (InvalidOperationException error) when (error.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable")
        {
            return null;
        }
    }
}

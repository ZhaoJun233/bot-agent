using BotAgent.Services.Plugins;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Services.Memory;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Tools;
using System.Text.Json.Nodes;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.Ops;
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
    private readonly IMusicUseCase? _music;
    private readonly TurnTraceStore? _traces;

    public InlineTurnTools(
        IResearchUseCase research,
        ApprovalUseCase approvals,
        ParticipationUseCase participation,
        Action<string> log,
        PluginGate? plugins = null,
        IPlatformPolicyResolver? policies = null,
        Func<bool>? isDisposed = null,
        IMusicUseCase? music = null,
        TurnTraceStore? traces = null)
    {
        _research = research;
        _approvals = approvals;
        _participation = participation;
        _log = log;
        _plugins = plugins;
        _music = music;
        _traces = traces;
        _tools = new ReplyToolAvailability(plugins, policies, isDisposed, () => research.IsReady, () => approvals.Capabilities.Catalog);
    }

    private void RecordGateDenied(string sourceKey, string toolId, string reasonCode)
        => _traces?.Node(sourceKey, TurnNodeKind.Gate, "denied", toolId: toolId, reasonCode: reasonCode);

    private void RecordToolExecError(string sourceKey, string toolId, string reasonCode)
        => _traces?.Node(sourceKey, TurnNodeKind.ToolExec, "error", toolId: toolId, reasonCode: reasonCode);

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
        var musicAllowed = MemoryUseScope.HasMemory
            && MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress)
            && (conversationSettings is null ? snapshot.EnableMusic : conversationSettings.FeatureEnabled("music"))
            && _music?.IsReady == true && (_plugins?.AllowsTool("music.listen") ?? true);
        if (musicAllowed) return true;
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
        if (ct.IsCancellationRequested) return null;
        var search = !string.IsNullOrWhiteSpace(result.Search);
        var hasRead = !search && !string.IsNullOrWhiteSpace(result.Read);
        var hasCall = !search && !hasRead && result.Call is not null;
        var hasMemListen = !search && !hasRead && !hasCall && MemoryUseScope.HasMemory && !string.IsNullOrWhiteSpace(result.Listen);
        if (!search && !hasRead && !hasCall && !hasMemListen) return null;

        var requestedId = search ? "web.search"
            : hasRead ? "web.read"
            : hasCall ? result.Call!.ToolId
            : "music.listen";
        if (result.Action == ReplyAction.Tool)
        {
            RecordGateDenied(conversation.SourceKey, requestedId, "approval_action_blocked");
            return null;
        }
        if (!Enabled(snapshot, conversationSettings))
        {
            var denyReason = MemoryUseScope.HasMemory && !MemoryUseScope.AllowsEgress(ToolEgressClass.SameScopeRead)
                ? "memory_taint_denied"
                : conversationSettings is not null && !conversationSettings.AllowsReadOnlyTools
                    ? "tool_tier_denied"
                    : "inline_tools_disabled";
            RecordGateDenied(conversation.SourceKey, requestedId, denyReason);
            return null;
        }

        if (search || hasRead)
            return await RunWebToolAsync(conversation, snapshot, caps, search, search ? result.Search! : result.Read!, platformPolicy, ct, conversationSettings);

        if (hasCall)
        {
            var inlineCall = result.Call!;
            if (inlineCall.ToolId is "web.search" or "web.read")
            {
                if (!TryResolveWebArgument(inlineCall, out var isSearch, out var argVal))
                {
                    RecordGateDenied(conversation.SourceKey, inlineCall.ToolId, "missing_argument");
                    return null;
                }
                return await RunWebToolAsync(conversation, snapshot, caps, isSearch, argVal, platformPolicy, ct, conversationSettings);
            }
            return await RunPluginCallAsync(conversation, snapshot, caps, inlineCall, platformPolicy, ct, conversationSettings);
        }

        return await RunMemoryInlineListenAsync(conversation, snapshot, caps, result.Listen!, platformPolicy, ct, conversationSettings);
    }

    /// <summary>归一化模型以 call 形式传入的联网工具参数别名（兼容 query/search/q 与 url/read）。</summary>
    internal static bool TryResolveWebArgument(InlineToolCall call, out bool isSearch, out string value)
    {
        isSearch = call.ToolId == "web.search";
        ReadOnlySpan<string> keys = isSearch ? ["query", "search", "q"] : ["url", "read"];
        foreach (var key in keys)
        {
            if (call.Arguments.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
            {
                value = raw.Trim();
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    private async Task<string?> RunWebToolAsync(
        BotConversation conversation, AppSettings snapshot, ChatCapabilitySet caps, bool search, string value,
        EffectivePlatformPolicy? platformPolicy, CancellationToken ct, EffectiveConversationSettings? conversationSettings)
    {
        var id = search ? "web.search" : "web.read";
        if (MemoryUseScope.HasMemory && (!MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress) || !MemoryUseScope.CanUse))
        {
            RecordGateDenied(conversation.SourceKey, id, "memory_taint_denied");
            return null;
        }
        var call = new ToolCall(id, new JsonObject { [search ? "query" : "url"] = value });
        var binding = caps.Catalog.FindBinding(id);
        if (binding is null)
        {
            RecordGateDenied(conversation.SourceKey, id, "tool_unbound");
            return null;
        }
        if (!_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings))
        {
            RecordGateDenied(conversation.SourceKey, id, "tool_not_visible");
            return null;
        }
        var verdict = ToolArguments.Validate(binding.Definition, call.Arguments);
        if (!verdict.Ok)
        {
            _log($"[循环] 工具参数不合契约（{verdict.ReasonCode}）→ 不执行");
            RecordGateDenied(conversation.SourceKey, id, verdict.ReasonCode ?? "invalid_args");
            return null;
        }
        if (!_approvals.AllowCapability(conversation, id, value, out _, pinned: caps)) return null;
        if (!search) _log("[循环] 当场读页面");
        await ReplyCommitBoundary.BeforeEffectAsync();
        if (ct.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings))
        {
            RecordGateDenied(conversation.SourceKey, id, ct.IsCancellationRequested ? "cancelled" : "tool_not_visible");
            return null;
        }
        if (MemoryUseScope.HasMemory && (!MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress) || !MemoryUseScope.CanUse))
        {
            RecordGateDenied(conversation.SourceKey, id, "memory_taint_denied");
            return null;
        }
        try
        {
            if (search && !binding.GetContribution<IResearchUseCase>()
                .TryBeginSearch(conversation.SourceKey, snapshot.WebSearchCooldownSeconds, out _))
            {
                RecordGateDenied(conversation.SourceKey, id, "cooldown");
                return null;
            }
            if (search) _log("[循环] 当场搜索");
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, PlatformSourceExecution.Current?.Lifetime ?? default);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings))
            {
                RecordGateDenied(conversation.SourceKey, id, lifetime.IsCancellationRequested ? "cancelled" : "tool_not_visible");
                return null;
            }
            var outcome = await ExecuteWithMemoryNoteCleanupAsync(conversation.SourceKey, binding, call, lifetime.Token);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)) return null;
            if (MemoryUseScope.HasMemory && !MemoryUseScope.CanUse)
            {
                RecordGateDenied(conversation.SourceKey, id, "memory_taint_denied");
                return null;
            }
            if (!outcome.Ok)
            {
                if (search) _participation.Observe(conversation.SourceKey, ParticipationEvent.ToolFailure);
                RecordToolExecError(conversation.SourceKey, id, outcome.ReasonCode ?? "exec_failed");
                return null;
            }
            return outcome.ModelText;
        }
        catch (InvalidOperationException error) when (error.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable")
        {
            RecordToolExecError(conversation.SourceKey, id, error.Message);
            return null;
        }
    }

    private async Task<string?> RunPluginCallAsync(
        BotConversation conversation, AppSettings snapshot, ChatCapabilitySet caps, InlineToolCall inlineCall,
        EffectivePlatformPolicy? platformPolicy, CancellationToken ct, EffectiveConversationSettings? conversationSettings)
    {
        var id = inlineCall.ToolId;
        var binding = caps.Catalog.FindBinding(id);
        if (binding is null)
        {
            RecordGateDenied(conversation.SourceKey, id, "tool_unbound");
            return null;
        }
        if (!binding.Definition.Spec.ReadOnly)
        {
            RecordGateDenied(conversation.SourceKey, id, "write_requires_approval");
            return null;
        }
        if (binding.Definition.Spec.Origin is not (ToolOrigin.Plugin or ToolOrigin.Mcp))
        {
            RecordGateDenied(conversation.SourceKey, id, "tool_origin_unsupported");
            return null;
        }
        var egress = MemoryTaintPolicy.ClassifyTool(id, binding.Definition.Spec.ReadOnly, binding.Definition.Spec.Origin.ToString());
        if (MemoryUseScope.HasMemory && (!MemoryUseScope.AllowsEgress(egress) || !MemoryUseScope.CanUse))
        {
            RecordGateDenied(conversation.SourceKey, id, "memory_taint_denied");
            return null;
        }
        if (!_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings))
        {
            RecordGateDenied(conversation.SourceKey, id, "tool_not_visible");
            return null;
        }
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
                RecordGateDenied(conversation.SourceKey, id, verdict.ReasonCode ?? "invalid_args");
                return null;
            }
        }
        if (!_approvals.AllowCapability(conversation, id, id, out _, pinned: caps)) return null;
        _log($"[循环] 当场调用只读工具 {id}");
        await ReplyCommitBoundary.BeforeEffectAsync();
        if (ct.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings))
        {
            RecordGateDenied(conversation.SourceKey, id, ct.IsCancellationRequested ? "cancelled" : "tool_not_visible");
            return null;
        }
        if (MemoryUseScope.HasMemory && (!MemoryUseScope.AllowsEgress(egress) || !MemoryUseScope.CanUse))
        {
            RecordGateDenied(conversation.SourceKey, id, "memory_taint_denied");
            return null;
        }
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, PlatformSourceExecution.Current?.Lifetime ?? default);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings))
            {
                RecordGateDenied(conversation.SourceKey, id, lifetime.IsCancellationRequested ? "cancelled" : "tool_not_visible");
                return null;
            }
            var outcome = await binding.ExecuteAsync(conversation.SourceKey, call, lifetime.Token);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)) return null;
            if (MemoryUseScope.HasMemory && !MemoryUseScope.CanUse)
            {
                RecordGateDenied(conversation.SourceKey, id, "memory_taint_denied");
                return null;
            }
            if (!outcome.Ok)
            {
                _participation.Observe(conversation.SourceKey, ParticipationEvent.ToolFailure);
                RecordToolExecError(conversation.SourceKey, id, outcome.ReasonCode ?? "exec_failed");
                return null;
            }
            return outcome.ModelText;
        }
        catch (InvalidOperationException error) when (error.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable")
        {
            RecordToolExecError(conversation.SourceKey, id, error.Message);
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

    private async Task<string?> RunMemoryInlineListenAsync(
        BotConversation conversation, AppSettings snapshot, ChatCapabilitySet caps, string rawSong,
        EffectivePlatformPolicy? platformPolicy, CancellationToken ct, EffectiveConversationSettings? conversationSettings)
    {
        const string id = "music.listen";
        if (_music is not { IsReady: true }
            || !MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress) || !MemoryUseScope.CanUse)
        {
            RecordGateDenied(conversation.SourceKey, id, _music is not { IsReady: true } ? "tool_unbound" : "memory_taint_denied");
            return null;
        }
        var wantedSong = rawSong.Trim();
        if (wantedSong.Length is < 2 or > 60)
        {
            RecordGateDenied(conversation.SourceKey, id, "invalid_args");
            return null;
        }
        if (!_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings))
        {
            RecordGateDenied(conversation.SourceKey, id, "tool_not_visible");
            return null;
        }
        if (!_approvals.AllowCapability(conversation, id, wantedSong, out _, pinned: caps)) return null;
        await ReplyCommitBoundary.BeforeEffectAsync();
        if (ct.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)
            || !MemoryUseScope.AllowsEgress(ToolEgressClass.ExternalReadEgress) || !MemoryUseScope.CanUse)
        {
            RecordGateDenied(conversation.SourceKey, id, "memory_taint_denied");
            return null;
        }
        try
        {
            if (!_music.TryBeginListen(conversation.SourceKey, snapshot.MusicListenCooldownSeconds, out _))
            {
                RecordGateDenied(conversation.SourceKey, id, "cooldown");
                return null;
            }
            _log("[循环] 当场听歌");
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, PlatformSourceExecution.Current?.Lifetime ?? default);
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings))
            {
                RecordGateDenied(conversation.SourceKey, id, lifetime.IsCancellationRequested ? "cancelled" : "tool_not_visible");
                return null;
            }
            string? note;
            try
            {
                note = await MemoryUseScope.Use(() => _music.ListenAsync(conversation.SourceKey, wantedSong, "群友", lifetime.Token));
            }
            finally
            {
                _ = _music.TakeNote(conversation.SourceKey);
            }
            if (lifetime.IsCancellationRequested || !_tools.Allows(snapshot, platformPolicy, id, caps.Catalog, conversationSettings)
                || !MemoryUseScope.CanUse)
                return null;
            if (string.IsNullOrWhiteSpace(note))
            {
                _participation.Observe(conversation.SourceKey, ParticipationEvent.ToolFailure);
                RecordToolExecError(conversation.SourceKey, id, "listen_failed");
                return null;
            }
            return note;
        }
        catch (MemoryUseInvalidatedException)
        {
            RecordGateDenied(conversation.SourceKey, id, "memory_invalidated");
            return null;
        }
        catch (InvalidOperationException error) when (error.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing")
        {
            RecordToolExecError(conversation.SourceKey, id, error.Message);
            return null;
        }
    }
}

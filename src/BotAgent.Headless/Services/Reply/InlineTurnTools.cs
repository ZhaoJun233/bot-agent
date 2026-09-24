using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Tools;
using System.Text.Json.Nodes;
using BotAgent.Services.Net;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;

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
///   · **冷却照旧**：搜索仍受同会话冷却（<see cref="ResearchUseCase.TryBeginSearch" />），拒绝就不做；
///   · **只做只读**：只接 search / read。语音/表情/戳/分享一律不在这里做 ——
///     那些会"打扰别人"，仍走发送阶段统一裁决（§8：两条路的授权边界不许合一）。
/// </summary>
public sealed class InlineTurnTools
{
    private readonly ResearchUseCase _research;
    private readonly ApprovalUseCase _approvals;
    private readonly ParticipationUseCase _participation;
    private readonly Action<string> _log;

    public InlineTurnTools(
        ResearchUseCase research,
        ApprovalUseCase approvals,
        ParticipationUseCase participation,
        Action<string> log)
    {
        _research = research;
        _approvals = approvals;
        _participation = participation;
        _log = log;
    }

    /// <summary>这一轮有没有"当场做工具"的可能（没开循环 / 没有搜索服务 = 没有）。</summary>
    public bool Enabled(AppSettings snapshot) => snapshot.MaxAgentSteps > 1 && _research.IsReady;

    /// <summary>
    /// 做掉这一步点名的工具，返回**喂回给模型**的那段文本；null/空 = 没有可当场做的事（循环据此收工）。
    /// </summary>
    public async Task<string?> RunAsync(
        BotConversation conversation, AppSettings snapshot, ChatCapabilitySet caps, CompletionResult result)
    {
        if (!Enabled(snapshot))
        {
            return null;   // 没开循环 / 没有搜索服务：什么都不做（这一支就是"改造前"）
        }

        if (!string.IsNullOrWhiteSpace(result.Search))
        {
            var query = result.Search!;

            // 批次 A 收尾（§9.3 第 4 格）：**参数契约的执行前校验**（只对已接入校验的工具严格）。
            // 不合契约就不执行 —— fail-closed，且只记原因码不记正文。
            var verdict = ToolArgs.Validate("web.search", new JsonObject { ["query"] = query });
            if (!verdict.Ok)
            {
                _log($"[循环] 当场搜索的参数不合契约（{verdict.ReasonCode}）→ 不执行");
                return null;
            }

            if (!_approvals.AllowCapability(conversation, "web.search", query, out var whySearch, pinned: caps))
            {
                _log($"[循环] 当场搜索被闸门拒绝（{whySearch}）→ 不再多问一次");
                return null;
            }

            if (!_research.TryBeginSearch(conversation.SourceKey, snapshot.WebSearchCooldownSeconds, out var cooldownWhy))
            {
                _log($"[循环] 当场搜索被冷却挡下（{cooldownWhy}）→ 不再多问一次");
                return null;
            }

            _log($"[循环] 当场搜索「{query}」（第一步就做掉，不再拖到下一轮）");
            var found = await _research.SearchAsync(conversation.SourceKey, query);
            if (found is null || !found.HasContent)
            {
                _participation.Observe(conversation.SourceKey, ParticipationEvent.ToolFailure);
                return null;   // 没搜到：不再多问一次（让它按"没查到"如实说）
            }

            return found.Describe();
        }

        if (!string.IsNullOrWhiteSpace(result.Read))
        {
            var url = result.Read!;

            var verdict = ToolArgs.Validate("web.read", new JsonObject { ["url"] = url });
            if (!verdict.Ok)
            {
                _log($"[循环] 当场读页面的参数不合契约（{verdict.ReasonCode}）→ 不执行");
                return null;
            }

            if (!_approvals.AllowCapability(conversation, "web.read", url, out var whyRead, pinned: caps))
            {
                _log($"[循环] 当场读页面被闸门拒绝（{whyRead}）→ 不再多问一次");
                return null;
            }

            _log($"[循环] 当场读页面 {TextRules.Shorten(url, 80)}");
            var text = await _research.ReadPageAsync(conversation.SourceKey, url);
            return string.IsNullOrWhiteSpace(text) ? null : "页面正文：\n" + text;
        }

        return null;
    }
}

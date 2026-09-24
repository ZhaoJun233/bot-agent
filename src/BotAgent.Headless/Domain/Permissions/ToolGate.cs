using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// Fail-Closed 的能力闸门（V3 §9.2）。**判定顺序就是安全边界**：
/// 先看登记表 → 再看策略白名单 → 再看类别禁令 → 再看目标与预算 → 最后才轮到审批。
/// 审批永远排在“类别禁令”之后 —— 高风险能力不因为有人点了同意就变得可执行。
/// </summary>
public static class ToolGate
{
    public static ToolDecision Evaluate(
        ToolRegistry? registry,
        ToolPolicy? policy,
        ToolRequest? request,
        ApprovalTicket? approval = null)
    {
        // 拿不到登记表 / 策略 / 请求：拒绝，而不是降级到更高权限（V3 §9.2）
        if (registry is null)
        {
            return new ToolDecision(false, "no_registry");
        }

        if (policy is null)
        {
            return new ToolDecision(false, "no_policy");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ToolId))
        {
            return new ToolDecision(false, "bad_request");
        }

        if (string.IsNullOrWhiteSpace(request.ConversationKey))
        {
            // 没有会话上下文就不允许任何会话相关的动作（避免“凭空”执行）
            return new ToolDecision(false, "no_conversation");
        }

        if (!registry.TryGet(request.ToolId, out var descriptor))
        {
            return new ToolDecision(false, "unknown_tool");
        }

        if (!policy.AllowedTools.Contains(descriptor.Id))
        {
            return new ToolDecision(false, "not_allowlisted");
        }

        // 高风险档：**任何**审批都不能放开（这条必须在审批判断之前）。
        // 唯一的出口是策略**显式点名**的那几个（ToolPolicy.HighRiskExceptions）——
        // 那是“另一条已存在的授权边界”的显式化（只可能来自服务端配置：// 路径的面板开关 + 工作目录 + 超时），
        // 不是审批放开的，也不是模型/网页能影响的。默认 null = 一个都不放开。
        if (ToolDescriptor.AlwaysDenied(descriptor.Category)
            && !(policy.HighRiskExceptions?.Contains(descriptor.Id) ?? false))
        {
            return new ToolDecision(false, "category_denied", descriptor.Category.ToString());
        }

        if (descriptor.Category == ToolCategory.SendMessage &&
            !string.IsNullOrWhiteSpace(request.TargetConversationKey) &&
            !string.Equals(request.TargetConversationKey, request.ConversationKey, StringComparison.Ordinal))
        {
            // 只能发当前会话：不能由模型切换到别的群/私聊（V3 §8.3 / §9.1）
            return new ToolDecision(false, "cross_conversation");
        }

        if (request.CallIndex >= policy.MaxCallsPerRun)
        {
            // 重试、换字段、改工具名都不能绕过一次运行的预算
            return new ToolDecision(false, "budget_exhausted");
        }

        // 递了票据就必须与本次请求**完全对上**（工具 + 会话）。
        // 对不上说明调用方把授权搞错了 —— 宁可拒绝（不确定时降级、不升级），
        // 也不要想当然地“反正这个能力本来就不需要审批”而放行。
        if (approval is { } presented)
        {
            if (!string.Equals(presented.ToolId, descriptor.Id, StringComparison.Ordinal))
            {
                return new ToolDecision(false, "approval_tool_mismatch");
            }

            if (!string.Equals(presented.ConversationKey, request.ConversationKey, StringComparison.Ordinal))
            {
                return new ToolDecision(false, "approval_conversation_mismatch");
            }
        }

        if (policy.RequiresApproval(descriptor))
        {
            if (approval is null)
            {
                return new ToolDecision(false, "approval_required");
            }

            if (!policy.ApprovalCouldGrant(descriptor))
            {
                // 这个类别不在“审批可放开”的名单里 → 拒绝（不因为票据存在就放行）
                return new ToolDecision(false, "approval_cannot_grant");
            }

            var ticket = approval.Value;
            if (!string.Equals(ticket.ToolId, descriptor.Id, StringComparison.Ordinal))
            {
                return new ToolDecision(false, "approval_tool_mismatch");
            }

            if (!string.Equals(ticket.ConversationKey, request.ConversationKey, StringComparison.Ordinal))
            {
                return new ToolDecision(false, "approval_conversation_mismatch");
            }
        }

        return new ToolDecision(true, "allowed");
    }
}

/// <summary>
/// 一次“运行”里已经用掉多少次工具调用（V3 §9.2 的预算记账）。
///
/// 为什么单独一个类：预算的**语义**在闸门里（`callIndex >= MaxCallsPerRun` → 拒绝），
/// 而**记账**在调用方。把它抽出来，记账规则就能被确定性探针验证，而不是只能靠端到端场景碰运气。
/// 口径：新的一轮用户消息 → <see cref="Reset" />；放行一次 → <see cref="Commit" />；
/// 序号用 <see cref="Peek" /> 传给闸门（**只有放行才自增**，被拒绝的调用不占预算）。
/// </summary>
public sealed class ToolCallBudget
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _used = new(StringComparer.Ordinal);

    /// <summary>这个会话本轮已经用掉几次（= 下一次调用要传给闸门的 callIndex）。</summary>
    public int Peek(string conversationKey)
        => _used.TryGetValue(conversationKey ?? string.Empty, out var used) ? used : 0;

    /// <summary>放行一次调用 → 记一笔。</summary>
    public void Commit(string conversationKey)
    {
        var key = conversationKey ?? string.Empty;
        _used[key] = Peek(key) + 1;
    }

    /// <summary>新的一轮用户消息 → 预算重来。</summary>
    public void Reset(string conversationKey) => _used[conversationKey ?? string.Empty] = 0;

    /// <summary>会话被删除 → 记账也丢掉。</summary>
    public void Forget(string conversationKey) => _used.TryRemove(conversationKey ?? string.Empty, out _);
}

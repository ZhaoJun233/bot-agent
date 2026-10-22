using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// <see cref="ApprovalStore" /> 的**决策**部分：批 / 撤 / 取一次性票据。
/// 三条路都走同一把锁（并发竞争只会有一个调用拿到票）；台账本体与状态推进见 ApprovalStore.cs，
/// 纯策略函数（脱敏 / 资格 / 名单归一）见 ApprovalStore.Policy.cs。
/// </summary>
public sealed partial class ApprovalStore
{
    /// <summary>审批（同意 / 拒绝）。只有名单内的人、且在有效期内、且还没被决定过才有效。</summary>
    public ApprovalOutcome Decide(
        string requestId,
        string approver,
        string conversationKey,
        bool approve,
        DateTimeOffset now,
        string? approverRole = null)
    {
        lock (_sync)
        {
            if (!_byId.TryGetValue(requestId ?? string.Empty, out var request))
            {
                return new ApprovalOutcome(false, ApprovalStatus.Pending, "unknown_request");
            }

            request = Expire(request, now);
            if (request.Status == ApprovalStatus.Expired)
            {
                return new ApprovalOutcome(false, ApprovalStatus.Expired, "expired", request);
            }

            if (request.Status != ApprovalStatus.Pending)
            {
                return new ApprovalOutcome(false, request.Status, "already_decided", request);
            }

            if (!string.Equals(request.ConversationKey, conversationKey ?? string.Empty, StringComparison.Ordinal))
            {
                // 别的会话里说“通过”不能替这个会话批准
                return new ApprovalOutcome(false, ApprovalStatus.Pending, "cross_conversation", request);
            }

            if (!IsAuthorizedApprover(request, approver, approverRole))
            {
                // 普通群成员（既不在名单里、也不是 owner/admin）发“通过”不算审批
                return new ApprovalOutcome(false, ApprovalStatus.Pending, "not_an_approver", request);
            }

            var decided = request with
            {
                Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
                DecidedBy = (approver ?? string.Empty).Trim(),
                DecidedAt = now,
                ReasonCode = approve ? "approved" : "rejected",
            };

            _byId[decided.RequestId] = decided;
            return new ApprovalOutcome(true, decided.Status, decided.ReasonCode, decided);
        }
    }

    /// <summary>取消（发起方撤回）。取消后不可再批、也不可用。</summary>
    public ApprovalOutcome Cancel(string requestId, string conversationKey, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_byId.TryGetValue(requestId ?? string.Empty, out var request))
            {
                return new ApprovalOutcome(false, ApprovalStatus.Pending, "unknown_request");
            }

            if (!string.Equals(request.ConversationKey, conversationKey ?? string.Empty, StringComparison.Ordinal))
            {
                return new ApprovalOutcome(false, request.Status, "cross_conversation", request);
            }

            if (request.Status != ApprovalStatus.Pending)
            {
                return new ApprovalOutcome(false, request.Status, "already_decided", request);
            }

            var cancelled = request with { Status = ApprovalStatus.Cancelled, ReasonCode = "cancelled", DecidedAt = now };
            _byId[cancelled.RequestId] = cancelled;
            return new ApprovalOutcome(true, ApprovalStatus.Cancelled, "cancelled", cancelled);
        }
    }

    /// <summary>
    /// 消费审批（一次性）。成功才返回票据；重复消费 / 未批准 / 过期 / 跨会话全部失败。
    /// 并发竞争由同一把锁串行化 —— 只会有一个调用拿到票据。
    /// </summary>
    public (ApprovalOutcome Outcome, ApprovalTicket? Ticket) Consume(
        string requestId,
        string conversationKey,
        DateTimeOffset now,
        int currentPolicyVersion = -1)
    {
        lock (_sync)
        {
            if (!_byId.TryGetValue(requestId ?? string.Empty, out var request))
            {
                return (new ApprovalOutcome(false, ApprovalStatus.Pending, "unknown_request"), null);
            }

            if (currentPolicyVersion >= 0 && request.PolicyVersion > 0 && request.PolicyVersion != currentPolicyVersion)
            {
                // 期间改过配置（策略版本变了）→ 这张单子是**旧策略**下批的，作废不执行（V3 §9.4）
                return (new ApprovalOutcome(false, request.Status, "stale_policy", request), null);
            }

            request = Expire(request, now);
            if (request.Status == ApprovalStatus.Expired)
            {
                return (new ApprovalOutcome(false, ApprovalStatus.Expired, "expired", request), null);
            }

            if (request.Status != ApprovalStatus.Approved)
            {
                return (new ApprovalOutcome(false, request.Status, request.ReasonCode, request), null);
            }

            if (request.Consumed)
            {
                // 重放：这张票据已经用过了
                return (new ApprovalOutcome(false, request.Status, "already_consumed", request), null);
            }

            if (!string.Equals(request.ConversationKey, conversationKey ?? string.Empty, StringComparison.Ordinal))
            {
                return (new ApprovalOutcome(false, request.Status, "cross_conversation", request), null);
            }

            var consumed = request with { Consumed = true };
            _byId[consumed.RequestId] = consumed;

            var ticket = new ApprovalTicket(
                consumed.RequestId,
                consumed.ToolId,
                consumed.ConversationKey,
                now);

            return (new ApprovalOutcome(true, ApprovalStatus.Approved, "consumed", consumed), ticket);
        }
    }
}

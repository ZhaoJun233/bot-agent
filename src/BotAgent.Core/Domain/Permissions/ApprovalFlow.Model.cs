using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// <see cref="ApprovalFlow" /> 的**模型提议**入口：模型说“我想调工具 / 我想问一句”，
/// 服务端决定要不要开一张待批（待答）单。开出来的永远是 <see cref="ApprovalStatus.Pending" /> —— 不是授权。
/// </summary>
public static partial class ApprovalFlow
{
    /// <summary>
    /// 模型想问问题（<c>action=ask</c>）时，服务端要不要开一张**待答**单。
    ///
    /// 与工具审批的区别：这里**没有“执行”这一步** —— 提问只是往当前会话说一句话，
    /// 谁回答都行（回答也只是一条普通消息，不触发任何动作）。所以它不需要审批人名单，
    /// 只需要：一次性编号、短有效期、会话绑定（同一会话里已有待答的问题就不再开第二张，免得刷屏）。
    /// </summary>
    public static ApprovalCreateResult CreateForModelQuestion(
        ApprovalStore? store,
        string? questionText,
        string conversationKey,
        string requesterFingerprint,
        DateTimeOffset now,
        Func<string> newRequestId,
        int policyVersion = 0)
    {
        if (store is null)
        {
            return new ApprovalCreateResult(false, "no_store");
        }

        var question = Domain.Reply.ReplyDecisionRules.SanitizeQuestion(questionText);
        if (question is null)
        {
            // 空问题不值得开单（模型写了 action=ask 却没写问题）→ 不放行
            return new ApprovalCreateResult(false, "empty_question");
        }

        if (store.FindPending(conversationKey, QuestionToolId, now) is not null)
        {
            return new ApprovalCreateResult(false, "already_pending");
        }

        var request = store.Create(
            requestId: newRequestId(),
            toolId: QuestionToolId,
            summaryHint: question,
            conversationKey: conversationKey,
            requesterFingerprint: requesterFingerprint,
            approvers: Array.Empty<string>(),   // 提问不需要审批人：谁答都算答完
            now: now,
            ttlSeconds: TtlSeconds,
            approverRoles: Array.Empty<string>(),
            policyVersion: policyVersion);

        return new ApprovalCreateResult(true, "pending", BuildQuestionAnnouncement(request.RequestId, question), request);
    }

    /// <summary>
    /// 模型说“我想调工具”时，服务端要不要开一张待批单。
    /// 只有**服务端固定**的那个假工具会开（模型写别的名字不认）；
    /// 同一会话同一工具已经有待批的单子就不再开第二张（免得刷屏）。
    /// </summary>
    public static ApprovalCreateResult CreateForModelTool(
        ApprovalStore? store,
        string? requestedToolId,
        string conversationKey,
        string requesterFingerprint,
        IEnumerable<string>? configuredApprovers,
        bool allowGroupAdmins,
        bool isGroup,
        DateTimeOffset now,
        Func<string> newRequestId,
        int policyVersion = 0)
    {
        if (store is null)
        {
            return new ApprovalCreateResult(false, "no_store");
        }

        if (!string.Equals((requestedToolId ?? string.Empty).Trim(), FixedToolId, StringComparison.Ordinal))
        {
            // 模型点名的工具不在服务端固定集合里 → 不开单（更谈不上执行）
            return new ApprovalCreateResult(false, "tool_not_fixed");
        }

        if (store.FindPending(conversationKey, FixedToolId, now) is not null)
        {
            return new ApprovalCreateResult(false, "already_pending");
        }

        var request = store.Create(
            requestId: newRequestId(),
            toolId: FixedToolId,
            summaryHint: FixedToolSummary,
            conversationKey: conversationKey,
            requesterFingerprint: requesterFingerprint,
            approvers: configuredApprovers,
            now: now,
            ttlSeconds: TtlSeconds,
            approverRoles: allowGroupAdmins && isGroup ? GroupApproverRoles : Array.Empty<string>(),
            policyVersion: policyVersion);

        return new ApprovalCreateResult(true, "pending", BuildRequestAnnouncement(request.RequestId, TtlSeconds,
            allowGroupAdmins: request.ApproverRoles is { Count: > 0 }), request);
    }

    /// <summary>服务端构建真实受控工具的人机审批公告（全部由服务端拼，严禁拼入原始不可信参数值）。</summary>
    public static string BuildToolRequestAnnouncement(
        string requestId,
        string toolId,
        string safeSummary,
        int ttlSeconds,
        bool allowGroupAdmins)
    {
        var who = allowGroupAdmins ? "群主/管理员" : "授权审批人";
        return $"【需要确认】执行工具 {toolId}（{safeSummary}）｜编号 {requestId}"
            + $"（{who}回复「同意 {requestId}」或「拒绝 {requestId}」；"
            + $"{ttlSeconds} 秒内有效，过期作废，只能批一次）";
    }

    /// <summary>为真实非只读工具开一张一次性审批单（需审批总开关打开且在策略允许集合内）。</summary>
    public static ApprovalCreateResult CreateForTool(
        ApprovalStore? store,
        string toolId,
        string safeSummary,
        string conversationKey,
        string requesterFingerprint,
        IEnumerable<string>? configuredApprovers,
        bool allowGroupAdmins,
        bool isGroup,
        DateTimeOffset now,
        Func<string> newRequestId,
        int policyVersion = 0)
    {
        if (store is null)
            return new ApprovalCreateResult(false, "no_store");
        var cleanId = (toolId ?? string.Empty).Trim();
        if (cleanId.Length == 0)
            return new ApprovalCreateResult(false, "bad_tool");
        if (store.FindPending(conversationKey, cleanId, now) is not null)
            return new ApprovalCreateResult(false, "already_pending");

        var request = store.Create(
            requestId: newRequestId(),
            toolId: cleanId,
            summaryHint: safeSummary,
            conversationKey: conversationKey,
            requesterFingerprint: requesterFingerprint,
            approvers: configuredApprovers,
            now: now,
            ttlSeconds: TtlSeconds,
            approverRoles: allowGroupAdmins && isGroup ? GroupApproverRoles : Array.Empty<string>(),
            policyVersion: policyVersion);

        return new ApprovalCreateResult(
            true,
            "pending",
            BuildToolRequestAnnouncement(request.RequestId, cleanId, ApprovalStore.RedactSummary(safeSummary), TtlSeconds,
                request.ApproverRoles is { Count: > 0 }),
            request);
    }
}

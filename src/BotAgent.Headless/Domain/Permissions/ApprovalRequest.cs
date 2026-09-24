using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>审批单的状态（V3 §9.4：只有这五种）。</summary>
public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Cancelled = 4,
}

/// <summary>
/// 一张**审批单**。注意这里刻意只存“摘要”：
/// 工具 id、脱敏后的短摘要、会话 key、请求者指纹、时间与状态 ——
/// **不存**密钥、完整提示词、网页正文或原始参数（V3 §9.4）。
/// </summary>
public sealed record ApprovalRequest(
    string RequestId,
    string ToolId,
    string Summary,
    string ConversationKey,
    string RequesterFingerprint,
    IReadOnlySet<string> Approvers,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ApprovalStatus Status = ApprovalStatus.Pending,
    string? DecidedBy = null,
    DateTimeOffset? DecidedAt = null,
    string ReasonCode = "pending",
    bool Consumed = false,

    /// <summary>
    /// **按身份**的审批人（如群里的 <c>owner</c> / <c>admin</c>）。
    /// 与 <see cref="Approvers" />（按 id 点名的名单）取**并集**：
    /// 群里普通成员发“通过”既不在名单里、也没有这两个身份 → 批不动。
    /// 默认空集 = 只能靠 id 名单（Fail-Closed）。
    /// </summary>
    IReadOnlySet<string>? ApproverRoles = null,

    /// <summary>
    /// 建单时的**策略版本**（V3 §9.4 要求审批绑定原策略）。
    /// 消费时若当前策略版本已经不是这个数，说明期间改过配置 → 单据作废、不执行。
    /// </summary>
    int PolicyVersion = 0);

/// <summary>一次审批决策的结果。</summary>
public readonly record struct ApprovalOutcome(
    bool Ok,
    ApprovalStatus Status,
    string ReasonCode,
    ApprovalRequest? Request = null)
{
    public string Describe() => $"ok={Ok} status={Status} reason={ReasonCode}";
}

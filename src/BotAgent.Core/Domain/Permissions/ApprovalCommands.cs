using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>群里那条“同意 / 拒绝”命令。</summary>
public enum ApprovalCommandKind
{
    Approve = 0,
    Reject = 1,
}

/// <summary>解析出来的审批命令（动词 + 编号）。动词不认识或编号形状不对 → 根本解析不出来。</summary>
public readonly record struct ApprovalCommand(ApprovalCommandKind Kind, string RequestId);

/// <summary>处理一条“同意 / 拒绝”之后要做什么（结构化；正文一律由服务端拼，不用模型的话）。</summary>
public readonly record struct ApprovalInboundResult(
    bool Handled,
    bool ShouldExecute,
    string ReasonCode,
    string? Reply = null,
    ApprovalTicket? Ticket = null);

/// <summary>模型想调工具时，服务端要不要开一张审批单。</summary>
public readonly record struct ApprovalCreateResult(
    bool Created,
    string ReasonCode,
    string? Announcement = null,
    ApprovalRequest? Request = null);

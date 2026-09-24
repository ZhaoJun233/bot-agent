using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// <see cref="ApprovalFlow" /> 的**人回话**入口：把群里那条“同意 XXXX / 拒绝 XXXX”变成执行或拒绝，
/// 并给出服务端拼的回执。所有校验（身份 / 会话 / 有效期 / 一次性 / 策略版本）都在 Handle 里。
/// </summary>
public static partial class ApprovalFlow
{
    /// <summary>批准并执行之后的回执（明确写清“无真实副作用”）。</summary>
    public static string BuildExecutedReply(string requestId)
        => $"【演示】编号 {requestId} 已确认，执行了固定假工具 {FixedToolId}。"
           + "它没有任何真实副作用 —— 不代表本系统具备 shell / 文件 / 进程控制能力。";

    /// <summary>拒绝之后的回执（不执行任何东西）。</summary>
    public static string BuildRejectedReply(string requestId)
        => $"【演示】编号 {requestId} 已被拒绝，没有执行任何动作。";

    /// <summary>固定假工具的说明（也是审批单上的摘要）。</summary>
    public const string FixedToolSummary = "执行固定假工具 demo.echo（演示用，无真实副作用）";

    /// <summary>
    /// 处理一条审批命令。**所有校验都在这里**（身份 / 会话 / 有效期 / 一次性 / 策略版本）。
    /// 返回 <c>Handled=true</c> 表示这条消息被审批流程吃掉了（不再走模型）。
    /// </summary>
    public static ApprovalInboundResult Handle(
        ApprovalStore? store,
        ApprovalCommand command,
        string requesterId,
        string? requesterRole,
        string conversationKey,
        DateTimeOffset now,
        int currentPolicyVersion)
    {
        if (store is null)
        {
            // 台账不可用 → 不执行（Fail-Closed），而且**不吞消息**：让它走普通链路，
            // 免得“审批没配好”表现为“机器人不理人”。
            return new ApprovalInboundResult(false, false, "no_store");
        }

        var id = command.RequestId;

        if (command.Kind == ApprovalCommandKind.Reject)
        {
            var rejected = store.Decide(id, requesterId, conversationKey, approve: false, now, requesterRole);
            if (!rejected.Ok)
            {
                return new ApprovalInboundResult(true, false, rejected.ReasonCode);
            }

            return new ApprovalInboundResult(true, false, "rejected", BuildRejectedReply(id));
        }

        var decided = store.Decide(id, requesterId, conversationKey, approve: true, now, requesterRole);
        if (!decided.Ok)
        {
            // 伪造 / 过期 / 跨会话 / 已决定：都不执行，也不回话（只记一行结构化日志）
            return new ApprovalInboundResult(true, false, decided.ReasonCode);
        }

        var (outcome, ticket) = store.Consume(id, conversationKey, now, currentPolicyVersion);
        if (!outcome.Ok || ticket is null)
        {
            // 重放 / 版本过期 / 并发竞争失败：**不执行**
            return new ApprovalInboundResult(true, false, outcome.ReasonCode);
        }

        return new ApprovalInboundResult(true, true, "approved", BuildExecutedReply(id), ticket);
    }
}

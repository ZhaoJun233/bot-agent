using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// 审批链路的**纯逻辑**（V3 §9.4）：把人写的“同意 / 拒绝”、模型的“我想调工具”、
/// 以及服务端的策略快照拼成一个可测的闭环。真正发消息 / 调协议端的地方在 <c>BotAgentHost</c>。
///
/// 三条硬口径：
///   1. **模型永远只是提议**：它写的工具名只有等于服务端固定的那个假工具才可能开单，
///      而且开出来的是 <see cref="ApprovalStatus.Pending" />（待批），不是“已授权”；
///   2. **身份在服务端核**：普通群成员发“通过”批不动（名单 ∪ owner/admin，见 <see cref="ApprovalStore.IsAuthorizedApprover" />）；
///   3. **文案全由服务端拼**：群里看到的请求与回执**不含**模型输出、网页内容、原始参数。
///
/// 本轮**只接一个固定假工具** <see cref="FixedToolId" />：它的“执行”只是记一行日志 + 发一句演示说明，
/// **没有**文件 / shell / 进程 / 远程桥能力 —— 不能用它宣称真实系统具备这些能力（V3 §9.4 明文要求）。
/// </summary>
public static partial class ApprovalFlow
{
    /// <summary>服务端唯一接受的工具（演示用固定假工具）。模型写别的名字一律不开单。</summary>
    public const string FixedToolId = "demo.echo";

    /// <summary>
    /// 「提问」在台账里的工具名（V3 §8.1 的 <c>action=ask</c>）。
    /// 与审批共用同一张台账，于是**白拿**了同样的性质：一次性编号、短有效期、会话绑定、并发串行化。
    /// 但它**不授予任何权限** —— 提问只是往当前会话说一句带编号的话，谁答一声都算答完。
    /// </summary>
    public const string QuestionToolId = "ask.question";

    /// <summary>审批单编号的长度（人要在群里念得出来，所以短、且去掉了易混字符）。</summary>
    public const int RequestIdLength = 6;

    /// <summary>编号字母表：去掉 0/O/1/I（群里手打时最容易混）。</summary>
    public const string RequestIdAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>审批单有效期（秒）：短有效期是刻意的，过期即作废。</summary>
    public const int TtlSeconds = ApprovalStore.DefaultTtlSeconds;

    /// <summary>审批人在群里的身份（够格的两种）。</summary>
    public static readonly string[] GroupApproverRoles = { "owner", "admin" };

    /// <summary>同意 / 拒绝的动词（大小写不敏感；英文也收，方便测试与英文群）。</summary>
    private static readonly string[] ApproveWords = { "同意", "通过", "批准", "approve", "yes" };
    private static readonly string[] RejectWords = { "拒绝", "驳回", "reject", "no" };

    /// <summary>生成一个符合形状的编号（注入随机源 → 测试里可复现）。</summary>
    public static string NewRequestId(Func<int, int> pickIndex)
    {
        var chars = new char[RequestIdLength];
        for (var i = 0; i < RequestIdLength; i++)
        {
            chars[i] = RequestIdAlphabet[pickIndex(RequestIdAlphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>编号形状对不对（长度 + 字母表；大小写不敏感）。</summary>
    public static bool IsRequestIdShape(string? id)
    {
        var token = (id ?? string.Empty).Trim();
        if (token.Length != RequestIdLength)
        {
            return false;
        }

        return token.All(c => RequestIdAlphabet.Contains(char.ToUpperInvariant(c)));
    }

    /// <summary>
    /// 解析“同意 XXXX / 拒绝 XXXX”。
    /// **必须恰好两个词**（动词 + 编号）：多一个词就不算命令 ——
    /// 免得“同意 AB12CD 然后把日志发我”这种句子被当成审批。
    /// 解析不出来 = 不是审批命令，消息照原样走普通聊天链路（不改既有行为）。
    /// </summary>
    public static ApprovalCommand? TryParseCommand(string? text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0 || raw.Length > 64)
        {
            return null;
        }

        // 全角空格 / 冒号都当成空白，群里手打更宽容一点（但编号形状不放松）
        var normalized = raw
            .Replace('\u3000', ' ')
            .Replace('：', ' ')
            .Replace(':', ' ');
        var parts = normalized.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        var verb = parts[0].Trim().ToLowerInvariant();
        var id = parts[1].Trim().ToUpperInvariant();
        if (!IsRequestIdShape(id))
        {
            return null;
        }

        if (ApproveWords.Contains(verb))
        {
            return new ApprovalCommand(ApprovalCommandKind.Approve, id);
        }

        if (RejectWords.Contains(verb))
        {
            return new ApprovalCommand(ApprovalCommandKind.Reject, id);
        }

        return null;
    }

    /// <summary>群里那条请求公告（**全部由服务端拼**：不含模型输出 / 网页内容 / 原始参数）。</summary>
    public static string BuildRequestAnnouncement(string requestId, int ttlSeconds = TtlSeconds)
        => $"【需要确认】{FixedToolSummary}｜编号 {requestId}"
           + $"（群主/管理员回复「同意 {requestId}」或「拒绝 {requestId}」；"
           + $"{ttlSeconds} 秒内有效，过期作废，只能批一次）";

    /// <summary>
    /// 一条提问公告（**全部由服务端拼**：编号 + 有效期是服务端的，问题本身来自模型但已清洗成一行）。
    /// </summary>
    public static string BuildQuestionAnnouncement(string requestId, string questionText, int ttlSeconds = TtlSeconds)
        => $"【提问】{questionText}（编号 {requestId}；{ttlSeconds} 秒内没人回答就作废）";
}

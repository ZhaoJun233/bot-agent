using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// <see cref="ApprovalStore" /> 的**纯策略**部分：摘要脱敏、审批人资格、名单归一。
/// 这里不碰台账状态（无锁、无副作用）—— 于是这几条口径能被探针直接调（见 SafetyProbe）。
/// </summary>
public sealed partial class ApprovalStore
{
    /// <summary>
    /// 摘要脱敏：只留短的、无凭据形状的提示。
    /// 审批界面不该出现密钥、完整提示词、网页正文或原始参数（V3 §9.4）。
    /// </summary>
    public static string RedactSummary(string? hint)
    {
        const int maxLen = 40;
        var text = (hint ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "(无摘要)";
        }

        var looksSecret =
            text.Length > 60 ||
            text.Contains("sk-", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("BEGIN ", StringComparison.Ordinal) ||
            text.Any(c => c == '\n' || c == '\r');

        if (looksSecret)
        {
            return "(已脱敏)";
        }

        return text.Length <= maxLen ? text : text[..maxLen] + "…";
    }

    /// <summary>
    /// 这个人有资格批这张单吗：**按 id 点名的名单** ∪ **按身份**（<c>owner</c> / <c>admin</c>）。
    /// 两样都不占 → 批不动（普通群成员发“通过”不算审批，V3 §9.4）。
    /// </summary>
    public static bool IsAuthorizedApprover(ApprovalRequest? request, string? approver, string? approverRole)
    {
        if (request is null)
        {
            return false;
        }

        var who = (approver ?? string.Empty).Trim();
        if (who.Length > 0 && request.Approvers.Contains(who))
        {
            return true;
        }

        var role = (approverRole ?? string.Empty).Trim().ToLowerInvariant();
        return role.Length > 0 && (request.ApproverRoles?.Contains(role) ?? false);
    }

    /// <summary>
    /// 把面板里那串“审批人名单”解析成身份集合：裸 QQ 号（面板占位提示就是 <c>10001</c>）统一补成
    /// <c>user:10001</c>，已经写了前缀的照原样。**审批时传进来的也是 <c>user:&lt;uid&gt;</c>** ——
    /// 不归一化，点名名单就永远不命中（V3 §9.4 的“核验审批者身份”）。
    /// 分隔符与面板一致：半角/全角逗号、分号、空白都认。
    /// </summary>
    public static HashSet<string> NormalizeApprovers(string? raw)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in (raw ?? string.Empty).Split(
                     new[] { ',', '\uFF0C', ';', '\uFF1B', ' ', '\t', '\n', '\r' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            set.Add(trimmed.StartsWith("user:", StringComparison.OrdinalIgnoreCase)
                ? "user:" + trimmed[5..].Trim()
                : "user:" + trimmed);
        }

        return set;
    }
}

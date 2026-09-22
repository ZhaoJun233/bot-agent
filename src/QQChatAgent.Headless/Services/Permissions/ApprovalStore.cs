using System;
using System.Collections.Generic;
using System.Linq;

namespace QQChatAgent.Services.Permissions;

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

/// <summary>
/// 人在回路的审批台账（V3 §9.4）。纯内存、可注入时间 → 能被确定性单测。
///
/// 硬性约束（每条都有对应单测）：
///   · **身份**：只有 <see cref="ApprovalRequest.Approvers" /> 里的人才批得动；
///     名单为空 = 谁都批不动（Fail-Closed）。普通群成员发“通过”不算审批。
///   · **会话绑定**：审批与消费都必须来自发起审批的那个会话，不能转给别的会话。
///   · **有效期**：过期即 <see cref="ApprovalStatus.Expired" />，过期后消费必定失败。
///   · **一次性**：<see cref="Consume" /> 成功一次就作废，重放/并发竞争都只会有一个人成功。
///   · **失败即不执行**：拒绝、过期、取消、跨会话，全部返回 Ok=false。
/// </summary>
public sealed class ApprovalStore
{
    /// <summary>审批单的默认有效期（秒）。短有效期是刻意的：审批不该长期挂着。</summary>
    public const int DefaultTtlSeconds = 120;

    /// <summary>有效期上限（秒）：配得再长也不会无限期挂着。</summary>
    public const int MaxTtlSeconds = 900;

    private readonly Dictionary<string, ApprovalRequest> _byId = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _byId.Count;
            }
        }
    }

    /// <summary>
    /// 建一张待批的审批单。摘要只接受“短的、脱敏过的东西” ——
    /// 超长或像凭据的一律换成占位，避免审批界面把不该露的露出去。
    /// </summary>
    public ApprovalRequest Create(
        string requestId,
        string toolId,
        string? summaryHint,
        string conversationKey,
        string requesterFingerprint,
        IEnumerable<string>? approvers,
        DateTimeOffset now,
        int ttlSeconds = DefaultTtlSeconds,
        IEnumerable<string>? approverRoles = null,
        int policyVersion = 0)
    {
        var ttl = Math.Clamp(ttlSeconds, 1, MaxTtlSeconds);
        var request = new ApprovalRequest(
            RequestId: string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId.Trim(),
            ToolId: (toolId ?? string.Empty).Trim(),
            Summary: RedactSummary(summaryHint),
            ConversationKey: conversationKey ?? string.Empty,
            RequesterFingerprint: (requesterFingerprint ?? string.Empty).Trim(),
            Approvers: new HashSet<string>(approvers ?? Array.Empty<string>(), StringComparer.Ordinal),
            CreatedAt: now,
            ExpiresAt: now.AddSeconds(ttl),
            ApproverRoles: new HashSet<string>(
                (approverRoles ?? Array.Empty<string>())
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Select(r => r.Trim().ToLowerInvariant()),
                StringComparer.Ordinal),
            PolicyVersion: policyVersion);

        lock (_sync)
        {
            _byId[request.RequestId] = request;
        }

        return request;
    }

    /// <summary>取一张审批单（顺带把到期状态推进为 Expired）。</summary>
    public ApprovalRequest? Get(string requestId, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_byId.TryGetValue(requestId ?? string.Empty, out var request))
            {
                return null;
            }

            return Expire(request, now);
        }
    }

    /// <summary>
    /// 找这个会话里**还没结束**的同工具审批单（用于“同一件事别开两张单”）。
    /// 过期但还没被推进的会在这里被推进成 Expired，于是不会挡住新单。
    /// </summary>
    public ApprovalRequest? FindPending(string conversationKey, string toolId, DateTimeOffset now)
    {
        lock (_sync)
        {
            foreach (var key in _byId.Keys.ToList())
            {
                var request = Expire(_byId[key], now);
                if (request.Status == ApprovalStatus.Pending &&
                    string.Equals(request.ConversationKey, conversationKey ?? string.Empty, StringComparison.Ordinal) &&
                    string.Equals(request.ToolId, (toolId ?? string.Empty).Trim(), StringComparison.Ordinal))
                {
                    return request;
                }
            }

            return null;
        }
    }

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

    /// <summary>
    /// 把一条「待办提问」标记为**已被回答**（一次性）。
    ///
    /// 与审批的区别：提问不需要“谁批准”，只要**原会话**里有人应了一声就算答完。
    /// 语义上归到 <see cref="ApprovalStatus.Cancelled" />（不再可被追踪/消费），
    /// 区别写在 <see cref="ApprovalRequest.ReasonCode" /> —— 运行记录里能看出是「有人答了」还是「自己撤了」。
    /// 过期、跨会话、已经答过、编号不存在，一律 Ok=false（失败关闭，但**不做任何动作**）。
    /// </summary>
    public ApprovalOutcome MarkAnswered(
        string requestId, string conversationKey, DateTimeOffset now, string? answeredBy = null)
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
                return new ApprovalOutcome(false, ApprovalStatus.Pending, "cross_conversation", request);
            }

            var answered = request with
            {
                Status = ApprovalStatus.Cancelled,
                ReasonCode = "answered",
                Consumed = true,
                DecidedAt = now,
                // 谁答的也记下来（身份摘要，只进内存台账 + 结构化日志；V3 §8.1「有身份」）
                DecidedBy = (answeredBy ?? string.Empty).Trim(),
            };

            _byId[answered.RequestId] = answered;
            return new ApprovalOutcome(true, answered.Status, answered.ReasonCode, answered);
        }
    }
    /// <summary>清掉已结束（非 Pending）的审批单，避免台账无限增长。</summary>
    public int Prune(DateTimeOffset now)
    {
        lock (_sync)
        {
            var dead = _byId.Values
                .Where(r => Expire(r, now).Status != ApprovalStatus.Pending)
                .Select(r => r.RequestId)
                .ToList();

            foreach (var id in dead)
            {
                _byId.Remove(id);
            }

            return dead.Count;
        }
    }

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
    /// 把**过了有效期**的单子推进为 Expired（惰性推进；不改库、不改外部状态）。
    /// 不只是 Pending：**已批准的单也会过期** —— 批准不等于永久有效，票过期之后就不能再消费
    /// （V3 §9.4「有短有效期、不可重复消费」）。只有已经终结的状态（拒绝/取消/过期）不再改动。
    /// </summary>
    private ApprovalRequest Expire(ApprovalRequest request, DateTimeOffset now)
    {
        if (request.Status is ApprovalStatus.Expired or ApprovalStatus.Rejected or ApprovalStatus.Cancelled
            || now < request.ExpiresAt)
        {
            return request;
        }

        var expired = request with { Status = ApprovalStatus.Expired, ReasonCode = "expired" };
        _byId[expired.RequestId] = expired;
        return expired;
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

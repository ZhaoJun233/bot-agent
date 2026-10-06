using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

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
public sealed partial class ApprovalStore
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

    /// <summary>
    /// 列出**所有还挂着**的待批单（面板的审批卡靠它；顺带把到期的推进成 Expired）。
    /// 只读语义：不改任何单子的状态（除了“时间到了”这一条由时间本身决定的事实）。
    /// 顺序按建立时间升序 —— 面板上先来的先显示（也方便先处理快过期的）。
    /// </summary>
    public IReadOnlyList<ApprovalRequest> Pending(DateTimeOffset now)
    {
        lock (_sync)
        {
            var list = new List<ApprovalRequest>();
            foreach (var key in _byId.Keys.ToList())
            {
                var request = Expire(_byId[key], now);
                if (request.Status == ApprovalStatus.Pending)
                {
                    list.Add(request);
                }
            }

            return list.OrderBy(r => r.CreatedAt).ToArray();
        }
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
}

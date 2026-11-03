using System.Security.Cryptography;
using System.Text;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Runtime;
using BotAgent.Panel;
using BotAgent.Services.Agent;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>
/// ZBA 的任务台账 —— 现在只是**通道适配器**（ADR-0002：执行与状态在 <c>AgentTaskRuntime</c>）。
///
/// 它仍然负责、也只负责四件通道级的事：
///   • <b>幂等键</b>（请求哈希 + 回执，重启/淘汰后区分 <c>record_evicted</c> / <c>previous_instance</c>）；
///   • <b>回执</b>（<see cref="ZbaTaskReceiptStore" />：只存哈希、任务 id、实例与时间）；
///   • <b>事件</b>（把运行时的状态变化投影成 <c>task.updated</c> 与 <c>task</c> 错误桶）；
///   • <b>结果投影</b>（只有 <c>panel:</c> 源的成功任务才把有界正文交出去）。
///
/// 不再做：起 Task、持有 CancellationTokenSource、判终态、决定成功/失败/取消 ——
/// 那些都是 <see cref="AgentTaskRuntime" /> 的职责。台账保留上限（200 条）仍在这里，
/// 因为"保留多少条"是对外面向的容量语义；淘汰时调 <c>Forget</c> 释放运行时内存。
/// </summary>
internal sealed class ZbaTaskRegistry : IAsyncDisposable
{
    private const int MaxActive = 4;
    private const int MaxRecords = 200;

    private readonly IAgentSessionStore _sessions;
    private readonly AgentTaskRuntime _runtime;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _serverEnabled;
    private readonly ZbaEventBuffer _events;
    private readonly ZbaTaskReceiptStore _receipts;
    private readonly string _instanceId;
    private readonly Action<string>? _log;

    private readonly object _gate = new();

    /// <summary>幂等键 → 任务 id（与运行时台账同进退：淘汰/遗忘时一起删）。</summary>
    private readonly Dictionary<string, string> _idempotency = new(StringComparer.Ordinal);

    /// <summary>任务 id → 幂等键与请求哈希（重放时核对是否同一个请求）。</summary>
    private readonly Dictionary<string, TaskBinding> _bindings = new(StringComparer.Ordinal);

    private bool _disposed;

    internal ZbaTaskRegistry(
        IAgentSessionStore sessions,
        AgentTaskRuntime runtime,
        Func<DateTimeOffset> now,
        Func<bool> serverEnabled,
        ZbaEventBuffer events,
        ZbaTaskReceiptStore receipts,
        string instanceId,
        Action<string>? log = null)
    {
        _sessions = sessions;
        _runtime = runtime;
        _now = now;
        _serverEnabled = serverEnabled;
        _events = events;
        _receipts = receipts;
        _instanceId = instanceId;
        _log = log;
        _runtime.Changed += OnRuntimeChanged;
    }

    internal ZbaTaskAccepted Submit(ZbaTaskSubmission submission, string idempotencyKey, out bool replayed)
    {
        replayed = false;
        if (!submission.Target.Equals("server", StringComparison.Ordinal))
            throw new ZbaBackendException(400, "invalid_target", "只支持 server 目标。");
        var target = "server";
        var hash = RequestHash(submission, target);
        string taskId;
        DateTimeOffset acceptedAt;
        lock (_gate)
        {
            if (_disposed) throw new ZbaBackendException(503, "zba_stopping", "服务正在停止。");
            if (_idempotency.TryGetValue(idempotencyKey, out var existingId))
            {
                var existing = _runtime.Get(existingId);
                if (existing is null
                    || !_bindings.TryGetValue(existingId, out var binding)
                    || !binding.Hash.Equals(hash, StringComparison.Ordinal))
                {
                    throw new ZbaBackendException(409, "idempotency_conflict", "幂等键已用于另一个任务。");
                }

                replayed = true;
                return new ZbaTaskAccepted(existing.Id, ToZba(existing.State), existing.CreatedAt);
            }

            // 内存台账没有、回执有：说明是上一实例受理的，或本实例已把记录淘汰。
            // 两种情况都不能核对请求哈希，只能拒绝重放，绝不重新执行。
            var keyHash = ReceiptHash(idempotencyKey);
            var receipt = _receipts.Find(keyHash);
            if (receipt is not null)
            {
                var reason = receipt.InstanceId == _instanceId ? "record_evicted" : "previous_instance";
                throw new ZbaBackendException(409, "idempotency_unverifiable",
                    "该幂等键已受理但结果不可核对，请勿重放。", reason);
            }

            var session = _sessions.Find(submission.SourceKey, submission.SessionId);
            if (session is null)
                throw new ZbaBackendException(404, "session_not_found", "Agent 会话不存在或已被删除。");
            if (session.Backend != "server")
                throw new ZbaBackendException(400, "host_backend_removed", "旧 host 会话只读留存，不能提交服务器任务。");
            if (!_serverEnabled())
                throw new ZbaBackendException(409, "server_agent_disabled", "服务器内置 agent 未启用。");

            var (active, total) = _runtime.Counts();
            if (active >= MaxActive)
                throw new ZbaBackendException(429, "task_capacity_exceeded", "并发任务已达上限。");

            // 台账满 200 条时，只要还有终态记录可淘汰，就先受理本次提交；
            // 真正淘汰放在回执写入成功之后，避免回执写失败时白白丢掉旧记录。
            // 这也是 find 的 expired 与同键重放的 record_evicted 能由真实路径产生的条件。
            var existingItems = _runtime.List(0, total, out _);
            if (total >= MaxRecords && !existingItems.Any(item => AgentTaskTransitions.IsTerminal(item.State)))
                throw new ZbaBackendException(429, "task_capacity_exceeded", "任务台账已达上限。");

            var now = _now();
            taskId = "zt-" + now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + "-" + Guid.NewGuid().ToString("N")[..8];
            acceptedAt = now;

            // 先写回执再提交：回执写失败时任务还**没有开始**，可以如实回答"本次提交未受理"；
            // 反过来做（先提交再写回执）在磁盘故障时会出现"客户端以为失败、其实已经在跑"。
            // 代价：回执写过之后提交若被拒（此路径只在运行时停机等异常下发生），会留下一条回执，
            // 重试会得到 idempotency_unverifiable —— 保守拒绝重放，比重复执行安全。
            if (!_receipts.TryAdd(keyHash, taskId, _instanceId, now))
                throw new ZbaBackendException(503, "receipt_store_unavailable", "任务回执不可用，本次提交未受理。");

            var accepted = _runtime.Submit(new AgentTaskRequest
            {
                Id = taskId,
                SourceKey = submission.SourceKey,
                SessionId = submission.SessionId,
                Prompt = submission.Prompt,
                Target = target,
                Timeout = TimeSpan.FromSeconds(submission.TimeoutSeconds),
                CaptureResult = submission.SourceKey.StartsWith("panel:", StringComparison.Ordinal)
            });
            if (!accepted.Accepted || accepted.Task is null)
            {
                throw new InvalidOperationException(
                    "任务运行时拒绝了已通过通道校验的提交：" + accepted.Rejection);
            }

            _idempotency[idempotencyKey] = taskId;
            _bindings[taskId] = new TaskBinding(idempotencyKey, hash);

            // 回执已落盘后再淘汰最旧终态记录：上限仍为 200 条，回执保留用于 expired/record_evicted。
            PruneLocked();

            _log?.Invoke($"[Zba] task={taskId} state=queued target={target}");
            _events.Append("task.updated", taskId, "queued", null, null, now);
        }

        return new ZbaTaskAccepted(taskId, ZbaTaskState.Queued, acceptedAt);
    }

    internal ZbaTaskPage List(int limit, string? cursor)
    {
        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, out offset) || offset < 0))
            throw new ZbaBackendException(400, "invalid_cursor", "游标无效。");
        var snapshots = _runtime.List(offset, limit, out var total);
        var items = snapshots.Select(Summary).ToArray();
        var next = offset + items.Length < total
            ? (offset + items.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return new ZbaTaskPage(items, next);
    }

    internal ZbaTaskSummary? Get(string id)
    {
        var task = _runtime.Get(id);
        return task is null ? null : Summary(task);
    }

    internal ZbaTaskResult? GetResult(string id)
    {
        var task = _runtime.Get(id);
        if (task is null) return null;
        var state = ToZba(task.State);
        var available = state == ZbaTaskState.Succeeded && task.ResultSummary is not null;
        var reason = available ? null : state switch
        {
            ZbaTaskState.Succeeded => task.SourceKey.StartsWith("panel:", StringComparison.Ordinal)
                ? "no_summary"
                : "scope_not_allowed",
            ZbaTaskState.Failed or ZbaTaskState.Cancelled or ZbaTaskState.Unknown => "not_succeeded",
            _ => "not_terminal"
        };
        return new ZbaTaskResult(
            task.Id,
            state,
            task.UpdatedAt,
            available,
            reason,
            available ? task.ResultSummary : null,
            task.ResultTruncated,
            task.ResultChars,
            task.ToolCalls,
            task.DurationMs,
            task.ErrorCode);
    }

    internal ZbaTaskFindResult Find(string idempotencyKey)
    {
        lock (_gate)
        {
            if (_idempotency.TryGetValue(idempotencyKey, out var taskId))
            {
                var task = _runtime.Get(taskId);
                if (task is not null) return new ZbaTaskFindResult("found", _instanceId, Summary(task), null, null);
            }

            var receipt = _receipts.Find(ReceiptHash(idempotencyKey));
            if (receipt is null) return new ZbaTaskFindResult("unknown", _instanceId, null, null, null);
            var code = receipt.InstanceId == _instanceId ? "expired" : "accepted_unknown";
            return new ZbaTaskFindResult(code, _instanceId, null, receipt.TaskId, receipt.AcceptedAt);
        }
    }

    internal ZbaTaskCancelResult Cancel(string id)
    {
        var outcome = _runtime.Cancel(id);
        if (outcome == AgentTaskCancelOutcome.NotFound)
            throw new ZbaBackendException(404, "task_not_found", "任务不存在。");

        var state = ToZba(_runtime.Get(id)?.State ?? AgentTaskState.Unknown);
        if (outcome == AgentTaskCancelOutcome.TooLate)
            return new ZbaTaskCancelResult(state, false, "too_late");
        return new ZbaTaskCancelResult(state, true, "cancel_requested");
    }

    internal (int Total, int Active) Counts()
    {
        var (active, total) = _runtime.Counts();
        return (total, active);
    }

    /// <summary>
    /// 运行时状态变化 → ZBA 事件。只做投影，不再判状态（终态、取消、超时全由运行时定）。
    /// </summary>
    private void OnRuntimeChanged(AgentTaskSnapshot task)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _events.Append("task.updated", task.Id, WireState(task.State), null, task.ErrorCode, task.UpdatedAt);
            if (task.State is AgentTaskState.Failed or AgentTaskState.Unknown or AgentTaskState.TimedOut)
                _events.AppendError("task", task.ErrorCode ?? "agent_failed", task.UpdatedAt);
        }
    }

    /// <summary>台账淘汰：只淘汰最旧的终态记录，回执不删（find 仍能识别 expired / record_evicted）。</summary>
    private void PruneLocked()
    {
        var (_, total) = _runtime.Counts();
        while (total > MaxRecords)
        {
            var items = _runtime.List(0, total, out var remaining);
            total = remaining;
            var victim = items.LastOrDefault(item => AgentTaskTransitions.IsTerminal(item.State));
            if (victim is null) return;
            if (!_runtime.Forget(victim.Id)) return;
            if (_bindings.Remove(victim.Id, out var binding)) _idempotency.Remove(binding.IdempotencyKey);
            (_, total) = _runtime.Counts();
        }
    }

    private static ZbaTaskSummary Summary(AgentTaskSnapshot task) => new(
        task.Id, ToZba(task.State), task.Target, task.SessionId,
        task.CreatedAt, task.UpdatedAt, task.DurationMs, task.ToolCalls,
        task.ErrorCode, task.CancellationRequested);

    /// <summary>
    /// 运行时状态 → ZBA 契约状态。契约<b>没有</b>超时态：<c>TimedOut</c> 投影为 <c>Failed</c>，
    /// 原因在 <c>errorCode=timeout</c>（ADR-0002 §2.2：不改对外枚举，避免客户端与面板状态标签连锁改动）。
    /// </summary>
    private static ZbaTaskState ToZba(AgentTaskState state) => state switch
    {
        AgentTaskState.Queued => ZbaTaskState.Queued,
        AgentTaskState.Running => ZbaTaskState.Running,
        AgentTaskState.WaitingApproval => ZbaTaskState.WaitingApproval,
        AgentTaskState.Succeeded => ZbaTaskState.Succeeded,
        AgentTaskState.Failed => ZbaTaskState.Failed,
        AgentTaskState.CancelRequested => ZbaTaskState.CancelRequested,
        AgentTaskState.Cancelled => ZbaTaskState.Cancelled,
        AgentTaskState.Unknown => ZbaTaskState.Unknown,
        _ => ZbaTaskState.Failed
    };

    /// <summary>事件里的状态串（对面板是既有约定：<c>cancel_requested</c> 带下划线）。</summary>
    private static string WireState(AgentTaskState state) => state switch
    {
        AgentTaskState.Queued => "queued",
        AgentTaskState.Running => "running",
        AgentTaskState.WaitingApproval => "waiting_approval",
        AgentTaskState.CancelRequested => "cancel_requested",
        AgentTaskState.Cancelled => "cancelled",
        AgentTaskState.Succeeded => "succeeded",
        AgentTaskState.Failed => "failed",
        AgentTaskState.Unknown => "unknown",
        _ => "failed"
    };

    private static string ReceiptHash(string idempotencyKey)
    {
        var bytes = Encoding.UTF8.GetBytes(idempotencyKey);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string RequestHash(ZbaTaskSubmission submission, string target)
    {
        var bytes = Encoding.UTF8.GetBytes(
            submission.SourceKey + "\n" + submission.SessionId + "\n" + target + "\n"
            + submission.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"
            + submission.Prompt);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _runtime.Changed -= OnRuntimeChanged;
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record TaskBinding(string IdempotencyKey, string Hash);
}

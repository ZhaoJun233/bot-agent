using System.Security.Cryptography;
using System.Text;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Panel;
using BotAgent.Services.Agent;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>
/// ZBA 的任务台账：稳定的 id、有界队列、幂等键、取消请求与“远端结果未知”的区分。
/// 只持久化在内存；进程重启后旧任务不可查询，客户端应把它视为 unknown 而不是重做。
/// 台账不保存提示词或工具载荷；只对 <c>panel:</c> 源的成功任务暂存有界结果摘要（≤4000 字符），
/// 用于 ZBA 结果端点，重启/淘汰即丢失。
/// </summary>
internal sealed class ZbaTaskRegistry : IAsyncDisposable
{
    private const int MaxActive = 4;
    private const int MaxRecords = 200;

    private readonly IAgentSessionStore _sessions;
    private readonly AgentCommandService _agent;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _serverEnabled;
    private readonly ZbaEventBuffer _events;
    private readonly ZbaTaskReceiptStore _receipts;
    private readonly string _instanceId;
    private readonly Action<string>? _log;

    private readonly object _gate = new();
    private readonly Dictionary<string, ZbaTaskRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idempotency = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private bool _disposed;

    internal ZbaTaskRegistry(
        IAgentSessionStore sessions,
        AgentCommandService agent,
        Func<DateTimeOffset> now,
        Func<bool> serverEnabled,
        ZbaEventBuffer events,
        ZbaTaskReceiptStore receipts,
        string instanceId,
        Action<string>? log = null)
    {
        _sessions = sessions;
        _agent = agent;
        _now = now;
        _serverEnabled = serverEnabled;
        _events = events;
        _receipts = receipts;
        _instanceId = instanceId;
        _log = log;
    }

    internal ZbaTaskAccepted Submit(ZbaTaskSubmission submission, string idempotencyKey, out bool replayed)
    {
        replayed = false;
        if (!submission.Target.Equals("server", StringComparison.Ordinal))
            throw new ZbaBackendException(400, "invalid_target", "只支持 server 目标。");
        var target = "server";
        var hash = RequestHash(submission, target);
        ZbaTaskRecord record;
        lock (_gate)
        {
            if (_disposed) throw new ZbaBackendException(503, "zba_stopping", "服务正在停止。");
            if (_idempotency.TryGetValue(idempotencyKey, out var existingId))
            {
                if (!_records.TryGetValue(existingId, out var existing))
                    throw new ZbaBackendException(409, "idempotency_conflict", "幂等键已用于另一个任务。");
                if (!existing.RequestHash.Equals(hash, StringComparison.Ordinal))
                    throw new ZbaBackendException(409, "idempotency_conflict", "幂等键已用于另一个任务。");
                replayed = true;
                return new ZbaTaskAccepted(existing.Id, existing.State, existing.CreatedAt);
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
            if (_order.Count(recordId => !Terminal(_records[recordId].State)) >= MaxActive)
                throw new ZbaBackendException(429, "task_capacity_exceeded", "并发任务已达上限。");

            // 台账满 200 条时，只要还有终态记录可淘汰，就先受理本次提交；
            // 真正淘汰放在回执写入成功之后，避免回执写失败时白白丢掉旧记录。
            // 这也是 find 的 expired 与同键重放的 record_evicted 能由真实路径产生的条件。
            if (_order.Count >= MaxRecords && !_order.Any(recordId => Terminal(_records[recordId].State)))
                throw new ZbaBackendException(429, "task_capacity_exceeded", "任务台账已达上限。");

            var now = _now();
            record = new ZbaTaskRecord
            {
                Id = "zt-" + now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + "-" + Guid.NewGuid().ToString("N")[..8],
                Target = target,
                SessionId = submission.SessionId,
                SourceKey = submission.SourceKey,
                CreatedAt = now,
                UpdatedAt = now,
                RequestHash = hash,
                IdempotencyKey = idempotencyKey
            };
            _records[record.Id] = record;
            _idempotency[idempotencyKey] = record.Id;
            _order.Insert(0, record.Id);
            if (!_receipts.TryAdd(keyHash, record.Id, _instanceId, now))
            {
                _order.Remove(record.Id);
                _records.Remove(record.Id);
                _idempotency.Remove(idempotencyKey);
                throw new ZbaBackendException(503, "receipt_store_unavailable", "任务回执不可用，本次提交未受理。");
            }

            // 回执已落盘后才淘汰最旧终态记录：上限仍为 200 条，回执保留用于 expired/record_evicted。
            PruneLocked();

            _log?.Invoke($"[Zba] task={record.Id} state=queued target={target}");
            _events.Append("task.updated", record.Id, "queued", null, null, now);
        }

        record.Work = Task.Run(() => RunAsync(record, submission));
        return new ZbaTaskAccepted(record.Id, ZbaTaskState.Queued, record.CreatedAt);
    }

    internal ZbaTaskPage List(int limit, string? cursor)
    {
        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, out offset) || offset < 0))
            throw new ZbaBackendException(400, "invalid_cursor", "游标无效。");
        lock (_gate)
        {
            var items = _order.Skip(offset).Take(limit).Select(id => Summary(_records[id])).ToArray();
            var next = offset + items.Length < _order.Count ? (offset + items.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return new ZbaTaskPage(items, next);
        }
    }

    internal ZbaTaskSummary? Get(string id)
    {
        lock (_gate) return _records.TryGetValue(id, out var record) ? Summary(record) : null;
    }

    internal ZbaTaskResult? GetResult(string id)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record)) return null;
            var available = record.State == ZbaTaskState.Succeeded && record.ResultSummary is not null;
            var reason = available ? null : record.State switch
            {
                ZbaTaskState.Succeeded => record.SourceKey.StartsWith("panel:", StringComparison.Ordinal)
                    ? "no_summary"
                    : "scope_not_allowed",
                ZbaTaskState.Failed or ZbaTaskState.Cancelled or ZbaTaskState.Unknown => "not_succeeded",
                _ => "not_terminal"
            };
            return new ZbaTaskResult(
                record.Id,
                record.State,
                record.UpdatedAt,
                available,
                reason,
                available ? record.ResultSummary : null,
                record.ResultTruncated,
                record.ResultChars,
                record.ToolCalls,
                record.DurationMs,
                record.ErrorCode);
        }
    }

    internal ZbaTaskCancelResult Cancel(string id)
    {
        ZbaTaskRecord record;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out record!))
                throw new ZbaBackendException(404, "task_not_found", "任务不存在。");
            if (Terminal(record.State)) return new ZbaTaskCancelResult(record.State, false, "too_late");
            if (record.CancellationRequested) return new ZbaTaskCancelResult(record.State, true, "cancel_requested");
            record.CancellationRequested = true;
            record.State = ZbaTaskState.CancelRequested;
            record.UpdatedAt = _now();
            _events.Append("task.updated", record.Id, "cancel_requested", null, null, record.UpdatedAt);
            _log?.Invoke($"[Zba] task={record.Id} state=cancel_requested target={record.Target}");
        }

        try
        {
            record.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务恰好在收尾：状态以最终写入为准。
        }

        return new ZbaTaskCancelResult(ZbaTaskState.CancelRequested, true, "cancel_requested");
    }

    internal ZbaTaskFindResult Find(string idempotencyKey)
    {
        lock (_gate)
        {
            if (_idempotency.TryGetValue(idempotencyKey, out var taskId)
                && _records.TryGetValue(taskId, out var record))
            {
                return new ZbaTaskFindResult("found", _instanceId, Summary(record), null, null);
            }

            var receipt = _receipts.Find(ReceiptHash(idempotencyKey));
            if (receipt is null) return new ZbaTaskFindResult("unknown", _instanceId, null, null, null);
            var code = receipt.InstanceId == _instanceId ? "expired" : "accepted_unknown";
            return new ZbaTaskFindResult(code, _instanceId, null, receipt.TaskId, receipt.AcceptedAt);
        }
    }

    internal (int Total, int Active) Counts()
    {
        lock (_gate) return (_order.Count, _order.Count(id => !Terminal(_records[id].State)));
    }

    private async Task RunAsync(ZbaTaskRecord record, ZbaTaskSubmission submission)
    {
        lock (_gate)
        {
            if (record.State is ZbaTaskState.Queued)
            {
                record.State = ZbaTaskState.Running;
                record.UpdatedAt = _now();
                _events.Append("task.updated", record.Id, "running", null, null, record.UpdatedAt);
            }
        }

        string? errorCode = null;
        ZbaTaskState state;
        try
        {
            var task = await _agent.RunPanelAgentDirectAsync(
                submission.SourceKey, submission.SessionId, submission.Prompt,
                submission.TimeoutSeconds, record.Target, record.Cts.Token).ConfigureAwait(false);
            var summary = Summarize(record.SourceKey, task);
            lock (_gate)
            {
                record.DurationMs = task.DurationMs;
                record.ToolCalls = task.ToolCalls;
                record.ResultSummary = summary.Summary;
                record.ResultTruncated = summary.Truncated;
                record.ResultChars = summary.Chars;
                if (task.Ok) state = ZbaTaskState.Succeeded;
                else if (record.Cts.IsCancellationRequested || task.CancelRequested)
                {
                    // server：本地取消可确认。
                    state = ZbaTaskState.Cancelled;
                    errorCode = "cancelled";
                }
                else
                {
                    state = ZbaTaskState.Failed;
                    errorCode = "agent_failed";
                }
            }
        }
        catch (OperationCanceledException)
        {
            state = ZbaTaskState.Cancelled;
            errorCode = "cancelled";
        }
        catch (Exception exception)
        {
            state = ZbaTaskState.Failed;
            errorCode = exception is InvalidOperationException ? "agent_rejected" : "agent_failed";
        }

        Finish(record, state, errorCode);
    }

    private void Finish(ZbaTaskRecord record, ZbaTaskState state, string? errorCode)
    {
        lock (_gate)
        {
            // 已经先写入终态时，迟到的完成结果不覆盖（尤其是“取消成功”的冒报）。
            if (Terminal(record.State) && record.State != ZbaTaskState.CancelRequested) return;
            record.State = state;
            record.ErrorCode = errorCode;
            record.UpdatedAt = _now();
            _log?.Invoke($"[Zba] task={record.Id} state={state.ToString().ToLowerInvariant()} target={record.Target}");
            _events.Append("task.updated", record.Id, state.ToString().ToLowerInvariant(), null, errorCode, record.UpdatedAt);
            if (state is ZbaTaskState.Failed or ZbaTaskState.Unknown)
                _events.AppendError("task", errorCode ?? "agent_failed", record.UpdatedAt);
            record.Cts.Dispose();
        }
    }

    /// <summary>
    /// 提交成功入账后调用：把台账压回上限，只淘汰最旧的终态记录（<c>_order</c> 新在前）。
    /// 回执不删除，因此被淘汰键仍可被 find 识别为 <c>expired</c>、重放识别为 <c>record_evicted</c>。
    /// </summary>
    private void PruneLocked()
    {
        while (_order.Count > MaxRecords)
        {
            var index = _order.FindLastIndex(id => Terminal(_records[id].State));
            if (index < 0) return;
            var id = _order[index];
            _order.RemoveAt(index);
            if (_records.Remove(id, out var record)) _idempotency.Remove(record.IdempotencyKey);
        }
    }

    private ZbaTaskSummary Summary(ZbaTaskRecord record) => new(
        record.Id, record.State, record.Target, record.SessionId,
        record.CreatedAt, record.UpdatedAt, record.DurationMs, record.ToolCalls,
        record.ErrorCode, record.CancellationRequested);

    private static bool Terminal(ZbaTaskState state)
        => state is ZbaTaskState.Succeeded or ZbaTaskState.Failed or ZbaTaskState.Cancelled or ZbaTaskState.Unknown;

    private const int MaxResultChars = 4000;

    /// <summary>
    /// 只对 <c>panel:</c> 源的成功任务采集摘要：清洗控制字符、截断到上限，
    /// 截断点不落在代理对中间。失败或非工作区来源一律不保存正文。
    /// </summary>
    private static (string? Summary, bool Truncated, int Chars) Summarize(string sourceKey, AgentTask task)
    {
        if (!sourceKey.StartsWith("panel:", StringComparison.Ordinal) || !task.Ok
            || string.IsNullOrWhiteSpace(task.Text))
        {
            return (null, false, 0);
        }

        var cleaned = Clean(task.Text);
        if (cleaned.Length == 0) return (null, false, 0);
        if (cleaned.Length <= MaxResultChars) return (cleaned, false, cleaned.Length);

        var cut = MaxResultChars;
        if (char.IsHighSurrogate(cleaned[cut - 1])) cut--;
        return (cleaned[..cut], true, cleaned.Length);
    }

    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\r')
            {
                // CRLF 归一为 LF；孤立 CR 也归一为换行。
                if (index + 1 < text.Length && text[index + 1] == '\n') continue;
                builder.Append('\n');
                continue;
            }

            if (current == '\n' || current == '\t') builder.Append(current);
            else if (!char.IsControl(current)) builder.Append(current);
        }

        return builder.ToString().Trim();
    }

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

    private static string RequestHash(ZbaTaskSubmission submission, string target)    {
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
        ZbaTaskRecord[] records;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            records = _records.Values.ToArray();
            foreach (var record in records)
            {
                try { record.Cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        var work = records.Where(record => record.Work is not null).Select(record => record.Work!).ToArray();
        if (work.Length > 0)
        {
            try { await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* 停机收口有界：未完成的任务由进程退出兜底 */ }
        }
    }

    private sealed class ZbaTaskRecord
    {
        internal required string Id { get; init; }
        internal required string Target { get; init; }
        internal required string SessionId { get; init; }
        internal required string SourceKey { get; init; }
        internal required DateTimeOffset CreatedAt { get; init; }
        internal required string RequestHash { get; init; }
        internal required string IdempotencyKey { get; init; }
        internal DateTimeOffset UpdatedAt { get; set; }
        internal ZbaTaskState State { get; set; } = ZbaTaskState.Queued;
        internal bool CancellationRequested { get; set; }
        internal long? DurationMs { get; set; }
        internal int? ToolCalls { get; set; }
        internal string? ErrorCode { get; set; }
        internal string? ResultSummary { get; set; }
        internal bool ResultTruncated { get; set; }
        internal int ResultChars { get; set; }
        internal CancellationTokenSource Cts { get; } = new();
        internal Task? Work { get; set; }
    }
}

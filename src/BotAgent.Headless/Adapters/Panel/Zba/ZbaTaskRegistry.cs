using System.Security.Cryptography;
using System.Text;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Panel;
using BotAgent.Services.Agent;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>
/// ZBA 的任务台账：稳定的 id、有界队列、幂等键、取消请求与“远端结果未知”的区分。
/// 只持久化在内存；进程重启后旧任务不可查询，客户端应把它视为 unknown 而不是重做。
/// 台账不保存提示词或结果正文，只保存长度无关的请求哈希与安全错误码。
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
        Action<string>? log = null)
    {
        _sessions = sessions;
        _agent = agent;
        _now = now;
        _serverEnabled = serverEnabled;
        _events = events;
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

            var session = _sessions.Find(submission.SourceKey, submission.SessionId);
            if (session is null)
                throw new ZbaBackendException(404, "session_not_found", "Agent 会话不存在或已被删除。");
            if (session.Backend != "server")
                throw new ZbaBackendException(400, "host_backend_removed", "旧 host 会话只读留存，不能提交服务器任务。");
            if (!_serverEnabled())
                throw new ZbaBackendException(409, "server_agent_disabled", "服务器内置 agent 未启用。");
            if (_order.Count(recordId => !Terminal(_records[recordId].State)) >= MaxActive)
                throw new ZbaBackendException(429, "task_capacity_exceeded", "并发任务已达上限。");

            PruneLocked();
            if (_order.Count >= MaxRecords)
                throw new ZbaBackendException(429, "task_capacity_exceeded", "任务台账已达上限。");

            var now = _now();
            record = new ZbaTaskRecord
            {
                Id = "zt-" + now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + "-" + Guid.NewGuid().ToString("N")[..8],
                Target = target,
                SessionId = submission.SessionId,
                CreatedAt = now,
                UpdatedAt = now,
                RequestHash = hash,
                IdempotencyKey = idempotencyKey
            };
            _records[record.Id] = record;
            _idempotency[idempotencyKey] = record.Id;
            _order.Insert(0, record.Id);
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
            lock (_gate)
            {
                record.DurationMs = task.DurationMs;
                record.ToolCalls = task.ToolCalls;
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
        internal required DateTimeOffset CreatedAt { get; init; }
        internal required string RequestHash { get; init; }
        internal required string IdempotencyKey { get; init; }
        internal DateTimeOffset UpdatedAt { get; set; }
        internal ZbaTaskState State { get; set; } = ZbaTaskState.Queued;
        internal bool CancellationRequested { get; set; }
        internal long? DurationMs { get; set; }
        internal int? ToolCalls { get; set; }
        internal string? ErrorCode { get; set; }
        internal CancellationTokenSource Cts { get; } = new();
        internal Task? Work { get; set; }
    }
}

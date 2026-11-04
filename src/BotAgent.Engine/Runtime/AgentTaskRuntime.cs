using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;

namespace BotAgent.Engine.Runtime;

/// <summary>
/// 显式任务运行时（#114）：进程内的内存台账 + 状态机 + 取消/超时/失败三路收口。
///
/// 边界（ADR-0002）：
///   • 只依赖 <see cref="IAgentTaskExecutor"/> 与 Core 的状态机，不认识 HTTP、数据库、ZBA 事件；
///   • 可选台账端口负责持久化；状态提交顺序也决定持久化与通知顺序；
///   • 不保存提示词；结果正文只在通道声明 <c>CaptureResult</c> 时按有界口径保存；
///   • 时间：状态时间戳走注入的 <c>now</c>（不直接读系统时间，R1 钉着）；超时计时用 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>，
///     这是<b>真实计时器</b>而不是预算时钟 —— 预算时钟在测试里是"立刻返回"，会把超时测成 0 毫秒。
///
/// 状态推进只有一个入口（<c>MoveTo</c>/<c>Finish</c>），每次都要过
/// <see cref="AgentTaskTransitions"/> —— 终态拒绝任何后续写入，取消是请求不是事实。
/// </summary>
public sealed partial class AgentTaskRuntime : IAsyncDisposable
{
    /// <summary>默认并发上限（与改写前的 ZBA 台账一致）。</summary>
    public const int DefaultMaxActive = 4;

    /// <summary>结果正文默认上限（与改写前的 ZBA 台账一致）。</summary>
    public const int DefaultMaxResultChars = 4000;

    private readonly IAgentTaskExecutor _executor;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _maxActive;
    private readonly int _maxResultChars;
    private readonly Action<string>? _log;
    private readonly IAgentTaskLedger? _ledger;
    private readonly long _bootSeq;
    private readonly IAgentTaskApprovalGate? _approvalGate;

    /// <summary>启动时最多恢复多少条历史任务（与通道台账上限一致）。</summary>
    private const int MaxRestore = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, TaskRecord> _records = new(StringComparer.Ordinal);
    /// <summary>台账顺序：新的在前（与 ZBA 列表语义一致）。</summary>
    private readonly List<string> _order = new();
    private bool _disposed;

    /// <summary>状态变化通知：持久化后按提交顺序在锁外触发；重入产生的变化排在当前通知之后。</summary>
    public event Action<AgentTaskSnapshot>? Changed;

    public AgentTaskRuntime(
        IAgentTaskExecutor executor,
        Func<DateTimeOffset> now,
        int maxActive = DefaultMaxActive,
        int maxResultChars = DefaultMaxResultChars,
        Action<string>? log = null,
        IAgentTaskLedger? ledger = null,
        long bootSeq = 0,
        IAgentTaskApprovalGate? approvalGate = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _maxActive = maxActive > 0 ? maxActive : throw new ArgumentOutOfRangeException(nameof(maxActive));
        _maxResultChars = maxResultChars > 0 ? maxResultChars : throw new ArgumentOutOfRangeException(nameof(maxResultChars));
        _log = log;
        _ledger = ledger;
        _bootSeq = bootSeq;
        _approvalGate = approvalGate;
        if (_ledger is not null) Restore();
    }

    /// <summary>
    /// 受理一次任务：容量与 id 唯一性在同一临界区内判定，随后立刻在后台启动执行。
    /// 受理成功时返回 <see cref="AgentTaskState.Queued"/> 快照。
    /// </summary>
    public AgentTaskSubmitResult Submit(AgentTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // #137：派发前的审批闸门。返回 null = 与改造前完全一致（立刻排队执行）。
        var approval = _approvalGate?.Create(request);
        AgentTaskSnapshot accepted;
        try
        {
            lock (_gate)
            {
                if (_disposed) return AgentTaskSubmitResult.Rejected(AgentTaskRejection.Disposed);
                // Expiry and admission share a critical section: reads are not required
                // to reclaim slots, and callbacks cannot race admission between them.
                SweepApprovalsLocked(_now());
                if (_records.ContainsKey(request.Id)) return AgentTaskSubmitResult.Rejected(AgentTaskRejection.DuplicateId);
                if (_records.Values.Count(entry => !AgentTaskTransitions.IsTerminal(entry.State)) >= _maxActive)
                    return AgentTaskSubmitResult.Rejected(AgentTaskRejection.CapacityExceeded);

                var task = new TaskRecord(request, _now());
                _records[request.Id] = task;
                _order.Insert(0, request.Id);
                if (approval is not null)
                {
                    task.Approval = approval;
                    task.State = AgentTaskState.WaitingApproval;
                    task.UpdatedAt = _now();
                }
                accepted = Project(task);
                QueuePublication(accepted, notify: false);
                if (approval is null)
                {
                    // Dispose must see Work. The worker first acquires _gate, so its
                    // first transition cannot overtake the acceptance publication.
                    task.Work = Task.Run(() => RunAsync(task));
                }
            }
        }
        finally { DrainPublications(); } // Also publish expiry on rejected admission.

        _log?.Invoke(approval is null
            ? $"[Task] id={request.Id} state=queued source={request.SourceKey}"
            : $"[Task] id={request.Id} state=waiting_approval approval={approval.RequestId} tool={approval.ToolId}");
        return AgentTaskSubmitResult.AcceptedTask(accepted);
    }

    /// <summary>取快照；没有就返回 null（通道据此回答 404 或 expired）。</summary>
    public AgentTaskSnapshot? Get(string id)
    {
        // 过期待批单不该一直显示成“等待中”：读的时候顺手收口一次。
        SweepApprovals(_now());
        lock (_gate) return _records.TryGetValue(id, out var task) ? Project(task) : null;
    }

    /// <summary>
    /// 按台账顺序分页（新的在前）。<paramref name="total"/> 是当前台账条数，
    /// 供通道判断"台账是否已满"（保留策略在通道，见 ADR §2.7）。
    /// </summary>
    public IReadOnlyList<AgentTaskSnapshot> List(int offset, int limit, out int total)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        SweepApprovals(_now());
        lock (_gate)
        {
            total = _order.Count;
            var count = Math.Min(limit, Math.Max(0, total - offset));
            var items = new AgentTaskSnapshot[count];
            for (var index = 0; index < count; index++) items[index] = Project(_records[_order[offset + index]]);
            return items;
        }
    }

    /// <summary>（活跃, 总数）：活跃 = 未到终态的条数。</summary>
    public (int Active, int Total) Counts()
    {
        (int Active, int Total) counts;
        lock (_gate)
        {
            if (!_disposed) SweepApprovalsLocked(_now());
            counts = (_records.Values.Count(entry => !AgentTaskTransitions.IsTerminal(entry.State)), _records.Count);
        }
        DrainPublications();
        return counts;
    }

    /// <summary>
    /// 请求取消：状态先推到 <see cref="AgentTaskState.CancelRequested"/> 再取消执行器令牌。
    /// 已经到终态返回 <see cref="AgentTaskCancelOutcome.TooLate"/>（不改变任何东西）。
    /// </summary>
    public AgentTaskCancelOutcome Cancel(string id)
    {
        TaskRecord task;
        AgentTaskSnapshot? snapshot = null;
        CancellationTokenSource? cancelSource;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out task!)) return AgentTaskCancelOutcome.NotFound;
            if (AgentTaskTransitions.IsTerminal(task.State)) return AgentTaskCancelOutcome.TooLate;
            if (task.CancellationRequested) return AgentTaskCancelOutcome.AlreadyRequested;

            task.CancellationRequested = true;
            if (AgentTaskTransitions.CanTransition(task.State, AgentTaskState.CancelRequested))
            {
                task.State = AgentTaskState.CancelRequested;
                task.UpdatedAt = _now();
                snapshot = Project(task);
            }
            else if (task.State == AgentTaskState.WaitingApproval
                     && AgentTaskTransitions.CanTransition(task.State, AgentTaskState.Cancelled))
            {
                // #137：待批任务没有执行器可取消，取消请求直接把它收口（不再等人批）。
                task.State = AgentTaskState.Cancelled;
                task.ErrorCode = "approval_cancelled";
                task.UpdatedAt = _now();
                snapshot = Project(task);
            }

            if (snapshot is not null) QueuePublication(snapshot);
            cancelSource = task.CancelSource;
        }

        try { cancelSource?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (snapshot is not null)
        {
            _log?.Invoke($"[Task] id={id} state={snapshot.State.ToString().ToLowerInvariant()}");
            DrainPublications();
        }

        return AgentTaskCancelOutcome.Requested;
    }

    /// <summary>
    /// 通道淘汰台账时调用：只允许忘掉<b>终态</b>记录（运行中的返回 false，绝不静默丢活任务）。
    /// </summary>
    public bool Forget(string id)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var task) || !AgentTaskTransitions.IsTerminal(task.State)) return false;
            _records.Remove(id);
            _order.Remove(id);
            return true;
        }
    }

    /// <summary>
    /// 待批任务获批（#137）：转 Running 并**此时才**开始执行。返回值给通道翻译成 409/404。
    /// </summary>
    public AgentTaskApprovalDecision Resume(string id)
    {
        AgentTaskSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed) return AgentTaskApprovalDecision.Disposed;
            if (!_records.TryGetValue(id, out var task)) return AgentTaskApprovalDecision.NotFound;
            if (task.State != AgentTaskState.WaitingApproval) return AgentTaskApprovalDecision.NotPending;
            if (!AgentTaskTransitions.CanTransition(task.State, AgentTaskState.Running))
                return AgentTaskApprovalDecision.NotPending;

            task.State = AgentTaskState.Running;
            task.UpdatedAt = _now();
            snapshot = Project(task);
            QueuePublication(snapshot);
            task.Work = Task.Run(() => RunAsync(task, alreadyRunning: true));
        }

        _log?.Invoke($"[Task] id={id} state=running（审批通过，开始执行）");
        DrainPublications();
        return AgentTaskApprovalDecision.Approved;
    }

    /// <summary>待批任务被拒（#137）：终态 <see cref="AgentTaskState.Cancelled" />，原因码原样带回执。</summary>
    public AgentTaskApprovalDecision Reject(string id, string reasonCode)
    {
        AgentTaskSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed) return AgentTaskApprovalDecision.Disposed;
            if (!_records.TryGetValue(id, out var task)) return AgentTaskApprovalDecision.NotFound;
            if (task.State != AgentTaskState.WaitingApproval) return AgentTaskApprovalDecision.NotPending;
            if (!AgentTaskTransitions.CanTransition(task.State, AgentTaskState.Cancelled))
                return AgentTaskApprovalDecision.NotPending;

            task.State = AgentTaskState.Cancelled;
            task.ErrorCode = reasonCode;
            task.UpdatedAt = _now();
            snapshot = Project(task);
            QueuePublication(snapshot);
        }

        _log?.Invoke($"[Task] id={id} state=cancelled reason={reasonCode}");
        DrainPublications();
        return AgentTaskApprovalDecision.Rejected;
    }

    /// <summary>
    /// 一次审批决策（#137）：按审批编号找待批任务，批准 → <see cref="Resume" />，拒绝 → <see cref="Reject" />。
    /// 先扫一遍过期，免得把“早就过期”的单子当成“正在等待”。
    /// </summary>
    public AgentTaskApprovalDecision DecideApproval(string requestId, bool approve, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        SweepApprovals(now);
        string? taskId = null;
        lock (_gate)
        {
            foreach (var entry in _records.Values)
            {
                if (entry.Approval is { } approval && string.Equals(approval.RequestId, requestId, StringComparison.Ordinal))
                {
                    taskId = entry.Request.Id;
                    break;
                }
            }
        }

        if (taskId is null) return AgentTaskApprovalDecision.NotFound;
        return approve ? Resume(taskId) : Reject(taskId, "approval_rejected");
    }

    /// <summary>把过了有效期的待批任务收口成 <c>Cancelled</c> + <c>approval_expired</c>（#137）。</summary>
    public int SweepApprovals(DateTimeOffset now)
    {
        int expired;
        lock (_gate)
        {
            if (_disposed) return 0;
            expired = SweepApprovalsLocked(now);
        }
        DrainPublications();
        return expired;
    }

    // Caller holds _gate; only state mutation and outbox enqueue happen here.
    private int SweepApprovalsLocked(DateTimeOffset now)
    {
        var expired = 0;
        foreach (var task in _records.Values)
        {
            if (task.State != AgentTaskState.WaitingApproval) continue;
            if (task.Approval is not { } approval || approval.ExpiresAt > now) continue;
            if (!AgentTaskTransitions.CanTransition(task.State, AgentTaskState.Cancelled)) continue;
            task.State = AgentTaskState.Cancelled;
            task.ErrorCode = "approval_expired";
            task.UpdatedAt = now;
            QueuePublication(Project(task));
            expired++;
        }
        return expired;
    }

    private async Task RunAsync(TaskRecord task, bool alreadyRunning = false)
    {
        using var cancelSource = new CancellationTokenSource();
        var timeoutSource = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelSource.Token, timeoutSource.Token);
        lock (_gate)
        {
            task.CancelSource = cancelSource;
            task.TimeoutSource = timeoutSource;
        }

        try
        {
            // #137：审批通过后恢复的任务已经是 Running，不再走一次 Queued → Running。
            if (!alreadyRunning) MoveTo(task, AgentTaskState.Running, null);
            // 停止过程中被排进来的任务：MoveTo 会静默丢弃，这里直接不执行（不把活儿带进停机）。
            bool stopping;
            lock (_gate) stopping = _disposed;
            if (stopping)
            {
                lock (_gate) task.CancellationRequested = true;
                Finish(task, new AgentTaskExecution { Ok = false });
                return;
            }
            if (task.CancellationRequested) cancelSource.Cancel();

            var executionTask = _executor.ExecuteAsync(task.Request, linked.Token);
            // 延迟只挂在"超时"令牌上，不挂 linked：linked 在用户取消时也会立刻取消，
            // 若挂上去，取消会让延迟任务先完成、被下面误判成超时（取消变 TimedOut）。
            var timeoutTask = Task.Delay(task.Request.Timeout, timeoutSource.Token);
            var first = await Task.WhenAny(executionTask, timeoutTask).ConfigureAwait(false);
            if (first == timeoutTask)
            {
                // 硬截止：状态先落终态，再取消令牌；执行器的迟到结果按"终态不可覆盖"丢弃。
                var timedOut = !timeoutTask.IsCanceled;
                lock (_gate) task.TimedOut = timedOut;
                MoveTo(task, timedOut ? AgentTaskState.TimedOut : AgentTaskState.Cancelled,
                    timedOut ? "timeout" : "cancelled");
                try { cancelSource.Cancel(); }
                catch (ObjectDisposedException) { }
                ObserveLateFault(executionTask);
                return;
            }

            var execution = await executionTask.ConfigureAwait(false);
            // 执行器先返回：停掉超时计时器，免得 finally 里还留着一个会烧掉的延迟任务。
            TryCancel(timeoutSource);
            Finish(task, execution);
        }
        catch (OperationCanceledException)
        {
            var timedOut = task.TimedOut;
            MoveTo(task, timedOut ? AgentTaskState.TimedOut : AgentTaskState.Cancelled,
                timedOut ? "timeout" : "cancelled");
        }
        catch (Exception exception)
        {
            MoveTo(task, AgentTaskState.Failed,
                exception is InvalidOperationException ? "agent_rejected" : "agent_failed");
        }
        finally
        {
            lock (_gate)
            {
                task.CancelSource = null;
                task.TimeoutSource = null;
            }

            try { linked.Cancel(); }
            catch (ObjectDisposedException) { }
            TryCancel(timeoutSource);
            timeoutSource.Dispose();
        }
    }

    /// <summary>
    /// 把一次状态变化写进台账。<b>写失败只记日志</b>：任务成败由执行结果决定，不能被存储问题改写。
    /// 放在锁外调用（SQLite 写入有 I/O 成本，不该占着状态机的锁）。
    /// </summary>
    private void Persist(AgentTaskSnapshot snapshot)
    {
        if (_ledger is null) return;
        try
        {
            _ledger.Save(AgentTaskLedgerEntry.From(snapshot, _bootSeq));
        }
        catch (Exception exception)
        {
            LogPublicationFailure("台账写入失败", exception);
        }
    }

    /// <summary>
    /// 启动对账（#116 / ADR-0003）：把上一次进程留下的行读回来，<b>非终态一律收口成终态，绝不重做</b>。
    ///
    ///   • Queued / WaitingApproval —— 还没交给执行器就被打断，本地能证明没有副作用 ⇒ <c>Cancelled</c>（cancelled_before_dispatch）；
    ///   • Running / CancelRequested —— 已经交出去了，本地说不清远端做到哪一步 ⇒ <c>Unknown</c>（restart_state_unknown）。
    ///
    /// 为什么不用 Failed：Failed 暗示"我们知道它失败了"，而这里只知道"不知道"。Unknown 是唯一不撒谎的结论。
    /// </summary>
    private void Restore()
    {
        IReadOnlyList<AgentTaskLedgerEntry> rows;
        try
        {
            rows = _ledger!.LoadRecent(MaxRestore);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"[Task] 台账读取失败，按空台账启动：{exception.Message}");
            return;
        }

        if (rows.Count == 0) return;
        var recovered = 0;
        var unknown = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id)) continue;
            if (_records.ContainsKey(row.Id)) continue;
            if (!_order.Contains(row.Id)) _order.Add(row.Id);

            var state = row.State;
            var errorCode = row.ErrorCode;
            var updatedAt = row.UpdatedAt;
            if (!AgentTaskTransitions.IsTerminal(state))
            {
                var dispatched = state is AgentTaskState.Running or AgentTaskState.CancelRequested;
                var waiting = state == AgentTaskState.WaitingApproval;
                state = dispatched ? AgentTaskState.Unknown : AgentTaskState.Cancelled;
                errorCode = dispatched
                    ? "restart_state_unknown"
                    : waiting
                        ? "approval_lost_on_restart"
                        : "cancelled_before_dispatch";
                updatedAt = _now();
                if (dispatched) unknown++;
            }

            var task = new TaskRecord(
                new AgentTaskRequest
                {
                    Id = row.Id,
                    // 正文不落库，也没有重做路径：恢复出来的记录只用于查询与追溯。
                    Prompt = string.Empty,
                    SourceKey = row.SourceKey,
                    SessionId = row.SessionId,
                    Target = row.Target,
                    CaptureResult = false
                },
                row.CreatedAt)
            {
                State = state,
                ErrorCode = errorCode,
                UpdatedAt = updatedAt,
                DurationMs = row.DurationMs,
                ToolCalls = row.ToolCalls,
                CancellationRequested = row.CancellationRequested || state is AgentTaskState.Cancelled or AgentTaskState.CancelRequested,
                ResultChars = row.ResultChars,
                ResultTruncated = row.ResultTruncated
            };
            _records[row.Id] = task;
            recovered++;

            // 收口结果立刻回写：否则下一次重启又会把同一行当成"非终态"重算一遍。
            if (state != row.State) Persist(Project(task));
        }

        _log?.Invoke($"[Task] 恢复 {recovered} 条历史任务（其中 {unknown} 条按 Unknown 收口，不重做）");
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void Finish(TaskRecord task, AgentTaskExecution execution)
    {
        AgentTaskSnapshot? snapshot = null;
        lock (_gate)
        {
            if (AgentTaskTransitions.IsTerminal(task.State)) return;
            task.DurationMs = execution.DurationMs;
            task.ToolCalls = execution.ToolCalls;

            if (execution.Ok && task.Request.CaptureResult && !string.IsNullOrWhiteSpace(execution.Text))
            {
                var cleaned = AgentTaskText.Clean(execution.Text);
                if (cleaned.Length > 0)
                {
                    var clamped = AgentTaskText.Clamp(cleaned, _maxResultChars);
                    task.ResultSummary = clamped.Text;
                    task.ResultTruncated = clamped.Truncated;
                    task.ResultChars = clamped.Chars;
                }
            }

            AgentTaskState state;
            string? errorCode;
            if (execution.Ok)
            {
                state = AgentTaskState.Succeeded;
                errorCode = null;
            }
            else if (task.CancellationRequested || execution.CancelRequested)
            {
                state = AgentTaskState.Cancelled;
                errorCode = "cancelled";
            }
            else
            {
                state = AgentTaskState.Failed;
                errorCode = execution.ErrorCode ?? "agent_failed";
            }

            if (!AgentTaskTransitions.CanTransition(task.State, state)) return;
            task.State = state;
            task.ErrorCode = errorCode;
            task.UpdatedAt = _now();
            snapshot = Project(task);
            QueuePublication(snapshot);
        }

        _log?.Invoke($"[Task] id={task.Request.Id} state={snapshot!.State.ToString().ToLowerInvariant()}");
        DrainPublications();
    }

    /// <summary>状态推进的唯一入口：非法转移静默丢弃（并发下别人可能先写了终态）。</summary>
    private void MoveTo(TaskRecord task, AgentTaskState state, string? errorCode)
    {
        AgentTaskSnapshot? snapshot = null;
        lock (_gate)
        {
            // Shutdown stops new dispatch, not terminal confirmation from in-flight work.
            if (_disposed && !AgentTaskTransitions.IsTerminal(state)) return;
            if (!AgentTaskTransitions.CanTransition(task.State, state)) return;
            task.State = state;
            task.ErrorCode = errorCode;
            task.UpdatedAt = _now();
            snapshot = Project(task);
            QueuePublication(snapshot);
        }

        _log?.Invoke($"[Task] id={task.Request.Id} state={state.ToString().ToLowerInvariant()}");
        DrainPublications();
    }

    private static void ObserveLateFault(Task task)
        => _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static AgentTaskSnapshot Project(TaskRecord task) => new()
    {
        Id = task.Request.Id,
        SourceKey = task.Request.SourceKey,
        SessionId = task.Request.SessionId,
        Target = task.Request.Target,
        State = task.State,
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt,
        DurationMs = task.DurationMs,
        ToolCalls = task.ToolCalls,
        ErrorCode = task.ErrorCode,
        CancellationRequested = task.CancellationRequested,
        ResultSummary = task.ResultSummary,
        ResultTruncated = task.ResultTruncated,
        ResultChars = task.ResultChars,
        ApprovalRequestId = task.Approval?.RequestId,
        ApprovalToolId = task.Approval?.ToolId,
        ApprovalSummary = task.Approval?.Summary,
        ApprovalExpiresAt = task.Approval?.ExpiresAt
    };


    private sealed class TaskRecord(AgentTaskRequest request, DateTimeOffset createdAt)
    {
        internal AgentTaskRequest Request { get; } = request;
        internal AgentTaskState State { get; set; } = AgentTaskState.Queued;

        /// <summary>待批信息（#137）：只有 WaitingApproval 期间有值。</summary>
        internal AgentTaskApprovalRequest? Approval { get; set; }
        internal DateTimeOffset CreatedAt { get; } = createdAt;
        internal DateTimeOffset UpdatedAt { get; set; } = createdAt;
        internal bool CancellationRequested { get; set; }
        internal bool TimedOut { get; set; }
        internal long? DurationMs { get; set; }
        internal int ToolCalls { get; set; }
        internal string? ErrorCode { get; set; }
        internal string? ResultSummary { get; set; }
        internal bool ResultTruncated { get; set; }
        internal int ResultChars { get; set; }
        internal CancellationTokenSource? CancelSource { get; set; }
        internal CancellationTokenSource? TimeoutSource { get; set; }
        internal Task? Work { get; set; }
    }
}

using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;

namespace BotAgent.Engine.Runtime;

/// <summary>
/// 显式任务运行时（#114）：进程内的内存台账 + 状态机 + 取消/超时/失败三路收口。
///
/// 边界（ADR-0002）：
///   • 只依赖 <see cref="IAgentTaskExecutor"/> 与 Core 的状态机，不认识 HTTP、数据库、ZBA 事件；
///   • <b>不落库</b>（#116 才做持久化与重启恢复）；
///   • 不保存提示词；结果正文只在通道声明 <c>CaptureResult</c> 时按有界口径保存；
///   • 时间：状态时间戳走注入的 <c>now</c>（不直接读系统时间，R1 钉着）；超时计时用 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>，
///     这是<b>真实计时器</b>而不是预算时钟 —— 预算时钟在测试里是"立刻返回"，会把超时测成 0 毫秒。
///
/// 状态推进只有一个入口（<c>MoveTo</c>/<c>Finish</c>），每次都要过
/// <see cref="AgentTaskTransitions"/> —— 终态拒绝任何后续写入，取消是请求不是事实。
/// </summary>
public sealed class AgentTaskRuntime : IAsyncDisposable
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

    private readonly object _gate = new();
    private readonly Dictionary<string, TaskRecord> _records = new(StringComparer.Ordinal);
    /// <summary>台账顺序：新的在前（与 ZBA 列表语义一致）。</summary>
    private readonly List<string> _order = new();
    private bool _disposed;

    /// <summary>状态变化通知（在锁外触发；订阅方自己保证线程安全）。</summary>
    public event Action<AgentTaskSnapshot>? Changed;

    public AgentTaskRuntime(
        IAgentTaskExecutor executor,
        Func<DateTimeOffset> now,
        int maxActive = DefaultMaxActive,
        int maxResultChars = DefaultMaxResultChars,
        Action<string>? log = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _maxActive = maxActive > 0 ? maxActive : throw new ArgumentOutOfRangeException(nameof(maxActive));
        _maxResultChars = maxResultChars > 0 ? maxResultChars : throw new ArgumentOutOfRangeException(nameof(maxResultChars));
        _log = log;
    }

    /// <summary>
    /// 受理一次任务：容量与 id 唯一性在同一临界区内判定，随后立刻在后台启动执行。
    /// 受理成功时返回 <see cref="AgentTaskState.Queued"/> 快照。
    /// </summary>
    public AgentTaskSubmitResult Submit(AgentTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        TaskRecord task;
        AgentTaskSnapshot accepted;
        lock (_gate)
        {
            if (_disposed) return AgentTaskSubmitResult.Rejected(AgentTaskRejection.Disposed);
            if (_records.ContainsKey(request.Id)) return AgentTaskSubmitResult.Rejected(AgentTaskRejection.DuplicateId);
            if (_records.Values.Count(entry => !AgentTaskTransitions.IsTerminal(entry.State)) >= _maxActive)
                return AgentTaskSubmitResult.Rejected(AgentTaskRejection.CapacityExceeded);

            task = new TaskRecord(request, _now());
            _records[request.Id] = task;
            _order.Insert(0, request.Id);
            accepted = Project(task);
        }

        task.Work = Task.Run(() => RunAsync(task));
        _log?.Invoke($"[Task] id={request.Id} state=queued source={request.SourceKey}");
        return AgentTaskSubmitResult.AcceptedTask(accepted);
    }

    /// <summary>取快照；没有就返回 null（通道据此回答 404 或 expired）。</summary>
    public AgentTaskSnapshot? Get(string id)
    {
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
        lock (_gate)
        {
            return (_records.Values.Count(entry => !AgentTaskTransitions.IsTerminal(entry.State)), _records.Count);
        }
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

            cancelSource = task.CancelSource;
        }

        try { cancelSource?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (snapshot is not null)
        {
            _log?.Invoke($"[Task] id={id} state=cancel_requested");
            Changed?.Invoke(snapshot);
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

    private async Task RunAsync(TaskRecord task)
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
            MoveTo(task, AgentTaskState.Running, null);
            if (task.CancellationRequested) cancelSource.Cancel();

            var executionTask = _executor.ExecuteAsync(task.Request, linked.Token);
            // 延迟只挂在"超时"令牌上，不挂 linked：linked 在用户取消时也会立刻取消，
            // 若挂上去，取消会让延迟任务先完成、被下面误判成超时（取消变 TimedOut）。
            var timeoutTask = Task.Delay(task.Request.Timeout, timeoutSource.Token);
            var first = await Task.WhenAny(executionTask, timeoutTask).ConfigureAwait(false);
            if (first == timeoutTask)
            {
                // 硬截止：状态先落终态，再取消令牌；执行器的迟到结果按"终态不可覆盖"丢弃。
                lock (_gate) task.TimedOut = true;
                MoveTo(task, AgentTaskState.TimedOut, "timeout");
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
        }

        _log?.Invoke($"[Task] id={task.Request.Id} state={snapshot!.State.ToString().ToLowerInvariant()}");
        Changed?.Invoke(snapshot);
    }

    /// <summary>状态推进的唯一入口：非法转移静默丢弃（并发下别人可能先写了终态）。</summary>
    private void MoveTo(TaskRecord task, AgentTaskState state, string? errorCode)
    {
        AgentTaskSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (!AgentTaskTransitions.CanTransition(task.State, state)) return;
            task.State = state;
            task.ErrorCode = errorCode;
            task.UpdatedAt = _now();
            snapshot = Project(task);
        }

        _log?.Invoke($"[Task] id={task.Request.Id} state={state.ToString().ToLowerInvariant()}");
        Changed?.Invoke(snapshot);
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
        ResultChars = task.ResultChars
    };

    public async ValueTask DisposeAsync()
    {
        TaskRecord[] tasks;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            tasks = _records.Values.ToArray();
        }

        foreach (var task in tasks)
        {
            try { task.CancelSource?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { task.TimeoutSource?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        var work = tasks.Where(task => task.Work is not null).Select(task => task.Work!).ToArray();
        if (work.Length == 0) return;
        try { await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* 停机收口有界：未完成的任务由进程退出兜底 */ }
    }

    private sealed class TaskRecord(AgentTaskRequest request, DateTimeOffset createdAt)
    {
        internal AgentTaskRequest Request { get; } = request;
        internal AgentTaskState State { get; set; } = AgentTaskState.Queued;
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

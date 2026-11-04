using System.Threading.Channels;

namespace BotAgent.Services.Evaluation;

/// <summary>
/// 后台评估队列的可调参数（构造时冻结；记录对象本身不可变）。
/// 取值范围在 <see cref="BackgroundEvaluatorQueue" /> 构造时校验。
/// </summary>
public sealed record BackgroundEvaluatorOptions
{
    /// <summary>默认等待队列容量（只统计尚未被 worker 取走的任务）。</summary>
    public const int DefaultCapacity = 128;

    /// <summary>默认并发执行数。</summary>
    public const int DefaultMaxConcurrency = 2;

    /// <summary>默认单任务最多尝试次数（含第一次）。</summary>
    public const int DefaultMaxAttempts = 2;

    /// <summary>默认单次尝试超时时间。</summary>
    public static readonly TimeSpan DefaultPerJobTimeout = TimeSpan.FromSeconds(30);

    /// <summary>默认停机排空预算。</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>等待队列容量，必须 ≥ 1。运行中的任务不占队列槽位。</summary>
    public int Capacity { get; init; } = DefaultCapacity;

    /// <summary>并发 worker 数，必须 ≥ 1。</summary>
    public int MaxConcurrency { get; init; } = DefaultMaxConcurrency;

    /// <summary>单个任务最多尝试次数（含第一次），必须 ≥ 1。</summary>
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    /// <summary>单次尝试的超时时间；必须为正，或 <see cref="Timeout.InfiniteTimeSpan" />。</summary>
    public TimeSpan PerJobTimeout { get; init; } = DefaultPerJobTimeout;

    /// <summary>停机时等待运行中任务自然结束的预算；必须 ≥ 0 且有限（上限 1 天）。</summary>
    public TimeSpan DrainTimeout { get; init; } = DefaultDrainTimeout;

    internal void Validate()
    {
        if (Capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Capacity), Capacity, "容量必须 ≥ 1。");
        }

        if (MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency), MaxConcurrency, "并发数必须 ≥ 1。");
        }

        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "尝试次数必须 ≥ 1。");
        }

        if (PerJobTimeout != Timeout.InfiniteTimeSpan
            && (PerJobTimeout <= TimeSpan.Zero || PerJobTimeout > TimeSpan.FromDays(1)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PerJobTimeout), PerJobTimeout, "超时时间必须为正（上限 1 天）或 Timeout.InfiniteTimeSpan。");
        }

        if (DrainTimeout < TimeSpan.Zero || DrainTimeout == Timeout.InfiniteTimeSpan || DrainTimeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(DrainTimeout), DrainTimeout, "排空预算必须为 0 到 1 天之间的有限值。");
        }
    }
}

/// <summary>后台任务的安全终态。结果里不含异常消息或任何任务载荷。</summary>
public enum BackgroundJobStatus
{
    /// <summary>执行成功。</summary>
    Succeeded,

    /// <summary>尝试次数用尽仍失败，或内部执行异常。</summary>
    Failed,

    /// <summary>单次尝试超过超时预算（不重试）。</summary>
    TimedOut,

    /// <summary>被取消（停机、外部取消或任务自身抛取消；不重试）。任务已确认停止。</summary>
    Canceled,

    /// <summary>
    /// 已放弃等待：停机/超时后已请求取消，但底层工作仍未结束，实际结果未知。
    /// 该任务不再重试、去重键保持占用，直到工作真正结束；宿主应按“未收敛/待核对”处理。
    /// </summary>
    Abandoned,

    /// <summary>入队时被拒绝（队列已满或已停机），未执行。</summary>
    Rejected,

    /// <summary>相同 dedupKey 的任务正在排队/运行，本次未重复执行。</summary>
    Deduplicated,
}

/// <summary>一个后台任务的安全结果（JobId + 终态 + 已尝试次数）。</summary>
public sealed record BackgroundJobResult(string JobId, BackgroundJobStatus Status, int Attempts);

/// <summary>
/// 队列快照：只有计数，没有任务 ID、异常文本或任何消息内容。
/// <see cref="Failed" /> 汇总 Failed / TimedOut / Canceled 三种已确认终态；
/// <see cref="Abandoned" /> 单独表示“已放弃等待、工作可能仍在运行”的未知终态。
/// </summary>
public readonly record struct QueueSnapshot(
    int Queued,
    int Running,
    int Completed,
    int Failed,
    int Rejected,
    int Deduplicated,
    int Abandoned)
{
    /// <summary>已收敛的派发终态总数（完成 + 失败 + 拒绝 + 去重）；不含结果未知的 Abandoned。</summary>
    public int Terminal => Completed + Failed + Rejected + Deduplicated;

    /// <summary>尚未真正确认收敛的任务数（排队 + 运行 + 已放弃等待但工作可能仍在跑）。</summary>
    public int Outstanding => Queued + Running + Abandoned;
}

/// <summary>
/// 有界、可等待的后台评估队列。
///
/// 行为约定：
///   · <b>容量</b>：<see cref="BackgroundEvaluatorOptions.Capacity" /> 限制**等待中**的任务数；
///     正在运行的任务另计，因此最大在途数 = 容量 + 并发数。
///   · <b>去重</b>：同一 <c>dedupKey</c> 在任务排队/运行期间不会创建第二次执行；
///     重复入队得到 <see cref="BackgroundJobStatus.Deduplicated" />（<c>TryEnqueue</c> 返回 false）。
///     任务确认收敛后键立即释放；被放弃等待的任务保留到工作真正结束。空 dedupKey 表示不去重。
///   · <b>重试</b>：任务抛普通异常时按注入的退避函数重试，最多
///     <see cref="BackgroundEvaluatorOptions.MaxAttempts" /> 次；取消与超时**不重试**。
///   · <b>超时</b>：每次尝试使用独立的 <see cref="BackgroundEvaluatorOptions.PerJobTimeout" />，
///     等待由 <see cref="Task.WhenAny(Task, Task)" /> 真正限定；超时先取消令牌，
///     若任务仍不结束则结果记为 <see cref="BackgroundJobStatus.TimedOut" />，
///     worker 继续占住槽位观察该任务（迟到成功不改写结果，也不启动替代作业）。
///   · <b>停机</b>：<see cref="StopAsync" /> 先拒绝新任务，在
///     <see cref="BackgroundEvaluatorOptions.DrainTimeout" /> 内让运行中/排队中的任务自然完成；
///     超时后取消剩余任务，最多再等一个很短的宽容期，然后为未开始的任务记录
///     <see cref="BackgroundJobStatus.Canceled" />；仍在运行且无法确认结束的任务记录
///     <see cref="BackgroundJobStatus.Abandoned" />（结果未知）。去重键、<c>Outstanding</c>
///     与 <see cref="QueueSnapshot.Abandoned" /> 保留到工作真正结束。
///     即使任务完全忽略取消令牌，停机也不会挂起（后台 worker 可能仍然滞留，但不再阻塞宿主机）。
///     停机可重复调用，后续调用共享同一个收敛任务。
///   · <b>线程安全</b>：所有公开成员可并发调用；所有执行器都被等待/观察，不存在 fire-and-forget 的用户任务。
/// </summary>
public sealed class BackgroundEvaluatorQueue
{
    private const int QueuedState = 0;
    private const int RunningState = 1;
    private const int TerminalState = 2;

    private const double MaxCancelGraceMilliseconds = 1000;

    private readonly BackgroundEvaluatorOptions _options;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly Channel<QueuedJob> _channel;
    private readonly ChannelWriter<QueuedJob> _writer;
    private readonly ChannelReader<QueuedJob> _reader;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task[] _workers;

    private readonly object _gate = new();
    private readonly HashSet<QueuedJob> _live = new();
    private readonly Dictionary<string, QueuedJob> _dedup = new(StringComparer.Ordinal);
    private bool _stopped;

    private readonly object _stopGate = new();
    private Task? _stopTask;

    private int _queued;
    private int _running;
    private int _completed;
    private int _failed;
    private int _rejected;
    private int _deduplicated;
    private int _abandoned;

    /// <summary>用默认参数创建队列。</summary>
    public BackgroundEvaluatorQueue()
        : this(new BackgroundEvaluatorOptions())
    {
    }

    /// <summary>用给定参数创建队列，退避函数使用默认值（25ms × 尝试次数，最大 200ms）。</summary>
    public BackgroundEvaluatorQueue(BackgroundEvaluatorOptions options)
        : this(options, null)
    {
    }

    /// <summary>便捷构造：直接给四个核心参数，退避函数使用默认值。</summary>
    public BackgroundEvaluatorQueue(int capacity, int maxConcurrency, int maxAttempts, TimeSpan perJobTimeout)
        : this(
            new BackgroundEvaluatorOptions
            {
                Capacity = capacity,
                MaxConcurrency = maxConcurrency,
                MaxAttempts = maxAttempts,
                PerJobTimeout = perJobTimeout,
            })
    {
    }

    /// <summary>
    /// 创建并启动 worker。
    /// </summary>
    /// <param name="options">队列参数；不能为 null，且必须通过范围校验。</param>
    /// <param name="backoff">
    /// 可注入的确定性退避函数：入参为刚失败的尝试序号（从 1 开始），返回等待时长。
    /// 返回 ≤ 0 表示不等待。默认实现为 25ms × min(attempt, 8)。
    /// </param>
    public BackgroundEvaluatorQueue(BackgroundEvaluatorOptions options, Func<int, TimeSpan>? backoff)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _backoff = backoff ?? DefaultBackoff;

        _channel = Channel.CreateBounded<QueuedJob>(
            new BoundedChannelOptions(options.Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        _writer = _channel.Writer;
        _reader = _channel.Reader;

        _workers = new Task[options.MaxConcurrency];
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i] = WorkerLoopAsync();
        }
    }

    /// <summary>
    /// 尝试入队（不等待结果）。
    /// 返回 false 表示未创建执行：队列已满、已停机，或同 dedupKey 的任务正在排队/运行。
    /// 失败原因可从 <see cref="Snapshot" /> 的 Rejected / Deduplicated 计数区分。
    /// </summary>
    public bool TryEnqueue(string jobId, string dedupKey, Func<CancellationToken, Task> work)
    {
        ValidateArguments(jobId, work);
        return TryAccept(jobId, dedupKey ?? string.Empty, work, completion: null) == EnqueueOutcome.Accepted;
    }

    /// <summary>
    /// 入队并返回可等待的结果任务。
    /// 被拒绝或去重时立即返回 <see cref="BackgroundJobStatus.Rejected" /> /
    /// <see cref="BackgroundJobStatus.Deduplicated" /> 结果（Attempts = 0）。
    /// </summary>
    public Task<BackgroundJobResult> EnqueueAsync(
        string jobId,
        string dedupKey,
        Func<CancellationToken, Task> work)
    {
        ValidateArguments(jobId, work);

        var completion = new TaskCompletionSource<BackgroundJobResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = TryAccept(jobId, dedupKey ?? string.Empty, work, completion);

        return outcome switch
        {
            EnqueueOutcome.Accepted => completion.Task,
            EnqueueOutcome.Deduplicated => Task.FromResult(
                new BackgroundJobResult(jobId, BackgroundJobStatus.Deduplicated, 0)),
            _ => Task.FromResult(
                new BackgroundJobResult(jobId, BackgroundJobStatus.Rejected, 0)),
        };
    }

    /// <summary>
    /// 停止接收新任务并排空队列。可重复调用：所有调用共享同一个停机任务，不会重复排空。
    /// 传入的令牌只取消**本次等待**，不会中止已经开始的排空流程。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        var stopTask = EnsureStopStarted();
        return cancellationToken.CanBeCanceled ? stopTask.WaitAsync(cancellationToken) : stopTask;
    }

    /// <summary>只读计数快照；可在任务任意阶段并发调用。</summary>
    public QueueSnapshot Snapshot()
        => new(
            Volatile.Read(ref _queued),
            Volatile.Read(ref _running),
            Volatile.Read(ref _completed),
            Volatile.Read(ref _failed),
            Volatile.Read(ref _rejected),
            Volatile.Read(ref _deduplicated),
            Volatile.Read(ref _abandoned));

    // ───────────────────────── 入队 ─────────────────────────

    private EnqueueOutcome TryAccept(
        string jobId,
        string dedupKey,
        Func<CancellationToken, Task> work,
        TaskCompletionSource<BackgroundJobResult>? completion)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                Interlocked.Increment(ref _rejected);
                return EnqueueOutcome.Rejected;
            }

            if (dedupKey.Length > 0 && _dedup.TryGetValue(dedupKey, out var existing))
            {
                // 正常路径下终态任务会先从表里移除；这里兜住“刚收敛、清理尚未拿到锁”的窗口。
                // 被放弃等待的任务（Abandoned/超时后工作仍在跑）必须继续占用去重键：
                // 否则停机后立刻能启动同键替代作业，等于伪造有界性。
                if (Volatile.Read(ref existing.State) != TerminalState
                    || Volatile.Read(ref existing.AbandonRecorded) == 1
                    || existing.InFlight is { IsCompleted: false })
                {
                    Interlocked.Increment(ref _deduplicated);
                    return EnqueueOutcome.Deduplicated;
                }

                _dedup.Remove(dedupKey);
            }

            var job = new QueuedJob(jobId, dedupKey, work, completion);

            _live.Add(job);
            if (dedupKey.Length > 0)
            {
                _dedup[dedupKey] = job;
            }

            Interlocked.Increment(ref _queued);
            if (_writer.TryWrite(job))
            {
                return EnqueueOutcome.Accepted;
            }

            // 容量满（或 writer 已因停机完成）：回滚，不留下任何状态。
            Interlocked.Decrement(ref _queued);
            _live.Remove(job);
            if (dedupKey.Length > 0 && _dedup.TryGetValue(dedupKey, out var current) && ReferenceEquals(current, job))
            {
                _dedup.Remove(dedupKey);
            }

            Interlocked.Increment(ref _rejected);
            return EnqueueOutcome.Rejected;
        }
    }

    private static void ValidateArguments(string jobId, Func<CancellationToken, Task> work)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("JobId 不能为空。", nameof(jobId));
        }

        ArgumentNullException.ThrowIfNull(work);
    }

    // ───────────────────────── 执行 ─────────────────────────

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (await _reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (_reader.TryRead(out var job))
                {
                    // 停机取消后，不再启动尚未开始的任务，直接按安全终态收敛。
                    if (_shutdownCts.IsCancellationRequested)
                    {
                        TryFinalize(job, BackgroundJobStatus.Canceled, Volatile.Read(ref job.Attempts));
                        continue;
                    }

                    if (!TryStart(job))
                    {
                        continue; // 已被停机流程收敛。
                    }

                    await RunAttemptsAsync(job).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // 通道关闭 / 停机竞争：worker 正常退出。任务结果已由各自路径收敛。
        }
    }

    private async Task RunAttemptsAsync(QueuedJob job)
    {
        var status = BackgroundJobStatus.Failed;
        Task? abandoned = null;

        try
        {
            // 停机流程可能在 TryStart 与这里之间已把任务收敛为 Abandoned：
            // 此时不再启动工作，只把“未确认结束”的计数在空任务上结清。
            if (Volatile.Read(ref job.State) == TerminalState)
            {
                if (Volatile.Read(ref job.AbandonRecorded) == 1)
                {
                    MarkAbandoned(job, Task.CompletedTask);
                }

                return;
            }

            while (true)
            {
                var attempt = Interlocked.Increment(ref job.Attempts);
                var outcome = await RunOneAttemptAsync(job, attempt).ConfigureAwait(false);

                if (outcome.AbandonedWork is not null)
                {
                    status = outcome.Status;
                    abandoned = outcome.AbandonedWork;
                    break;
                }

                if (outcome.Status == BackgroundJobStatus.Failed && attempt < _options.MaxAttempts)
                {
                    TimeSpan delay;
                    try
                    {
                        delay = _backoff(attempt);
                    }
                    catch
                    {
                        delay = TimeSpan.Zero;
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, _shutdownCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            status = BackgroundJobStatus.Canceled;
                            break;
                        }
                    }

                    continue;
                }

                status = outcome.Status;
                break;
            }
        }
        catch
        {
            // 防御性兜底：任何未预期异常都收敛成 Failed，绝不外泄异常文本或让 worker 崩溃。
            status = BackgroundJobStatus.Failed;
        }
        finally
        {
            // 超时/停机后工作仍可能在跑：先按“已放弃等待”收敛，去重键保留到工作真正结束。
            TryFinalize(job, status, Volatile.Read(ref job.Attempts), holdDedup: abandoned is not null);
        }

        if (abandoned is not null)
        {
            MarkAbandoned(job, abandoned);
            // 占住 worker 槽位直到工作真正结束：不能放出容量后无限启动替代作业来伪造有界性。
            await ObserveAsync(abandoned).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 执行一次尝试。超时用 <see cref="Task.WhenAny" /> 真正限定等待；工作忽略取消令牌时
    /// 返回 <see cref="BackgroundJobStatus.TimedOut" />（停机时为 <see cref="BackgroundJobStatus.Abandoned" />），
    /// 并把未结束的 Task 交给调用方继续占位观察 —— 迟到的成功绝不改写成成功。
    /// </summary>
    private async Task<AttemptOutcome> RunOneAttemptAsync(QueuedJob job, int attempt)
    {
        var timeoutCts = _options.PerJobTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(_options.PerJobTimeout);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCts.Token, timeoutCts?.Token ?? CancellationToken.None);

        // 委托的同步前缀同样要受预算约束：放到独立 Task 上调用，让 WhenAny 从尝试开始
        // 就同时覆盖“调用委托”和“等待异步工作”，不会先卡在 WhenAny 之前再谎报成功。
        var workTask = Task.Run(() => job.Work(linked.Token), CancellationToken.None);

        job.InFlight = workTask;
        if (Volatile.Read(ref job.AbandonRecorded) == 1)
        {
            // 停机流程已在工作启动前把它记为 Abandoned：补上对真实任务的观察。
            AttachAbandoned(job, workTask);
        }

        if (timeoutCts is null)
        {
            var unbounded = await ClassifyCompletedAsync(workTask, timeoutCts).ConfigureAwait(false);
            DisposeScope(timeoutCts, linked);
            return unbounded;
        }

        var winner = await Task.WhenAny(workTask, Task.Delay(_options.PerJobTimeout)).ConfigureAwait(false);
        if (ReferenceEquals(winner, workTask))
        {
            var completed = await ClassifyCompletedAsync(workTask, timeoutCts).ConfigureAwait(false);
            DisposeScope(timeoutCts, linked);
            return completed;
        }

        // 超时先到：先显式取消令牌（Dispose 不会取消，定时器也可能尚未跑）。
        // 关键：计时器往往已经进入“正在通知”状态，此时 Cancel() 会立即返回，
        // 而 linked 还没收到父令牌的回调。若在返回时 Dispose 掉链接源，注册会被摘掉，
        // 真实工作就永远收不到取消（表现为 token=False、Task 一直 WaitingForActivation）。
        // 因此两个 CTS 都保留到工作真正结束；停机令牌的链接也一并保留。
        try
        {
            timeoutCts.Cancel();
        }
        catch (AggregateException)
        {
            // 任务注册在取消令牌上的回调自行抛错；取消动作本身仍然生效。
        }

        DisposeAfterCompletion(workTask, timeoutCts, linked);
        return new AttemptOutcome(
            _shutdownCts.IsCancellationRequested
                ? BackgroundJobStatus.Abandoned
                : BackgroundJobStatus.TimedOut,
            workTask);
    }

    private async Task<AttemptOutcome> ClassifyCompletedAsync(Task workTask, CancellationTokenSource? timeoutCts)
    {
        try
        {
            await workTask.ConfigureAwait(false);
            return new AttemptOutcome(BackgroundJobStatus.Succeeded, null);
        }
        catch (OperationCanceledException)
        {
            if (_shutdownCts.IsCancellationRequested)
            {
                return new AttemptOutcome(BackgroundJobStatus.Canceled, null);
            }

            return new AttemptOutcome(
                timeoutCts?.IsCancellationRequested == true
                    ? BackgroundJobStatus.TimedOut
                    : BackgroundJobStatus.Canceled,
                null);
        }
        catch (Exception)
        {
            return new AttemptOutcome(
                _shutdownCts.IsCancellationRequested
                    ? BackgroundJobStatus.Canceled
                    : BackgroundJobStatus.Failed,
                null);
        }
    }

    private static async Task ObserveAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch
        {
            // 已放弃等待的作业：只观察，绝不把迟到结果写成成功或失败。
        }
    }

    private static void DisposeScope(CancellationTokenSource? timeoutCts, CancellationTokenSource linked)
    {
        linked.Dispose();
        timeoutCts?.Dispose();
    }

    /// <summary>
    /// 超时返回时工作可能仍在跑：链接源必须活到它真正结束，否则父令牌还在通知途中，
    /// 注册就被 Dispose 摘掉，取消永远送不到工作里。工作结束后再释放两个 CTS。
    /// </summary>
    private static void DisposeAfterCompletion(
        Task work, CancellationTokenSource? timeoutCts, CancellationTokenSource linked)
    {
        if (work.IsCompleted)
        {
            DisposeScope(timeoutCts, linked);
            return;
        }

        _ = work.ContinueWith(
            static (_, state) =>
            {
                var (timeout, link) = ((CancellationTokenSource?, CancellationTokenSource))state!;
                DisposeScope(timeout, link);
            },
            (timeoutCts, linked),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool TryStart(QueuedJob job)
    {
        if (Interlocked.CompareExchange(ref job.State, RunningState, QueuedState) != QueuedState)
        {
            return false;
        }

        Interlocked.Decrement(ref _queued);
        Interlocked.Increment(ref _running);
        return true;
    }

    /// <summary>
    /// 把任务收敛到唯一终态（CAS 保证只生效一次，停机强制收敛与 worker 自然结束不会重复计数）。
    /// <paramref name="holdDedup" /> = true 时保留去重键，直到被放弃等待的底层工作真正结束。
    /// </summary>
    private bool TryFinalize(QueuedJob job, BackgroundJobStatus status, int attempts, bool holdDedup = false)
    {
        var previous = Interlocked.Exchange(ref job.State, TerminalState);
        if (previous == TerminalState)
        {
            return false;
        }

        if (previous == QueuedState)
        {
            Interlocked.Decrement(ref _queued);
        }
        else
        {
            Interlocked.Decrement(ref _running);
        }

        if (status == BackgroundJobStatus.Succeeded)
        {
            Interlocked.Increment(ref _completed);
        }
        else if (status != BackgroundJobStatus.Abandoned)
        {
            // 已确认的失败/超时/取消；Abandoned 单独计数，表示“结果未知”，不混进 Failed。
            Interlocked.Increment(ref _failed);
        }

        lock (_gate)
        {
            _live.Remove(job);
            if (!holdDedup)
            {
                ReleaseDedupLocked(job);
            }
        }

        job.Completion?.TrySetResult(new BackgroundJobResult(job.JobId, status, attempts));
        return true;
    }

    /// <summary>把“已停止等待、但工作可能仍在跑”的任务记入未知终态计数（只记一次）。</summary>
    private void MarkAbandoned(QueuedJob job, Task? work)
    {
        if (Interlocked.Exchange(ref job.AbandonRecorded, 1) == 0)
        {
            Interlocked.Increment(ref _abandoned);
        }

        AttachAbandoned(job, work);
    }

    /// <summary>工作真正结束后释放未知终态计数与去重键；同一个 Task 只挂一次 continuation。</summary>
    private void AttachAbandoned(QueuedJob job, Task? work)
    {
        if (work is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref job.AbandonObserved, work, null) is not null)
        {
            return;
        }

        _ = work.ContinueWith(
            _ =>
            {
                Interlocked.Decrement(ref _abandoned);
                ReleaseDedup(job);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseDedup(QueuedJob job)
    {
        if (job.DedupKey.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            ReleaseDedupLocked(job);
        }
    }

    private void ReleaseDedupLocked(QueuedJob job)
    {
        if (job.DedupKey.Length > 0
            && _dedup.TryGetValue(job.DedupKey, out var current)
            && ReferenceEquals(current, job))
        {
            _dedup.Remove(job.DedupKey);
        }
    }

    // ───────────────────────── 停机 ─────────────────────────

    private Task EnsureStopStarted()
    {
        lock (_stopGate)
        {
            if (_stopTask is null)
            {
                lock (_gate)
                {
                    _stopped = true;
                }

                _writer.TryComplete();
                _stopTask = StopCoreAsync();
            }

            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            var workers = Task.WhenAll(_workers);
            var drained = await Task.WhenAny(workers, Task.Delay(_options.DrainTimeout)).ConfigureAwait(false);

            if (!ReferenceEquals(drained, workers))
            {
                try
                {
                    _shutdownCts.Cancel();
                }
                catch (AggregateException)
                {
                    // 任务注册在取消令牌上的回调自行抛错；取消动作本身仍然生效。
                }

                // 给响应取消的任务一个很短的收敛窗口；不响应的任务不会阻塞停机返回。
                var graceMs = Math.Min(_options.DrainTimeout.TotalMilliseconds, MaxCancelGraceMilliseconds);
                if (graceMs > 0)
                {
                    await Task.WhenAny(workers, Task.Delay(TimeSpan.FromMilliseconds(graceMs))).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // 停机流程不向宿主抛异常。
        }
        finally
        {
            FinalizeRemaining();
        }
    }

    private void FinalizeRemaining()
    {
        QueuedJob[] outstanding;
        lock (_gate)
        {
            outstanding = _live.ToArray();
        }

        foreach (var job in outstanding)
        {
            var attempts = Volatile.Read(ref job.Attempts);
            if (Volatile.Read(ref job.State) != RunningState)
            {
                // 还没开始：可以确认它没有产生任何副作用。
                TryFinalize(job, BackgroundJobStatus.Canceled, attempts);
                continue;
            }

            var work = job.InFlight;
            if (work is { IsCompleted: true })
            {
                // 工作已经结束，只是 worker 还没来得及收敛：按真实完成状态结算。
                var status = work.Status switch
                {
                    TaskStatus.RanToCompletion => BackgroundJobStatus.Succeeded,
                    TaskStatus.Canceled => BackgroundJobStatus.Canceled,
                    _ => BackgroundJobStatus.Failed,
                };
                TryFinalize(job, status, attempts);
                continue;
            }

            // 在途工作无法确认是否真的停下：显式记为“结果未知”，不能谎报 Canceled。
            // 去重键与计数保留到工作真正结束，避免停机后立刻启动同键替代作业。
            if (TryFinalize(job, BackgroundJobStatus.Abandoned, attempts, holdDedup: true))
            {
                MarkAbandoned(job, work);
            }
        }
    }

    private static TimeSpan DefaultBackoff(int attempt)
        => TimeSpan.FromMilliseconds(25 * Math.Clamp(attempt, 1, 8));

    private enum EnqueueOutcome
    {
        Accepted,
        Deduplicated,
        Rejected,
    }

    private sealed class QueuedJob
    {
        public QueuedJob(
            string jobId,
            string dedupKey,
            Func<CancellationToken, Task> work,
            TaskCompletionSource<BackgroundJobResult>? completion)
        {
            JobId = jobId;
            DedupKey = dedupKey;
            Work = work;
            Completion = completion;
        }

        public string JobId { get; }

        public string DedupKey { get; }

        public Func<CancellationToken, Task> Work { get; }

        public TaskCompletionSource<BackgroundJobResult>? Completion { get; }

        /// <summary>0 = 排队，1 = 运行，2 = 已收敛；只用 Interlocked 访问。</summary>
        public int State;

        /// <summary>已开始的尝试次数；worker 与停机流程并发读写。</summary>
        public int Attempts;

        /// <summary>当前尝试返回的 Task（工作一启动就写入）；停机时用来判断是否真的结束。</summary>
        public Task? InFlight;

        /// <summary>0 = 未记录“已放弃等待”，1 = 已计入 <c>_abandoned</c>；只用 Interlocked 访问。</summary>
        public int AbandonRecorded;

        /// <summary>已挂上结束观察的 Task（同一任务只挂一次）；只用 Interlocked 访问。</summary>
        public Task? AbandonObserved;
    }

    private readonly record struct AttemptOutcome(BackgroundJobStatus Status, Task? AbandonedWork);
}

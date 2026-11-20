using BotAgent.Plugins.Execution;

[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(BotAgent.Services.Resilience.ToolRunResult<>))]

namespace BotAgent.Services.Resilience;

/// <summary>工具级熔断状态。工具状态只驻留当前进程，不包含请求正文或密钥。</summary>
public enum ToolCircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>工具级熔断的可观察快照。</summary>
public sealed record ToolCircuitSnapshot(
    string ToolName,
    ToolCircuitState State,
    int ConsecutiveTimeouts,
    DateTimeOffset? CooldownUntil,
    bool ProbeInFlight);

/// <summary>
/// L2 工具熔断器：单次调用硬超时 8 秒，连续 3 次超时摘除 5 分钟。
/// 不在工具层自动重试；调用方拿到明确原因码后自行决定是否降级。
/// </summary>
public sealed class ToolCircuitBreaker : IToolExecution
{
    public static readonly TimeSpan DefaultHardTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly int _timeoutThreshold;
    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _hardTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private ToolCircuitState _state = ToolCircuitState.Closed;
    private int _consecutiveTimeouts;
    private DateTimeOffset? _cooldownUntil;
    private bool _probeInFlight;

    public ToolCircuitBreaker(
        string toolName,
        int timeoutThreshold = 3,
        TimeSpan? cooldown = null,
        TimeSpan? hardTimeout = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new ArgumentException("Tool name is required.", nameof(toolName));
        }

        if (timeoutThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutThreshold));
        }

        if (cooldown is { } cooldownValue && cooldownValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        if (hardTimeout is { } timeoutValue && timeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(hardTimeout));
        }

        ToolName = toolName.Trim();
        _timeoutThreshold = timeoutThreshold;
        _cooldown = cooldown ?? DefaultCooldown;
        _hardTimeout = hardTimeout ?? DefaultHardTimeout;
        _clock = clock ?? (() => Clock.UtcNow);
    }

    public string ToolName { get; }
    public TimeSpan HardTimeout => _hardTimeout;

    /// <summary>尝试取得调用许可；熔断期间或 half-open 已有探测时拒绝。</summary>
    public bool TryEnter(out ToolCircuitSnapshot snapshot)
    {
        var now = _clock();
        lock (_gate)
        {
            if (_state == ToolCircuitState.Open)
            {
                if (_cooldownUntil is null || now < _cooldownUntil.Value)
                {
                    snapshot = SnapshotUnsafe();
                    return false;
                }

                _state = ToolCircuitState.HalfOpen;
                _probeInFlight = false;
            }

            if (_state == ToolCircuitState.HalfOpen)
            {
                if (_probeInFlight)
                {
                    snapshot = SnapshotUnsafe();
                    return false;
                }

                _probeInFlight = true;
            }

            snapshot = SnapshotUnsafe();
            return true;
        }
    }

    /// <summary>执行一次工具调用。外部取消会原样抛出，不会污染超时计数。</summary>
    public Task<ToolRunResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
        => RunWithLifetimeAsync(operation, ct, null);

    // Ownership transfers on entry; even validation/rejection releases the lease. The public
    // result deadline is independent of plugin delegate and cancellation-callback completion.
    internal async Task<ToolRunResult<T>> RunWithLifetimeAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct, IDisposable? lifetime)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (!TryEnter(out _))
            {
                return new ToolRunResult<T>(false, default, "tool_circuit_open", false);
            }

            var timeoutCts = new CancellationTokenSource();
            var cancellationGate = new object();
            Task? cancellationWork = null;
            Task RequestCancellation()
            {
                // 同一调用只启动一次取消；并发请求不能丢掉正在运行的回调任务。
                lock (cancellationGate) return cancellationWork ??= timeoutCts.CancelAsync();
            }
            // 不使用 linked CTS：调用方的 Cancel 不应同步执行工具注册的阻塞/抛异常回调。
            var callerCancellation = ct.Register(() => { _ = RequestCancellation(); });
            var operationToken = timeoutCts.Token;
            // Schedule invocation too: delegates may block before returning their Task.
            var work = Task.Run(() => operation(operationToken), operationToken);
            try
            {
                var value = await work.WaitAsync(_hardTimeout, ct).ConfigureAwait(false);
                RecordSuccess();
                return new ToolRunResult<T>(true, value, "ok", false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _ = RequestCancellation();
                RecordTimeout();
                return new ToolRunResult<T>(false, default, "tool_timeout", true);
            }
            catch (OperationCanceledException)
            {
                _ = RequestCancellation();
                // 调用方主动取消：释放 half-open 探测锁并原样抛出，不记录任何失败或超时计数，避免污染熔断状态。
                ReleaseProbeOnCancellation();
                throw;
            }
            catch (TimeoutException)
            {
                _ = RequestCancellation();
                RecordTimeout();
                return new ToolRunResult<T>(false, default, "tool_timeout", true);
            }
            catch
            {
                RecordFailure();
                return new ToolRunResult<T>(false, default, "tool_exception", false);
            }
            finally
            {
                // 调用方不等 late work；CTS 由清理任务持有至 work 和取消回调都完成。
                // 清理仅观察异常、释放资源，绝不再写 circuit 状态。
                var completedLifetime = lifetime;
                lifetime = null; // Cleanup now owns the generation, not the returned result.
                _ = ObserveAndDisposeAsync(work, callerCancellation, timeoutCts, () =>
                {
                    lock (cancellationGate) return cancellationWork ?? Task.CompletedTask;
                }, completedLifetime);
            }
        }
        finally { lifetime?.Dispose(); }
    }

    private static async Task ObserveAndDisposeAsync(Task work,
        CancellationTokenRegistration callerCancellation, CancellationTokenSource source,
        Func<Task> cancellationWork, IDisposable? lifetime)
    {
        // 先解除调用方桥接，避免永不完成的工具一直保留 caller token 的注册。
        await callerCancellation.DisposeAsync().ConfigureAwait(false);
        callerCancellation = default; // late work 不应额外持有已解除的调用方注册句柄。
        // 两条路径各自立即观察 fault；不能因其中一条永不完成而漏掉另一条的异常。
        static async Task ObserveAsync(Task completion)
        {
            try { await completion.ConfigureAwait(false); }
            catch { /* 结果已确定，late fault/cancel 只观察，不改状态。 */ }
        }
        await Task.WhenAll(ObserveAsync(work), ObserveAsync(cancellationWork())).ConfigureAwait(false);
        source.Dispose();
        lifetime?.Dispose();
    }

    public ToolCircuitSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotUnsafe();
        }
    }

    /// <summary>成功调用清除连续超时并关闭熔断器。</summary>
    public ToolCircuitSnapshot RecordSuccess()
    {
        lock (_gate)
        {
            _state = ToolCircuitState.Closed;
            _consecutiveTimeouts = 0;
            _cooldownUntil = null;
            _probeInFlight = false;
            return SnapshotUnsafe();
        }
    }

    /// <summary>记录一次硬超时；达到阈值后摘除工具。</summary>
    public ToolCircuitSnapshot RecordTimeout()
    {
        var now = _clock();
        lock (_gate)
        {
            _consecutiveTimeouts++;
            _probeInFlight = false;
            if (_state == ToolCircuitState.HalfOpen || _consecutiveTimeouts >= _timeoutThreshold)
            {
                _state = ToolCircuitState.Open;
                _cooldownUntil = now + _cooldown;
            }

            return SnapshotUnsafe();
        }
    }

    /// <summary>非超时异常不累计阈值，但 half-open 探测失败仍需重新摘除工具。</summary>
    public ToolCircuitSnapshot RecordFailure()
    {
        var now = _clock();
        lock (_gate)
        {
            _probeInFlight = false;
            if (_state == ToolCircuitState.HalfOpen)
            {
                _state = ToolCircuitState.Open;
                _cooldownUntil = now + _cooldown;
            }

            return SnapshotUnsafe();
        }
    }

    private void ReleaseProbeOnCancellation()
    {
        lock (_gate)
        {
            _probeInFlight = false;
        }
    }

    private ToolCircuitSnapshot SnapshotUnsafe()
        => new(ToolName, _state, _consecutiveTimeouts, _cooldownUntil, _probeInFlight);
}

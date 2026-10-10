namespace BotAgent.Services.Resilience;

/// <summary>模型 Provider 的三级状态中，L1 使用的状态。</summary>
public enum ProviderCircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>可观察的 Provider 熔断快照，不包含密钥、请求正文或原始响应。</summary>
public sealed record ProviderCircuitSnapshot(
    string ProviderId,
    ProviderCircuitState State,
    int ConsecutiveHardFailures,
    DateTimeOffset? CooldownUntil,
    bool ProbeInFlight);

/// <summary>
/// 单 Provider 的 L1 熔断器。
/// 连续硬错误达到阈值后打开；冷却结束只放行一个 half-open 探测，成功恢复，失败重新冷却。
/// </summary>
public sealed class ProviderCircuitBreaker
{
    private readonly object _gate = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTimeOffset> _clock;
    private ProviderCircuitState _state = ProviderCircuitState.Closed;
    private int _consecutiveHardFailures;
    private DateTimeOffset? _cooldownUntil;
    private bool _probeInFlight;

    public ProviderCircuitBreaker(
        string providerId,
        int failureThreshold = 3,
        TimeSpan? cooldown = null,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        }

        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }

        if (cooldown is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        ProviderId = providerId.Trim();
        _failureThreshold = failureThreshold;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(120);
        _clock = clock ?? (() => Clock.UtcNow);
    }

    public string ProviderId { get; }

    /// <summary>尝试取得一次调用许可；open 状态未到冷却时间时直接拒绝。</summary>
    public bool TryEnter(out ProviderCircuitSnapshot snapshot)
    {
        var now = _clock();
        lock (_gate)
        {
            if (_state == ProviderCircuitState.Open)
            {
                if (_cooldownUntil is null || now < _cooldownUntil.Value)
                {
                    snapshot = SnapshotUnsafe();
                    return false;
                }

                _state = ProviderCircuitState.HalfOpen;
                _probeInFlight = false;
            }

            if (_state == ProviderCircuitState.HalfOpen)
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

    /// <summary>记录成功调用，清除失败计数并关闭熔断器。</summary>
    public ProviderCircuitSnapshot RecordSuccess()
    {
        lock (_gate)
        {
            _state = ProviderCircuitState.Closed;
            _consecutiveHardFailures = 0;
            _cooldownUntil = null;
            _probeInFlight = false;
            return SnapshotUnsafe();
        }
    }

    /// <summary>记录 5xx、429、连接失败或超时等硬错误。</summary>
    public ProviderCircuitSnapshot RecordHardFailure()
    {
        var now = _clock();
        lock (_gate)
        {
            _consecutiveHardFailures++;
            _probeInFlight = false;
            if (_state == ProviderCircuitState.HalfOpen || _consecutiveHardFailures >= _failureThreshold)
            {
                _state = ProviderCircuitState.Open;
                _cooldownUntil = now + _cooldown;
            }

            return SnapshotUnsafe();
        }
    }

    /// <summary>外部取消时释放 half-open 探测锁，不计入失败，不改变状态。</summary>
    public void ReleaseProbe()
    {
        lock (_gate)
        {
            _probeInFlight = false;
        }
    }

    /// <summary>
    /// 从持久化快照恢复状态。正在执行的 half-open 探测不会跨进程恢复，避免把旧进程的在途请求误认为仍然有效。
    /// </summary>
    public ProviderCircuitSnapshot Restore(ProviderCircuitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Snapshot provider id does not match this breaker.", nameof(snapshot));
        }

        lock (_gate)
        {
            _state = snapshot.State;
            _consecutiveHardFailures = Math.Max(0, snapshot.ConsecutiveHardFailures);
            _cooldownUntil = snapshot.CooldownUntil;
            _probeInFlight = false;
            return SnapshotUnsafe();
        }
    }

    public ProviderCircuitSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotUnsafe();
        }
    }

    private ProviderCircuitSnapshot SnapshotUnsafe() => new(
        ProviderId,
        _state,
        _consecutiveHardFailures,
        _cooldownUntil,
        _probeInFlight);
}

/// <summary>一次 Provider 尝试的结构化结果；不携带模型原文。</summary>
public sealed record ProviderCallResult<T>(
    bool Succeeded,
    T? Value,
    bool HardFailure,
    string? ReasonCode = null)
{
    public static ProviderCallResult<T> Success(T value) => new(true, value, false);

    public static ProviderCallResult<T> Failure(string reasonCode, bool hardFailure = true)
        => new(false, default, hardFailure, reasonCode);
}

/// <summary>按优先级执行 Provider 并在硬错误时故障转移的结果。</summary>
public sealed record ProviderRunResult<T>(
    bool Succeeded,
    T? Value,
    string? ProviderId,
    int FallbackHops,
    string ReasonCode,
    bool Silent);

/// <summary>Provider 的非敏感路由元数据。</summary>
public sealed record ProviderCandidate(string Id, int Priority, bool Enabled = true);

/// <summary>
/// L1 Provider 故障转移执行器。
/// 它只接收调用方已经准备好的匿名结果，避免把密钥、提示词和模型响应写进熔断状态或诊断输出。
/// </summary>
public sealed class ProviderFailoverRunner
{
    private readonly IReadOnlyList<ProviderCandidate> _providers;
    private readonly Dictionary<string, ProviderCircuitBreaker> _breakers;
    private readonly Action<ProviderCircuitSnapshot>? _onSnapshotChanged;

    public ProviderFailoverRunner(
        IEnumerable<ProviderCandidate> providers,
        Func<DateTimeOffset>? clock = null,
        int failureThreshold = 3,
        TimeSpan? cooldown = null,
        IEnumerable<ProviderCircuitSnapshot>? initialSnapshots = null,
        Action<ProviderCircuitSnapshot>? onSnapshotChanged = null)
    {
        _providers = providers
            .Where(p => p is not null)
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();

        if (_providers.Count == 0)
        {
            throw new ArgumentException("At least one provider is required.", nameof(providers));
        }

        if (_providers.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != _providers.Count)
        {
            throw new ArgumentException("Provider ids must be unique.", nameof(providers));
        }

        _onSnapshotChanged = onSnapshotChanged;
        _breakers = _providers.ToDictionary(
            p => p.Id,
            p => new ProviderCircuitBreaker(p.Id, failureThreshold, cooldown, clock),
            StringComparer.Ordinal);

        foreach (var snapshot in initialSnapshots ?? Array.Empty<ProviderCircuitSnapshot>())
        {
            if (_breakers.TryGetValue(snapshot.ProviderId, out var breaker))
            {
                // half-open 探测锁不会跨进程恢复；Restore 会主动清掉 ProbeInFlight。
                breaker.Restore(snapshot);
            }
        }
    }

    private void Publish(ProviderCircuitSnapshot snapshot)
    {
        try
        {
            _onSnapshotChanged?.Invoke(snapshot);
        }
        catch
        {
            // 熔断台账写入失败不应反过来打断模型请求；下一次状态变更仍会重试持久化。
        }
    }

    public IReadOnlyList<ProviderCircuitSnapshot> Snapshots()
        => _providers.Select(p => _breakers[p.Id].Snapshot()).ToArray();

    public async Task<ProviderRunResult<T>> RunAsync<T>(
        Func<ProviderCandidate, CancellationToken, Task<ProviderCallResult<T>>> call,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var fallbackHops = 0;
        var attempted = false;
        string? lastReason = null;

        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            if (!provider.Enabled || !_breakers[provider.Id].TryEnter(out _))
            {
                continue;
            }

            attempted = true;
            ProviderCallResult<T> result;
            try
            {
                result = await call(provider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Publish(_breakers[provider.Id].RecordHardFailure());
                lastReason = "provider_timeout";
                fallbackHops++;
                continue;
            }
            catch (OperationCanceledException)
            {
                _breakers[provider.Id].ReleaseProbe();
                throw;
            }
            catch
            {
                Publish(_breakers[provider.Id].RecordHardFailure());
                lastReason = "provider_exception";
                fallbackHops++;
                continue;
            }

            if (result.Succeeded)
            {
                Publish(_breakers[provider.Id].RecordSuccess());
                return new ProviderRunResult<T>(true, result.Value, provider.Id, fallbackHops, "ok", false);
            }

            lastReason = result.ReasonCode ?? "provider_failure";
            if (result.HardFailure)
            {
                Publish(_breakers[provider.Id].RecordHardFailure());
            }

            fallbackHops++;
        }

        return new ProviderRunResult<T>(
            false,
            default,
            null,
            Math.Max(0, fallbackHops - (attempted ? 0 : 1)),
            lastReason ?? "all_providers_unavailable",
            true);
    }
}

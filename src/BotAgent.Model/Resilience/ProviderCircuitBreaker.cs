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
    private object _generation = new();
    private ProviderCircuitAdmission? _probeAdmission;

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
    public bool TryEnter(out ProviderCircuitSnapshot snapshot) => TryEnter(out snapshot, out _);

    internal bool TryEnter(out ProviderCircuitSnapshot snapshot, out ProviderCircuitAdmission? admission)
    {
        admission = null;
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
                _generation = new();
                _probeInFlight = false;
            }

            if (_state == ProviderCircuitState.HalfOpen)
            {
                if (_probeInFlight)
                {
                    snapshot = SnapshotUnsafe();
                    return false;
                }

                admission = new ProviderCircuitAdmission(_generation, IsProbe: true);
                _probeAdmission = admission;
                _probeInFlight = true;
            }

            admission ??= new ProviderCircuitAdmission(_generation, IsProbe: false);
            snapshot = SnapshotUnsafe();
            return true;
        }
    }

    /// <summary>记录成功调用，清除失败计数并关闭熔断器。</summary>
    public ProviderCircuitSnapshot RecordSuccess() => RecordSuccess(null);

    internal ProviderCircuitSnapshot RecordSuccess(ProviderCircuitAdmission? admission)
    {
        lock (_gate)
        {
            if (!CanCompleteUnsafe(admission)) return SnapshotUnsafe();
            if (_state != ProviderCircuitState.Closed) _generation = new();
            _probeAdmission = null;
            _state = ProviderCircuitState.Closed;
            _consecutiveHardFailures = 0;
            _cooldownUntil = null;
            _probeInFlight = false;
            return SnapshotUnsafe();
        }
    }

    /// <summary>记录 5xx、429、连接失败或超时等硬错误。</summary>
    public ProviderCircuitSnapshot RecordHardFailure() => RecordHardFailure(null);

    internal ProviderCircuitSnapshot RecordHardFailure(ProviderCircuitAdmission? admission)
    {
        var now = _clock();
        lock (_gate)
        {
            if (!CanCompleteUnsafe(admission)) return SnapshotUnsafe();
            _consecutiveHardFailures++;
            _probeAdmission = null;
            _probeInFlight = false;
            if (_state == ProviderCircuitState.HalfOpen || _consecutiveHardFailures >= _failureThreshold)
            {
                _state = ProviderCircuitState.Open;
                _generation = new();
                _cooldownUntil = now + _cooldown;
            }

            return SnapshotUnsafe();
        }
    }

    // Runner completion always supplies its admission; stale closed callers cannot clear a newer probe.
    internal ProviderCircuitSnapshot? ReleaseProbe(ProviderCircuitAdmission admission)
    {
        lock (_gate)
        {
            if (!admission.IsProbe || !CanCompleteUnsafe(admission)) return null;
            _probeAdmission = null;
            _probeInFlight = false;
            _generation = new();
            return SnapshotUnsafe();
        }
    }

    private bool CanCompleteUnsafe(ProviderCircuitAdmission? admission) => admission is null ||
        (ReferenceEquals(admission.Generation, _generation) &&
         (!admission.IsProbe || ReferenceEquals(admission, _probeAdmission)));

    /// <summary>兼容显式管理操作；并发调用应通过 runner 持有 admission。</summary>
    public void ReleaseProbe()
    {
        lock (_gate)
        {
            _probeAdmission = null;
            _probeInFlight = false;
            _generation = new();
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
            _generation = new();
            _probeAdmission = null;
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
    /// <summary>
    /// 一次装配好的候选与熔断器。运行期热替换时整体换掉，读侧在一次调用内始终看同一份（#128）。
    /// 之所以合成一个不可变记录：候选顺序与熔断器必须同进同退，否则会出现「新候选配旧熔断器」的半新状态。
    /// </summary>
    private sealed record CandidateSet(
        IReadOnlyList<ProviderCandidate> Providers,
        IReadOnlyDictionary<string, ProviderCircuitBreaker> Breakers);

    private readonly Func<DateTimeOffset>? _clock;
    private readonly int _failureThreshold;
    private readonly TimeSpan? _cooldown;
    private readonly Action<ProviderCircuitSnapshot>? _onSnapshotChanged;

    /// <summary>volatile：热重建直接换整份候选表，读侧不加锁。</summary>
    private volatile CandidateSet _set = null!;

    public ProviderFailoverRunner(
        IEnumerable<ProviderCandidate> providers,
        Func<DateTimeOffset>? clock = null,
        int failureThreshold = 3,
        TimeSpan? cooldown = null,
        IEnumerable<ProviderCircuitSnapshot>? initialSnapshots = null,
        Action<ProviderCircuitSnapshot>? onSnapshotChanged = null)
    {
        _clock = clock;
        _failureThreshold = failureThreshold;
        _cooldown = cooldown;
        _onSnapshotChanged = onSnapshotChanged;
        var normalized = Normalize(providers);
        var breakers = normalized.ToDictionary(
            p => p.Id,
            p => new ProviderCircuitBreaker(p.Id, failureThreshold, cooldown, clock),
            StringComparer.Ordinal);

        foreach (var snapshot in initialSnapshots ?? Array.Empty<ProviderCircuitSnapshot>())
        {
            if (breakers.TryGetValue(snapshot.ProviderId, out var breaker))
            {
                // half-open 探测锁不会跨进程恢复；Restore 会主动清掉 ProbeInFlight。
                breaker.Restore(snapshot);
            }
        }

        _set = new CandidateSet(normalized, breakers);
    }

    /// <summary>
    /// 运行期替换候选集合与顺序（#128：面板改了供应商的启用/优先级/地址/协议后不必重启进程）。
    /// 三条不变量：
    /// ① 同名供应商复用原来的熔断器 —— 正在冷却的不会因为改配置被放出来；
    /// ② 只为新增/删除的 id 增删熔断器，已删除供应商的历史状态仍留在库里（不清表）；
    /// ③ 非法输入直接抛，调用方保持旧候选表（宁可继续用旧路由，也不进入"没有可用供应商"）。
    /// </summary>
    public void ReplaceCandidates(IEnumerable<ProviderCandidate> providers)
    {
        var normalized = Normalize(providers);
        var current = _set;
        var breakers = new Dictionary<string, ProviderCircuitBreaker>(normalized.Length, StringComparer.Ordinal);
        foreach (var provider in normalized)
        {
            breakers[provider.Id] = current.Breakers.TryGetValue(provider.Id, out var existing)
                ? existing
                : new ProviderCircuitBreaker(provider.Id, _failureThreshold, _cooldown, _clock);
        }

        _set = new CandidateSet(normalized, breakers);
    }

    private static ProviderCandidate[] Normalize(IEnumerable<ProviderCandidate> providers)
    {
        var normalized = (providers ?? throw new ArgumentNullException(nameof(providers)))
            .Where(p => p is not null)
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one provider is required.", nameof(providers));
        }

        if (normalized.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("Provider ids must be unique.", nameof(providers));
        }

        return normalized;
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
    {
        var set = _set;
        return set.Providers.Select(p => set.Breakers[p.Id].Snapshot()).ToArray();
    }

    public async Task<ProviderRunResult<T>> RunAsync<T>(
        Func<ProviderCandidate, CancellationToken, Task<ProviderCallResult<T>>> call,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        // 一次失败转移全程用同一份候选表：中途被热重建换掉也只会从下一次调用开始生效，
        // 否则可能对同一个 provider 取到两份不同的熔断器。
        var candidates = _set;
        var fallbackHops = 0;
        var attempted = false;
        string? lastReason = null;

        foreach (var provider in candidates.Providers)
        {
            ct.ThrowIfCancellationRequested();
            var breaker = candidates.Breakers[provider.Id];
            if (!provider.Enabled || !breaker.TryEnter(out _, out var admission))
            {
                continue;
            }

            try
            {
                attempted = true;
                ProviderCallResult<T> result;
                try
                {
                    result = await call(provider, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Publish(breaker.RecordHardFailure(admission));
                    lastReason = "provider_timeout";
                    fallbackHops++;
                    continue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    Publish(breaker.RecordHardFailure(admission));
                    lastReason = "provider_exception";
                    fallbackHops++;
                    continue;
                }

                if (result.Succeeded)
                {
                    Publish(breaker.RecordSuccess(admission));
                    return new ProviderRunResult<T>(true, result.Value, provider.Id, fallbackHops, "ok", false);
                }

                lastReason = result.ReasonCode ?? "provider_failure";
                if (result.HardFailure)
                {
                    Publish(breaker.RecordHardFailure(admission));
                }

                fallbackHops++;
            }
            finally
            {
                if (breaker.ReleaseProbe(admission!) is { } released) Publish(released);
            }
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

namespace BotAgent.Services.Ops;

/// <summary>
/// 每日热备（#121）：把 SQLite 用 <c>VACUUM INTO</c> 拷一份到 <c>data/backups/</c>，保留最近 <see cref="MaxRetained" /> 份。
///
/// 为什么要有它：<c>AppDatabase.VacuumIntoBackup</c> 早就写好了，但**全仓没有一个调用方** ——
/// 能力存在、没在跑，等于没有备份。部署机只有一块盘，坏一次就什么都没了。
///
/// 设计上的三条约束：
///   • **首次 tick 延后 <see cref="InitialDelay" />**：容器重启风暴里十几个实例同时 VACUUM 会把
///     SQLite 的写锁排满，先让服务站稳再说；
///   • **一天最多一份**：按本地日历日判断，不是按 24 小时滚动 —— 重启不会把“今天已经备过”忘掉
///     （备过的时间记在 meta 表里，见 <c>AppDatabase.GetMeta</c>）；
///   • **失败必须可见**：抛异常只记 WARN + 记到 <see cref="LastError" />，绝不让定时器把进程带走；
///     同时把失败原因塞进健康日报的告警段（<see cref="FailureSummary" />）。
/// </summary>
public sealed class DatabaseBackupService : IDisposable
{
    /// <summary>meta 表里的键：最近一次成功热备的 UTC 时间戳（毫秒）。</summary>
    public const string LastBackupMetaKey = "backup_last_success_utc_ms";

    /// <summary>meta 表里的键：最近一次失败原因（成功后会清空）。</summary>
    public const string LastFailureMetaKey = "backup_last_failure";

    /// <summary>保留几份备份（与 <c>VacuumIntoBackup</c> 的默认值一致）。</summary>
    public const int MaxRetained = 3;

    /// <summary>首次热备的延迟：避开容器重启风暴。</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);

    /// <summary>两次检查的间隔（真实执行还要求“今天还没备过”）。</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly Func<string> _backup;
    private readonly Func<long?> _lastSuccessUtcMs;
    private readonly Func<string?> _lastFailure;
    private readonly Action<bool, string?>? _record;
    private readonly Action<string> _log;

    private Timer? _timer;
    private int _disposed;
    private int _firing;

    /// <summary>热备是单任务：定时器、面板按钮、重启后的新实例都可能同时点火（#121 验收③）。</summary>
    private readonly object _runGate = new();
    private int _running;

    /// <param name="backup">真正干活的那一步（生产上就是 <c>AppDatabase.VacuumIntoBackup</c>）。</param>
    /// <param name="lastSuccessUtcMs">读“上次成功”的毫秒时间戳（生产上读 meta 表）。</param>
    /// <param name="lastFailure">读“上次失败原因”。</param>
    /// <param name="log">写日志（tag, message）。</param>
    /// <param name="record">把本次结果记下来（生产上写 meta 表；测试里可以为 null）。</param>
    public DatabaseBackupService(
        Func<string> backup,
        Func<long?> lastSuccessUtcMs,
        Func<string?> lastFailure,
        Action<string, string> log,
        Action<bool, string?>? record = null)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(lastSuccessUtcMs);
        ArgumentNullException.ThrowIfNull(lastFailure);
        ArgumentNullException.ThrowIfNull(log);
        _backup = backup;
        _lastSuccessUtcMs = lastSuccessUtcMs;
        _lastFailure = lastFailure;
        _record = record;
        _log = message => log("Backup", message);
    }

    /// <summary>最近一次成功热备的时刻（本地时区；没有 = null）。</summary>
    public DateTimeOffset? LastSuccessAt
    {
        get
        {
            var utcMs = SafeRead(_lastSuccessUtcMs);
            if (utcMs is not { } value || value <= 0)
            {
                return null;
            }

            return new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime, TimeSpan.Zero)
                .ToLocalTime();
        }
    }

    /// <summary>最近一次失败原因（没有 = null）。</summary>
    public string? LastError
    {
        get
        {
            var text = SafeRead(_lastFailure);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    /// <summary>健康日报的告警段用：备份失败 / 超过 2 天没成功过，各返回一句话。</summary>
    public string? FailureSummary(DateTimeOffset now)
    {
        var failure = LastError;
        var last = LastSuccessAt;
        var stale = last is null || (now - last.Value).TotalDays >= 2;
        if (!stale && failure is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (last is null) parts.Add("从没成功备份过");
        else if (stale) parts.Add($"备份已 {(int)(now - last.Value).TotalDays} 天没跑");
        if (failure is not null) parts.Add("上次失败：" + failure);
        return string.Join("；", parts);
    }

    /// <summary>此刻是否有一个热备任务正在跑（水位里能看，测试也靠它断言）。</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>起定时器（幂等：重复调用只重排一次）。</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        _timer?.Dispose();
        _timer = new Timer(_ => OnTimer(), null, InitialDelay, CheckInterval);
    }

    /// <summary>
    /// 立刻跑一次（面板按钮 / 测试用）。返回本次产出的备份路径；今天已经备过则返回已有那份，
    /// 不做第二次（<paramref name="force" /> = true 时强制再来一份）。
    /// </summary>
    public async Task<(bool Ok, string? Path, string? Error)> RunNowAsync(bool force = false, CancellationToken ct = default)
    {
        return await Task.Run(() => RunOnce(force), ct).ConfigureAwait(false);
    }

    /// <summary>今天（本地日历日）是否已经成功备过一份 —— 定时器每小时醒，但只有它为 false 才真的动手。</summary>
    private bool AlreadyBackedUpToday()
    {
        var today = DateOnly.FromDateTime(Clock.LocalDateTime);
        return LastSuccessAt is { } previous && DateOnly.FromDateTime(previous.LocalDateTime) == today;
    }

    private (bool Ok, string? Path, string? Error) RunOnce(bool force = false)
    {
        // 单任务 + 去重（#121）：定时器、面板按钮、测试三条入口必须走同一套判定。
        // 去重放在这里而不是 RunNowAsync，否则「每小时检查」会退化成「每小时备一份」，
        // 保留 3 份就只剩 3 小时历史；单任务靠 _runGate 把并发调用排队，VACUUM 不必互相抢写锁。
        lock (_runGate)
        {
            if (!force && AlreadyBackedUpToday())
            {
                return (true, null, null);
            }

            Interlocked.Exchange(ref _running, 1);
            try
            {
                var path = _backup();
                _record?.Invoke(true, null);
                _log($"每日热备完成：{path}");
                return (true, path, null);
            }
            catch (Exception exception)
            {
                var reason = exception.GetType().Name + "：" + exception.Message;
                _record?.Invoke(false, reason);
                _log("每日热备失败：" + reason);
                return (false, null, reason);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }

    private void OnTimer()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (Interlocked.Exchange(ref _firing, 1) == 1)
        {
            return;
        }

        try
        {
            var (ok, _, error) = RunOnce();
            if (!ok)
            {
                _log("本轮跳过（下次检查再试）：" + error);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _firing, 0);
        }
    }

    private T? SafeRead<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            _log("读备份状态失败（不影响定时器）：" + exception.Message);
            return default;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _timer?.Dispose();
        _timer = null;
    }
}

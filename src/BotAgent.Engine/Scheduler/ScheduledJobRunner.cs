using BotAgent.Domain.Scheduler;
using BotAgent.Services.Scheduler;

namespace BotAgent.Engine.Scheduler;

/// <summary>
/// 单 Timer 防漂移调度运行器（沿用 MaxSleep = 1h 策略，严格通过注入时钟计算到期）：
/// 支持重启过期防风暴（900s 窗口）、幂等运行槽抢占、QQ 官方被动边界拦截。
/// </summary>
public sealed class ScheduledJobRunner : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    private readonly IScheduledJobStore _store;
    private readonly IScheduledDeliveryPort _delivery;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    private Timer? _timer;
    private CancellationTokenSource? _shutdownCts;
    private Task? _activeTickTask;
    private bool _started;
    private bool _disposed;
    private bool _rerunRequested;

    public ScheduledJobRunner(
        IScheduledJobStore store,
        IScheduledDeliveryPort delivery,
        Func<DateTimeOffset>? utcNow = null,
        Action<string>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _utcNow = utcNow ?? (Func<DateTimeOffset>)(() => BotAgent.Engine.Runtime.EngineClock.UtcNow);
        _log = log ?? (_ => { });
    }

    public bool IsRunning { get { lock (_gate) return _started && !_disposed; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
            _shutdownCts = new CancellationTokenSource();
        }
        _ = RunTickLoopAsync();
    }

    public void NotifyScheduleChanged()
    {
        lock (_gate)
        {
            if (_disposed || !_started) return;
        }
        ArmTimer();
    }

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _utcNow();
            var nowUnix = now.ToUnixTimeSeconds();
            var snapshot = _store.ReadSnapshot();
            var processed = 0;

            foreach (var job in snapshot.Jobs.Where(j => j.Enabled && j.NextFireUnix <= nowUnix).OrderBy(j => j.NextFireUnix))
            {
                if (ct.IsCancellationRequested) break;
                var scheduledUnix = job.NextFireUnix;
                var runKey = $"{job.JobId}:{scheduledUnix}";

                if (ScheduledJobValidator.IsQqOfficialScope(job.ScopeKey))
                {
                    var nextOfficial = ComputeNextUnix(job, now);
                    _store.RecordSkippedRun(runKey, job.JobId, scheduledUnix, nowUnix, "passive_trigger_required",
                        nextOfficial, disableJob: job.Kind == ScheduledJobKind.OnceReminder || !nextOfficial.HasValue);
                    continue;
                }

                var isMissed = nowUnix - scheduledUnix > SchedulerLimits.MissedWindowSeconds;
                var nextAfterCurrent = ComputeNextUnix(job, now > DateTimeOffset.FromUnixTimeSeconds(scheduledUnix)
                    ? now
                    : DateTimeOffset.FromUnixTimeSeconds(scheduledUnix));
                var disableAfter = job.Kind == ScheduledJobKind.OnceReminder || !nextAfterCurrent.HasValue;

                if (isMissed && job.MissedPolicy is MissedFirePolicy.SkipSilent or MissedFirePolicy.FireOnce15m)
                {
                    _store.RecordSkippedRun(runKey, job.JobId, scheduledUnix, nowUnix,
                        job.MissedPolicy == MissedFirePolicy.FireOnce15m ? "missed_window_expired" : "missed_skip_silent",
                        nextAfterCurrent, disableAfter);
                    continue;
                }

                if (!_store.TryClaimRunSlot(job.JobId, scheduledUnix, nowUnix, nextAfterCurrent, disableAfter, out var claimedKey))
                    continue;

                processed++;
                string? missedNotice = null;
                if (isMissed && job.MissedPolicy == MissedFirePolicy.SkipAndNotify)
                {
                    missedNotice = "错过原定触发窗口（离线超过15分钟），已跳过积压历史触发并恢复后续调度。";
                }

                try
                {
                    var result = await _delivery.DeliverAsync(job, scheduledUnix, missedNotice, ct).ConfigureAwait(false);
                    var finishUnix = _utcNow().ToUnixTimeSeconds();
                    var status = string.IsNullOrWhiteSpace(result.Status)
                        ? (result.Ok ? ScheduledDeliveryStatus.Succeeded : ScheduledDeliveryStatus.Failed)
                        : result.Status;
                    var incrementFailure = status is ScheduledDeliveryStatus.Failed or ScheduledDeliveryStatus.Unknown;
                    _store.CompleteRun(claimedKey, status, result.ReasonCode, finishUnix, incrementFailure);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _store.CompleteRun(claimedKey, ScheduledDeliveryStatus.Unknown, "cancelled", _utcNow().ToUnixTimeSeconds(), true);
                    break;
                }
                catch (Exception ex)
                {
                    _log($"[Scheduler] 投递异常（{job.JobId}）: {ex.GetType().Name}");
                    _store.CompleteRun(claimedKey, ScheduledDeliveryStatus.Failed, ex.GetType().Name, _utcNow().ToUnixTimeSeconds(), true);
                }
            }

            ArmTimer();
            return processed;
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private Task RunTickLoopAsync()
    {
        Task? loopTask;
        lock (_gate)
        {
            if (_disposed || _shutdownCts is null) return Task.CompletedTask;
            if (_activeTickTask is { IsCompleted: false } existing)
            {
                _rerunRequested = true;
                return existing;
            }
            var ct = _shutdownCts.Token;
            loopTask = RunTickBodyAsync(ct);
            _activeTickTask = loopTask;
        }
        return loopTask;
    }

    private async Task RunTickBodyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log($"[Scheduler] 调度巡检异常: {ex.GetType().Name}");
                ArmTimer();
            }
            lock (_gate)
            {
                if (!_rerunRequested || _disposed)
                {
                    _rerunRequested = false;
                    break;
                }
                _rerunRequested = false;
            }
        }
    }

    private void ArmTimer()
    {
        lock (_gate)
        {
            if (_disposed || !_started) return;
        }
        var now = _utcNow();
        var nowUnix = now.ToUnixTimeSeconds();
        TimeSpan delay;
        try
        {
            var nextJob = _store.ReadSnapshot().Jobs
                .Where(j => j.Enabled)
                .OrderBy(j => j.NextFireUnix)
                .FirstOrDefault();
            if (nextJob is null)
            {
                delay = MaxSleep;
            }
            else
            {
                var diffSec = Math.Max(1L, nextJob.NextFireUnix - nowUnix);
                var candidate = TimeSpan.FromSeconds(diffSec);
                delay = candidate > MaxSleep ? MaxSleep : candidate;
            }
        }
        catch
        {
            delay = TimeSpan.FromMinutes(1);
        }

        lock (_gate)
        {
            if (_disposed || !_started) return;
            _timer?.Dispose();
            _timer = new Timer(_ => _ = RunTickLoopAsync(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private static long? ComputeNextUnix(ScheduledJobRecord job, DateTimeOffset afterUtc)
    {
        if (job.Kind == ScheduledJobKind.OnceReminder)
            return null;
        if (!CronSchedule.TryParse(job.CronExpr, out var cron, out _) || cron is null)
            return null;
        if (!CronSchedule.TryResolveTimeZone(job.TimezoneIana, out var tz))
            tz = TimeZoneInfo.Utc;
        return cron.TryGetNextOccurrence(afterUtc, tz, out var next) ? next.ToUnixTimeSeconds() : null;
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Timer? timer;
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            timer = _timer;
            _timer = null;
            cts = _shutdownCts;
            _shutdownCts = null;
        }
        timer?.Dispose();
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? inflight;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _rerunRequested = false;
            inflight = _activeTickTask;
        }
        Stop();
        var drained = true;
        if (inflight is not null && !inflight.IsCompleted)
        {
            try
            {
                var finished = await Task.WhenAny(inflight, Task.Delay(5000)).ConfigureAwait(false);
                drained = ReferenceEquals(finished, inflight);
            }
            catch { }
        }
        if (drained) _tickGate.Dispose();
    }

    public void Dispose()
    {
        Task? inflight;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _rerunRequested = false;
            inflight = _activeTickTask;
        }
        Stop();
        var drained = true;
        if (inflight is not null && !inflight.IsCompleted)
        {
            try { drained = inflight.Wait(2000); }
            catch { }
        }
        if (drained) _tickGate.Dispose();
    }
}

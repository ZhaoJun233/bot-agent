using System.Globalization;
using BotAgent.Services.Ops;

namespace BotAgent.Packages.HealthReporting;

/// <summary>
/// Package-owned daily health report runtime coordinator: owns the Beijing-time timer,
/// daily-once deduplication, full multi-section report rendering, preview, and send orchestration.
/// Liveness <c>/healthz</c> remains strictly host-owned.
/// </summary>
public sealed class HealthReportCoordinator : IHealthReportingRuntimeContribution, IDisposable
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);
    private const int MaxReportChars = 800;
    private static readonly TimeZoneInfo BeijingTimeZone = ResolveBeijingTimeZone();

    private readonly HealthReportingEngine _engine;
    private readonly IHealthReportHostPorts? _ports;
    private readonly Func<DateTimeOffset>? _utcNow;
    private readonly object _lifecycleGate = new();

    private Timer? _timer;
    private CancellationTokenSource? _shutdownCts;
    private Task? _activeFire;
    private bool _disposed;
    private bool _started;
    private DateTimeOffset? _nextRunAt;
    private string _knownTime = string.Empty;
    private string _knownTargets = string.Empty;
    private bool _knownEnabled;
    private DateOnly? _lastSentDay;
    private DateTimeOffset? _lastSentAt;
    private string? _lastError;
    private int _sentCount;

    public HealthReportCoordinator(
        HealthReportingEngine? engine = null,
        IHealthReportHostPorts? ports = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _engine = engine ?? new HealthReportingEngine();
        _ports = ports;
        _utcNow = utcNow;
        if (_ports is not null)
        {
            var initial = _ports.ReadSettings();
            _knownTime = initial.TimeText;
            _knownTargets = initial.TargetsRaw;
            _knownEnabled = initial.Enabled;
        }
    }

    public DateTimeOffset? LastSentAt { get { lock (_lifecycleGate) return _lastSentAt; } }

    public string? LastError { get { lock (_lifecycleGate) return _lastError; } }

    public int SentCount { get { lock (_lifecycleGate) return _sentCount; } }

    public bool HasActiveTimer { get { lock (_lifecycleGate) return _timer is not null; } }

    public DateTimeOffset? NextRunAt
    {
        get
        {
            if (_ports is null)
            {
                return null;
            }

            var settings = _ports.ReadSettings();
            if (!settings.Enabled || ParseTargets(settings.TargetsRaw).Length == 0)
            {
                return null;
            }

            DateOnly? lastSentDay;
            lock (_lifecycleGate) lastSentDay = _lastSentDay;
            return _engine.ComputeNextRunBeijing(NowUtc(), settings.TimeText, lastSentDay);
        }
    }

    public DateTimeOffset ComputeNextRunBeijing(DateTimeOffset nowUtc, string hhmm, DateOnly? lastSentDay)
        => _engine.ComputeNextRunBeijing(nowUtc, hhmm, lastSentDay);

    public string FormatSummaryReport(HealthMetricsSnapshot snapshot, DateTimeOffset beijingNow, int maxChars = 800)
        => _engine.FormatSummaryReport(snapshot, beijingNow, maxChars);

    public bool CanSendToday(DateOnly todayBeijing, DateOnly? lastSentDay)
        => _engine.CanSendToday(todayBeijing, lastSentDay);

    public void Start()
    {
        if (_ports is null)
        {
            return;
        }

        var settings = _ports.ReadSettings();
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _started = true;
            _shutdownCts ??= new CancellationTokenSource();
            _knownTime = settings.TimeText;
            _knownTargets = settings.TargetsRaw;
            _knownEnabled = settings.Enabled;
        }

        var (hour, minute) = ParseClock(settings.TimeText);
        var targets = ParseTargets(settings.TargetsRaw);
        _ports.LogInfo(
            $"健康日报：{(settings.Enabled && targets.Length > 0 ? $"每天 {hour:00}:{minute:00}（北京时间）推给 {string.Join("/", targets)}" : "未启用（面板「服务器健康日报」里可开）")}");

        if (settings.Enabled && targets.Length > 0)
        {
            Arm();
        }
    }

    public void Reapply()
    {
        if (_ports is null)
        {
            return;
        }

        var settings = _ports.ReadSettings();
        var shouldArm = settings.Enabled && ParseTargets(settings.TargetsRaw).Length > 0;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            var unchanged = settings.TimeText == _knownTime
                && settings.TargetsRaw == _knownTargets
                && settings.Enabled == _knownEnabled;
            if (unchanged && (!shouldArm || _timer is not null))
            {
                return;
            }

            _knownTime = settings.TimeText;
            _knownTargets = settings.TargetsRaw;
            _knownEnabled = settings.Enabled;
            if (shouldArm)
            {
                _started = true;
                _shutdownCts ??= new CancellationTokenSource();
            }
        }

        Arm();
    }

    public void Stop()
    {
        Timer? previous;
        lock (_lifecycleGate)
        {
            _started = false;
            previous = _timer;
            _timer = null;
            _nextRunAt = null;
        }

        previous?.Dispose();
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        Timer? previous;
        CancellationTokenSource? cts;
        Task? active;
        lock (_lifecycleGate)
        {
            _disposed = true;
            _started = false;
            previous = _timer;
            _timer = null;
            _nextRunAt = null;
            cts = _shutdownCts;
            _shutdownCts = null;
            active = _activeFire;
        }

        previous?.Dispose();
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        if (active is not null)
        {
            try { await active.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { }
        }

        cts?.Dispose();
    }

    public void Dispose()
    {
        Timer? previous;
        CancellationTokenSource? cts;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _started = false;
            previous = _timer;
            _timer = null;
            _nextRunAt = null;
            cts = _shutdownCts;
            _shutdownCts = null;
        }

        previous?.Dispose();
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    public async Task<string> PreviewAsync(CancellationToken ct)
    {
        if (_ports is null)
        {
            return string.Empty;
        }

        var (text, _) = await BuildReportAsync(null, ct).ConfigureAwait(false);
        return text;
    }

    public async Task<HealthReportSendResult> SendNowAsync(string reason, DateTimeOffset? reportTime, CancellationToken ct)
    {
        if (_ports is null)
        {
            return new(false, string.Empty, "健康日报宿主端口未绑定");
        }

        var settings = _ports.ReadSettings();
        var targets = ParseTargets(settings.TargetsRaw);
        if (targets.Length == 0)
        {
            const string noTarget = "没有配置收件人（QQ 号）";
            lock (_lifecycleGate) _lastError = noTarget;
            _ports.LogWarn($"健康日报未发送：{noTarget}");
            return new(false, string.Empty, noTarget);
        }

        var (text, _) = await BuildReportAsync(reportTime, ct).ConfigureAwait(false);
        var failures = new List<string>();
        var skipped = new List<string>();
        var sentThisRun = 0;

        foreach (var uid in targets)
        {
            try
            {
                var blockReason = _ports.CheckPlatformBlockReason(uid);
                if (blockReason is not null)
                {
                    var tag = _ports.IsOfficialAlias(uid) ? "官方" : "私域";
                    skipped.Add($"{uid}（{tag}{blockReason}）");
                    _ports.LogInfo($"健康日报跳过 {uid}：{tag}{blockReason}（{reason}）");
                    continue;
                }

                var ok = await _ports.SendPrivateTextAsync(uid, text, ct).ConfigureAwait(false);
                if (ok)
                {
                    var sentAt = NowBeijing();
                    lock (_lifecycleGate)
                    {
                        _sentCount++;
                        _lastSentAt = sentAt;
                    }
                    sentThisRun++;
                    _ports.LogInfo($"健康日报已私聊发给 {uid}（{reason}，{text.Length} 字）");
                }
                else
                {
                    failures.Add($"{uid}：协议端返回失败");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{uid}：{ex.Message}");
            }
        }

        var error = failures.Count == 0 ? null : string.Join("；", failures);
        lock (_lifecycleGate) _lastError = error;
        if (error is not null)
        {
            _ports.LogWarn($"健康日报发送失败：{error}");
        }

        if (skipped.Count > 0 && failures.Count == 0 && sentThisRun == 0)
        {
            var note = "所有收件人所在平台均已停用或聊天静音，本次没发：" + string.Join("；", skipped);
            lock (_lifecycleGate) _lastError = note;
            _ports.LogWarn($"健康日报未发送：{note}");
            return new(false, text, note);
        }

        return new(error is null, text, error);
    }

    public long[] ParseTargets(string? raw)
    {
        var list = new List<long>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var pieces = raw.Split(
            new[] { ',', '，', ';', '；', '、', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var piece in pieces)
        {
            var digits = new string(piece.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 5 and <= 16 &&
                long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id > 0 && !list.Contains(id))
            {
                list.Add(id);
            }
        }

        return list.ToArray();
    }

    private void Arm()
    {
        if (_ports is null)
        {
            return;
        }

        var settings = _ports.ReadSettings();
        var targets = ParseTargets(settings.TargetsRaw);
        var nowUtc = NowUtc();
        var nowBeijing = ToBeijing(nowUtc);

        Timer? previous;
        lock (_lifecycleGate)
        {
            previous = _timer;
            _timer = null;

            if (_disposed || !_started || !settings.Enabled || targets.Length == 0)
            {
                _nextRunAt = null;
            }
            else
            {
                var target = _engine.ComputeNextRunBeijing(nowUtc, settings.TimeText, _lastSentDay);
                _nextRunAt = target;

                var delay = target - nowBeijing;
                if (delay < TimeSpan.FromSeconds(1))
                {
                    delay = TimeSpan.FromSeconds(1);
                }
                else if (delay > MaxSleep)
                {
                    delay = MaxSleep;
                }

                _timer = new Timer(_ => OnTimer(), null, delay, Timeout.InfiniteTimeSpan);
            }
        }

        previous?.Dispose();
    }

    private void OnTimer()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || !_started || _activeFire is not null)
            {
                return;
            }

            var ct = (_shutdownCts ??= new CancellationTokenSource()).Token;
            _activeFire = FireAsync(ct);
        }
    }

    private async Task FireAsync(CancellationToken ct)
    {
        var shouldRearm = false;
        try
        {
            if (_ports is null || ct.IsCancellationRequested)
            {
                return;
            }

            var now = NowBeijing();
            DateTimeOffset? due;
            DateOnly? lastSentDay;
            lock (_lifecycleGate)
            {
                if (_disposed || !_started)
                {
                    return;
                }

                due = _nextRunAt;
                lastSentDay = _lastSentDay;
            }

            if (due is { } scheduledDue)
            {
                if (now < scheduledDue.AddSeconds(-5))
                {
                    shouldRearm = true;
                    return;
                }

                if (now < scheduledDue)
                {
                    var remain = scheduledDue - now + TimeSpan.FromMilliseconds(50);
                    if (remain > TimeSpan.Zero && remain <= TimeSpan.FromSeconds(5))
                    {
                        await Task.Delay(remain, ct).ConfigureAwait(false);
                        now = NowBeijing();
                    }
                }
            }

            var today = DateOnly.FromDateTime(now.DateTime);
            if (!_engine.CanSendToday(today, lastSentDay))
            {
                shouldRearm = true;
                return;
            }

            var settings = _ports.ReadSettings();
            if (!settings.Enabled || ParseTargets(settings.TargetsRaw).Length == 0)
            {
                shouldRearm = true;
                return;
            }

            DateTimeOffset? scheduledTime;
            lock (_lifecycleGate)
            {
                if (_disposed || !_started || !_engine.CanSendToday(today, _lastSentDay))
                {
                    shouldRearm = !_disposed && _started;
                    return;
                }

                scheduledTime = _nextRunAt;
                _lastSentDay = today;
            }

            await SendNowAsync("定时推送", scheduledTime, ct).ConfigureAwait(false);
            shouldRearm = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            shouldRearm = false;
        }
        catch (Exception ex)
        {
            _ports?.LogWarn($"健康日报定时任务异常：{ex.GetType().Name} {ex.Message}");
            shouldRearm = !ct.IsCancellationRequested;
        }
        finally
        {
            var canArm = false;
            lock (_lifecycleGate)
            {
                _activeFire = null;
                canArm = shouldRearm && !_disposed && _started;
            }

            if (canArm)
            {
                Arm();
            }
        }
    }

    private async Task<(string Text, int Warnings)> BuildReportAsync(DateTimeOffset? reportTime, CancellationToken ct)
    {
        var now = reportTime ?? NowBeijing();
        var settings = _ports!.ReadSettings();
        var snap = await _ports.CollectRuntimeSnapshotAsync(now, ct).ConfigureAwait(false);
        var warnings = new List<string>();
        var lines = new List<string> { $"🩺 服务器健康日报 · {now:MM-dd HH:mm}" };

        var uptime = FormatUptime(now - snap.StartedAt);
        lines.Add($"机器人：运行 {uptime}｜会话 {snap.ConversationCount} 个｜在途 {snap.InFlightReplies}｜排队 {snap.QueuedReplies}");

        var uin = string.IsNullOrWhiteSpace(settings.NormalizedUin)
            ? (snap.SelfId > 0 ? snap.SelfId.ToString(CultureInfo.InvariantCulture) : "未识别")
            : settings.NormalizedUin;
        var connection = snap.ProtocolConnected ? "协议端已连接" : "协议端未连接";
        if (!snap.ProtocolConnected)
        {
            warnings.Add("协议端未连接 —— 消息收发都停了，看看 NapCat 容器还在不在");
        }

        var online = snap.AccountOnline switch
        {
            true => "QQ 在线",
            false => "⚠ QQ 离线",
            _ => "QQ 状态未知"
        };
        if (snap.AccountOnline is false)
        {
            warnings.Add("QQ 账号离线（被顶号或登录失效）—— 打开面板扫一下二维码就能回来");
        }

        lines.Add($"QQ：{uin} {online}｜{connection}");

        var model = string.IsNullOrWhiteSpace(settings.ModelName) ? "未配置" : settings.ModelName;
        var lastText = snap.LastGenerationMs > 0 ? $"，最近一次 {snap.LastGenerationMs / 1000.0:F1}s" : string.Empty;
        lines.Add(snap.ModelProbeOk
            ? $"模型：{model} 可达{lastText}"
            : $"模型：{model} ⚠ 接口不通（{snap.ModelProbeNote}）{lastText}");
        if (!snap.ModelProbeOk)
        {
            warnings.Add($"模型接口不通：{snap.ModelProbeNote}");
        }

        if (snap.VoiceChecked)
        {
            if (snap.VoiceHealthy)
            {
                lines.Add("语音：TTS 正常");
            }
            else if (snap.VoiceError is not null && snap.VoiceError.StartsWith("probe_exception:", StringComparison.Ordinal))
            {
                lines.Add($"语音：⚠ TTS 探测失败（{snap.VoiceError["probe_exception:".Length..]}）");
            }
            else
            {
                var err = Trim(snap.VoiceError, 60);
                lines.Add($"语音：⚠ TTS 异常（{err}）");
                warnings.Add($"TTS 服务异常：{err}");
            }
        }

        var parts = new List<string>();
        if (snap.MemoryUsedBytes is { } memUsed)
        {
            parts.Add(snap.MemoryLimitBytes is { } limit
                ? $"内存 {Mb(memUsed)}/{Mb(limit)}"
                : $"内存 {Mb(memUsed)}");
        }

        if (snap.LoadAverage is { } load)
        {
            parts.Add($"负载 {load:F2}");
        }

        if (snap.DiskFreeBytes is { } free && snap.DiskTotalBytes is { } total)
        {
            var usedPercent = total > 0 ? (int)Math.Round((total - free) * 100.0 / total) : 0;
            parts.Add($"磁盘剩 {Mb(free)}（{usedPercent}% 已用）");
            if (free < 500L * 1024 * 1024)
            {
                warnings.Add($"磁盘只剩 {Mb(free)}（{usedPercent}% 已用）—— 先 docker builder prune -af，别等它写满");
            }
        }

        if (parts.Count > 0)
        {
            lines.Add("资源：" + string.Join("｜", parts));
        }

        var logParts = new List<string>();
        if (snap.LogFileSizeBytes is { } size)
        {
            logParts.Add($"{size / 1024.0 / 1024.0:F1} MB");
        }

        logParts.Add($"近 1 小时告警 {snap.RecentWarnCount} 条");
        lines.Add("日志：" + string.Join("｜", logParts));
        if (snap.RecentWarnCount > 0 && snap.LastWarnText is { Length: > 0 })
        {
            warnings.Add("最近告警：" + Trim(snap.LastWarnText, 70));
        }

        if (snap.HasBackups)
        {
            var backupText = snap.LastBackupAt is { } lastBackup
                ? $"最近一次 {TimeZoneInfo.ConvertTime(lastBackup, BeijingTimeZone):MM-dd HH:mm}"
                : "还没有成功过";
            if (snap.LastBackupError is { Length: > 0 } backupFailure)
            {
                backupText += $"｜⚠ {Trim(backupFailure, 60)}";
            }

            lines.Add("备份：" + backupText);
        }

        if (snap.BackupFailureSummary is { Length: > 0 } backupWarning)
        {
            warnings.Add(backupWarning);
        }

        foreach (var warning in warnings.Take(4))
        {
            lines.Add("⚠️ " + warning);
        }

        var text = string.Join("\n", lines);
        if (text.Length > MaxReportChars)
        {
            text = text[..MaxReportChars] + "…";
        }

        return (text, warnings.Count);
    }

    private DateTimeOffset NowUtc()
        => _ports?.UtcNow ?? _utcNow?.Invoke() ?? DateTimeOffset.UnixEpoch;

    private DateTimeOffset NowBeijing()
        => ToBeijing(NowUtc());

    private static DateTimeOffset ToBeijing(DateTimeOffset value)
        => TimeZoneInfo.ConvertTime(value, BeijingTimeZone);

    private static (int Hour, int Minute) ParseClock(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var parts = text.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute)
            && hour is >= 0 and <= 23
            && minute is >= 0 and <= 59)
        {
            return (hour, minute);
        }

        return (9, 0);
    }

    private static TimeZoneInfo ResolveBeijingTimeZone()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // try next
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "北京时间", "北京时间");
    }

    private static string FormatUptime(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
            : $"{Math.Max(1, (int)span.TotalMinutes)} 分";
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F0} MB";

    private static string Trim(string? text, int max)
    {
        var value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }
}

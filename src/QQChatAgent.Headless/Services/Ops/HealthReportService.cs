using System.Diagnostics;
using System.Globalization;
using System.Text;
using QQChatAgent.Services;
using QQChatAgent.Services.Qq;

namespace QQChatAgent.Services.Ops;

/// <summary>
/// 服务器健康日报：每天在指定时刻（**北京时间**）给指定 QQ 私聊发一条机器人与服务器状态。
///
/// ──────────────────────────────────────────────────────────────────────
/// 为什么长这样（两个都是号主明确要求）：
///   1. **不经过外部设备 agent**：那台电脑可能根本没开。这条链路只用机器人自己 + 协议端：
///      自己的定时器 → 自己采集状态（内存/磁盘/日志/QQ 在线/模型接口/TTS）→ OneBot 私聊消息。
///      所以整条链路没有 subprocess、没有 ssh、没有桥，容器一活它就能跑。
///   2. **按北京时间算，不看容器 TZ**：号主在国内，容器时区怎么设都不该让 18:00 漂成别的点。
///      时刻一律用 <see cref="BeijingTimeZone" />（tzdata 缺失时退化成固定 UTC+8，见 <see cref="ResolveBeijingTimeZone" />）。
///
/// 调度方式：不是“每 N 秒轮询一次到点没有”，而是**算出下一次的时刻、把定时器排到那一刻**
/// （到点即发，秒级准确；也省掉了每天 86400/N 次的无用唤醒）。两个细节：
///   • 等待超过 1 小时就分段重排 —— 系统时间被改过、或容器被挂起一段时间之后能自动纠偏；
///   • 同一天绝不发第二条（<see cref="_lastSentDay" />）：定时器重建 / 设置重存都不会刷屏。
/// 进程在当天推送时刻之后才启动时**不补发**（下一次是明天那个点）—— 重启多的时候补发就成了刷屏。
/// ──────────────────────────────────────────────────────────────────────
/// </summary>
public sealed class HealthReportService : IDisposable
{
    /// <summary>定时器最长一次只等这么久，到点再重排（应对改系统时间 / 长时间挂起）。</summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    /// <summary>推文长度上限：一条 QQ 消息，别把日报写成论文。</summary>
    private const int MaxReportChars = 800;

    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly TimeZoneInfo BeijingTimeZone = ResolveBeijingTimeZone();

    private readonly AppSettings _settings;
    private readonly BotAgent _agent;
    private readonly IQqChatSource _source;

    private Timer? _timer;
    private int _disposed;
    private int _firing;
    private DateTimeOffset? _nextRunAt;

    // 时间/收件人/开关的“上次已知值”：设置变了才重排定时器（面板保存任何字段都会走到 Reapply）
    private string _knownTime;
    private string _knownTargets;
    private bool _knownEnabled;

    private DateOnly? _lastSentDay;

    public HealthReportService(AppSettings settings, BotAgent agent, IQqChatSource source)
    {
        _settings = settings;
        _agent = agent;
        _source = source;
        _knownTime = settings.HealthReportTime;
        _knownTargets = settings.HealthReportTargets;
        _knownEnabled = settings.HealthReportEnabled;
        StartedAt = ResolveProcessStart();
    }

    /// <summary>进程启动时间（用来算“已运行多久”）。</summary>
    private DateTimeOffset StartedAt { get; }

    /// <summary>最近一次成功发出的时间（面板显示用）。</summary>
    public DateTimeOffset? LastSentAt { get; private set; }

    /// <summary>最近一次发送失败的原因（成功一次就清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>累计发出去几条（排障用）。</summary>
    public int SentCount { get; private set; }

    /// <summary>下一次推送时刻（北京时间带 +08:00 偏移）；未启用 / 没收件人时为 null。</summary>
    public DateTimeOffset? NextRunAt
    {
        get
        {
            if (!_settings.HealthReportEnabled)
            {
                return null;
            }

            return ParseTargets(_settings.HealthReportTargets).Count == 0 ? null : NextRunAfter(NowBeijing());
        }
    }

    /// <summary>面板显示用的时刻文本（已归一化）。</summary>
    public string TimeText => _settings.HealthReportTime;

    /// <summary>启动（常驻进程里调一次）。</summary>
    public void Start()
    {
        var (hour, minute) = AppSettings.ParseHealthReportClock(_settings.HealthReportTime);
        var targets = ParseTargets(_settings.HealthReportTargets);
        FileLog.Write("Health",
            $"健康日报：{(Enabled && targets.Count > 0 ? $"每天 {hour:00}:{minute:00}（北京时间）推给 {string.Join("/", targets)}" : "未启用（面板「服务器健康日报」里可开）")}");

        if (Enabled && targets.Count > 0)
        {
            Arm();
        }
    }

    /// <summary>
    /// 面板保存了设置之后调它：只有开关/时刻/收件人真的变了才重排定时器。
    /// 不重排的话，改了时刻要重启才生效 —— 这条踩过（静默兜底/画像巡检的定时器一个样）。
    /// </summary>
    public void Reapply()
    {
        if (_disposed == 1)
        {
            return;
        }

        var time = _settings.HealthReportTime;
        var targets = _settings.HealthReportTargets;
        var enabled = _settings.HealthReportEnabled;
        if (time == _knownTime && targets == _knownTargets && enabled == _knownEnabled)
        {
            return;
        }

        _knownTime = time;
        _knownTargets = targets;
        _knownEnabled = enabled;
        Arm();
    }

    /// <summary>只生成不发送（面板「预览」用；不会碰 QQ）。</summary>
    public async Task<string> PreviewAsync()
    {
        var (text, _) = await BuildReportAsync();
        return text;
    }

    /// <summary>
    /// 立刻发一条（面板「现在发一条」/ 定时器到点都走这里）。
    /// 返回：(是否全部发出去了, 发的内容, 失败原因)。
    /// </summary>
    public async Task<(bool Ok, string Text, string? Error)> SendNowAsync(string reason)
    {
        var targets = ParseTargets(_settings.HealthReportTargets);
        if (targets.Count == 0)
        {
            const string noTarget = "没有配置收件人（QQ 号）";
            LastError = noTarget;
            FileLog.Warn("Health", $"健康日报未发送：{noTarget}");
            return (false, string.Empty, noTarget);
        }

        var (text, _) = await BuildReportAsync();
        var failures = new List<string>();

        foreach (var uid in targets)
        {
            try
            {
                var result = await _source.SendTextAsync(false, uid, text);
                if (result.Ok)
                {
                    SentCount++;
                    LastSentAt = NowBeijing();
                    FileLog.Write("Health", $"健康日报已私聊发给 {uid}（{reason}，{text.Length} 字）");
                }
                else
                {
                    failures.Add($"{uid}：协议端返回失败");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{uid}：{ex.Message}");
            }
        }

        var error = failures.Count == 0 ? null : string.Join("；", failures);
        LastError = error;
        if (error is not null)
        {
            FileLog.Warn("Health", $"健康日报发送失败：{error}");
        }

        return (error is null, text, error);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _timer?.Dispose();
        _timer = null;
    }

    // ══════════════ 调度 ══════════════

    private bool Enabled => _settings.HealthReportEnabled;

    /// <summary>把定时器排到「下一次该发的时刻」。没启用 / 没收件人 → 不排（并清掉下次时刻）。</summary>
    private void Arm()
    {
        _timer?.Dispose();
        _timer = null;

        if (_disposed == 1 || !Enabled || ParseTargets(_settings.HealthReportTargets).Count == 0)
        {
            _nextRunAt = null;
            return;
        }

        var now = NowBeijing();
        var target = NextRunAfter(now);
        _nextRunAt = target;

        var delay = target - now;
        if (delay < TimeSpan.FromSeconds(1))
        {
            delay = TimeSpan.FromSeconds(1); // 兜底：绝不排成 0（会变成忙循环）
        }
        else if (delay > MaxSleep)
        {
            delay = MaxSleep;
        }

        _timer = new Timer(_ => OnTimer(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        _ = FireAsync();
    }

    private async Task FireAsync()
    {
        if (Interlocked.Exchange(ref _firing, 1) == 1)
        {
            return;
        }

        try
        {
            var now = NowBeijing();

            // 定时器提前醒了（改过系统时间 / 从挂起恢复）→ 不发，重排一次
            if (_nextRunAt is { } due && now < due.AddSeconds(-5))
            {
                Arm();
                return;
            }

            var today = DateOnly.FromDateTime(now.DateTime);
            if (_lastSentDay == today)
            {
                Arm();
                return;
            }

            if (!Enabled || ParseTargets(_settings.HealthReportTargets).Count == 0)
            {
                Arm();
                return;
            }

            _lastSentDay = today; // 先标记：发失败也不每轮重试（失败原因进日志与面板）
            await SendNowAsync("定时推送");
            Arm();
        }
        catch (Exception ex)
        {
            FileLog.Warn("Health", $"健康日报定时任务异常：{ex.GetType().Name} {ex.Message}");
            Arm();
        }
        finally
        {
            Interlocked.Exchange(ref _firing, 0);
        }
    }

    /// <summary>下一次推送时刻：今天那个点还没到就是今天，否则明天。</summary>
    private DateTimeOffset NextRunAfter(DateTimeOffset nowBeijing)
    {
        var (hour, minute) = AppSettings.ParseHealthReportClock(_settings.HealthReportTime);
        var today = new DateTimeOffset(
            nowBeijing.Year, nowBeijing.Month, nowBeijing.Day, hour, minute, 0, nowBeijing.Offset);
        return today > nowBeijing ? today : today.AddDays(1);
    }

    // ══════════════ 报告内容 ══════════════

    /// <summary>
    /// 采集 + 排版。返回 (文本, 告警条数)。
    /// 所有探测都必须“拿不到就不写/降级”，绝不能让一条日报因为某个探针抛异常而发不出去。
    /// </summary>
    private async Task<(string Text, int Warnings)> BuildReportAsync()
    {
        var now = NowBeijing();
        var warnings = new List<string>();
        var lines = new List<string> { $"🩺 服务器健康日报 · {now:MM-dd HH:mm}" };

        // ---- 机器人本体 / 会话 ----
        var uptime = FormatUptime(now - StartedAt);
        lines.Add($"机器人：运行 {uptime}｜会话 {_agent.Conversations.Count} 个｜在途 {_agent.InFlightReplies}｜排队 {_agent.QueuedReplies}");

        // ---- QQ 连接与账号在线（两者不是一回事：协议端连着 ≠ 账号在线）----
        var uin = string.IsNullOrWhiteSpace(_settings.NormalizedUin)
            ? (_agent.SelfId > 0 ? _agent.SelfId.ToString(CultureInfo.InvariantCulture) : "未识别")
            : _settings.NormalizedUin;
        var connection = _source.IsConnected ? "协议端已连接" : "协议端未连接";
        if (!_source.IsConnected)
        {
            warnings.Add("协议端未连接 —— 消息收发都停了，看看 NapCat 容器还在不在");
        }

        var online = _agent.AccountOnline switch
        {
            true => "QQ 在线",
            false => "⚠ QQ 离线",
            _ => "QQ 状态未知"
        };
        if (_agent.AccountOnline is false)
        {
            warnings.Add("QQ 账号离线（被顶号或登录失效）—— 打开面板扫一下二维码就能回来");
        }

        lines.Add($"QQ：{uin} {online}｜{connection}");

        // ---- 模型接口 + 最近一次生成耗时 ----
        var model = string.IsNullOrWhiteSpace(_settings.Model) ? "未配置" : _settings.Model;
        var lastMs = _agent.LastGenerationMilliseconds;
        var lastText = lastMs > 0 ? $"，最近一次 {lastMs / 1000.0:F1}s" : string.Empty;
        var (probeOk, probeNote) = await ProbeModelEndpointAsync();
        lines.Add(probeOk
            ? $"模型：{model} 可达{lastText}"
            : $"模型：{model} ⚠ 接口不通（{probeNote}）{lastText}");
        if (!probeOk)
        {
            warnings.Add($"模型接口不通：{probeNote}");
        }

        // ---- 语音（只在开了语音时才探；TTS 挂了不影响聊天，但要让人知道）----
        if (_settings.EnableVoice && !string.IsNullOrWhiteSpace(_settings.TtsServiceUrl) && _agent.Voice is { } voice)
        {
            try
            {
                var (ok, _, error) = await voice.HealthAsync(CancellationToken.None);
                lines.Add(ok ? "语音：TTS 正常" : $"语音：⚠ TTS 异常（{Trim(error, 60)}）");
                if (!ok)
                {
                    warnings.Add($"TTS 服务异常：{Trim(error, 60)}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"语音：⚠ TTS 探测失败（{ex.GetType().Name}）");
            }
        }

        // ---- 资源：内存 / 负载 / 磁盘（磁盘这条最要命，这台机器被写满过两次）----
        var parts = new List<string>();
        if (MemoryInfo() is { } memory)
        {
            parts.Add(memory.Limit is { } limit
                ? $"内存 {Mb(memory.Used)}/{Mb(limit)}"
                : $"内存 {Mb(memory.Used)}");
        }

        if (LoadAverage() is { } load)
        {
            parts.Add($"负载 {load:F2}");
        }

        if (DiskInfo() is { } disk)
        {
            var usedPercent = disk.Total > 0 ? (int)Math.Round((disk.Total - disk.Free) * 100.0 / disk.Total) : 0;
            parts.Add($"磁盘剩 {Mb(disk.Free)}（{usedPercent}% 已用）");
            if (disk.Free < 500L * 1024 * 1024)
            {
                warnings.Add($"磁盘只剩 {Mb(disk.Free)}（{usedPercent}% 已用）—— 先 docker builder prune -af，别等它写满");
            }
        }

        if (parts.Count > 0)
        {
            lines.Add("资源：" + string.Join("｜", parts));
        }

        // ---- 日志：大小 + 近一小时告警（面板看到的同一份环形缓冲）----
        var logParts = new List<string>();
        if (LogFileSize() is { } size)
        {
            logParts.Add($"{size / 1024.0 / 1024.0:F1} MB");
        }

        var (warnCount, lastWarn) = RecentWarnings(now);
        logParts.Add($"近 1 小时告警 {warnCount} 条");
        lines.Add("日志：" + string.Join("｜", logParts));
        if (warnCount > 0 && lastWarn is { Length: > 0 })
        {
            warnings.Add("最近告警：" + Trim(lastWarn, 70));
        }

        // ---- 告警收尾：最多 4 条，别把日报写成长篇 ----
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

    /// <summary>探一下模型网关通不通（GET {baseUrl}/models；401/503 也算“通”，只有连不上/超时才不算）。</summary>
    private async Task<(bool Ok, string Note)> ProbeModelEndpointAsync()
    {
        var baseUrl = _settings.ModelBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl + "/models", UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return (true, string.Empty); // 没配 / 不是 http(s)：当作“不适用”，别误报
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _settings.ApiKey.Trim());
            }

            using var response = await ProbeHttp.SendAsync(request, CancellationToken.None);
            return response.IsSuccessStatusCode
                ? (true, string.Empty)
                : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex is TaskCanceledException or OperationCanceledException ? "超时" : ex.GetType().Name);
        }
    }

    private static (long Used, long? Limit)? MemoryInfo()
    {
        try
        {
            var used = Process.GetCurrentProcess().WorkingSet64;
            long? limit = null;

            // cgroup v2 / v1 的内存上限（拿得到就顺便报“用了一半没有”）
            foreach (var path in new[] { "/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes" })
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var raw = File.ReadAllText(path).Trim();
                    if (long.TryParse(raw, out var value) && value > 0 && value < (1L << 50))
                    {
                        limit = value;
                        break;
                    }
                }
                catch
                {
                    // 单个文件读不到就换下一个
                }
            }

            return (used, limit);
        }
        catch
        {
            return null;
        }
    }

    private static (long Free, long Total)? DiskInfo()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(AppPaths.RuntimeRoot));
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch
        {
            return null;
        }
    }

    private static double? LoadAverage()
    {
        try
        {
            if (!File.Exists("/proc/loadavg"))
            {
                return null; // 非 Linux：不报这一项
            }

            var first = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static long? LogFileSize()
    {
        try
        {
            var info = new FileInfo(FileLog.LogFilePath);
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>近一小时的告警条数与最后一条（FileLog 的环形缓冲就是面板日志页看的那份）。</summary>
    private static (int Count, string? Last) RecentWarnings(DateTimeOffset nowBeijing)
    {
        try
        {
            var since = nowBeijing.AddHours(-1).ToUnixTimeMilliseconds();
            var count = 0;
            string? last = null;

            foreach (var (time, text) in FileLog.RecentLines(FileLog.RecentCapacity))
            {
                if (time < since || !text.Contains(" WARN ", StringComparison.Ordinal))
                {
                    continue;
                }

                count++;
                last = text;
            }

            return (count, last);
        }
        catch
        {
            return (0, null);
        }
    }

    // ══════════════ 小工具 ══════════════

    /// <summary>收件人：QQ 号，逗号/空格/顿号/分号/换行分隔（顺手容忍 “QQ:123” 这类写法）。</summary>
    public static List<long> ParseTargets(string? raw)
    {
        var list = new List<long>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return list;
        }

        var pieces = raw.Split(
            new[] { ',', '，', ';', '；', '、', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var piece in pieces)
        {
            var digits = new string(piece.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 5 and <= 12 &&
                long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id > 0 && !list.Contains(id))
            {
                list.Add(id);
            }
        }

        return list;
    }

    /// <summary>当前北京时间（带 +08:00 偏移）。</summary>
    public static DateTimeOffset NowBeijing()
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, BeijingTimeZone);

    private static TimeZoneInfo ResolveBeijingTimeZone()
    {
        // Linux 用 "Asia/Shanghai"，Windows 用 "China Standard Time"；都拿不到就退化成固定 UTC+8
        // （北京 1991 年后就没有夏令时了，固定偏移与 tzdata 等价 —— 关键是不能让日报时间漂掉）
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // 换下一个
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "北京时间", "北京时间");
    }

    private static DateTimeOffset ResolveProcessStart()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime);
        }
        catch
        {
            return DateTimeOffset.Now;
        }
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

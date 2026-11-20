using System.Diagnostics;
using System.Globalization;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services.Conversations;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Voice;

namespace BotAgent.Services.Ops;

/// <summary>
/// 服务器健康日报宿主薄门面：北京时间调度定时器、模板排版、收件人解析与发送编排全部归
/// <c>system.health-reporting</c> 独立包经 <see cref="IHealthReportingRuntimeContribution" /> 拥有；
/// 宿主仅保留只读遥测快照采集与 <c>/healthz</c> 探活。缺包或禁用时不会起任何日报定时器。
/// </summary>
public sealed class HealthReportService : IDisposable
{
    public const string ProviderId = "system.health-reporting";

    private static readonly TimeZoneInfo BeijingTimeZone = ResolveBeijingTimeZone();

    private AppSettings _settings => _box.Current;

    private readonly SettingsBox _box;
    private readonly ConversationRegistry _conversations;
    private readonly ReplyPipeline _reply;
    private readonly BotIdentity _identity;
    private readonly BotScheduler _scheduler;
    private readonly VoiceUseCase _voice;
    private readonly IQqChatSource _source;
    private readonly IHttpFetcher _http;
    private readonly IHostFacts _host;
    private readonly DatabaseBackupService? _backups;
    private readonly PluginManager? _plugins;
    private IHealthReportingRuntimeContribution? _direct;
    private int _disposed;

    public HealthReportService(
        SettingsBox box,
        ConversationRegistry conversations,
        ReplyPipeline reply,
        BotIdentity identity,
        BotScheduler scheduler,
        VoiceUseCase voice,
        IQqChatSource source,
        IHttpFetcher http,
        IHostFacts host,
        DatabaseBackupService? backups = null,
        PluginManager? plugins = null,
        IHealthReportingRuntimeContribution? direct = null)
    {
        _box = box;
        _conversations = conversations;
        _reply = reply;
        _identity = identity;
        _scheduler = scheduler;
        _voice = voice;
        _source = source;
        _http = http;
        _host = host;
        _backups = backups;
        _plugins = plugins;
        _direct = direct;
        StartedAt = ResolveProcessStart();
    }

    private DateTimeOffset StartedAt { get; }

    /// <summary>仅供无插件宿主的独立单测注入包内真实运行时实现。</summary>
    public void BindDirect(IHealthReportingRuntimeContribution runtime) => _direct = runtime;

    /// <summary>供宿主装配层注册到插件服务容器，向 <c>system.health-reporting</c> 提供窄只读宿主端口。</summary>
    public IHealthReportHostPorts CreateHostPorts() => new HostPorts(this);

    private IHealthReportingRuntimeContribution? CurrentRuntime
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            if (_direct is not null)
            {
                return _direct;
            }

            return _plugins is not null && _plugins.TryGetContribution<IHealthReportingRuntimeContribution>(ProviderId, out var runtime)
                ? runtime
                : null;
        }
    }

    private static bool IsUnavailable(InvalidOperationException ex)
        => ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing";

    private T ReadRuntime<T>(Func<IHealthReportingRuntimeContribution, T> selector, T fallback)
    {
        var runtime = CurrentRuntime;
        if (runtime is null) return fallback;
        try { return selector(runtime); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return fallback; }
    }

    public DateTimeOffset? LastSentAt => ReadRuntime(r => r.LastSentAt, null);

    public string? LastError => ReadRuntime(r => r.LastError, null);

    public int SentCount => ReadRuntime(r => r.SentCount, 0);

    public DateTimeOffset? NextRunAt => ReadRuntime(r => r.NextRunAt, null);

    public string TimeText => _settings.HealthReportTime;

    public void Start()
    {
        var runtime = CurrentRuntime;
        if (runtime is null) return;
        try { runtime.Start(); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public void Reapply()
    {
        var runtime = CurrentRuntime;
        if (runtime is null) return;
        try { runtime.Reapply(); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public async Task<string> PreviewAsync()
    {
        var runtime = CurrentRuntime;
        if (runtime is null)
        {
            return "健康日报插件（system.health-reporting）未启用或未安装";
        }

        try { return await runtime.PreviewAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            return "健康日报插件（system.health-reporting）未启用或未安装";
        }
    }

    public async Task<(bool Ok, string Text, string? Error)> SendNowAsync(string reason, DateTimeOffset? reportTime = null)
    {
        var runtime = CurrentRuntime;
        if (runtime is null)
        {
            return (false, string.Empty, "健康日报插件未启用或不可用");
        }

        try
        {
            var result = await runtime.SendNowAsync(reason, reportTime, CancellationToken.None).ConfigureAwait(false);
            return (result.Ok, result.Text, result.Error);
        }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            return (false, string.Empty, "健康日报插件未启用或不可用");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var runtime = _direct;
        if (runtime is null && _plugins is not null)
        {
            _plugins.TryGetContribution<IHealthReportingRuntimeContribution>(ProviderId, out runtime);
        }

        if (runtime is null) return;
        try { runtime.Stop(); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    /// <summary>解析收件人列表（面板展示回显复用；核心发送逻辑由独立包持有）。</summary>
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
            if (digits.Length is >= 5 and <= 16 &&
                long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id > 0 && !list.Contains(id))
            {
                list.Add(id);
            }
        }

        return list;
    }

    public static DateTimeOffset NowBeijing()
        => TimeZoneInfo.ConvertTime(Clock.UtcNow, BeijingTimeZone);

    private string? ChatBlockReasonFor(long target)
    {
        var platform = Channels.IsAliasId(target)
            ? Domain.Platforms.PlatformId.QqOfficial : Domain.Platforms.PlatformId.QqPrivate;
        var switches = Platforms.PlatformSwitchSettings.Read(_settings, platform, Domain.Platforms.AccountScope.Legacy);
        return !switches.Enabled ? "平台已停用" : !switches.ChatEnabled ? "聊天已静音" : null;
    }

    private async Task<(bool Ok, string Note)> ProbeModelEndpointAsync()
    {
        var baseUrl = _settings.ModelBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl + "/models", UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return (true, string.Empty);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _settings.ApiKey.Trim());
            }

            using var response = await _http.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, string.Empty)
                : (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex is TaskCanceledException or OperationCanceledException ? "超时" : ex.GetType().Name);
        }
    }

    private static (long Used, long? Limit)? MemoryInfo(IHostFacts host)
    {
        try
        {
            var used = Process.GetCurrentProcess().WorkingSet64;
            return (used, host.MemoryLimitBytes());
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

    private static DateTimeOffset ResolveProcessStart()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime);
        }
        catch
        {
            return Clock.Now;
        }
    }

    private sealed class HostPorts(HealthReportService owner) : IHealthReportHostPorts
    {
        public DateTimeOffset UtcNow => Clock.UtcNow;

        public HealthReportSettingsSnapshot ReadSettings()
        {
            var s = owner._settings;
            return new(
                Enabled: s.HealthReportEnabled,
                TimeText: s.HealthReportTime,
                TargetsRaw: s.HealthReportTargets,
                NormalizedUin: s.NormalizedUin,
                ModelName: s.Model,
                VoiceEnabled: s.EnableVoice,
                TtsServiceUrl: s.TtsServiceUrl);
        }

        public async Task<HealthReportRuntimeSnapshot> CollectRuntimeSnapshotAsync(DateTimeOffset beijingNow, CancellationToken ct)
        {
            var (probeOk, probeNote) = await owner.ProbeModelEndpointAsync().ConfigureAwait(false);
            var voiceChecked = false;
            var voiceHealthy = false;
            string? voiceError = null;
            if (owner._settings.EnableVoice && !string.IsNullOrWhiteSpace(owner._settings.TtsServiceUrl) && owner._voice.Client is { } voice)
            {
                voiceChecked = true;
                try
                {
                    var (ok, _, error) = await voice.HealthAsync(ct).ConfigureAwait(false);
                    voiceHealthy = ok;
                    voiceError = error;
                }
                catch (Exception ex)
                {
                    voiceHealthy = false;
                    voiceError = "probe_exception:" + ex.GetType().Name;
                }
            }

            var mem = MemoryInfo(owner._host);
            var disk = DiskInfo();
            var (warnCount, lastWarn) = RecentWarnings(beijingNow);

            return new(
                StartedAt: owner.StartedAt,
                ConversationCount: owner._conversations.Snapshot().Count,
                InFlightReplies: owner._reply.InFlightReplies,
                QueuedReplies: owner._reply.QueuedReplies,
                SelfId: owner._identity.SelfId,
                ProtocolConnected: owner._source.IsConnected,
                AccountOnline: owner._scheduler.AccountOnline,
                LastGenerationMs: owner._reply.LastGenerationMilliseconds,
                ModelProbeOk: probeOk,
                ModelProbeNote: probeNote,
                VoiceChecked: voiceChecked,
                VoiceHealthy: voiceHealthy,
                VoiceError: voiceError,
                MemoryUsedBytes: mem?.Used,
                MemoryLimitBytes: mem?.Limit,
                LoadAverage: owner._host.LoadAverage(),
                DiskFreeBytes: disk?.Free,
                DiskTotalBytes: disk?.Total,
                LogFileSizeBytes: LogFileSize(),
                RecentWarnCount: warnCount,
                LastWarnText: lastWarn,
                HasBackups: owner._backups is not null,
                LastBackupAt: owner._backups?.LastSuccessAt,
                LastBackupError: owner._backups?.LastError,
                BackupFailureSummary: owner._backups?.FailureSummary(beijingNow));
        }

        public string? CheckPlatformBlockReason(long targetId) => owner.ChatBlockReasonFor(targetId);

        public bool IsOfficialAlias(long targetId) => Channels.IsAliasId(targetId);

        public async Task<bool> SendPrivateTextAsync(long targetId, string text, CancellationToken ct)
            => (await owner._source.SendTextAsync(false, targetId, text, ct).ConfigureAwait(false)).Ok;

        public void LogInfo(string message) => FileLog.Write("Health", message);

        public void LogWarn(string message) => FileLog.Warn("Health", message);
    }
}

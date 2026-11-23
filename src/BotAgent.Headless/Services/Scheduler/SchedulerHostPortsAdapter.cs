using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BotAgent.Domain.Scheduler;

namespace BotAgent.Services.Scheduler;

/// <summary>
/// <c>preset.feature.scheduler</c> 的宿主端口适配器：解析相对/绝对时间并绑定实时时区，严格按发起会话范围隔离操作。
/// </summary>
public sealed class SchedulerHostPortsAdapter : ISchedulerHostPorts
{
    private static readonly Regex RelativeTimePattern = new(
        @"^\s*(\d+)\s*(秒|秒钟|s|sec|分钟|分|m|min|小时|时|h|hr|天|d)\s*(后)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IScheduledJobStore _store;
    private readonly Func<IReadOnlyDictionary<string, JsonElement>?> _readSettings;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action? _onScheduleChanged;

    public SchedulerHostPortsAdapter(
        IScheduledJobStore store,
        Func<IReadOnlyDictionary<string, JsonElement>?> readSettings,
        Func<DateTimeOffset>? utcNow = null,
        Action? onScheduleChanged = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readSettings = readSettings ?? throw new ArgumentNullException(nameof(readSettings));
        _utcNow = utcNow ?? (() => Clock.UtcNow);
        _onScheduleChanged = onScheduleChanged;
    }

    public SchedulerOptions ReadOptions()
    {
        var values = _readSettings();
        if (values is null) return new SchedulerOptions();
        return new SchedulerOptions(
            Math.Clamp(ReadInt(values, "maxJobsPerScope", SchedulerLimits.MaxJobsPerScope), 1, SchedulerLimits.MaxJobsPerScope),
            Math.Clamp(ReadInt(values, "maxGlobalJobs", SchedulerLimits.MaxGlobalJobs), 8, SchedulerLimits.MaxGlobalJobs),
            Math.Clamp(ReadInt(values, "minCronIntervalMinutes", SchedulerLimits.MinCronIntervalMinutes), SchedulerLimits.MinCronIntervalMinutes, 1440));
    }

    public SchedulerMutationResult CreateReminder(
        string sourceKey,
        string creatorUserId,
        string whenText,
        string messageText,
        string? timezoneIana)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
            return new(false, false, "source_required");
        if (ScheduledJobValidator.IsQqOfficialScope(sourceKey))
            return new(false, false, "passive_trigger_required");

        var cleanText = (messageText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanText))
            return new(false, false, "scheduler_text_required");

        var tzId = string.IsNullOrWhiteSpace(timezoneIana) ? SchedulerLimits.DefaultTimeZone : timezoneIana.Trim();
        if (!CronSchedule.TryResolveTimeZone(tzId, out var tz))
            return new(false, false, "invalid_timezone");

        var opts = ReadOptions();
        var now = _utcNow();
        var trimmedWhen = (whenText ?? string.Empty).Trim();
        string kind;
        string? cronExpr = null;
        long nextFireUnix;

        if (CronSchedule.TryParse(trimmedWhen, out var cron, out _) && cron is not null)
        {
            if (!cron.TryGetNextOccurrence(now, tz, out var nextCron))
                return new(false, false, "cron_no_future_occurrence");
            if (!cron.TryValidateMinimumInterval(opts.MinCronIntervalMinutes, now, tz))
                return new(false, false, "cron_interval_too_short");
            kind = ScheduledJobKind.CronText;
            cronExpr = trimmedWhen;
            nextFireUnix = nextCron.ToUnixTimeSeconds();
        }
        else if (TryParseWhenTime(trimmedWhen, now, tz, out var fireAt))
        {
            kind = ScheduledJobKind.OnceReminder;
            nextFireUnix = fireAt.ToUnixTimeSeconds();
        }
        else
        {
            return new(false, false, "scheduler_time_invalid");
        }

        var payloadJson = JsonSerializer.Serialize(new { text = cleanText });
        var nowUnix = now.ToUnixTimeSeconds();
        if (!ScheduledJobValidator.ValidateJobInput(
                sourceKey, kind, cronExpr, tzId, payloadJson, MissedFirePolicy.SkipAndNotify,
                nextFireUnix, now, out _, out nextFireUnix, out var err, opts.MinCronIntervalMinutes))
        {
            return new(false, false, err);
        }

        var job = new ScheduledJobRecord(
            JobId: "job-" + Guid.NewGuid().ToString("N")[..12],
            ScopeKey: sourceKey,
            CreatorUserId: string.IsNullOrWhiteSpace(creatorUserId) ? "unknown" : creatorUserId.Trim(),
            Kind: kind,
            CronExpr: cronExpr,
            TimezoneIana: tzId,
            PayloadJson: payloadJson,
            MissedPolicy: MissedFirePolicy.SkipAndNotify,
            Enabled: true,
            NextFireUnix: nextFireUnix,
            LastFiredUnix: null,
            FailureCount: 0,
            CreatedUnix: nowUnix,
            UpdatedUnix: nowUnix);

        var res = _store.UpsertJob(job, maxJobsPerScope: opts.MaxJobsPerScope, maxGlobalJobs: opts.MaxGlobalJobs);
        if (res.Ok) _onScheduleChanged?.Invoke();
        return res;
    }

    public ScheduledJobRecord[] ListReminders(string sourceKey)
        => string.IsNullOrWhiteSpace(sourceKey)
            ? []
            : _store.ReadSnapshot(sourceKey).Jobs.ToArray();

    public SchedulerMutationResult SetReminderEnabled(string sourceKey, string jobId, bool enabled)
    {
        var existing = _store.FindJob(jobId);
        if (existing is null || !string.Equals(existing.ScopeKey, sourceKey, StringComparison.Ordinal))
            return new(false, false, "job_not_found");
        var opts = ReadOptions();
        var res = _store.SetEnabled(jobId, enabled, _utcNow().ToUnixTimeSeconds(), opts.MaxJobsPerScope, opts.MaxGlobalJobs);
        if (res.Ok) _onScheduleChanged?.Invoke();
        return res;
    }

    public SchedulerMutationResult DeleteReminder(string sourceKey, string jobId)
    {
        var existing = _store.FindJob(jobId);
        if (existing is null || !string.Equals(existing.ScopeKey, sourceKey, StringComparison.Ordinal))
            return new(false, false, "job_not_found");
        var res = _store.DeleteJob(jobId);
        if (res.Ok) _onScheduleChanged?.Invoke();
        return res;
    }

    public static bool TryParseWhenTime(string input, DateTimeOffset nowUtc, TimeZoneInfo tz, out DateTimeOffset fireUtc)
    {
        fireUtc = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var m = RelativeTimePattern.Match(input);
        if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) && amount > 0)
        {
            var unit = m.Groups[2].Value.ToLowerInvariant();
            var span = unit switch
            {
                "秒" or "秒钟" or "s" or "sec" => TimeSpan.FromSeconds(Math.Min(amount, 31_536_000)),
                "分钟" or "分" or "m" or "min" => TimeSpan.FromMinutes(Math.Min(amount, 525_600)),
                "小时" or "时" or "h" or "hr" => TimeSpan.FromHours(Math.Min(amount, 8_760)),
                "天" or "d" => TimeSpan.FromDays(Math.Min(amount, 365)),
                _ => TimeSpan.Zero
            };
            if (span > TimeSpan.Zero)
            {
                fireUtc = nowUtc.Add(span);
                return true;
            }
        }

        if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso)
            && (input.Contains('Z', StringComparison.OrdinalIgnoreCase) || input.Contains('+') || input.LastIndexOf('-') > 9))
        {
            fireUtc = iso.ToUniversalTime();
            return fireUtc > nowUtc;
        }

        string[] localFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy/MM/dd HH:mm"];
        if (DateTime.TryParseExact(input.Trim(), localFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (!tz.IsInvalidTime(unspecified))
            {
                fireUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, tz), TimeSpan.Zero);
                return fireUtc > nowUtc;
            }
        }

        if (TimeSpan.TryParseExact(input.Trim(), ["hh\\:mm", "h\\:mm"], CultureInfo.InvariantCulture, out var tod))
        {
            var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
            var candidateLocal = new DateTime(localNow.Year, localNow.Month, localNow.Day, tod.Hours, tod.Minutes, 0, DateTimeKind.Unspecified);
            if (candidateLocal <= localNow.DateTime) candidateLocal = candidateLocal.AddDays(1);
            if (!tz.IsInvalidTime(candidateLocal))
            {
                fireUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidateLocal, tz), TimeSpan.Zero);
                return fireUtc > nowUtc;
            }
        }

        return false;
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var element)) return fallback;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var num)) return num;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out num))
            return num;
        return fallback;
    }
}

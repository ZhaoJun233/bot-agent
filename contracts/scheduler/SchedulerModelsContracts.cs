namespace BotAgent.Domain.Scheduler;

public static class SchedulerLimits
{
    public const int MaxJobsPerScope = 32;
    public const int MaxGlobalJobs = 256;
    public const int MinCronIntervalMinutes = 5;
    public const int MaxPayloadBytes = 2048;
    public const int MaxRunHistory = 1024;
    public const int MissedWindowSeconds = 900;
    public const string DefaultTimeZone = "Asia/Shanghai";
    public const string WorkspaceOwnerScopeKey = "workspace:owner";
}

public static class ScheduledJobKind
{
    public const string OnceReminder = "once_reminder";
    public const string CronText = "cron_text";
    public const string CronDigest = "cron_digest";
    public const string CronTask = "cron_task";

    public static bool IsValid(string? kind) => kind is OnceReminder or CronText or CronDigest or CronTask;
}

public static class MissedFirePolicy
{
    public const string SkipAndNotify = "skip_and_notify";
    public const string FireOnce15m = "fire_once_15m";
    public const string SkipSilent = "skip_silent";

    public static bool IsValid(string? policy) => policy is SkipAndNotify or FireOnce15m or SkipSilent;
}

public static class ScheduledDeliveryStatus
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Degraded = "degraded";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
    public const string Skipped = "skipped";
}

public sealed record ScheduledJobRecord(
    string JobId,
    string ScopeKey,
    string CreatorUserId,
    string Kind,
    string? CronExpr,
    string TimezoneIana,
    string PayloadJson,
    string MissedPolicy,
    bool Enabled,
    long NextFireUnix,
    long? LastFiredUnix,
    int FailureCount,
    long CreatedUnix,
    long UpdatedUnix);

public sealed record ScheduledJobRunRecord(
    string RunKey,
    string JobId,
    long ScheduledUnix,
    long StartedUnix,
    long? FinishedUnix,
    string DeliveryStatus,
    string? ReasonCode);

public sealed record ScheduledDeliveryResult(bool Ok, string Status, string ReasonCode);

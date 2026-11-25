using BotAgent.Domain.Plugins;
using BotAgent.Domain.Scheduler;

namespace BotAgent.Services.Scheduler;

public sealed record SchedulerOptions(
    int MaxJobsPerScope = SchedulerLimits.MaxJobsPerScope,
    int MaxGlobalJobs = SchedulerLimits.MaxGlobalJobs,
    int MinCronIntervalMinutes = SchedulerLimits.MinCronIntervalMinutes);

public sealed record SchedulerMutationResult(
    bool Ok,
    bool Replayed,
    string ReasonCode,
    ScheduledJobRecord? Job = null,
    long Revision = 0);

public sealed record SchedulerSnapshotView(
    long Revision,
    IReadOnlyList<ScheduledJobRecord> Jobs);

/// <summary>Pure storage port implemented by <c>BotAgent.Storage.ScheduledJobStore</c> (R9).</summary>
public interface IScheduledJobStore
{
    SchedulerSnapshotView ReadSnapshot(string? scopeKey = null);

    ScheduledJobRecord? FindJob(string jobId);

    SchedulerMutationResult UpsertJob(
        ScheduledJobRecord job,
        long? expectedRevision = null,
        string? idempotencyKey = null,
        string? idempotencyDigest = null,
        int maxJobsPerScope = SchedulerLimits.MaxJobsPerScope,
        int maxGlobalJobs = SchedulerLimits.MaxGlobalJobs);

    SchedulerMutationResult DeleteJob(
        string jobId,
        long? expectedRevision = null,
        string? idempotencyKey = null);

    SchedulerMutationResult SetEnabled(
        string jobId,
        bool enabled,
        long updatedUnix,
        int maxJobsPerScope = SchedulerLimits.MaxJobsPerScope,
        int maxGlobalJobs = SchedulerLimits.MaxGlobalJobs);

    void DisableByScope(string scopeKey, long updatedUnix);

    bool TryClaimRunSlot(
        string jobId,
        long scheduledFireUnix,
        long nowUnix,
        long? nextFireUnix,
        bool disableAfterClaim,
        out string runKey);

    void CompleteRun(
        string runKey,
        string deliveryStatus,
        string? reasonCode,
        long finishedUnix,
        bool incrementFailure);

    void RecordSkippedRun(
        string runKey,
        string jobId,
        long scheduledUnix,
        long nowUnix,
        string reasonCode,
        long? nextFireUnix,
        bool disableJob);

    IReadOnlyList<ScheduledJobRunRecord> ListRuns(string? jobId = null, int limit = 50);
}

/// <summary>Host multi-channel delivery port checking policy, whitelist, lease, quota, and reply boundary.</summary>
public interface IScheduledDeliveryPort
{
    Task<ScheduledDeliveryResult> DeliverAsync(ScheduledJobRecord job, long scheduledUnix, string? missedNotice, CancellationToken ct);
}

/// <summary>Narrow host port exposed to <c>preset.feature.scheduler</c>.</summary>
public interface ISchedulerHostPorts
{
    SchedulerOptions ReadOptions();

    SchedulerMutationResult CreateReminder(string sourceKey, string creatorUserId, string whenText, string messageText, string? timezoneIana);

    ScheduledJobRecord[] ListReminders(string sourceKey);

    SchedulerMutationResult SetReminderEnabled(string sourceKey, string jobId, bool enabled);

    SchedulerMutationResult DeleteReminder(string sourceKey, string jobId);
}

/// <summary>Package contribution exposed by <c>preset.feature.scheduler</c>.</summary>
public interface ISchedulerContribution : IPluginContribution
{
    bool IsReady { get; }

    SchedulerMutationResult CreateReminder(string sourceKey, string creatorUserId, string whenText, string messageText, string? timezoneIana);

    ScheduledJobRecord[] ListReminders(string sourceKey);

    SchedulerMutationResult SetReminderEnabled(string sourceKey, string jobId, bool enabled);

    SchedulerMutationResult DeleteReminder(string sourceKey, string jobId);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Scheduler;
using BotAgent.Services;
using BotAgent.Services.Scheduler;

namespace BotAgent.Storage;

/// <summary>
/// 持久化定时任务与幂等运行账本（Schema v15 兼容）：
/// 经 <c>AppDatabase.Write</c> 单事务读写 <c>meta['scheduler:jobs:v1']</c> 与 <c>meta['scheduler:runs:v1']</c>，
/// 抢占运行槽与推进 <c>nextFireUnix</c> 在同一事务内完成，确保重启零重放。
/// </summary>
public sealed class ScheduledJobStore : IScheduledJobStore
{
    private const string JobsMetaKey = "scheduler:jobs:v1";
    private const string RunsMetaKey = "scheduler:runs:v1";
    private const int MaxReceipts = 64;
    private const int MaxStoredJobsHard = 512;

    private readonly object _gate = new();

    private sealed record ReceiptEntry(string Key, string Digest, string JobId, long Revision);

    private sealed record JobsLedger(
        long Revision,
        List<ScheduledJobRecord> Jobs,
        List<ReceiptEntry> Receipts)
    {
        public static JobsLedger Empty() => new(0, new List<ScheduledJobRecord>(), new List<ReceiptEntry>());
    }

    private sealed record RunsLedger(List<ScheduledJobRunRecord> Runs)
    {
        public static RunsLedger Empty() => new(new List<ScheduledJobRunRecord>());
    }

    public SchedulerSnapshotView ReadSnapshot(string? scopeKey = null)
    {
        lock (_gate)
        {
            var ledger = LoadJobs();
            var filtered = string.IsNullOrWhiteSpace(scopeKey)
                ? ledger.Jobs.ToArray()
                : ledger.Jobs.Where(j => string.Equals(j.ScopeKey, scopeKey, StringComparison.Ordinal)).ToArray();
            return new SchedulerSnapshotView(ledger.Revision, filtered);
        }
    }

    public ScheduledJobRecord? FindJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        lock (_gate)
        {
            return LoadJobs().Jobs.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
        }
    }

    public SchedulerMutationResult UpsertJob(
        ScheduledJobRecord job,
        long? expectedRevision = null,
        string? idempotencyKey = null,
        string? idempotencyDigest = null,
        int maxJobsPerScope = SchedulerLimits.MaxJobsPerScope,
        int maxGlobalJobs = SchedulerLimits.MaxGlobalJobs)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!ScheduledJobValidator.IsValidJobId(job.JobId))
            return new(false, false, "job_id_invalid");
        if (!ScheduledJobValidator.ValidatePayload(job.PayloadJson, out _, out var payloadErr))
            return new(false, false, payloadErr);

        if (!IsValidIdempotencyKey(idempotencyKey))
            return new(false, false, "invalid_idempotency_key");

        var scopeCap = Math.Clamp(maxJobsPerScope, 1, SchedulerLimits.MaxJobsPerScope);
        var globalCap = Math.Clamp(maxGlobalJobs, 1, SchedulerLimits.MaxGlobalJobs);
        lock (_gate)
        {
            SchedulerMutationResult? result = null;
            AppDatabase.Write(conn =>
            {
                var rawJobs = AppDatabase.GetMetaInTransaction(conn, JobsMetaKey);
                var ledger = ParseJobs(rawJobs, out var corrupted);
                if (corrupted && !string.IsNullOrWhiteSpace(rawJobs))
                    AppDatabase.SetMetaInTransaction(conn, JobsMetaKey + ":corrupt", rawJobs);

                var digest = string.IsNullOrEmpty(idempotencyDigest) ? HashJob(job) : idempotencyDigest;
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    var receipt = ledger.Receipts.FirstOrDefault(r => string.Equals(r.Key, idempotencyKey, StringComparison.Ordinal));
                    if (receipt is not null)
                    {
                        if (!string.Equals(receipt.Digest, digest, StringComparison.Ordinal))
                        {
                            result = new(false, false, "idempotency_key_conflict", null, ledger.Revision);
                            return;
                        }
                        var existingReplayed = ledger.Jobs.FirstOrDefault(j => string.Equals(j.JobId, receipt.JobId, StringComparison.Ordinal));
                        result = new(true, true, "replayed", existingReplayed, ledger.Revision);
                        return;
                    }
                }

                if (expectedRevision is { } expected && expected != ledger.Revision)
                {
                    result = new(false, false, "scheduler_revision_conflict", null, ledger.Revision);
                    return;
                }

                var idx = ledger.Jobs.FindIndex(j => string.Equals(j.JobId, job.JobId, StringComparison.Ordinal));
                var wasEnabledHere = idx >= 0 && ledger.Jobs[idx].Enabled;
                if (job.Enabled && !wasEnabledHere)
                {
                    var activeGlobal = ledger.Jobs.Count(j => j.Enabled);
                    if (activeGlobal >= globalCap)
                    {
                        result = new(false, false, "scheduler_global_quota_exceeded", null, ledger.Revision);
                        return;
                    }
                    var activeScope = ledger.Jobs.Count(j => j.Enabled && string.Equals(j.ScopeKey, job.ScopeKey, StringComparison.Ordinal));
                    if (activeScope >= scopeCap)
                    {
                        result = new(false, false, "scheduler_scope_quota_exceeded", null, ledger.Revision);
                        return;
                    }
                }

                if (idx >= 0)
                {
                    var prev = ledger.Jobs[idx];
                    job = job with { CreatedUnix = prev.CreatedUnix, LastFiredUnix = prev.LastFiredUnix };
                    ledger.Jobs[idx] = job;
                }
                else
                {
                    if (ledger.Jobs.Count >= MaxStoredJobsHard)
                    {
                        var deadIdx = ledger.Jobs.FindIndex(j => !j.Enabled);
                        if (deadIdx >= 0) ledger.Jobs.RemoveAt(deadIdx);
                    }
                    if (ledger.Jobs.Count >= MaxStoredJobsHard)
                    {
                        result = new(false, false, "scheduler_storage_capacity", null, ledger.Revision);
                        return;
                    }
                    ledger.Jobs.Add(job);
                }

                var nextRev = checked(ledger.Revision + 1);
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    ledger.Receipts.Add(new ReceiptEntry(idempotencyKey, digest, job.JobId, nextRev));
                    while (ledger.Receipts.Count > MaxReceipts) ledger.Receipts.RemoveAt(0);
                }

                var updated = ledger with { Revision = nextRev };
                AppDatabase.SetMetaInTransaction(conn, JobsMetaKey, JsonSerializer.Serialize(updated));
                result = new(true, false, idx >= 0 ? "updated" : "created", job, nextRev);
            });
            return result ?? new(false, false, "scheduler_save_failed");
        }
    }

    public SchedulerMutationResult DeleteJob(string jobId, long? expectedRevision = null, string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return new(false, false, "job_id_required");
        if (!IsValidIdempotencyKey(idempotencyKey))
            return new(false, false, "invalid_idempotency_key");
        lock (_gate)
        {
            SchedulerMutationResult? result = null;
            AppDatabase.Write(conn =>
            {
                var rawJobs = AppDatabase.GetMetaInTransaction(conn, JobsMetaKey);
                var ledger = ParseJobs(rawJobs, out var corrupted);
                if (corrupted && !string.IsNullOrWhiteSpace(rawJobs))
                    AppDatabase.SetMetaInTransaction(conn, JobsMetaKey + ":corrupt", rawJobs);
                var digest = "del:" + jobId;
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    var receipt = ledger.Receipts.FirstOrDefault(r => string.Equals(r.Key, idempotencyKey, StringComparison.Ordinal));
                    if (receipt is not null)
                    {
                        result = string.Equals(receipt.Digest, digest, StringComparison.Ordinal)
                            ? new(true, true, "replayed", null, ledger.Revision)
                            : new(false, false, "idempotency_key_conflict", null, ledger.Revision);
                        return;
                    }
                }

                if (expectedRevision is { } expected && expected != ledger.Revision)
                {
                    result = new(false, false, "scheduler_revision_conflict", null, ledger.Revision);
                    return;
                }

                var removed = ledger.Jobs.RemoveAll(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
                if (removed == 0)
                {
                    result = new(false, false, "job_not_found", null, ledger.Revision);
                    return;
                }

                var nextRev = checked(ledger.Revision + 1);
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    ledger.Receipts.Add(new ReceiptEntry(idempotencyKey, digest, jobId, nextRev));
                    while (ledger.Receipts.Count > MaxReceipts) ledger.Receipts.RemoveAt(0);
                }

                var updated = ledger with { Revision = nextRev };
                AppDatabase.SetMetaInTransaction(conn, JobsMetaKey, JsonSerializer.Serialize(updated));
                result = new(true, false, "deleted", null, nextRev);
            });
            return result ?? new(false, false, "scheduler_delete_failed");
        }
    }

    private static bool IsValidIdempotencyKey(string? idempotencyKey)
        => string.IsNullOrEmpty(idempotencyKey)
           || (!string.IsNullOrWhiteSpace(idempotencyKey) && idempotencyKey.Length <= 128 && !idempotencyKey.Any(char.IsControl));

    public SchedulerMutationResult SetEnabled(
        string jobId,
        bool enabled,
        long updatedUnix,
        int maxJobsPerScope = SchedulerLimits.MaxJobsPerScope,
        int maxGlobalJobs = SchedulerLimits.MaxGlobalJobs)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return new(false, false, "job_id_required");
        var scopeCap = Math.Clamp(maxJobsPerScope, 1, SchedulerLimits.MaxJobsPerScope);
        var globalCap = Math.Clamp(maxGlobalJobs, 1, SchedulerLimits.MaxGlobalJobs);
        lock (_gate)
        {
            SchedulerMutationResult? result = null;
            AppDatabase.Write(conn =>
            {
                var ledger = ParseJobs(AppDatabase.GetMetaInTransaction(conn, JobsMetaKey));
                var idx = ledger.Jobs.FindIndex(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
                if (idx < 0)
                {
                    result = new(false, false, "job_not_found", null, ledger.Revision);
                    return;
                }
                var current = ledger.Jobs[idx];
                var nextFire = current.NextFireUnix;
                if (enabled && !current.Enabled)
                {
                    if (current.Kind == ScheduledJobKind.OnceReminder
                        && (current.LastFiredUnix.HasValue || current.NextFireUnix <= updatedUnix))
                    {
                        result = new(false, false, "once_reminder_already_elapsed", current, ledger.Revision);
                        return;
                    }
                    if (current.Kind != ScheduledJobKind.OnceReminder && current.NextFireUnix <= updatedUnix)
                    {
                        if (!BotAgent.Domain.Scheduler.CronSchedule.TryParse(current.CronExpr, out var cron, out _) || cron is null)
                        {
                            result = new(false, false, "cron_expr_invalid", current, ledger.Revision);
                            return;
                        }
                        if (!BotAgent.Domain.Scheduler.CronSchedule.TryResolveTimeZone(current.TimezoneIana, out var tz))
                            tz = TimeZoneInfo.Utc;
                        if (!cron.TryGetNextOccurrence(DateTimeOffset.FromUnixTimeSeconds(updatedUnix), tz, out var recomputed))
                        {
                            result = new(false, false, "cron_no_future_occurrence", current, ledger.Revision);
                            return;
                        }
                        nextFire = recomputed.ToUnixTimeSeconds();
                    }
                    if (ledger.Jobs.Count(j => j.Enabled) >= globalCap)
                    {
                        result = new(false, false, "scheduler_global_quota_exceeded", null, ledger.Revision);
                        return;
                    }
                    if (ledger.Jobs.Count(j => j.Enabled && string.Equals(j.ScopeKey, current.ScopeKey, StringComparison.Ordinal)) >= scopeCap)
                    {
                        result = new(false, false, "scheduler_scope_quota_exceeded", null, ledger.Revision);
                        return;
                    }
                }

                var next = current with { Enabled = enabled, NextFireUnix = nextFire, UpdatedUnix = updatedUnix };
                ledger.Jobs[idx] = next;
                var nextRev = checked(ledger.Revision + 1);
                AppDatabase.SetMetaInTransaction(conn, JobsMetaKey, JsonSerializer.Serialize(ledger with { Revision = nextRev }));
                result = new(true, false, enabled ? "enabled" : "paused", next, nextRev);
            });
            return result ?? new(false, false, "scheduler_update_failed");
        }
    }

    public void DisableByScope(string scopeKey, long updatedUnix)
    {
        if (string.IsNullOrWhiteSpace(scopeKey)) return;
        lock (_gate)
        {
            AppDatabase.Write(conn =>
            {
                var ledger = ParseJobs(AppDatabase.GetMetaInTransaction(conn, JobsMetaKey));
                var changed = false;
                for (var i = 0; i < ledger.Jobs.Count; i++)
                {
                    var j = ledger.Jobs[i];
                    if (j.Enabled && string.Equals(j.ScopeKey, scopeKey, StringComparison.Ordinal))
                    {
                        ledger.Jobs[i] = j with { Enabled = false, UpdatedUnix = updatedUnix };
                        changed = true;
                    }
                }
                if (!changed) return;
                var nextRev = checked(ledger.Revision + 1);
                AppDatabase.SetMetaInTransaction(conn, JobsMetaKey, JsonSerializer.Serialize(ledger with { Revision = nextRev }));
            });
        }
    }

    public bool TryClaimRunSlot(
        string jobId,
        long scheduledFireUnix,
        long nowUnix,
        long? nextFireUnix,
        bool disableAfterClaim,
        out string runKey)
    {
        runKey = $"{jobId}:{scheduledFireUnix}";
        var keyLocal = runKey;
        lock (_gate)
        {
            var claimed = false;
            AppDatabase.Write(conn =>
            {
                var ledger = ParseJobs(AppDatabase.GetMetaInTransaction(conn, JobsMetaKey));
                var idx = ledger.Jobs.FindIndex(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
                if (idx < 0) return;
                var job = ledger.Jobs[idx];
                if (!job.Enabled || job.NextFireUnix != scheduledFireUnix) return;

                var runs = ParseRuns(AppDatabase.GetMetaInTransaction(conn, RunsMetaKey));
                var alreadyClaimed = runs.Runs.Any(r => string.Equals(r.RunKey, keyLocal, StringComparison.Ordinal));
                if (alreadyClaimed)
                {
                    // 修复崩溃遗留的游标未推进：确保不会卡在同一已执行的时刻。
                    ledger.Jobs[idx] = job with
                    {
                        Enabled = !disableAfterClaim,
                        NextFireUnix = nextFireUnix ?? job.NextFireUnix,
                        UpdatedUnix = nowUnix
                    };
                    var repaired = ledger with { Revision = checked(ledger.Revision + 1) };
                    AppDatabase.SetMetaInTransaction(conn, JobsMetaKey, JsonSerializer.Serialize(repaired));
                    return;
                }

                runs.Runs.Add(new ScheduledJobRunRecord(
                    keyLocal, jobId, scheduledFireUnix, nowUnix, null, ScheduledDeliveryStatus.Running, null));
                while (runs.Runs.Count > SchedulerLimits.MaxRunHistory)
                    runs.Runs.RemoveAt(0);

                ledger.Jobs[idx] = job with
                {
                    Enabled = !disableAfterClaim,
                    LastFiredUnix = nowUnix,
                    NextFireUnix = nextFireUnix ?? job.NextFireUnix,
                    UpdatedUnix = nowUnix
                };

                AppDatabase.SetMetaInTransaction(conn, RunsMetaKey, JsonSerializer.Serialize(runs));
                AppDatabase.SetMetaInTransaction(conn, JobsMetaKey, JsonSerializer.Serialize(ledger with { Revision = checked(ledger.Revision + 1) }));
                claimed = true;
            });
            return claimed;
        }
    }

    public void CompleteRun(
        string runKey,
        string deliveryStatus,
        string? reasonCode,
        long finishedUnix,
        bool incrementFailure)
    {
        if (string.IsNullOrWhiteSpace(runKey)) return;
        lock (_gate)
        {
            AppDatabase.Write(conn =>
            {
                var runs = ParseRuns(AppDatabase.GetMetaInTransaction(conn, RunsMetaKey));
                var idx = runs.Runs.FindIndex(r => string.Equals(r.RunKey, runKey, StringComparison.Ordinal));
                string? jobId = null;
                if (idx >= 0)
                {
                    var current = runs.Runs[idx];
                    jobId = current.JobId;
                    runs.Runs[idx] = current with
                    {
                        FinishedUnix = finishedUnix,
                        DeliveryStatus = deliveryStatus,
                        ReasonCode = reasonCode
                    };
                    AppDatabase.SetMetaInTransaction(conn, RunsMetaKey, JsonSerializer.Serialize(runs));
                }

                if (!string.IsNullOrEmpty(jobId) && (incrementFailure || deliveryStatus == ScheduledDeliveryStatus.Succeeded))
                {
                    var ledger = ParseJobs(AppDatabase.GetMetaInTransaction(conn, JobsMetaKey));
                    var jIdx = ledger.Jobs.FindIndex(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
                    if (jIdx >= 0)
                    {
                        var j = ledger.Jobs[jIdx];
                        var nextFailures = deliveryStatus == ScheduledDeliveryStatus.Succeeded ? 0 : j.FailureCount + 1;
                        if (nextFailures != j.FailureCount)
                        {
                            ledger.Jobs[jIdx] = j with { FailureCount = nextFailures, UpdatedUnix = finishedUnix };
                            AppDatabase.SetMetaInTransaction(conn, JobsMetaKey, JsonSerializer.Serialize(ledger with { Revision = checked(ledger.Revision + 1) }));
                        }
                    }
                }
            });
        }
    }

    public void RecordSkippedRun(
        string runKey,
        string jobId,
        long scheduledUnix,
        long nowUnix,
        string reasonCode,
        long? nextFireUnix,
        bool disableJob)
    {
        lock (_gate)
        {
            AppDatabase.Write(conn =>
            {
                var ledger = ParseJobs(AppDatabase.GetMetaInTransaction(conn, JobsMetaKey));
                var idx = ledger.Jobs.FindIndex(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
                if (idx < 0) return;
                var job = ledger.Jobs[idx];
                if (!job.Enabled || job.NextFireUnix != scheduledUnix) return;

                var runs = ParseRuns(AppDatabase.GetMetaInTransaction(conn, RunsMetaKey));
                if (!runs.Runs.Any(r => string.Equals(r.RunKey, runKey, StringComparison.Ordinal)))
                {
                    runs.Runs.Add(new ScheduledJobRunRecord(
                        runKey, jobId, scheduledUnix, nowUnix, nowUnix, ScheduledDeliveryStatus.Skipped, reasonCode));
                    while (runs.Runs.Count > SchedulerLimits.MaxRunHistory)
                        runs.Runs.RemoveAt(0);
                    AppDatabase.SetMetaInTransaction(conn, RunsMetaKey, JsonSerializer.Serialize(runs));
                }

                ledger.Jobs[idx] = job with
                {
                    Enabled = !disableJob,
                    NextFireUnix = nextFireUnix ?? job.NextFireUnix,
                    UpdatedUnix = nowUnix
                };
                AppDatabase.SetMetaInTransaction(conn, JobsMetaKey, JsonSerializer.Serialize(ledger with { Revision = checked(ledger.Revision + 1) }));
            });
        }
    }

    public IReadOnlyList<ScheduledJobRunRecord> ListRuns(string? jobId = null, int limit = 50)
    {
        var bounded = Math.Clamp(limit, 1, 200);
        lock (_gate)
        {
            var runs = LoadRuns().Runs;
            IEnumerable<ScheduledJobRunRecord> query = string.IsNullOrWhiteSpace(jobId)
                ? runs
                : runs.Where(r => string.Equals(r.JobId, jobId, StringComparison.Ordinal));
            return query.OrderByDescending(r => r.ScheduledUnix).Take(bounded).ToArray();
        }
    }

    private JobsLedger LoadJobs()
        => ParseJobs(AppDatabase.GetMeta(JobsMetaKey));

    private RunsLedger LoadRuns()
        => ParseRuns(AppDatabase.GetMeta(RunsMetaKey));

    private static JobsLedger ParseJobs(string? raw)
        => ParseJobs(raw, out _);

    private static JobsLedger ParseJobs(string? raw, out bool corrupted)
    {
        corrupted = false;
        if (string.IsNullOrWhiteSpace(raw)) return JobsLedger.Empty();
        try
        {
            var obj = JsonSerializer.Deserialize<JobsLedger>(raw);
            if (obj is null) return JobsLedger.Empty();
            var jobs = (obj.Jobs ?? new List<ScheduledJobRecord>()).Where(j => j is not null).ToList();
            var receipts = (obj.Receipts ?? new List<ReceiptEntry>()).Where(r => r is not null).ToList();
            return new JobsLedger(obj.Revision, jobs, receipts);
        }
        catch (JsonException)
        {
            corrupted = true;
            return JobsLedger.Empty();
        }
    }

    private static RunsLedger ParseRuns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return RunsLedger.Empty();
        try
        {
            var obj = JsonSerializer.Deserialize<RunsLedger>(raw);
            if (obj?.Runs is null) return RunsLedger.Empty();
            return new RunsLedger(obj.Runs.Where(r => r is not null).ToList());
        }
        catch (JsonException) { return RunsLedger.Empty(); }
    }

    private static string HashJob(ScheduledJobRecord job)
    {
        var raw = JsonSerializer.Serialize(new object?[]
        {
            job.ScopeKey, job.Kind, job.CronExpr, job.TimezoneIana,
            job.PayloadJson, job.MissedPolicy, job.Enabled, job.NextFireUnix
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}

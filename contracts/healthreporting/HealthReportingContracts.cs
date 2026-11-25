using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Ops;

/// <summary>Read-only operational metrics snapshot collected from host and optional installed packages.</summary>
public sealed record HealthMetricsSnapshot(
    int ConversationCount,
    int InFlightReplies,
    int PendingReplies,
    long? LastReplyMs,
    bool ProtocolOnline,
    string? BackupFailureSummary,
    IReadOnlyDictionary<string, string>? OptionalPackageMetrics = null);

/// <summary>Read-only port supplied by host so health reporting never couples directly to optional packages.</summary>
public interface IHealthMetricsSnapshotProvider
{
    HealthMetricsSnapshot CaptureSnapshot(DateTimeOffset now);
}

/// <summary>Read-only settings snapshot supplied to <c>system.health-reporting</c>.</summary>
public sealed record HealthReportSettingsSnapshot(
    bool Enabled,
    string TimeText,
    string TargetsRaw,
    string NormalizedUin,
    string ModelName,
    bool VoiceEnabled,
    string TtsServiceUrl);

/// <summary>Read-only host runtime telemetry snapshot used to render a daily health report.</summary>
public sealed record HealthReportRuntimeSnapshot(
    DateTimeOffset StartedAt,
    int ConversationCount,
    int InFlightReplies,
    int QueuedReplies,
    long SelfId,
    bool ProtocolConnected,
    bool? AccountOnline,
    long LastGenerationMs,
    bool ModelProbeOk,
    string ModelProbeNote,
    bool VoiceChecked,
    bool VoiceHealthy,
    string? VoiceError,
    long? MemoryUsedBytes,
    long? MemoryLimitBytes,
    double? LoadAverage,
    long? DiskFreeBytes,
    long? DiskTotalBytes,
    long? LogFileSizeBytes,
    int RecentWarnCount,
    string? LastWarnText,
    bool HasBackups,
    DateTimeOffset? LastBackupAt,
    string? LastBackupError,
    string? BackupFailureSummary);

/// <summary>Structured outcome of sending one daily health report.</summary>
public sealed record HealthReportSendResult(bool Ok, string Text, string? Error);

/// <summary>
/// Narrow, credential-free host ports provided to <c>system.health-reporting</c> for
/// read-only telemetry capture, platform block checks, private text send, and logging.
/// </summary>
public interface IHealthReportHostPorts
{
    DateTimeOffset UtcNow { get; }

    HealthReportSettingsSnapshot ReadSettings();

    Task<HealthReportRuntimeSnapshot> CollectRuntimeSnapshotAsync(DateTimeOffset beijingNow, CancellationToken ct);

    string? CheckPlatformBlockReason(long targetId);

    bool IsOfficialAlias(long targetId);

    Task<bool> SendPrivateTextAsync(long targetId, string text, CancellationToken ct);

    void LogInfo(string message);

    void LogWarn(string message);
}

/// <summary>
/// Generation-bound Beijing-time daily health report scheduling and template contribution
/// owned by <c>system.health-reporting</c>. Liveness <c>/healthz</c> remains host-owned.
/// </summary>
public interface IHealthReportingContribution : IPluginContribution
{
    DateTimeOffset ComputeNextRunBeijing(DateTimeOffset nowUtc, string hhmm, DateOnly? lastSentDay);

    string FormatSummaryReport(HealthMetricsSnapshot snapshot, DateTimeOffset beijingNow, int maxChars = 800);

    bool CanSendToday(DateOnly todayBeijing, DateOnly? lastSentDay);
}

/// <summary>
/// Full runtime coordinator owned by <c>system.health-reporting</c>: Beijing-time timer scheduling,
/// daily-once deduplication, report rendering, preview, and recipient delivery orchestration.
/// </summary>
public interface IHealthReportingRuntimeContribution : IHealthReportingContribution
{
    DateTimeOffset? LastSentAt { get; }

    string? LastError { get; }

    int SentCount { get; }

    DateTimeOffset? NextRunAt { get; }

    bool HasActiveTimer { get; }

    void Start();

    void Reapply();

    void Stop();

    Task<string> PreviewAsync(CancellationToken ct);

    Task<HealthReportSendResult> SendNowAsync(string reason, DateTimeOffset? reportTime, CancellationToken ct);

    long[] ParseTargets(string? raw);
}

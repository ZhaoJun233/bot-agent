using System.Globalization;
using System.Text;
using BotAgent.Services.Ops;

namespace BotAgent.Packages.HealthReporting;

/// <summary>
/// Owns Beijing-time daily scheduling, same-day deduplication, and bounded health report rendering.
/// Optional package metrics are rendered when present and gracefully omitted when absent.
/// </summary>
public sealed class HealthReportingEngine : IHealthReportingContribution
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    public DateTimeOffset ComputeNextRunBeijing(DateTimeOffset nowUtc, string hhmm, DateOnly? lastSentDay)
    {
        var beijingNow = nowUtc.ToOffset(BeijingOffset);
        var (hour, minute) = ParseTime(hhmm);
        var targetDate = DateOnly.FromDateTime(beijingNow.DateTime);
        var candidate = new DateTimeOffset(targetDate.Year, targetDate.Month, targetDate.Day, hour, minute, 0, BeijingOffset);
        if (candidate <= beijingNow || (lastSentDay is { } sent && sent == targetDate))
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }

    public bool CanSendToday(DateOnly todayBeijing, DateOnly? lastSentDay)
        => lastSentDay is null || lastSentDay.Value != todayBeijing;

    public string FormatSummaryReport(HealthMetricsSnapshot snapshot, DateTimeOffset beijingNow, int maxChars = 800)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【健康日报 {beijingNow:yyyy-MM-dd HH:mm}】");
        sb.AppendLine($"协议端：{(snapshot.ProtocolOnline ? "在线" : "离线")}｜会话：{snapshot.ConversationCount}");
        sb.AppendLine($"回复：在途 {snapshot.InFlightReplies} / 排队 {snapshot.PendingReplies}" +
                      (snapshot.LastReplyMs is { } ms ? $" / 最近 {ms}ms" : string.Empty));
        if (!string.IsNullOrWhiteSpace(snapshot.BackupFailureSummary))
        {
            sb.AppendLine($"告警（备份）：{snapshot.BackupFailureSummary}");
        }

        if (snapshot.OptionalPackageMetrics is { Count: > 0 } extras)
        {
            foreach (var (k, v) in extras.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    sb.AppendLine($"{k}：{v}");
                }
            }
        }

        var text = sb.ToString().TrimEnd();
        var budget = Math.Max(120, maxChars);
        return text.Length <= budget ? text : text[..budget] + "…";
    }

    private static (int Hour, int Minute) ParseTime(string hhmm)
    {
        if (!string.IsNullOrWhiteSpace(hhmm) &&
            TimeOnly.TryParseExact(hhmm.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return (parsed.Hour, parsed.Minute);
        }

        return (9, 0);
    }
}

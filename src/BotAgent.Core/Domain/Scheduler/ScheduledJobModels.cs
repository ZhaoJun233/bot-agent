using System.Text;
using System.Text.Json;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;

namespace BotAgent.Domain.Scheduler;

public static class ScheduledJobValidator
{
    public static bool IsQqOfficialScope(string? scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey)) return false;
        var trimmed = scopeKey.Trim();
        if (trimmed.StartsWith("official:", StringComparison.OrdinalIgnoreCase))
            return true;
        return ConversationIdCodec.TryParse(trimmed, out var id)
            && string.Equals(id.PlatformId, PlatformId.QqOfficial, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidJobId(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length is < 3 or > 64)
            return false;
        foreach (var c in jobId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return false;
        }
        return true;
    }

    public static bool IsValidScopeKey(string? scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey) || scopeKey.Length > 256 || scopeKey.Any(char.IsControl)
            || scopeKey.Any(char.IsWhiteSpace))
            return false;
        var trimmed = scopeKey.Trim();
        if (trimmed == SchedulerLimits.WorkspaceOwnerScopeKey)
            return true;
        if (ConversationIdCodec.TryParse(trimmed, out _))
            return true;
        return HasNonEmptyPrefix(trimmed, "group:")
            || HasNonEmptyPrefix(trimmed, "private:")
            || HasNonEmptyPrefix(trimmed, "feishu:")
            || HasNonEmptyPrefix(trimmed, "local:")
            || HasNonEmptyPrefix(trimmed, "official:");
    }

    private static bool HasNonEmptyPrefix(string value, string prefix)
        => value.Length > prefix.Length && value.StartsWith(prefix, StringComparison.Ordinal);

    internal delegate bool FindTimeZoneDelegate(string id, out TimeZoneInfo tz);

    internal static bool TryResolveFallbackTimeZone(string raw, out TimeZoneInfo timeZone, FindTimeZoneDelegate findSystem)
    {
        var fallback = raw.ToLowerInvariant() switch
        {
            "asia/shanghai" or "asia/chongqing" or "asia/harbin" or "asia/urumqi"
                or "asia/hong_kong" or "asia/taipei" or "asia/singapore" => ("China Standard Time", 8.0),
            "asia/tokyo" => ("Tokyo Standard Time", 9.0),
            "asia/seoul" => ("Korea Standard Time", 9.0),
            "asia/bangkok" or "asia/jakarta" => ("SE Asia Standard Time", 7.0),
            "asia/manila" => ("Singapore Standard Time", 8.0),
            "asia/kolkata" or "asia/calcutta" => ("India Standard Time", 5.5),
            "europe/london" => ("GMT Standard Time", 0.0),
            "europe/berlin" or "europe/paris" => ("W. Europe Standard Time", 1.0),
            "europe/moscow" => ("Russian Standard Time", 3.0),
            "america/new_york" or "america/toronto" => ("Eastern Standard Time", -5.0),
            "america/chicago" => ("Central Standard Time", -6.0),
            "america/denver" => ("Mountain Standard Time", -7.0),
            "america/los_angeles" => ("Pacific Standard Time", -8.0),
            "australia/sydney" => ("AUS Eastern Standard Time", 10.0),
            "pacific/auckland" => ("New Zealand Standard Time", 12.0),
            _ => default
        };
        if (fallback.Item1 is null)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
        if (findSystem(fallback.Item1, out timeZone))
            return true;
        timeZone = TimeZoneInfo.CreateCustomTimeZone(raw, TimeSpan.FromHours(fallback.Item2), raw, raw);
        return true;
    }

    public static bool ValidatePayload(string? payloadJson, out string messageText, out string errorCode)
    {
        messageText = string.Empty;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            errorCode = "scheduler_payload_required";
            return false;
        }
        if (Encoding.UTF8.GetByteCount(payloadJson) > SchedulerLimits.MaxPayloadBytes)
        {
            errorCode = "scheduler_payload_too_large";
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "scheduler_payload_invalid";
                return false;
            }
            if (doc.RootElement.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String)
                messageText = textNode.GetString() ?? string.Empty;
            else if (doc.RootElement.TryGetProperty("prompt", out var promptNode) && promptNode.ValueKind == JsonValueKind.String)
                messageText = promptNode.GetString() ?? string.Empty;
            errorCode = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            errorCode = "scheduler_payload_invalid";
            return false;
        }
    }

    public static bool ValidateJobInput(
        string? scopeKey,
        string? kind,
        string? cronExpr,
        string? timezoneIana,
        string? payloadJson,
        string? missedPolicy,
        long nextFireUnix,
        DateTimeOffset referenceUtc,
        out TimeZoneInfo timeZone,
        out long resolvedNextFireUnix,
        out string errorCode,
        int minCronIntervalMinutes = SchedulerLimits.MinCronIntervalMinutes)
    {
        timeZone = TimeZoneInfo.Utc;
        resolvedNextFireUnix = nextFireUnix;

        if (!IsValidScopeKey(scopeKey))
        {
            errorCode = "invalid_scope_key";
            return false;
        }
        if (IsQqOfficialScope(scopeKey))
        {
            errorCode = "passive_trigger_required";
            return false;
        }
        if (!ScheduledJobKind.IsValid(kind))
        {
            errorCode = "invalid_scheduler_kind";
            return false;
        }
        if (!MissedFirePolicy.IsValid(missedPolicy))
        {
            errorCode = "invalid_missed_policy";
            return false;
        }
        if (!CronSchedule.TryResolveTimeZone(timezoneIana, out timeZone))
        {
            errorCode = "invalid_timezone";
            return false;
        }
        if (!ValidatePayload(payloadJson, out var text, out errorCode))
            return false;
        if (kind is ScheduledJobKind.OnceReminder or ScheduledJobKind.CronText or ScheduledJobKind.CronTask
            && string.IsNullOrWhiteSpace(text))
        {
            errorCode = "scheduler_text_required";
            return false;
        }

        if (kind == ScheduledJobKind.OnceReminder)
        {
            if (nextFireUnix <= 0)
            {
                errorCode = "scheduler_fire_time_required";
                return false;
            }
            if (nextFireUnix <= referenceUtc.ToUnixTimeSeconds())
            {
                errorCode = "scheduler_fire_time_in_past";
                return false;
            }
            errorCode = string.Empty;
            return true;
        }

        if (!CronSchedule.TryParse(cronExpr, out var cron, out errorCode) || cron is null)
            return false;
        if (!cron.TryGetNextOccurrence(referenceUtc, timeZone, out var firstNext))
        {
            errorCode = "cron_no_future_occurrence";
            return false;
        }
        var effectiveMin = Math.Max(SchedulerLimits.MinCronIntervalMinutes, minCronIntervalMinutes);
        if (!cron.TryValidateMinimumInterval(effectiveMin, referenceUtc, timeZone))
        {
            errorCode = "cron_interval_too_short";
            return false;
        }
        if (resolvedNextFireUnix <= 0)
        {
            resolvedNextFireUnix = firstNext.ToUnixTimeSeconds();
        }
        errorCode = string.Empty;
        return true;
    }
}

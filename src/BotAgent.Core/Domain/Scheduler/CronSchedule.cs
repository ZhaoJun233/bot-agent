using System.Globalization;

namespace BotAgent.Domain.Scheduler;

/// <summary>
/// 5 段标准 Cron 解析与最小间隔校验（分/时/日/月/周），支持指定时区计算下一触发时刻。
/// </summary>
public sealed class CronSchedule
{
    private const int SearchDaysLimit = 1466;
    private const int MaxExpressionLength = 256;
    private const int MaxFieldSegments = 64;

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _days;
    private readonly bool[] _months;
    private readonly bool[] _weekdays;
    private readonly bool _domWildcard;
    private readonly bool _dowWildcard;

    private CronSchedule(
        bool[] minutes,
        bool[] hours,
        bool[] days,
        bool[] months,
        bool[] weekdays,
        bool domWildcard,
        bool dowWildcard)
    {
        _minutes = minutes;
        _hours = hours;
        _days = days;
        _months = months;
        _weekdays = weekdays;
        _domWildcard = domWildcard;
        _dowWildcard = dowWildcard;
    }

    public static bool TryParse(string? expression, out CronSchedule? schedule, out string errorCode)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaxExpressionLength)
        {
            errorCode = string.IsNullOrWhiteSpace(expression) ? "cron_expr_required" : "cron_expr_invalid";
            return false;
        }

        var parts = expression.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            errorCode = "cron_expr_invalid";
            return false;
        }

        if (!TryParseField(parts[0], 0, 59, out var minutes, out _)
            || !TryParseField(parts[1], 0, 23, out var hours, out _)
            || !TryParseField(parts[2], 1, 31, out var days, out var domWild)
            || !TryParseField(parts[3], 1, 12, out var months, out _)
            || !TryParseField(parts[4], 0, 7, out var rawDow, out var dowWild))
        {
            errorCode = "cron_expr_invalid";
            return false;
        }

        var weekdays = new bool[7];
        for (var i = 0; i < 7; i++)
            weekdays[i] = rawDow[i];
        if (rawDow[7]) weekdays[0] = true;

        schedule = new CronSchedule(minutes, hours, days, months, weekdays, domWild, dowWild);
        errorCode = string.Empty;
        return true;
    }

    public bool TryValidateMinimumInterval(int minMinutes, DateTimeOffset referenceUtc, TimeZoneInfo timeZone)
    {
        if (minMinutes <= 1) return TryGetNextOccurrence(referenceUtc, timeZone, out _);
        var mins = new List<int>(60);
        for (var m = 0; m < 60; m++) if (_minutes[m]) mins.Add(m);
        if (mins.Count == 0) return false;
        for (var i = 1; i < mins.Count; i++)
            if (mins[i] - mins[i - 1] < minMinutes) return false;
        var wrapGap = (60 - mins[^1]) + mins[0];
        if (wrapGap < minMinutes && HasConsecutiveHourWindow()) return false;
        return TryGetNextOccurrence(referenceUtc, timeZone, out _);
    }

    private bool HasConsecutiveHourWindow()
    {
        for (var h = 0; h < 23; h++) if (_hours[h] && _hours[h + 1]) return true;
        if (!_hours[23] || !_hours[0]) return false;
        var start = new DateTime(2024, 1, 1);
        var prevMatched = false;
        for (var d = 0; d < 370; d++)
        {
            var cur = start.AddDays(d);
            var matched = _months[cur.Month] && MatchesDay(cur);
            if (matched && prevMatched) return true;
            prevMatched = matched;
        }
        return false;
    }

    public bool TryGetNextOccurrence(DateTimeOffset afterUtc, TimeZoneInfo timeZone, out DateTimeOffset nextUtc)
    {
        var utcStart = new DateTimeOffset(
            afterUtc.UtcDateTime.Year,
            afterUtc.UtcDateTime.Month,
            afterUtc.UtcDateTime.Day,
            afterUtc.UtcDateTime.Hour,
            afterUtc.UtcDateTime.Minute,
            0,
            TimeSpan.Zero).AddMinutes(1);

        var localStart = TimeZoneInfo.ConvertTimeFromUtc(utcStart.UtcDateTime, timeZone);
        var candidate = new DateTime(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, localStart.Minute, 0, DateTimeKind.Unspecified);
        var limit = candidate.AddDays(SearchDaysLimit);

        while (candidate <= limit)
        {
            if (!_months[candidate.Month])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }

            if (!MatchesDay(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                var nextHour = candidate.Hour + 1;
                candidate = nextHour < 24
                    ? new DateTime(candidate.Year, candidate.Month, candidate.Day, nextHour, 0, 0, DateTimeKind.Unspecified)
                    : candidate.Date.AddDays(1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            if (timeZone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            if (timeZone.IsAmbiguousTime(candidate))
            {
                var offsets = timeZone.GetAmbiguousTimeOffsets(candidate);
                Array.Sort(offsets, static (a, b) => b.CompareTo(a));
                foreach (var offset in offsets)
                {
                    var ambiguousDto = new DateTimeOffset(candidate, offset).ToUniversalTime();
                    if (ambiguousDto > afterUtc)
                    {
                        nextUtc = ambiguousDto;
                        return true;
                    }
                }
            }
            else
            {
                var utc = TimeZoneInfo.ConvertTimeToUtc(candidate, timeZone);
                var dto = new DateTimeOffset(utc, TimeSpan.Zero);
                if (dto > afterUtc)
                {
                    nextUtc = dto;
                    return true;
                }
            }

            candidate = candidate.AddMinutes(1);
        }

        nextUtc = default;
        return false;
    }

    private bool MatchesDay(DateTime local)
    {
        var domMatch = _days[local.Day];
        var dowMatch = _weekdays[(int)local.DayOfWeek];
        if (!_domWildcard && !_dowWildcard)
            return domMatch || dowMatch;
        return domMatch && dowMatch;
    }

    public static bool TryResolveTimeZone(string? ianaOrId, out TimeZoneInfo timeZone)
    {
        var raw = string.IsNullOrWhiteSpace(ianaOrId) ? ScheduledJobModelsDefaults.DefaultTimeZone : ianaOrId.Trim();
        if (raw.Length > 80 || raw.Any(char.IsControl))
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
        if (raw.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("Etc/GMT", StringComparison.OrdinalIgnoreCase))
        {
            timeZone = TimeZoneInfo.Utc;
            return true;
        }
        if (TryFindSystemTimeZone(raw, out timeZone))
            return true;

        if (ScheduledJobValidator.TryResolveFallbackTimeZone(raw, out timeZone, TryFindSystemTimeZone))
            return true;
        return false;
    }

    private static bool TryFindSystemTimeZone(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }

    private static bool TryParseField(string token, int min, int max, out bool[] allowed, out bool isWildcard)
    {
        allowed = new bool[max + 1];
        isWildcard = token == "*";
        if (string.IsNullOrWhiteSpace(token)) return false;

        var segments = token.Split(',', StringSplitOptions.None);
        if (segments.Length > MaxFieldSegments) return false;
        var maxSpan = max - min + 1;
        foreach (var seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg)) return false;
            var slashIdx = seg.IndexOf('/');
            var rangePart = slashIdx >= 0 ? seg[..slashIdx] : seg;
            var step = 1;
            if (slashIdx >= 0)
            {
                if (slashIdx == seg.Length - 1
                    || !int.TryParse(seg[(slashIdx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step)
                    || step <= 0 || step > maxSpan)
                    return false;
            }

            int start;
            int end;
            if (rangePart == "*")
            {
                start = min;
                end = max;
            }
            else if (rangePart.Contains('-', StringComparison.Ordinal))
            {
                var dashIdx = rangePart.IndexOf('-');
                if (dashIdx <= 0 || dashIdx >= rangePart.Length - 1
                    || rangePart.IndexOf('-', dashIdx + 1) >= 0
                    || !int.TryParse(rangePart[..dashIdx], NumberStyles.None, CultureInfo.InvariantCulture, out start)
                    || !int.TryParse(rangePart[(dashIdx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out end)
                    || start < min || end > max || start > end)
                    return false;
            }
            else
            {
                if (!int.TryParse(rangePart, NumberStyles.None, CultureInfo.InvariantCulture, out start)
                    || start < min || start > max)
                    return false;
                end = slashIdx >= 0 ? max : start;
            }

            for (long v = start; v <= end; v += step)
                allowed[(int)v] = true;
        }

        return allowed.Any(x => x);
    }
}

internal static class ScheduledJobModelsDefaults
{
    internal const string DefaultTimeZone = "Asia/Shanghai";
}

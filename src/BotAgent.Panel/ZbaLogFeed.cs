using System.Globalization;

namespace BotAgent.Panel;

/// <summary>A safe append-order row. Sequence is decimal text to preserve Int64 precision in clients.</summary>
public sealed record ZbaLogFeedEntry(
    string Id, string Sequence, long Time, string Level, string Source, string Message, bool Redacted)
{
    public override string ToString() => "ZbaLogFeedEntry";
}

/// <summary>Ascending rows and their atomic delivery watermark; a reset replaces the client tail.</summary>
public sealed record ZbaLogFeedPage(
    IReadOnlyList<ZbaLogFeedEntry> Items, string Cursor, bool HasMore, int Capacity,
    string? ResetReason = null, long? OmittedCount = null);

/// <summary>Wire cursors are opaque to clients. Only canonical, bounded epoch/sequence tokens are accepted.</summary>
public readonly record struct ZbaLogFeedCursor(Guid Epoch, long Sequence)
{
    public const int PageSize = 200;
    public const int MaxLength = 55;

    public override string ToString() => "v1." + Epoch.ToString("N") + "." + Sequence.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out ZbaLogFeedCursor cursor)
    {
        cursor = default;
        if (text is null || text.Length is < 37 or > MaxLength || !text.StartsWith("v1.", StringComparison.Ordinal)
            || text[35] != '.' || !Guid.TryParseExact(text.AsSpan(3, 32), "N", out var epoch)) return false;
        var number = text.AsSpan(36);
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)) return false;
        var parsed = new ZbaLogFeedCursor(epoch, sequence);
        if (!string.Equals(text, parsed.ToString(), StringComparison.Ordinal)) return false;
        cursor = parsed;
        return true;
    }
}

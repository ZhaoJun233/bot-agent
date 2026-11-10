using System.Globalization;
using System.Text.Json;
using BotAgent.Panel;
using BotAgent.Services;

namespace BotAgent.ReviewRemediationProbe;

internal static class RuntimeLogFeedTests
{
    internal static void Run(Action<bool, string> check)
    {
        CheckPaging(check);
        CheckResets(check);
        CheckPrivacy(check);
        CheckConcurrentAppend(check);
        CheckFileLogProvenance(check);
        CheckCursorGrammar(check);
    }

    private static void CheckPaging(Action<bool, string> check)
    {
        var feed = new RuntimeLogFeedBuffer();
        var empty = feed.Read(null);
        var start = Cursor(empty);
        check(empty.Items.Count == 0 && start.Sequence == 0 && !empty.HasMore && empty.Capacity == 600,
            "feed starts with an atomic empty cursor and capacity 600");
        Append(feed, 650);
        var initial = feed.Read(null);
        check(Sequences(initial).SequenceEqual(Enumerable.Range(451, 200).Select(value => (long)value))
            && Cursor(initial).Sequence == 650 && !initial.HasMore && initial.ResetReason is null,
            "initial feed returns latest 200 ascending with covered watermark");
        var cursor = start with { Sequence = 50 };
        var delivered = new List<ZbaLogFeedEntry>();
        for (var pageIndex = 0; pageIndex < 3; pageIndex++)
        {
            var page = feed.Read(cursor);
            check(page.Items.Count == 200 && page.HasMore == (pageIndex < 2) && page.ResetReason is null,
                "incremental feed drains full retained ring in 200-row pages");
            check(page.Items.Select(row => row.Id).SequenceEqual(feed.Read(cursor).Items.Select(row => row.Id)),
                "retrying the same cursor preserves row identities");
            delivered.AddRange(page.Items);
            cursor = Cursor(page);
        }
        check(delivered.Select(row => long.Parse(row.Sequence, CultureInfo.InvariantCulture))
            .SequenceEqual(Enumerable.Range(51, 600).Select(value => (long)value)), "feed drains without gaps or duplicates");
        check(delivered.Select(row => row.Id).Distinct().Count() == 600
            && delivered.Select(row => row.Time).Distinct().Count() == 1,
            "identical timestamps and duplicate messages still get distinct stable IDs");
        var unchanged = feed.Read(cursor);
        check(unchanged.Items.Count == 0 && unchanged.Cursor == cursor.ToString() && !unchanged.HasMore,
            "empty poll does not advance delivery cursor");
        feed.Append(1, "[Runtime] 已启动", true);
        var next = feed.Read(Cursor(initial));
        check(next.Items.Count == 1 && next.Items[0].Sequence == "651" && Cursor(next).Sequence == 651,
            "append after initial snapshot remains visible even with older wall clock");
    }

    private static void CheckResets(Action<bool, string> check)
    {
        var feed = new RuntimeLogFeedBuffer();
        var zero = Cursor(feed.Read(null));
        Append(feed, 650);
        var expired = feed.Read(zero);
        check(expired.ResetReason == "expired" && expired.OmittedCount == 450 && expired.Items.Count == 200
            && expired.Items[0].Sequence == "451" && Cursor(expired).Sequence == 650,
            "expiry reports exact loss including retained rows skipped by latest-tail reset");
        var boundary = feed.Read(zero with { Sequence = 50 });
        var pastBoundary = feed.Read(zero with { Sequence = 49 });
        check(boundary.ResetReason is null && boundary.Items[0].Sequence == "51"
            && pastBoundary.ResetReason == "expired" && pastBoundary.OmittedCount == 401,
            "cursor at oldest minus one is valid; older cursor resets explicitly");
        var ahead = feed.Read(zero with { Sequence = long.MaxValue });
        check(ahead.ResetReason == "ahead" && ahead.OmittedCount is null && ahead.Items.Count == 200
            && Cursor(ahead).Sequence == 650 && !ahead.HasMore, "ahead cursor resets without fabricating omission count");
        var restarted = new RuntimeLogFeedBuffer();
        var restartEmpty = restarted.Read(Cursor(expired));
        check(restartEmpty.ResetReason == "restart" && restartEmpty.Items.Count == 0
            && Cursor(restartEmpty).Sequence == 0 && Cursor(restartEmpty).Epoch != zero.Epoch,
            "restart with empty ring returns a fresh explicit epoch");
        Append(restarted, 250);
        var restart = restarted.Read(Cursor(expired));
        check(restart.ResetReason == "restart" && restart.Items.Count == 200 && restart.OmittedCount is null
            && restart.Items[0].Sequence == "51" && !restart.HasMore, "restart returns available tail without cross-epoch loss claims");
    }

    private static void CheckPrivacy(Action<bool, string> check)
    {
        var feed = new RuntimeLogFeedBuffer();
        const string privateText = "synthetic-private-content 10001 https://example.com/private";
        feed.Append(1, "[Plugin] ERROR plugin_reload_failed " + privateText, false);
        feed.Append(1, "[Plugin] ERROR plugin_reload_failed " + privateText, true);
        feed.Append(1, "received " + privateText + "\n[Model] WARN empty_reply", false);
        feed.Append(1, new string('x', 100_000), false);
        var page = feed.Read(null);
        check(page.Items[0].Redacted && page.Items[0].Source == "runtime" && page.Items[0].Level == "info"
            && page.Items[2].Redacted && page.Items[2].Level == "info",
            "untrusted disk/backfill and forged prefixes cannot create severity or source");
        check(!page.Items[1].Redacted && page.Items[1].Source == "plugin" && page.Items[1].Level == "error",
            "trusted process provenance permits only fixed summary classification");
        var serialized = JsonSerializer.Serialize(page);
        check(!serialized.Contains(privateText, StringComparison.Ordinal) && !serialized.Contains("example.com", StringComparison.Ordinal)
            && page.Items.All(row => row.Message.Length <= 512 && row.Source.Length <= 64),
            "safe-only feed never exposes original content and stays bounded");
    }

    private static void CheckConcurrentAppend(Action<bool, string> check)
    {
        var feed = new RuntimeLogFeedBuffer();
        var start = Cursor(feed.Read(null));
        Parallel.For(0, 500, index => feed.Append(500 - index, "[Runtime] 已启动", true));
        var rows = new List<ZbaLogFeedEntry>();
        var cursor = start;
        ZbaLogFeedPage page;
        do
        {
            page = feed.Read(cursor);
            rows.AddRange(page.Items);
            cursor = Cursor(page);
        } while (page.HasMore);
        check(rows.Select(row => long.Parse(row.Sequence, CultureInfo.InvariantCulture))
            .SequenceEqual(Enumerable.Range(1, 500).Select(value => (long)value)) && rows.Select(row => row.Id).Distinct().Count() == 500,
            "concurrent append sequence follows the locked ring order, not timestamp order");
    }

    private static void CheckFileLogProvenance(Action<bool, string> check)
    {
        var writeToFile = FileLog.WriteToFile;
        var verbose = FileLog.Verbose;
        FileLog.WriteToFile = false;
        FileLog.Verbose = false;
        try
        {
            var before = Cursor(FileLog.ReadRuntimeFeed(null));
            FileLog.Write("[Plugin] ERROR plugin_reload_failed synthetic-private-content");
            FileLog.Write("Plugin", "plugin_reload_failed synthetic-private-content");
            FileLog.Write("Plugin] ERROR plugin_reload_failed [", "synthetic-private-content");
            var page = FileLog.ReadRuntimeFeed(before);
            check(page.Items.Count == 3 && page.Items[0].Redacted && page.Items[0].Level == "info"
                && !page.Items[1].Redacted && page.Items[1].Level == "error" && page.Items[2].Redacted,
                "FileLog feed trusts explicit intact tags only, never raw or forged tag text");
            check(Cursor(page).Sequence == before.Sequence + 3, "FileLog appends assign monotonic feed identities under its gate");
        }
        finally
        {
            FileLog.WriteToFile = writeToFile;
            FileLog.Verbose = verbose;
        }
    }

    private static void CheckCursorGrammar(Action<bool, string> check)
    {
        var valid = new ZbaLogFeedCursor(Guid.NewGuid(), long.MaxValue).ToString();
        check(ZbaLogFeedCursor.TryParse(valid, out var cursor) && cursor.Sequence == long.MaxValue,
            "cursor preserves full Int64 precision in canonical decimal text");
        var prefix = valid[..36];
        foreach (var invalid in new[] { "", "garbage", prefix + "-1", prefix + "+1", prefix + "01", prefix + "1.0",
                     prefix + "9223372036854775808", valid.ToUpperInvariant(), valid + " ", new string('x', 600) })
            check(!ZbaLogFeedCursor.TryParse(invalid, out _), "feed rejects malformed/noncanonical/out-of-bounds cursor");
    }

    private static ZbaLogFeedCursor Cursor(ZbaLogFeedPage page)
        => ZbaLogFeedCursor.TryParse(page.Cursor, out var cursor) ? cursor : throw new InvalidOperationException("Invalid test cursor");

    private static IEnumerable<long> Sequences(ZbaLogFeedPage page)
        => page.Items.Select(row => long.Parse(row.Sequence, CultureInfo.InvariantCulture));

    private static void Append(RuntimeLogFeedBuffer feed, int count)
    {
        for (var index = 0; index < count; index++) feed.Append(1000, "[Model] empty_reply", true);
    }
}

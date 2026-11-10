using System.Globalization;
using BotAgent.Panel;

namespace BotAgent.Services;

/// <summary>
/// Safe-only ring, independent of raw SSE and state events. The owner holds FileLog.Gate for both append and read;
/// no callbacks, disk IO or mutable raw text escape this buffer. Backfill uses the same gate with untrusted provenance.
/// </summary>
public sealed class RuntimeLogFeedBuffer
{
    private readonly object _gate = new();
    private readonly Guid _epoch = Guid.NewGuid();
    private readonly Queue<ZbaLogFeedEntry> _entries = new();
    private long _sequence;

    public void Append(long time, string text, bool canClassify)
    {
        lock (_gate) AppendLocked(time, text, canClassify);
    }

    private void AppendLocked(long time, string text, bool canClassify)
    {
        var safe = RuntimeLogProjection.ProjectLine(time, text, canClassify);
        var sequence = checked(++_sequence).ToString(CultureInfo.InvariantCulture);
        _entries.Enqueue(new ZbaLogFeedEntry(_epoch.ToString("N") + ":" + sequence, sequence,
            safe.Time, safe.Level, safe.Source, safe.Message, safe.Redacted));
        while (_entries.Count > RuntimeLogProjection.Capacity) _entries.Dequeue();
    }

    public ZbaLogFeedPage Read(ZbaLogFeedCursor? cursor)
    {
        lock (_gate) return ReadLocked(cursor);
    }

    private ZbaLogFeedPage ReadLocked(ZbaLogFeedCursor? cursor)
    {
        var oldest = _sequence - _entries.Count + 1;
        var reset = cursor is null ? null
            : cursor.Value.Epoch != _epoch ? "restart"
            : cursor.Value.Sequence > _sequence ? "ahead"
            : cursor.Value.Sequence < oldest - 1 ? "expired" : null;
        // Initial and reset responses show the available latest tail. Normal reads never skip pending rows.
        var tail = cursor is null || reset is not null;
        var count = tail ? Math.Min(_entries.Count, ZbaLogFeedCursor.PageSize)
            : (int)Math.Min(_sequence - cursor!.Value.Sequence, ZbaLogFeedCursor.PageSize);
        var skip = tail ? _entries.Count - count : (int)(cursor!.Value.Sequence - oldest + 1);
        var items = _entries.Skip(skip).Take(count).ToArray();
        var delivered = count > 0 ? oldest + skip + count - 1 : tail ? _sequence : cursor!.Value.Sequence;
        // Only same-epoch expiry can measure missing rows (including the retained rows skipped by a reset tail).
        long? omitted = reset == "expired" ? oldest + skip - cursor!.Value.Sequence - 1 : null;
        return new ZbaLogFeedPage(items, new ZbaLogFeedCursor(_epoch, delivered).ToString(),
            delivered < _sequence, RuntimeLogProjection.Capacity, reset, omitted);
    }
}

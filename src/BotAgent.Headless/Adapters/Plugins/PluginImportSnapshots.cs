using System.Text.Json;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

/// <summary>Caller-bound snapshots: at most 64 entries and 8 MiB serialized payload (not total CLR heap).</summary>
internal sealed class PluginImportSnapshots(Func<DateTimeOffset> now)
{
    private const int MaxSnapshots = 64;
    private const int MaxBytes = 8 * 1024 * 1024;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _sequence;
    private sealed record Entry(string Caller, string Kind, DateTimeOffset Expires, byte[] Payload, long Sequence);

    internal (string Id, string ExpiresAt) Add<T>(string caller, string kind, T payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > MaxBytes) throw new ZbaBackendException(422, "plugin_snapshot_too_large", "来源资料过大，请缩小范围后重试。");
        lock (_gate)
        {
            Prune();
            while (_entries.Count >= MaxSnapshots || _entries.Values.Sum(e => e.Payload.Length) + bytes.Length > MaxBytes)
                _entries.Remove(_entries.MinBy(e => e.Value.Sequence).Key);
            var id = kind + "-" + Guid.NewGuid().ToString("N");
            var expires = now().AddMinutes(10);
            _entries.Add(id, new(caller, kind, expires, bytes, ++_sequence));
            return (id, expires.ToString("O"));
        }
    }

    internal T Get<T>(string? id, string caller, string kind)
    {
        lock (_gate)
        {
            Prune();
            if (id is null || !_entries.TryGetValue(id, out var entry) || entry.Caller != caller || entry.Kind != kind)
                throw Refresh();
            return JsonSerializer.Deserialize<T>(entry.Payload) ?? throw Refresh();
        }
    }

    private void Prune()
    {
        var current = now();
        foreach (var key in _entries.Where(p => p.Value.Expires <= current).Select(p => p.Key).ToArray()) _entries.Remove(key);
    }

    internal static ZbaBackendException Refresh() => new(409, "plugin_preview_refresh_required", "来源、目录或预览已变化/过期，请重新发现并预览，再确认风险。");
}
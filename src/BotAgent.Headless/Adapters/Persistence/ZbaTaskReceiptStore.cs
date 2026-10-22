using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 提交回执：只保存幂等键哈希、任务 id、受理实例与时间，用于“POST 响应丢失 / 重启后”
/// 证明提交曾被受理。有界（512 条 / 7 天）、原子替换；不保存 prompt、结果或任何正文。
/// 读取失败按空回执继续（可用性优先），但绝不把损坏内容当成 found。
/// 文件读写复用 <see cref="ZbaEventSequenceStore"/> 的持久化原语，不在这里直接碰文件系统。
/// </summary>
internal sealed class ZbaTaskReceiptStore
{
    private const int MaxReceipts = 512;

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    /// <summary>旧 → 新；写入时整体替换，失败时不提交候选列表。</summary>
    private List<ZbaTaskReceipt> _receipts = new();

    internal ZbaTaskReceiptStore(string path, Func<DateTimeOffset> now, Action<string>? log)
    {
        _path = path;
        _now = now;
        _log = log;
        Load();
    }

    internal int Count
    {
        get
        {
            lock (_gate) return _receipts.Count;
        }
    }

    /// <summary>写入一条回执；返回 false 表示没有持久化，调用方必须回滚本次受理。</summary>
    internal bool TryAdd(string keyHash, string taskId, string instanceId, DateTimeOffset acceptedAt)
    {
        lock (_gate)
        {
            var candidate = PruneLocked(new List<ZbaTaskReceipt>(_receipts), out _);
            var receipt = new ZbaTaskReceipt(keyHash, taskId, instanceId, acceptedAt);
            var existing = candidate.FindIndex(item => item.KeyHash == keyHash);
            if (existing >= 0) candidate[existing] = receipt;
            else candidate.Add(receipt);

            candidate = PruneLocked(candidate, out _);
            if (!SaveLocked(candidate)) return false;
            _receipts = candidate;
            return true;
        }
    }

    internal ZbaTaskReceipt? Find(string keyHash)
    {
        lock (_gate)
        {
            _receipts = PruneLocked(_receipts, out var pruned);
            // 过期不只从内存消失：物理文件也要压缩，否则任务 id/时间会一直留在磁盘上。
            if (pruned) SaveLocked(_receipts);
            return _receipts.FirstOrDefault(receipt => receipt.KeyHash == keyHash);
        }
    }

    private List<ZbaTaskReceipt> PruneLocked(List<ZbaTaskReceipt> receipts, out bool changed)
    {
        var before = receipts.Count;
        var now = _now();
        receipts.RemoveAll(receipt => now - receipt.AcceptedAt > Lifetime);
        if (receipts.Count > MaxReceipts) receipts.RemoveRange(0, receipts.Count - MaxReceipts);
        changed = receipts.Count != before;
        return receipts;
    }

    private void Load()
    {
        var outcome = ZbaEventSequenceStore.TryReadText(_path, out var text, out var failure);
        if (outcome == ZbaEventSequenceStore.ReadOutcome.Missing) return;
        if (outcome != ZbaEventSequenceStore.ReadOutcome.Ok)
        {
            _log?.Invoke($"[Zba] 任务回执读取失败（{failure?.GetType().Name ?? "unknown"}）：按空回执继续");
            _receipts = new List<ZbaTaskReceipt>();
            return;
        }

        try
        {
            var root = JsonNode.Parse(text);
            if (root?["receipts"] is not JsonArray array) return;
            var storedCount = array.Count;

            var loaded = new List<ZbaTaskReceipt>();
            foreach (var node in array)
            {
                var keyHash = node?["keyHash"]?.GetValue<string>();
                var taskId = node?["taskId"]?.GetValue<string>();
                var instanceId = node?["instanceId"]?.GetValue<string>();
                var acceptedAtText = node?["acceptedAt"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(keyHash) || string.IsNullOrWhiteSpace(taskId)
                    || string.IsNullOrWhiteSpace(instanceId)
                    || !DateTimeOffset.TryParse(acceptedAtText, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var acceptedAt))
                {
                    continue;
                }

                loaded.Add(new ZbaTaskReceipt(keyHash, taskId, instanceId, acceptedAt));
            }

            _receipts = PruneLocked(loaded, out var pruned);
            // 启动时压缩磁盘：过期或无效条目不能只从内存消失。写失败只记日志，
            // 内存仍按过期处理，下一次成功写入会再次压缩。
            if (pruned || _receipts.Count != storedCount) SaveLocked(_receipts);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or FormatException or ArgumentException)
        {
            _log?.Invoke($"[Zba] 任务回执解析失败（{exception.GetType().Name}）：按空回执继续");
            _receipts = new List<ZbaTaskReceipt>();
        }
    }

    private bool SaveLocked(List<ZbaTaskReceipt> receipts)
    {
        var array = new JsonArray();
        foreach (var receipt in receipts)
        {
            array.Add(new JsonObject
            {
                ["keyHash"] = receipt.KeyHash,
                ["taskId"] = receipt.TaskId,
                ["instanceId"] = receipt.InstanceId,
                ["acceptedAt"] = receipt.AcceptedAt.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        var root = new JsonObject { ["version"] = 1, ["receipts"] = array };
        return ZbaEventSequenceStore.TryWriteTextAtomic(_path, root.ToJsonString(), _log,
            exception => $"[Zba] 任务回执写入失败（{exception.GetType().Name}）：本次提交不受理");
    }
}

internal sealed record ZbaTaskReceipt(string KeyHash, string TaskId, string InstanceId, DateTimeOffset AcceptedAt);

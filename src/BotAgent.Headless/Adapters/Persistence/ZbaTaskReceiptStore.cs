using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 提交回执：只保存幂等键哈希、任务 id、受理实例与时间，用于“POST 响应丢失 / 重启后”
/// 证明提交曾被受理。有界（512 条 / 7 天）、原子替换；不保存 prompt、结果或任何正文。
/// 历史回执不可读或无效时，查询保持 unknown，禁止新受理覆盖未知历史；修复后重启再验证。
/// 文件读写复用 <see cref="ZbaEventSequenceStore"/> 的持久化原语，不在这里直接碰文件系统。
/// </summary>
internal sealed class ZbaTaskReceiptStore
{
    private const int MaxReceipts = 512;

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly string _path;
    private readonly string _markerPath;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    /// <summary>旧 → 新；写入时整体替换，失败时不提交候选列表。</summary>
    private List<ZbaTaskReceipt> _receipts = new();
    private bool _historyAvailable = true;
    private bool _markerEstablished;

    internal ZbaTaskReceiptStore(string path, Func<DateTimeOffset> now, Action<string>? log)
    {
        _path = path;
        _markerPath = path + ".initialized";
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
            if (!_historyAvailable) return false;
            // 新安装先落盘有效空历史，再写标记，最后才可写受理回执。
            // 任一步中断都不会留下“任务已受理、却未建立丢失保护”的状态。
            if (!_markerEstablished && (!SaveLocked(_receipts) || !WriteMarker())) return false;
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
        if (!LoadMarker()) return;
        var outcome = ZbaEventSequenceStore.TryReadText(_path, out var text, out var failure);
        if (outcome == ZbaEventSequenceStore.ReadOutcome.Missing)
        {
            if (_markerEstablished) MarkUnavailable("读取", "missing_initialized_history");
            return;
        }
        if (outcome != ZbaEventSequenceStore.ReadOutcome.Ok)
        {
            MarkUnavailable("读取", failure?.GetType().Name ?? "unknown");
            return;
        }

        try
        {
            var root = JsonNode.Parse(text);
            if (root is not JsonObject document
                || document["version"] is not JsonValue version
                || !version.TryGetValue<int>(out var formatVersion) || formatVersion != 1
                || document["receipts"] is not JsonArray array)
                throw new JsonException("Invalid task receipt document");

            var loaded = new List<ZbaTaskReceipt>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in array)
            {
                var keyHash = node?["keyHash"]?.GetValue<string>();
                var taskId = node?["taskId"]?.GetValue<string>();
                var instanceId = node?["instanceId"]?.GetValue<string>();
                var acceptedAtText = node?["acceptedAt"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(keyHash) || string.IsNullOrWhiteSpace(taskId)
                    || string.IsNullOrWhiteSpace(instanceId)
                    || !DateTimeOffset.TryParse(acceptedAtText, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var acceptedAt)
                    || !keys.Add(keyHash))
                {
                    throw new JsonException("Invalid task receipt entry");
                }

                loaded.Add(new ZbaTaskReceipt(keyHash, taskId, instanceId, acceptedAt));
            }

            _receipts = PruneLocked(loaded, out var pruned);
            // 旧版本的有效回执在启动时补标记，不依赖下一次提交。
            if (!_markerEstablished && !WriteMarker())
            {
                MarkUnavailable("初始化标记", "write_failed");
                return;
            }
            // 整个文档验证后才提交到内存；无效条目不能静默丢弃，否则旧键可能再次执行。
            // 启动时压缩磁盘中的过期条目。写失败只记日志，
            // 内存仍按过期处理，下一次成功写入会再次压缩。
            if (pruned) SaveLocked(_receipts);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or FormatException or ArgumentException)
        {
            MarkUnavailable("解析", exception.GetType().Name);
        }
    }

    private bool LoadMarker()
    {
        var outcome = ZbaEventSequenceStore.TryReadText(_markerPath, out var text, out var failure);
        if (outcome == ZbaEventSequenceStore.ReadOutcome.Missing) return true;
        if (outcome == ZbaEventSequenceStore.ReadOutcome.Ok && text == "1")
        {
            _markerEstablished = true;
            return true;
        }

        MarkUnavailable("初始化标记", failure?.GetType().Name ?? "invalid_marker");
        return false;
    }

    private bool WriteMarker()
    {
        _markerEstablished = ZbaEventSequenceStore.TryWriteTextAtomic(_markerPath, "1", _log,
            exception => $"[Zba] 任务回执初始化标记写入失败（{exception.GetType().Name}）：本次提交不受理");
        return _markerEstablished;
    }

    private void MarkUnavailable(string operation, string reason)
    {
        _historyAvailable = false;
        _receipts = new List<ZbaTaskReceipt>();
        _log?.Invoke($"[Zba] 任务回执{operation}失败（{reason}）：拒绝新任务受理，修复回执后重启");
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

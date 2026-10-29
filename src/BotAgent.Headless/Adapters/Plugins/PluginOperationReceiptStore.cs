extern alias StorageModule;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Panel;
using TextSnapshotFile = StorageModule::BotAgent.Adapters.Persistence.TextSnapshotFile;

namespace BotAgent.Adapters.Plugins;

/// <summary>
/// 插件操作回执：只保存幂等键哈希、操作形状、代际与时间，用于“POST 响应丢失 / 重启后”
/// 证明这次变更曾被受理。有界（256 条 / 7 天）、原子替换；不保存清单正文、下载地址或任何密钥。
/// 读取失败按空回执继续（可用性优先），但绝不把损坏内容当成 found。
/// </summary>
public sealed class PluginOperationReceiptStore
{
    private const int MaxReceipts = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16
    };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private List<StoredReceipt> _receipts = new();

    public PluginOperationReceiptStore(string path, Func<DateTimeOffset> now, Action<string>? log = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _log = log;
        Load();
    }

    /// <summary>写入一条回执；返回 false 表示没有落盘，调用方仍应返回本次结果但必须记日志。</summary>
    public bool TryAdd(string keyHash, ZbaPluginOperation operation, string revision, string caller)
    {
        lock (_gate)
        {
            var candidate = Prune(new List<StoredReceipt>(_receipts));
            var receipt = new StoredReceipt(keyHash, operation, revision, caller, _now());
            var existing = candidate.FindIndex(item => item.KeyHash == keyHash);
            if (existing >= 0) candidate[existing] = receipt;
            else candidate.Add(receipt);
            candidate = Prune(candidate);
            if (!Save(candidate)) return false;
            _receipts = candidate;
            return true;
        }
    }

    public ZbaPluginOperationResult? Find(string keyHash)
    {
        lock (_gate)
        {
            var pruned = Prune(_receipts);
            if (pruned.Count != _receipts.Count) Save(pruned);
            _receipts = pruned;
            var found = pruned.FirstOrDefault(receipt => receipt.KeyHash == keyHash);
            return found is null ? null : new ZbaPluginOperationResult(found.Operation, found.Revision);
        }
    }

    private List<StoredReceipt> Prune(List<StoredReceipt> receipts)
    {
        var now = _now();
        receipts.RemoveAll(receipt => now - receipt.RecordedAt > Lifetime);
        if (receipts.Count > MaxReceipts) receipts.RemoveRange(0, receipts.Count - MaxReceipts);
        return receipts;
    }

    private void Load()
    {
        string? text;
        try { text = TextSnapshotFile.Read(_path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("[Plugin] 操作回执读取失败（" + exception.GetType().Name + "）：按空回执继续");
            return;
        }

        if (text is null) return;
        try
        {
            var root = JsonNode.Parse(text);
            if (root?["receipts"] is not JsonArray array) return;
            var loaded = new List<StoredReceipt>();
            foreach (var node in array)
            {
                var keyHash = node?["keyHash"]?.GetValue<string>();
                var revision = node?["revision"]?.GetValue<string>();
                var caller = node?["caller"]?.GetValue<string>();
                var recordedAtText = node?["recordedAt"]?.GetValue<string>();
                var operationNode = node?["operation"];
                if (string.IsNullOrWhiteSpace(keyHash) || string.IsNullOrWhiteSpace(revision)
                    || operationNode is null
                    || !DateTimeOffset.TryParse(recordedAtText, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var recordedAt))
                    continue;
                var operation = operationNode.Deserialize<ZbaPluginOperation>(Json);
                if (operation is null || string.IsNullOrWhiteSpace(operation.Id) || string.IsNullOrWhiteSpace(operation.Kind))
                    continue;
                loaded.Add(new StoredReceipt(keyHash, operation, revision, caller ?? string.Empty, recordedAt));
            }

            _receipts = Prune(loaded);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or FormatException or ArgumentException or NotSupportedException)
        {
            _log?.Invoke("[Plugin] 操作回执解析失败（" + exception.GetType().Name + "）：按空回执继续");
            _receipts = new List<StoredReceipt>();
        }
    }

    private bool Save(List<StoredReceipt> receipts)
    {
        var array = new JsonArray();
        foreach (var receipt in receipts)
        {
            array.Add(new JsonObject
            {
                ["keyHash"] = receipt.KeyHash,
                ["operation"] = JsonSerializer.SerializeToNode(receipt.Operation, Json),
                ["revision"] = receipt.Revision,
                ["caller"] = receipt.Caller,
                ["recordedAt"] = receipt.RecordedAt.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        try
        {
            TextSnapshotFile.Write(_path, new JsonObject { ["version"] = 1, ["receipts"] = array }.ToJsonString());
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("[Plugin] 操作回执写入失败（" + exception.GetType().Name + "）：本次回执未落盘");
            return false;
        }
    }

    private sealed record StoredReceipt(string KeyHash, ZbaPluginOperation Operation, string Revision,
        string Caller, DateTimeOffset RecordedAt);
}

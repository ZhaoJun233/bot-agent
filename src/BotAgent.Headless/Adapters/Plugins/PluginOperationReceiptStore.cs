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
/// 读取损坏时拒绝写入；旧回执仍可查询，但缺少意图指纹时不可安全重放变更。
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
    private bool _unavailable;

    public PluginOperationReceiptStore(string path, Func<DateTimeOffset> now, Action<string>? log = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _log = log;
        Load();
    }

    /// <summary>Return false unless the candidate was durably written; callers must not stage without a reservation.</summary>
    public bool TryAdd(string keyHash, ZbaPluginOperation operation, string revision, string caller, string? fingerprint = null)
    {
        lock (_gate)
        {
            if (_unavailable) return false;
            var candidate = Prune(new List<StoredReceipt>(_receipts));
            var receipt = new StoredReceipt(keyHash, operation, revision, caller, _now(), fingerprint);
            var existing = candidate.FindIndex(item => item.KeyHash == keyHash);
            if (existing >= 0)
            {
                var previous = candidate[existing];
                if (previous.Caller != caller || previous.Fingerprint != fingerprint)
                    throw new ZbaBackendException(409, "idempotency_conflict", "这个操作键已经用于另一份变更。");
                candidate[existing] = receipt with { RecordedAt = previous.RecordedAt };
            }
            else candidate.Add(receipt);
            candidate = Prune(candidate);
            if (!Save(candidate)) return false;
            _receipts = candidate;
            return true;
        }
    }

    public ZbaPluginOperationResult? Find(string keyHash, string? fingerprint = null, string? caller = null)
    {
        lock (_gate)
        {
            if (_unavailable)
                throw new ZbaBackendException(503, "plugin_operation_unknown", "操作回执暂时不可用。");
            var pruned = Prune(new List<StoredReceipt>(_receipts));
            if (pruned.Count != _receipts.Count) Save(pruned);
            _receipts = pruned;
            var found = pruned.FirstOrDefault(receipt => receipt.KeyHash == keyHash);
            if (found is not null && fingerprint is not null &&
                (found.Fingerprint is null || found.Fingerprint != fingerprint || found.Caller != caller))
                throw new ZbaBackendException(409, "idempotency_conflict", "这个操作键已经用于另一份变更或无法核对旧请求意图。");
            if (found?.Operation.ErrorCode == "plugin_operation_unknown")
                throw new ZbaBackendException(503, "plugin_operation_unknown", "这个操作仍无法确认，请保留原操作键继续核对，不要发起新的变更。" );
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
        try { text = TextSnapshotFile.Read(_path, maxBytes: 1_048_576); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _unavailable = true;
            _log?.Invoke("[Plugin] 操作回执读取失败（" + exception.GetType().Name + "）：拒绝变更");
            return;
        }

        if (text is null) return;
        try
        {
            var root = JsonNode.Parse(text);
            if (root?["version"]?.GetValue<int>() != 1 || root?["receipts"] is not JsonArray array)
                throw new JsonException("Invalid receipt format.");
            var loaded = new List<StoredReceipt>();
            foreach (var node in array)
            {
                var keyHash = node?["keyHash"]?.GetValue<string>();
                var revision = node?["revision"]?.GetValue<string>();
                var caller = node?["caller"]?.GetValue<string>();
                var fingerprint = node?["fingerprint"]?.GetValue<string>();
                if (fingerprint is not null && (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)))
                    throw new JsonException("Invalid intent fingerprint.");
                var recordedAtText = node?["recordedAt"]?.GetValue<string>();
                var operationNode = node?["operation"];
                if (string.IsNullOrWhiteSpace(keyHash) || string.IsNullOrWhiteSpace(revision)
                    || operationNode is null
                    || !DateTimeOffset.TryParse(recordedAtText, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var recordedAt))
                    throw new JsonException("Invalid receipt.");
                var operation = operationNode.Deserialize<ZbaPluginOperation>(Json);
                if (operation is null || string.IsNullOrWhiteSpace(operation.Id) || string.IsNullOrWhiteSpace(operation.Kind))
                    throw new JsonException("Invalid receipt.");
                if (loaded.Any(item => item.KeyHash == keyHash)) throw new JsonException("Duplicate receipt key.");
                loaded.Add(new StoredReceipt(keyHash, operation, revision, caller ?? string.Empty, recordedAt, fingerprint));
            }

            _receipts = Prune(loaded);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or FormatException or ArgumentException or NotSupportedException)
        {
            _unavailable = true;
            _log?.Invoke("[Plugin] 操作回执解析失败（" + exception.GetType().Name + "）：拒绝变更");
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
                ["fingerprint"] = receipt.Fingerprint,
                ["recordedAt"] = receipt.RecordedAt.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        try
        {
            TextSnapshotFile.Write(_path, new JsonObject { ["version"] = 1, ["receipts"] = array }.ToJsonString());
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log?.Invoke("[Plugin] 操作回执写入失败（" + exception.GetType().Name + "）：本次回执未落盘");
            return false;
        }
    }

    private sealed record StoredReceipt(string KeyHash, ZbaPluginOperation Operation, string Revision,
        string Caller, DateTimeOffset RecordedAt, string? Fingerprint);
}

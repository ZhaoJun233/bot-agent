using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Ports;

namespace BotAgent.Storage;

/// <summary>
/// 个人工作台专属消息流存储（Schema v15 兼容）：
/// 仅存入 <c>meta['workspace:owner:messages:v1']</c>，严格保留最近 200 条与幂等键回执，绝不触碰 QQ/飞书会话库。
/// </summary>
public sealed class WorkspaceConversationStore : IWorkspaceConversationStore
{
    private const string MetaKey = "workspace:owner:messages:v1";
    private const int MaxMessages = 200;
    private const int MaxIdempotencyKeys = 64;
    private const int MaxMessageTextChars = 16_000;
    private const int MaxLedgerBytes = 512 * 1024;

    private readonly object _gate = new();

    private sealed record IdempotencyReceipt(string Key, long Sequence);

    private sealed record WorkspaceLedger(
        long LastSequence,
        List<WorkspaceMessageItem> Messages,
        List<IdempotencyReceipt> Receipts)
    {
        public static WorkspaceLedger Empty() => new(0, new List<WorkspaceMessageItem>(), new List<IdempotencyReceipt>());
    }

    public WorkspaceMessagesSnapshot ReadAfter(long afterSequence, int limit = 50)
    {
        var bounded = Math.Clamp(limit, 1, 200);
        lock (_gate)
        {
            var ledger = Load();
            var items = ledger.Messages
                .Where(m => m.Sequence > afterSequence)
                .TakeLast(bounded)
                .ToArray();
            return new WorkspaceMessagesSnapshot(ledger.LastSequence, items);
        }
    }

    public bool TryFindByIdempotencyKey(string idempotencyKey, out WorkspaceMessageItem? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return false;
        lock (_gate)
        {
            var ledger = Load();
            var receipt = ledger.Receipts.FirstOrDefault(r => string.Equals(r.Key, idempotencyKey, StringComparison.Ordinal));
            if (receipt is null) return false;
            item = ledger.Messages.FirstOrDefault(m => m.Sequence == receipt.Sequence)
                ?? new WorkspaceMessageItem(receipt.Sequence, "user", string.Empty, "chat", null, 0, null, "replayed");
            return true;
        }
    }

    public WorkspaceMessageItem Append(
        string role,
        string text,
        string mode,
        long nowUnix,
        string? idempotencyKey = null,
        string? attachmentName = null,
        string? taskId = null,
        string? status = null,
        bool allowIdempotentReplay = true)
    {
        lock (_gate)
        {
            WorkspaceMessageItem? result = null;
            AppDatabase.Write(conn =>
            {
                var raw = AppDatabase.GetMetaInTransaction(conn, MetaKey);
                var ledger = Parse(raw, out var corrupted);
                if (corrupted && !string.IsNullOrWhiteSpace(raw))
                    AppDatabase.SetMetaInTransaction(conn, MetaKey + ":corrupt", raw);
                if (allowIdempotentReplay && !string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    var receipt = ledger.Receipts.FirstOrDefault(r => string.Equals(r.Key, idempotencyKey, StringComparison.Ordinal));
                    if (receipt is not null)
                    {
                        result = ledger.Messages.FirstOrDefault(m => m.Sequence == receipt.Sequence)
                            ?? new WorkspaceMessageItem(receipt.Sequence, role, text, mode, attachmentName, nowUnix, taskId, "replayed");
                        return;
                    }
                }

                var safeText = text ?? string.Empty;
                if (safeText.Length > MaxMessageTextChars)
                    safeText = safeText[..MaxMessageTextChars] + "…";

                var nextSeq = checked(ledger.LastSequence + 1);
                var entry = new WorkspaceMessageItem(
                    nextSeq,
                    role,
                    safeText,
                    mode,
                    string.IsNullOrWhiteSpace(attachmentName) ? null : attachmentName.Trim(),
                    nowUnix,
                    string.IsNullOrWhiteSpace(taskId) ? null : taskId,
                    string.IsNullOrWhiteSpace(status) ? null : status);
                ledger.Messages.Add(entry);
                while (ledger.Messages.Count > MaxMessages)
                    ledger.Messages.RemoveAt(0);

                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    ledger.Receipts.RemoveAll(r => string.Equals(r.Key, idempotencyKey, StringComparison.Ordinal));
                    ledger.Receipts.Add(new IdempotencyReceipt(idempotencyKey, nextSeq));
                    while (ledger.Receipts.Count > MaxIdempotencyKeys)
                        ledger.Receipts.RemoveAt(0);
                }

                var nextLedger = ledger with { LastSequence = nextSeq };
                var serialized = JsonSerializer.Serialize(nextLedger);
                while (Encoding.UTF8.GetByteCount(serialized) > MaxLedgerBytes && nextLedger.Messages.Count > 1)
                {
                    nextLedger.Messages.RemoveAt(0);
                    serialized = JsonSerializer.Serialize(nextLedger);
                }
                AppDatabase.SetMetaInTransaction(conn, MetaKey, serialized);
                result = entry;
            });
            return result!;
        }
    }

    public void Clear(long nowUnix)
    {
        lock (_gate)
        {
            AppDatabase.Write(conn =>
            {
                var raw = AppDatabase.GetMetaInTransaction(conn, MetaKey);
                var ledger = Parse(raw, out var corrupted);
                if (corrupted && !string.IsNullOrWhiteSpace(raw))
                    AppDatabase.SetMetaInTransaction(conn, MetaKey + ":corrupt", raw);
                var cleared = new WorkspaceLedger(ledger.LastSequence, new List<WorkspaceMessageItem>(), new List<IdempotencyReceipt>());
                AppDatabase.SetMetaInTransaction(conn, MetaKey, JsonSerializer.Serialize(cleared));
            });
        }
    }

    private WorkspaceLedger Load()
        => Parse(AppDatabase.GetMeta(MetaKey), out _);

    private static WorkspaceLedger Parse(string? raw, out bool corrupted)
    {
        corrupted = false;
        if (string.IsNullOrWhiteSpace(raw)) return WorkspaceLedger.Empty();
        try
        {
            var obj = JsonSerializer.Deserialize<WorkspaceLedger>(raw);
            if (obj is null)
            {
                corrupted = true;
                return WorkspaceLedger.Empty();
            }
            return new WorkspaceLedger(
                obj.LastSequence,
                obj.Messages ?? new List<WorkspaceMessageItem>(),
                obj.Receipts ?? new List<IdempotencyReceipt>());
        }
        catch (JsonException)
        {
            corrupted = true;
            return WorkspaceLedger.Empty();
        }
    }
}

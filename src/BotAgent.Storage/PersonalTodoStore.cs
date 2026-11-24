using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Services.Todo;

namespace BotAgent.Storage;

/// <summary>
/// 个人待办隔离存储（Schema v15 兼容）：
/// 每个 <c>ownerScopeKey</c> 独立存入 <c>meta['personal_todo:scope:v1:&lt;sha256&gt;']</c>，
/// 单 <c>AppDatabase.Write</c> 事务保证 CAS 与幂等回执一致性。
/// </summary>
public sealed class PersonalTodoStore : IPersonalTodoStore
{
    private const string MetaPrefix = "personal_todo:scope:v1:";
    private const int MaxReceipts = 64;

    private readonly object _gate = new();

    private sealed record ReceiptEntry(string Key, string Digest, string TodoId, long Revision);

    private sealed record TodoLedger(
        long Revision,
        List<PersonalTodoItem> Items,
        List<ReceiptEntry> Receipts)
    {
        public static TodoLedger Empty() => new(0, new List<PersonalTodoItem>(), new List<ReceiptEntry>());
    }

    private static string OwnerMetaKey(string ownerScopeKey)
        => MetaPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ownerScopeKey)));

    public PersonalTodoSnapshotView ReadOwner(string ownerScopeKey)
    {
        if (string.IsNullOrWhiteSpace(ownerScopeKey))
            return new PersonalTodoSnapshotView(0, Array.Empty<PersonalTodoItem>());

        lock (_gate)
        {
            var key = OwnerMetaKey(ownerScopeKey);
            var ledger = ParseLedger(AppDatabase.GetMeta(key));
            return new PersonalTodoSnapshotView(ledger.Revision, ledger.Items.ToArray());
        }
    }

    public PersonalTodoMutationResult SaveItem(
        string ownerScopeKey,
        PersonalTodoItem item,
        long? expectedRevision = null,
        string? idempotencyKey = null,
        string? idempotencyDigest = null)
    {
        if (string.IsNullOrWhiteSpace(ownerScopeKey))
            return new(false, false, "owner_scope_required");
        ArgumentNullException.ThrowIfNull(item);
        if (!IsValidIdempotencyKey(idempotencyKey))
            return new(false, false, "invalid_idempotency_key");
        if (!IsValidTodoId(item.TodoId))
            return new(false, false, "todo_id_invalid");

        var title = (item.Title ?? string.Empty).Trim();
        if (title.Length == 0 || title.Length > PersonalTodoLimits.MaxTitleChars || title.Any(char.IsControl))
            return new(false, false, "todo_title_invalid");
        var notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes.Trim();
        if (notes is not null && notes.Length > PersonalTodoLimits.MaxNotesChars)
            return new(false, false, "todo_notes_too_long");
        if (notes is not null && notes.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            return new(false, false, "todo_notes_invalid");
        if (!PersonalTodoStatus.IsValid(item.Status))
            return new(false, false, "todo_status_invalid");

        var priority = Math.Clamp(item.Priority, 1, 3);
        var sourceHint = string.IsNullOrWhiteSpace(item.SourceHint)
            ? null
            : item.SourceHint.Trim()[..Math.Min(item.SourceHint.Trim().Length, PersonalTodoLimits.MaxSourceHintChars)];

        var normalized = item with
        {
            OwnerScopeKey = ownerScopeKey,
            Title = title,
            Notes = notes,
            Priority = priority,
            Status = item.Status,
            SourceHint = sourceHint
        };

        lock (_gate)
        {
            var key = OwnerMetaKey(ownerScopeKey);
            PersonalTodoMutationResult? result = null;
            AppDatabase.Write(conn =>
            {
                var raw = AppDatabase.GetMetaInTransaction(conn, key);
                var ledger = ParseLedger(raw, out var corrupted);
                if (corrupted && !string.IsNullOrWhiteSpace(raw))
                    AppDatabase.SetMetaInTransaction(conn, key + ":corrupt", raw);
                var digest = string.IsNullOrEmpty(idempotencyDigest) ? HashItem(normalized) : idempotencyDigest;
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    var receipt = ledger.Receipts.FirstOrDefault(r => string.Equals(r.Key, idempotencyKey, StringComparison.Ordinal));
                    if (receipt is not null)
                    {
                        if (!string.Equals(receipt.Digest, digest, StringComparison.Ordinal))
                        {
                            result = new(false, false, "idempotency_key_conflict", null, ledger.Revision);
                            return;
                        }
                        var existing = ledger.Items.FirstOrDefault(t => string.Equals(t.TodoId, receipt.TodoId, StringComparison.Ordinal));
                        result = new(true, true, "replayed", existing, ledger.Revision);
                        return;
                    }
                }

                if (expectedRevision is { } expected && expected != ledger.Revision)
                {
                    result = new(false, false, "todo_revision_conflict", null, ledger.Revision);
                    return;
                }

                var idx = ledger.Items.FindIndex(t => string.Equals(t.TodoId, normalized.TodoId, StringComparison.Ordinal));
                if (idx >= 0)
                {
                    var prev = ledger.Items[idx];
                    normalized = normalized with { CreatedUnix = prev.CreatedUnix };
                    ledger.Items[idx] = normalized;
                }
                else
                {
                    if (ledger.Items.Count >= PersonalTodoLimits.MaxPerOwner)
                    {
                        var archiveIdx = ledger.Items.FindIndex(t => t.Status == PersonalTodoStatus.Archived);
                        if (archiveIdx >= 0) ledger.Items.RemoveAt(archiveIdx);
                    }
                    if (ledger.Items.Count >= PersonalTodoLimits.MaxPerOwner)
                    {
                        result = new(false, false, "todo_quota_exceeded", null, ledger.Revision);
                        return;
                    }
                    ledger.Items.Add(normalized);
                }

                var nextRev = checked(ledger.Revision + 1);
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    ledger.Receipts.Add(new ReceiptEntry(idempotencyKey, digest, normalized.TodoId, nextRev));
                    while (ledger.Receipts.Count > MaxReceipts) ledger.Receipts.RemoveAt(0);
                }

                AppDatabase.SetMetaInTransaction(conn, key, JsonSerializer.Serialize(ledger with { Revision = nextRev }));
                result = new(true, false, idx >= 0 ? "updated" : "created", normalized, nextRev);
            });
            return result ?? new(false, false, "todo_save_failed");
        }
    }

    public PersonalTodoMutationResult DeleteItem(
        string ownerScopeKey,
        string todoId,
        long? expectedRevision = null,
        string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(ownerScopeKey) || string.IsNullOrWhiteSpace(todoId))
            return new(false, false, "todo_id_required");
        if (!IsValidIdempotencyKey(idempotencyKey))
            return new(false, false, "invalid_idempotency_key");

        lock (_gate)
        {
            var key = OwnerMetaKey(ownerScopeKey);
            PersonalTodoMutationResult? result = null;
            AppDatabase.Write(conn =>
            {
                var raw = AppDatabase.GetMetaInTransaction(conn, key);
                var ledger = ParseLedger(raw, out var corrupted);
                if (corrupted && !string.IsNullOrWhiteSpace(raw))
                    AppDatabase.SetMetaInTransaction(conn, key + ":corrupt", raw);
                var digest = "del:" + todoId;
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    var receipt = ledger.Receipts.FirstOrDefault(r => string.Equals(r.Key, idempotencyKey, StringComparison.Ordinal));
                    if (receipt is not null)
                    {
                        result = string.Equals(receipt.Digest, digest, StringComparison.Ordinal)
                            ? new(true, true, "replayed", null, ledger.Revision)
                            : new(false, false, "idempotency_key_conflict", null, ledger.Revision);
                        return;
                    }
                }

                if (expectedRevision is { } expected && expected != ledger.Revision)
                {
                    result = new(false, false, "todo_revision_conflict", null, ledger.Revision);
                    return;
                }

                var existing = ledger.Items.FirstOrDefault(t => string.Equals(t.TodoId, todoId, StringComparison.Ordinal));
                if (existing is null)
                {
                    result = new(false, false, "todo_not_found", null, ledger.Revision);
                    return;
                }
                ledger.Items.RemoveAll(t => string.Equals(t.TodoId, todoId, StringComparison.Ordinal));

                var nextRev = checked(ledger.Revision + 1);
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    ledger.Receipts.Add(new ReceiptEntry(idempotencyKey, digest, todoId, nextRev));
                    while (ledger.Receipts.Count > MaxReceipts) ledger.Receipts.RemoveAt(0);
                }

                AppDatabase.SetMetaInTransaction(conn, key, JsonSerializer.Serialize(ledger with { Revision = nextRev }));
                result = new(true, false, "deleted", existing, nextRev);
            });
            return result ?? new(false, false, "todo_delete_failed");
        }
    }

    private static TodoLedger ParseLedger(string? raw) => ParseLedger(raw, out _);

    private static TodoLedger ParseLedger(string? raw, out bool corrupted)
    {
        corrupted = false;
        if (string.IsNullOrWhiteSpace(raw)) return TodoLedger.Empty();
        try
        {
            var obj = JsonSerializer.Deserialize<TodoLedger>(raw);
            if (obj is null)
            {
                corrupted = true;
                return TodoLedger.Empty();
            }
            var items = (obj.Items ?? new List<PersonalTodoItem>()).Where(i => i is not null).ToList();
            var receipts = (obj.Receipts ?? new List<ReceiptEntry>()).Where(r => r is not null).ToList();
            return new TodoLedger(obj.Revision, items, receipts);
        }
        catch (JsonException)
        {
            corrupted = true;
            return TodoLedger.Empty();
        }
    }

    private static bool IsValidIdempotencyKey(string? idempotencyKey)
        => string.IsNullOrEmpty(idempotencyKey)
           || (!string.IsNullOrWhiteSpace(idempotencyKey) && idempotencyKey.Length <= 128 && !idempotencyKey.Any(char.IsControl));

    private static bool IsValidTodoId(string? todoId)
    {
        if (string.IsNullOrWhiteSpace(todoId) || todoId.Length is < 3 or > 64)
            return false;
        foreach (var c in todoId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return false;
        }
        return true;
    }

    private static string HashItem(PersonalTodoItem item)
    {
        var raw = JsonSerializer.Serialize(new object?[]
        {
            item.TodoId, item.Title, item.Notes, item.Priority,
            item.Status, item.DueUnix, item.LinkedJobId, item.SourceHint
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}

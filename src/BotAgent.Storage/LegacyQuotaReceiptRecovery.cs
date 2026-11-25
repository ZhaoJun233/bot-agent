using System.Text.Json;
using BotAgent.Domain.Ops;

namespace BotAgent.Adapters.Persistence;

/// <summary>Read-only compatibility for the old committed journal wrapper; Result remains opaque.</summary>
public static class LegacyQuotaReceiptRecovery
{
    public const string Owner = "preset.feature.token-quota";
    public static OperationRecoveryReceipt? Find(string callerScope, string idempotencyKey)
    {
        OperationRecoveryStore.ValidateRequest(callerScope, idempotencyKey);
        var scope = OperationRecoveryStore.Digest(callerScope);
        var key = OperationRecoveryStore.Digest(idempotencyKey);
        using var connection = AppDatabase.OpenIsolated();
        // Both indexes are observed in one SQLite snapshot; a concurrent commit cannot be
        // combined with an older missing-index observation to hide authoritative Unknown.
        using var snapshot = connection.BeginTransaction();
        var indexed = OperationRecoveryStore.Read(connection, Owner, scope, key);
        if (indexed is not null) return indexed;
        var raw = AppDatabase.GetMetaInTransaction(connection, "quota:receipts:" + scope);
        if (raw is null) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var journal = document.RootElement;
            if (journal.ValueKind != JsonValueKind.Object) throw new JsonException();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            JsonElement? found = null;
            foreach (var entry in journal.EnumerateObject())
            {
                if (!OperationRecoveryStore.IsDigest(entry.Name) || !keys.Add(entry.Name) || keys.Count > 4096) throw new JsonException();
                if (entry.Name == key) found = entry.Value;
            }
            if (found is null) return null;
            var receipt = found.Value;
            if (receipt.ValueKind != JsonValueKind.Object) throw new JsonException();
            var fields = receipt.EnumerateObject().Select(p => p.Name).ToArray();
            if (fields.Length != 2 || !fields.Contains("Fingerprint") || !fields.Contains("Result")
                || receipt.GetProperty("Result").ValueKind != JsonValueKind.Object
                || receipt.GetProperty("Fingerprint").ValueKind != JsonValueKind.String) throw new JsonException();
            var digest = receipt.GetProperty("Fingerprint").GetString();
            if (!OperationRecoveryStore.IsDigest(digest)) throw new JsonException();
            return new(1, Owner, scope, key, digest!, "quota.management", "1", null, "committed", "legacy");
        }
        catch (JsonException error) { throw new InvalidOperationException("operation_receipt_corrupt", error); }
    }
}

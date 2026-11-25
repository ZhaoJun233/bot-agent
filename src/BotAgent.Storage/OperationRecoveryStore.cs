using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Ops;
using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>Format-only recovery index. It never loads a business decoder or executes an operation.</summary>
public sealed class OperationRecoveryStore
{
    internal const string Prefix = "operation-recovery:v1:";
    public OperationRecoveryReceipt? Find(string owner, string callerScope, string idempotencyKey)
    {
        ValidateToken(owner, 80);
        ValidateRequest(callerScope, idempotencyKey);
        using var connection = AppDatabase.OpenIsolated();
        return Read(connection, owner, Digest(callerScope), Digest(idempotencyKey));
    }

    internal static void Record(SqliteConnection connection, OperationReceiptBinding binding, OperationReceiptWrite operation)
    {
        ValidateBinding(binding);
        ValidateRequest(operation.CallerScope, operation.IdempotencyKey);
        if (!IsDigest(operation.RequestDigest) || !IsState(operation.State)) Invalid();
        var receipt = new OperationRecoveryReceipt(1, binding.Owner, Digest(operation.CallerScope), Digest(operation.IdempotencyKey),
            operation.RequestDigest.ToUpperInvariant(), binding.Contract, binding.ContractVersion, binding.Generation, operation.State, "recorded");
        var current = Read(connection, receipt.Owner, receipt.ScopeDigest, receipt.KeyDigest);
        if (current is not null)
        {
            // An existing key belongs to its original contract/generation. Never relabel or replay it.
            if (current != receipt) throw new InvalidOperationException("operation_receipt_conflict");
            return;
        }
        AppDatabase.SetMetaInTransaction(connection, Key(receipt.Owner, receipt.ScopeDigest, receipt.KeyDigest), JsonSerializer.Serialize(receipt));
    }

    internal static OperationRecoveryReceipt? Read(SqliteConnection connection, string owner, string scope, string key)
    {
        var raw = AppDatabase.GetMetaInTransaction(connection, Key(owner, scope, key));
        if (raw is null) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != names.Distinct(StringComparer.Ordinal).Count()) throw new JsonException();
            var receipt = document.RootElement.Deserialize<OperationRecoveryReceipt>();
            if (receipt is null || receipt.FormatVersion != 1 || receipt.Owner != owner || receipt.ScopeDigest != scope
                || receipt.KeyDigest != key || !IsDigest(receipt.RequestDigest) || !IsState(receipt.State)
                || receipt.Provenance != "recorded" || receipt.Generation is null) throw new JsonException();
            ValidateBinding(new(receipt.Owner, receipt.Contract, receipt.ContractVersion, receipt.Generation));
            return receipt;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidOperationException("operation_receipt_corrupt", error); }
    }

    internal static void ValidateBinding(OperationReceiptBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateToken(binding.Owner, 80); ValidateToken(binding.Contract, 128);
        ValidateToken(binding.ContractVersion, 64); ValidateToken(binding.Generation, 128);
    }
    internal static void ValidateRequest(string scope, string key)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 256 || scope.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl)) Invalid();
    }
    private static void ValidateToken(string value, int maximum)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximum
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':'))) Invalid();
    }
    internal static bool IsDigest(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    internal static bool IsState(string state) => state is "committed" or "rejected" or "unknown";
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Key(string owner, string scope, string key) => Prefix + Digest(owner) + ":" + scope + ":" + key;
    private static void Invalid() => throw new ArgumentException("operation_receipt_invalid");
}

extern alias StorageModule;
using BotAgent.TestFixtures;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Ops;
using PackageDataStore = StorageModule::BotAgent.Adapters.Persistence.PackageDataStore;
using OperationRecoveryStore = StorageModule::BotAgent.Adapters.Persistence.OperationRecoveryStore;
using LegacyQuotaReceiptRecovery = StorageModule::BotAgent.Adapters.Persistence.LegacyQuotaReceiptRecovery;

namespace BotAgent.ProductionSpecProbe;

internal static class OperationRecoveryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const string scope = "synthetic-recovery-caller", key = "synthetic-recovery-original", owner = "synthetic.receipt";
        var binding = new OperationReceiptBinding(owner, "synthetic.operation", "1", "generation-one");
        var operation = new OperationReceiptWrite(scope, key, Hash("synthetic-request"), "unknown");
        var store = new PackageDataStore([], ["receipt-test:"], false, false, binding);
        var recovery = new OperationRecoveryStore();
        store.Write(session => { session.RecordReceipt(operation); return 0; });
        var found = recovery.Find(owner, scope, key);
        check(found is { State: "unknown", Generation: "generation-one", Provenance: "recorded" }
            && found.ScopeDigest == Hash(scope) && found.KeyDigest == Hash(key), "safe receipt retains unknown and exact owner generation");
        var raw = JsonSerializer.Serialize(found);
        check(!raw.Contains(scope) && !raw.Contains(key) && !raw.Contains("synthetic-request"), "recovery contains no raw caller key or request");
        check(recovery.Find(owner, "another-caller", key) is null && recovery.Find("another.owner", scope, key) is null
            && recovery.Find(owner, scope, "another-key") is null, "recovery isolates owner caller and original key");
        store.Write(session => { session.RecordReceipt(operation); return 0; });
        check(recovery.Find(owner, scope, key) == found, "same evidence is idempotent");
        Reject("operation_receipt_conflict", () => store.Write(session => { session.RecordReceipt(operation with { State = "committed" }); return 0; }), check, "unknown cannot be silently relabeled committed");
        var newGeneration = new PackageDataStore([], [], false, false, binding with { Generation = "generation-two" });
        Reject("operation_receipt_conflict", () => newGeneration.Write(session => { session.RecordReceipt(operation); return 0; }), check, "replacement cannot claim old operation generation");
        Reject("package_data_denied", () => new PackageDataStore([], []).Write(session => { session.RecordReceipt(operation); return 0; }), check, "ungranted receipt writer denied");
        Reject("package_data_denied", () => store.Read(session => { session.RecordReceipt(operation); return 0; }), check, "read transaction cannot write receipt");
        Reject("package_data_invalid_grant", () => new PackageDataStore([], ["operation-recovery:"]), check, "raw recovery namespace cannot be granted");
        Reject("package_data_transaction_aborted", () => store.Write(session =>
        {
            session.WriteState("receipt-test:rollback", "temporary");
            session.RecordReceipt(operation with { IdempotencyKey = "synthetic-aborted-key" });
            try { session.ReadState("foreign:state"); } catch (InvalidOperationException) { }
            return 0;
        }), check, "caught denial aborts receipt transaction");
        check(recovery.Find(owner, scope, "synthetic-aborted-key") is null && AppDatabase.GetMeta("receipt-test:rollback") is null, "business state and receipt roll back together");

        var ledger = new QuotaFixture();
        var saved = ledger.SaveManagement("synthetic:recovery", 123, 0, "synthetic-quota-receipt", scope);
        var quotaEvidence = recovery.Find(LegacyQuotaReceiptRecovery.Owner, scope, "synthetic-quota-receipt");
        check(quotaEvidence is { State: "committed", Contract: "quota.management", ContractVersion: "1" }
            && quotaEvidence.Generation?.Length == 32 && saved.Revision == 1, "real quota commit has same-transaction safe receipt");
        Reject("synthetic_audit_failure", () => ledger.SaveManagement("synthetic:recovery", 456, 1, "synthetic-quota-abort", scope,
            (_, _, _) => throw new InvalidOperationException("synthetic_audit_failure")), check, "quota audit failure refuses commit");
        check(recovery.Find(LegacyQuotaReceiptRecovery.Owner, scope, "synthetic-quota-abort") is null
            && ledger.ReadManagement("synthetic:recovery").Revision == 1, "audit failure rolls back limit revision and recovery");

        const string oldKey = "synthetic-legacy-key", oldScope = "synthetic-legacy-caller";
        var journal = JsonSerializer.Serialize(new Dictionary<string, object> { [Hash(oldKey)] = new
        { Fingerprint = Hash("old-request"), Result = new { unknownBusinessContract = "opaque-private-business-value" } } });
        AppDatabase.SetMeta("quota:receipts:" + Hash(oldScope), journal);
        var old = LegacyQuotaReceiptRecovery.Find(oldScope, oldKey);
        check(old is { State: "committed", Generation: null, Provenance: "legacy" }
            && !JsonSerializer.Serialize(old).Contains("opaque-private-business-value"), "legacy wrapper recovers without business decoder or invented generation");
        check(AppDatabase.GetMeta("quota:receipts:" + Hash(oldScope)) == journal, "legacy lookup preserves original bytes");
        var indexKey = "operation-recovery:v1:" + Hash(owner) + ":" + Hash(scope) + ":" + Hash(key);
        var original = AppDatabase.GetMeta(indexKey)!;
        AppDatabase.SetMeta(indexKey, original[..^1] + ",\"FuturePolicy\":true}");
        Reject("operation_receipt_corrupt", () => recovery.Find(owner, scope, key), check, "future receipt semantics fail closed");
        AppDatabase.SetMeta(indexKey, original[..^1] + ",\"State\":\"committed\"}");
        Reject("operation_receipt_corrupt", () => recovery.Find(owner, scope, key), check, "duplicate receipt fields cannot relabel unknown");
        AppDatabase.SetMeta(indexKey, original);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Reject(string code, Action action, Action<bool, string> check, string label)
    {
        try { action(); check(false, label); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { check(error.Message == code, label); }
    }
}

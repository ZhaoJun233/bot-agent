extern alias StorageModule;
using BotAgent.Domain.Configuration;
using BotAgent.Panel;
using OperationRecoveryStore = StorageModule::BotAgent.Adapters.Persistence.OperationRecoveryStore;
using LegacyQuotaReceiptRecovery = StorageModule::BotAgent.Adapters.Persistence.LegacyQuotaReceiptRecovery;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaControlBackend
{
    public ValueTask<ZbaOperationFindResult> FindOperationAsync(ConfigurationCaller caller, ZbaOperationFindRequest request, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration, allowDuringStartup: true);
        ct.ThrowIfCancellationRequested();
        try
        {
            var callerScope = ZbaCallerAuthorization.Scope(caller);
            var receipt = request.Owner == LegacyQuotaReceiptRecovery.Owner
                ? LegacyQuotaReceiptRecovery.Find(callerScope, request.IdempotencyKey)
                : new OperationRecoveryStore().Find(request.Owner, callerScope, request.IdempotencyKey);
            return ValueTask.FromResult(new ZbaOperationFindResult(receipt is not null, receipt, "unavailable"));
        }
        catch (InvalidOperationException error) when (error.Message == "operation_receipt_corrupt")
        { throw new ZbaBackendException(503, "operation_receipt_corrupt", "Historical evidence is unreadable; preserve the original key."); }
    }
}

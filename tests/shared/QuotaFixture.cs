extern alias StorageModule;

using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Packages.TokenQuota;
using PackageDataStore = StorageModule::BotAgent.Adapters.Persistence.PackageDataStore;
using PackageDataSession = StorageModule::BotAgent.Adapters.Persistence.PackageDataSession;
using LegacyQuotaReceiptRecovery = StorageModule::BotAgent.Adapters.Persistence.LegacyQuotaReceiptRecovery;
using BotAgent.Services.Ops;
using Microsoft.Data.Sqlite;

namespace BotAgent.TestFixtures;

/// <summary>Test-only access to the real package ledger and same-transaction legacy recovery scenarios.</summary>
internal sealed class QuotaFixture : ITenantQuotaLedger, ITokenUsageRecorder, ITenantQuotaManagement
{
    private readonly IPackageDataStore _data;
    private readonly QuotaLedger _ledger;
    public const int MaximumReceiptsPerCaller = QuotaLedger.MaximumReceiptsPerCaller;
    public QuotaFixture() : this(new PackageDataStore(["tenant_quotas"],
        ["quota:revision:", "quota:receipts:"], allowUsage: true, allowAudit: true,
        receipts: new(LegacyQuotaReceiptRecovery.Owner, "quota.management", "1", typeof(QuotaFixture).Module.ModuleVersionId.ToString("N")))) { }
    public QuotaFixture(IPackageDataStore data) { _data = data; _ledger = new(data); }
    public TenantQuotaSnapshot GetQuota(string tenantId) => _ledger.GetQuota(tenantId);
    public TenantQuotaManagementSnapshot ReadManagement(string tenantId) => _ledger.ReadManagement(tenantId);
    public TenantQuotaSnapshot SetDailyTokenLimit(string tenantId, int dailyTokenLimit) => _ledger.SetDailyTokenLimit(tenantId, dailyTokenLimit);
    public bool IsEnergySaving(string tenantId) => GetQuota(tenantId).EnergySaving;
    public TenantQuotaManagementSnapshot? FindManagement(string idempotencyKey, string callerScope) => _ledger.FindManagement(idempotencyKey, callerScope);
    public TenantQuotaManagementSnapshot SaveManagementWithAudit(string tenantId, int dailyTokenLimit,
        long expectedRevision, string idempotencyKey, string callerScope,
        Func<TenantQuotaSnapshot, TenantQuotaSnapshot, AuditEvent> audit)
        => _ledger.SaveManagementWithAudit(tenantId, dailyTokenLimit, expectedRevision, idempotencyKey, callerScope, audit);
    public TenantQuotaManagementSnapshot SaveManagement(string tenantId, int dailyTokenLimit,
        long expectedRevision, string idempotencyKey, string callerScope,
        Action<SqliteConnection, TenantQuotaSnapshot, TenantQuotaSnapshot>? audit = null)
        => _ledger.SaveCore(tenantId, dailyTokenLimit, expectedRevision, idempotencyKey, callerScope,
            audit is null ? null : (session, before, after) => ((PackageDataSession)session).Legacy(connection =>
            { audit(connection, before, after); return true; }));
    void ITenantQuotaLedger.RecordUsage(string tenantId, int promptTokens, int completionTokens) => RecordUsage(tenantId, promptTokens, completionTokens);
    void ITokenUsageRecorder.RecordUsage(string tenantId, int promptTokens, int completionTokens) => RecordUsage(tenantId, promptTokens, completionTokens);
    public TenantQuotaSnapshot RecordUsage(string tenantId, int promptTokens, int completionTokens)
    {
        var tenant = QuotaLedger.NormalizeTenant(tenantId);
        return _data.Write(session =>
        {
            ((PackageDataSession)session).RecordLegacyUsage(tenant, promptTokens, completionTokens);
            return QuotaLedger.Load(session, tenant);
        });
    }
}

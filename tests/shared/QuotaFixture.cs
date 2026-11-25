extern alias StorageModule;

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using PackageDataStore = StorageModule::BotAgent.Adapters.Persistence.PackageDataStore;
using PackageDataSession = StorageModule::BotAgent.Adapters.Persistence.PackageDataSession;
using LegacyQuotaReceiptRecovery = StorageModule::BotAgent.Adapters.Persistence.LegacyQuotaReceiptRecovery;
using BotAgent.Services.Ops;
using Microsoft.Data.Sqlite;

namespace BotAgent.TestFixtures;

/// <summary>Test-only access to the official marketplace token-quota ledger and same-transaction legacy recovery scenarios.</summary>
internal sealed class QuotaFixture : ITenantQuotaLedger, ITokenUsageRecorder, ITenantQuotaManagement
{
    private static readonly (AssemblyLoadContext Context, Assembly Assembly) Plugin = LoadPlugin();
    private static readonly Type LedgerType = Plugin.Assembly.GetType("BotAgent.Packages.TokenQuota.QuotaLedger", throwOnError: true)!;
    private static readonly MethodInfo SaveCoreMethod = LedgerType.GetMethod("SaveCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo NormalizeTenantMethod = LedgerType.GetMethod("NormalizeTenant", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo LoadMethod = LedgerType.GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly IPackageDataStore _data;
    private readonly ITenantQuotaManagement _ledger;
    public const int MaximumReceiptsPerCaller = 4096;

    public QuotaFixture() : this(new PackageDataStore(["tenant_quotas"],
        ["quota:revision:", "quota:receipts:"], allowUsage: true, allowAudit: true,
        receipts: new(LegacyQuotaReceiptRecovery.Owner, "quota.management", "1", typeof(QuotaFixture).Module.ModuleVersionId.ToString("N")))) { }

    public QuotaFixture(IPackageDataStore data)
    {
        _data = data;
        _ledger = (ITenantQuotaManagement)Activator.CreateInstance(LedgerType, data)!;
    }

    private static (AssemblyLoadContext Context, Assembly Assembly) LoadPlugin()
    {
        var root = OfficialPluginsTestLocator.ResolveMarketplaceRoot(OfficialPluginsTestLocator.FindRepoRoot());
        return OfficialPluginsTestLocator.LoadIsolatedPlugin(root, "token-quota", "BotAgent.Package.TokenQuota");
    }

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
    {
        Action<IPackageDataSession, TenantQuotaSnapshot, TenantQuotaSnapshot>? callback = audit is null
            ? null
            : (session, before, after) => ((PackageDataSession)session).Legacy(connection => { audit(connection, before, after); return true; });
        try
        {
            return (TenantQuotaManagementSnapshot)SaveCoreMethod.Invoke(_ledger,
                [tenantId, dailyTokenLimit, expectedRevision, idempotencyKey, callerScope, callback])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    void ITenantQuotaLedger.RecordUsage(string tenantId, int promptTokens, int completionTokens) => RecordUsage(tenantId, promptTokens, completionTokens);
    void ITokenUsageRecorder.RecordUsage(string tenantId, int promptTokens, int completionTokens) => RecordUsage(tenantId, promptTokens, completionTokens);

    public TenantQuotaSnapshot RecordUsage(string tenantId, int promptTokens, int completionTokens)
    {
        var tenant = (string)NormalizeTenantMethod.Invoke(null, [tenantId])!;
        return _data.Write(session =>
        {
            ((PackageDataSession)session).RecordLegacyUsage(tenant, promptTokens, completionTokens);
            return (TenantQuotaSnapshot)LoadMethod.Invoke(null, [session, tenant])!;
        });
    }
}

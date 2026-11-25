using BotAgent.Domain.Ops;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Ops;

namespace BotAgent.Domain.Ports;

/// <summary>Quota policy and management contribution; metering is an independent host capability.</summary>
public interface ITenantQuotaManagement : IPluginControlContribution
{
    TenantQuotaSnapshot GetQuota(string tenantId);
    TenantQuotaSnapshot SetDailyTokenLimit(string tenantId, int dailyTokenLimit);
    TenantQuotaManagementSnapshot ReadManagement(string tenantId);
    TenantQuotaManagementSnapshot SaveManagementWithAudit(string tenantId, int dailyTokenLimit,
        long expectedRevision, string idempotencyKey, string callerScope,
        Func<TenantQuotaSnapshot, TenantQuotaSnapshot, AuditEvent> audit);
    TenantQuotaManagementSnapshot? FindManagement(string idempotencyKey, string callerScope);
}

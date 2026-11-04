namespace BotAgent.Domain.Ops;

/// <summary>Management revision advances on accepted limit writes, never when usage advances.</summary>
public sealed record TenantQuotaManagementSnapshot(TenantQuotaSnapshot Quota, long Revision);

public sealed class TenantQuotaManagementException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

using BotAgent.Domain.Ops;

namespace BotAgent.Domain.Ports;

/// <summary>多租户每日配额账本端口（供回复链判定节能静默、记录与管理 Token 用量）。</summary>
public interface ITenantQuotaLedger
{
    /// <summary>读取指定租户当前 UTC 日的配额快照；跨日时重置用量。</summary>
    TenantQuotaSnapshot GetQuota(string tenantId);

    /// <summary>保存指定租户的每日 Token 上限，不清除当前日已有用量。</summary>
    TenantQuotaSnapshot SetDailyTokenLimit(string tenantId, int dailyTokenLimit);

    /// <summary>当前租户是否因达到当日 Token 配额上限而处于节能静默模式。</summary>
    bool IsEnergySaving(string tenantId);

    /// <summary>记录当前租户本轮消耗的 Token 用量。</summary>
    void RecordUsage(string tenantId, int promptTokens, int completionTokens);
}

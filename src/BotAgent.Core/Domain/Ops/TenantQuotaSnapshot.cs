namespace BotAgent.Domain.Ops;

/// <summary>单租户每日 Token 配额与节能静默状态快照。</summary>
public readonly record struct TenantQuotaSnapshot(
    string TenantId,
    int DailyTokenLimit,
    int UsedPromptTokens,
    int UsedCompletionTokens,
    string ResetDate,
    bool EnergySaving)
{
    /// <summary>当前 UTC 日累计消耗的 prompt + completion Token 数。</summary>
    public long UsedTotalTokens => (long)UsedPromptTokens + UsedCompletionTokens;

    /// <summary>当前 UTC 日尚可使用的 Token 数；账本超额时钳到 0。</summary>
    public int RemainingTokens => (int)Math.Max(0L, (long)DailyTokenLimit - UsedTotalTokens);
}

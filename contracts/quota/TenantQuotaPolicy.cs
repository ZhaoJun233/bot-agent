namespace BotAgent.Domain.Ops;

/// <summary>每日 Token 配额的服务端边界与默认值。</summary>
public static class TenantQuotaPolicy
{
    public const int MinimumDailyTokenLimit = 1;
    public const int MaximumDailyTokenLimit = 1_000_000_000;
    public const int DefaultDailyTokenLimit = 50_000;

    public static bool IsValidDailyTokenLimit(int value)
        => value >= MinimumDailyTokenLimit && value <= MaximumDailyTokenLimit;
}

namespace BotAgent.Domain.Ops;

/// <summary>Current UTC-day usage, independent of quota enforcement or configured limits.</summary>
public readonly record struct TokenUsageSnapshot(
    string TenantId, int UsedPromptTokens, int UsedCompletionTokens, string ResetDate)
{
    public long UsedTotalTokens => (long)UsedPromptTokens + UsedCompletionTokens;
}

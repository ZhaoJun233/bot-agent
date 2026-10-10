namespace BotAgent.Model;

public static class ThinkingBudgetResolver
{
    public static (string? Effort, int? MaxTokens) Resolve(string? depthSetting, bool isFastModel)
    {
        var raw = (depthSetting ?? "medium").Trim().ToLowerInvariant();
        return raw switch
        {
            "none" or "off" or "disabled" => ("none", null),
            "low"    => ("low", isFastModel ? 1024 : 2048),
            "medium" => ("medium", isFastModel ? 2048 : 4096),
            "high"   => ("high", isFastModel ? 4096 : 8192),
            "xhigh" or "max" => ("high", isFastModel ? 8192 : 16384),
            _ => int.TryParse(raw, out var customTokens)
                ? (customTokens > 0 ? "custom" : "none", customTokens > 0 ? customTokens : null)
                : ("medium", isFastModel ? 2048 : 4096)
        };
    }
}

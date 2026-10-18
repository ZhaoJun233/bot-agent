namespace BotAgent.Model;

public static class ThinkingBudgetResolver
{
    /// <summary>Canonical mapping used by ModelOptions and the legacy host projection.</summary>
    public static (string Effort, int BudgetTokens) ResolveBudget(string? budget, string? custom, bool isFastModel = false)
    {
        var defaultPreset = isFastModel ? "low" : "medium";
        var defaultTokens = isFastModel ? 1024 : 4096;
        return (budget ?? defaultPreset).Trim().ToLowerInvariant() switch
        {
            "off" or "none" or "disabled" => ("none", 0),
            "low" => ("low", isFastModel ? 1024 : 0),
            "medium" => ("medium", isFastModel ? 4096 : 0),
            "high" => ("high", isFastModel ? 16384 : 0),
            "xhigh" or "max" => ("xhigh", 0),
            "custom" => int.TryParse(custom, out var tokens) && tokens > 0
                ? (custom!, tokens)
                : (!string.IsNullOrWhiteSpace(custom) ? (custom, defaultTokens) : (defaultPreset, defaultTokens)),
            _ => (defaultPreset, isFastModel ? defaultTokens : 0)
        };
    }

    public static (string? Effort, int? MaxTokens) Resolve(string? depthSetting, bool isFastModel)
    {
        var numeric = int.TryParse(depthSetting, out _);
        var result = ResolveBudget(numeric ? "custom" : depthSetting, numeric ? depthSetting : null, isFastModel);
        return (result.Effort, result.BudgetTokens > 0 ? result.BudgetTokens : null);
    }
}

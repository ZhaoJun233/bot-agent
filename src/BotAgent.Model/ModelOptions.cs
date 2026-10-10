namespace BotAgent.Model;

/// <summary>
/// 模型层强类型配置切片（消灭巨石 T3）。
/// 包含大语言模型端点、凭据、模型名、快速档、思考预算及自适应采样等。
/// </summary>
public sealed class ModelOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string ModelBaseUrl
    {
        get => BaseUrl;
        set => BaseUrl = value;
    }

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4o-mini";

    public string? BaseUrlOverride { get; set; }

    public string? ModelOverride { get; set; }

    public bool FastReply { get; set; }

    public string FastModel { get; set; } = string.Empty;

    public string? ApiKeyOverride { get; set; }

    public int MaxTokens { get; set; } = 2048;

    public string ThinkingBudget { get; set; } = "medium";

    public string ThinkingCustomBudget { get; set; } = "4096";

    public string FastThinkingBudget { get; set; } = "low";

    public string FastThinkingCustomBudget { get; set; } = "1024";

    public int TimeoutSeconds { get; set; } = 120;

    public double DefaultTemperature { get; set; } = 0.7;

    public double DefaultTopP { get; set; } = 0.85;

    public bool EnableAtmosphereDamping { get; set; } = true;

    public int VoiceMaxChars { get; set; } = 100;

    public int VoiceEagerness { get; set; } = 30;

    public bool FilterActionNarration { get; set; } = true;

    public string MusicUnderstandModel { get; set; } = string.Empty;

    public bool MusicSendAudioToModel { get; set; } = true;

    public int MusicAudioToModelMaxKb { get; set; } = 1024;

    public string ReplyModel =>
        FastReply && !string.IsNullOrWhiteSpace(FastModel) ? FastModel.Trim() : Model;

    public (string Effort, int BudgetTokens) ResolveThinkingBudget(bool isFastModel = false)
    {
        var budget = isFastModel ? FastThinkingBudget : ThinkingBudget;
        var custom = isFastModel ? FastThinkingCustomBudget : ThinkingCustomBudget;
        var defaultPreset = isFastModel ? "low" : "medium";
        var defaultTokens = isFastModel ? 1024 : 4096;
        var preset = (budget ?? defaultPreset).Trim().ToLowerInvariant();
        return preset switch
        {
            "off" or "none" or "disabled" => ("none", 0),
            "low" => ("low", isFastModel ? 1024 : 0),
            "medium" => ("medium", isFastModel ? 4096 : 0),
            "high" => ("high", isFastModel ? 16384 : 0),
            "xhigh" or "max" => ("xhigh", 0),
            "custom" => int.TryParse(custom, out var c) && c > 0
                ? (custom, c)
                : (!string.IsNullOrWhiteSpace(custom) ? (custom, defaultTokens) : (defaultPreset, defaultTokens)),
            _ => (defaultPreset, isFastModel ? defaultTokens : 0)
        };
    }

    public (string Effort, int BudgetTokens) ResolveFastThinkingBudget() => ResolveThinkingBudget(isFastModel: true);
}

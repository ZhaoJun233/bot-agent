namespace BotAgent.Domain.Platforms;

/// <summary>
/// 平台能力声明与结构化降级决策。
/// </summary>
public sealed record PlatformCapabilities(
    bool SupportsText = true,
    bool SupportsImage = false,
    bool SupportsVoice = false,
    bool SupportsQuote = false,
    bool SupportsRecall = false,
    bool SupportsDirect = true,
    bool SupportsGroup = true,
    bool SupportsThread = false,
    bool SupportsStickers = false,
    bool SupportsMusic = false,
    bool SupportsPoke = false)
{
    public static PlatformCapabilities QqOneBot { get; } = new(
        SupportsText: true,
        SupportsImage: true,
        SupportsVoice: true,
        SupportsQuote: true,
        SupportsRecall: true,
        SupportsDirect: true,
        SupportsGroup: true,
        SupportsThread: false,
        SupportsStickers: true,
        SupportsMusic: true,
        SupportsPoke: true);

    public static PlatformCapabilities QqOfficial { get; } = new(
        SupportsText: true,
        SupportsImage: true,
        SupportsVoice: false,
        SupportsQuote: true,
        SupportsRecall: false,
        SupportsDirect: true,
        SupportsGroup: true,
        SupportsThread: false);

    public static PlatformCapabilities Local { get; } = new(
        SupportsText: true,
        SupportsImage: false,
        SupportsVoice: false,
        SupportsQuote: true,
        SupportsRecall: false,
        SupportsDirect: true,
        SupportsGroup: true,
        SupportsThread: false);

    public static PlatformCapabilities FeishuTextOnly { get; } = new(
        SupportsText: true,
        SupportsImage: false,
        SupportsVoice: false,
        SupportsQuote: true,
        SupportsRecall: false,
        SupportsDirect: true,
        SupportsGroup: true,
        SupportsThread: true);

    /// <summary>
    /// 按能力矩阵评估本轮发送计划，不支持的能力生成稳定原因码降级，而不直接抛异常或丢弃整条回复。
    /// </summary>
    public CapabilityDegradation EvaluateDegradation(bool wantsImage, bool wantsVoice, bool wantsQuote)
    {
        var reasons = new List<string>();
        var sendImage = wantsImage && SupportsImage;
        if (wantsImage && !SupportsImage)
        {
            reasons.Add("image_degraded_to_text");
        }

        var sendVoice = wantsVoice && SupportsVoice;
        if (wantsVoice && !SupportsVoice)
        {
            reasons.Add("voice_degraded_to_text");
        }

        var sendQuote = wantsQuote && SupportsQuote;
        if (wantsQuote && !SupportsQuote)
        {
            reasons.Add("quote_dropped");
        }

        return new CapabilityDegradation(sendImage, sendVoice, sendQuote, reasons);
    }
}

public sealed record CapabilityDegradation(
    bool SendImage,
    bool SendVoice,
    bool SendQuote,
    IReadOnlyList<string> ReasonCodes)
{
    public bool IsDegraded => ReasonCodes.Count > 0;
}

namespace BotAgent.Domain.Reply;

/// <summary>
/// 对话的认知意图（理性求真 vs 感性共情 vs 均衡中性）。
/// </summary>
public enum CognitiveIntent
{
    /// <summary>理性问题：代码、报错、数学、事实、逻辑、查证、配置。</summary>
    Rational = 0,

    /// <summary>感性互动：情绪倾诉、吐槽、安慰、闲聊、开玩笑、人设扮演。</summary>
    Emotional = 1,

    /// <summary>均衡/中性：普通群聊寒暄或意图不明确的常规交流。</summary>
    Balanced = 2
}

/// <summary>
/// 本轮下发给模型的自适应采样参数画像（纯值类型）。
/// </summary>
public readonly record struct SamplingProfile(
    double Temperature,
    double? TopP,
    CognitiveIntent Intent,
    string Reason)
{
    /// <summary>单行摘要（不含用户正文）。</summary>
    public string Describe() => $"intent={Intent} temp={Temperature:F2} top_p={TopP?.ToString("F2") ?? "none"} ({Reason})";
}

/// <summary>
/// 自适应采样配置契约（零外部依赖，纯领域定义）。
/// </summary>
public readonly record struct AdaptiveSamplingConfig(
    bool Enabled,
    double RationalTemperature,
    double? RationalTopP,
    double EmotionalTemperature,
    double? EmotionalTopP,
    double DefaultTemperature,
    double? DefaultTopP);

/// <summary>
/// 自适应采样与意图决策策略（纯领域类、零 I/O、无外部依赖）。
/// </summary>
public static class SamplingPolicy
{
    private static readonly string[] CodeMarkers =
    {
        "```", "def ", "function ", "class ", "SELECT ", "docker ", "import ", "git ",
        "error", "exception", "traceback", "nullpointer", "404", "500", "syntaxerror",
        "typeerror", "return ", "public ", "private ", "const ", "var ", "let ", "npm "
    };

    private static readonly string[] RationalWords =
    {
        "怎么写", "如何实现", "报错", "为什么会", "原理", "配置", "版本", "多少钱",
        "等于几", "计算", "推导", "区别是什么", "怎么解决", "解决办法", "代码", "命令",
        "参数", "文档", "教程", "接口", "算法", "逻辑"
    };

    private static readonly string[] EmotionalWords =
    {
        "呜呜", "好累", "难过", "哭", "救命", "心累", "好烦", "无语", "气死",
        "抱抱", "委屈", "失落", "贴贴", "emo", "哈哈", "笑死", "好玩", "喜欢你",
        "可爱", "摸摸", "安慰", "生气", "吐槽", "无聊", "睡不着", "郁闷"
    };

    /// <summary>
    /// 根据当前触发文本、资料状态、氛围先验与配置，解析生效的采样超参数。
    /// </summary>
    public static SamplingProfile Resolve(
        string? triggerText,
        bool hasSearchFacts,
        string? vibeHint,
        string? declaredIntent,
        AdaptiveSamplingConfig config)
    {
        if (!config.Enabled)
        {
            return new SamplingProfile(
                config.DefaultTemperature,
                config.DefaultTopP,
                CognitiveIntent.Balanced,
                "adaptive_disabled");
        }

        // 1. 如果模型已经在上一步明确给出了 intent 声明，直接采信模型的认知定性
        if (!string.IsNullOrWhiteSpace(declaredIntent))
        {
            var normalized = declaredIntent.Trim().ToLowerInvariant();
            if (normalized is "rational" or "理性")
            {
                return new SamplingProfile(
                    config.RationalTemperature,
                    config.RationalTopP,
                    CognitiveIntent.Rational,
                    "model_declared_rational");
            }
            if (normalized is "emotional" or "感性")
            {
                return new SamplingProfile(
                    config.EmotionalTemperature,
                    config.EmotionalTopP,
                    CognitiveIntent.Emotional,
                    "model_declared_emotional");
            }
            if (normalized is "balanced" or "中性")
            {
                return new SamplingProfile(
                    config.DefaultTemperature,
                    config.DefaultTopP,
                    CognitiveIntent.Balanced,
                    "model_declared_balanced");
            }
        }

        // 2. 本轮带着刚查到的资料：这是事实陈述阶段，强制为理性模式
        if (hasSearchFacts)
        {
            return new SamplingProfile(
                config.RationalTemperature,
                config.RationalTopP,
                CognitiveIntent.Rational,
                "search_facts");
        }

        // 3. 启发式双通道特征打分
        var text = triggerText ?? string.Empty;
        var (rationalScore, emotionalScore) = ComputeScores(text, vibeHint);

        if (rationalScore > emotionalScore && rationalScore >= 2)
        {
            return new SamplingProfile(
                config.RationalTemperature,
                config.RationalTopP,
                CognitiveIntent.Rational,
                $"heuristic_rational(r={rationalScore},e={emotionalScore})");
        }

        if (emotionalScore > rationalScore && emotionalScore >= 2)
        {
            return new SamplingProfile(
                config.EmotionalTemperature,
                config.EmotionalTopP,
                CognitiveIntent.Emotional,
                $"heuristic_emotional(r={rationalScore},e={emotionalScore})");
        }

        return new SamplingProfile(
            config.DefaultTemperature,
            config.DefaultTopP,
            CognitiveIntent.Balanced,
            $"balanced_default(r={rationalScore},e={emotionalScore})");
    }

    /// <summary>
    /// 计算理性与感性启发式特征分值。
    /// </summary>
    public static (int Rational, int Emotional) ComputeScores(string text, string? vibeHint)
    {
        var rational = 0;
        var emotional = 0;
        var lower = text.ToLowerInvariant();

        foreach (var marker in CodeMarkers)
        {
            if (lower.Contains(marker))
            {
                rational += 2; // 代码标记强相关
            }
        }

        foreach (var word in RationalWords)
        {
            if (lower.Contains(word))
            {
                rational++;
            }
        }

        foreach (var word in EmotionalWords)
        {
            if (lower.Contains(word))
            {
                emotional++;
            }
        }

        // 上一轮氛围先验加权
        if (!string.IsNullOrWhiteSpace(vibeHint))
        {
            if (vibeHint is "低落" or "求助" or "吐槽" or "生气" or "开心")
            {
                emotional++;
            }
        }

        return (rational, emotional);
    }
}

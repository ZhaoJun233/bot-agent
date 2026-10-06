namespace BotAgent.Domain.Conversation;

/// <summary>
/// 我们写给模型看的固定记号（不是群友输入的控制指令）。
/// 外部文本在进入上下文前必须先经过 <see cref="EscapeExternalControlTags" />，
/// 避免用户伪造系统、撤回、回复或工具结果标签。
/// </summary>
public static class MessageMarkers
{
    /// <summary>括号旁白的内部标记。</summary>
    public const string AsidePrefix = "〔旁白：";

    public const string AsideSuffix = "〕";

    /// <summary>把一段旁白包成标注形式。</summary>
    public static string AsAside(string inner) => AsidePrefix + inner + AsideSuffix;

    /// <summary>整条消息就是一段旁白（标注在开头）—— 这类不做回复引用目标。</summary>
    public static bool IsAside(string? text)
        => text is not null && text.TrimStart().StartsWith(AsidePrefix, StringComparison.Ordinal);

    /// <summary>
    /// 转义外部输入中可能伪造的内部控制标签。
    /// 只处理控制标签的 ASCII 变体，不改普通方括号内容，也不影响解析器生成的结构化标记。
    /// </summary>
    public static string EscapeExternalControlTags(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var escaped = text
            .Replace("[系统", "［系统", StringComparison.Ordinal)
            .Replace("[system", "［system", StringComparison.OrdinalIgnoreCase)
            .Replace("[assistant", "［assistant", StringComparison.OrdinalIgnoreCase)
            .Replace("[user", "［user", StringComparison.OrdinalIgnoreCase)
            .Replace("[tool", "［tool", StringComparison.OrdinalIgnoreCase)
            .Replace("[已撤回]", "［已撤回］", StringComparison.Ordinal)
            .Replace("[回复 ", "［回复 ", StringComparison.Ordinal)
            .Replace("<system>", "＜system＞", StringComparison.OrdinalIgnoreCase)
            .Replace("<tool>", "＜tool＞", StringComparison.OrdinalIgnoreCase);

        return escaped;
    }
}

namespace BotAgent.Domain.Conversation;

/// <summary>
/// 我们**写给模型看的**那几个固定记号（不是群友打的字，是我们给消息贴的标签）。
/// 放在这里是为了让“写标签的人”和“读标签的人”用同一份定义 ——
/// 以前这类记号散在各处（`[图片]`、`[已撤回]`、`（戳一戳）`），改一个忘一个就会对不上。
/// </summary>
public static class MessageMarkers
{
    /// <summary>
    /// 括号旁白（群友的“动作 / 表情说明”，如「（笑）」「（把猫抱过来）」）。
    ///
    /// 号主 2026-09-14 的口径：**不要单纯忽略，也要接收，但要特别注明** ——
    /// 所以旁白不会被丢掉，而是标成 <c>〔旁白：把猫抱过来〕</c> 进聊天记录与模型上下文：
    /// 模型能拿它理解现场，但一眼就知道那不是“他说的话”（见 BotAgentHost.AnnotateBracketAsides）。
    /// </summary>
    public const string AsidePrefix = "〔旁白：";

    public const string AsideSuffix = "〕";

    /// <summary>把一段旁白包成标注形式。</summary>
    public static string AsAside(string inner) => AsidePrefix + inner + AsideSuffix;

    /// <summary>整条消息就是一段旁白（标注在开头）—— 这类不做回复引用目标。</summary>
    public static bool IsAside(string? text)
        => text is not null && text.TrimStart().StartsWith(AsidePrefix, StringComparison.Ordinal);
}

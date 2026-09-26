namespace BotAgent.Domain.Conversation;

/// <summary>
/// 一条会话消息（headless 版：去掉所有 UI 属性，仅保留数据）。
///
/// ⚠ 时间默认值是 <c>default</c> 而**不是** <c>Clock.Now</c>：Domain 不许读时钟（R1 钉着，
/// 纯规则靠显式 now 参数）—— 与 <c>Domain/Agent/AgentSession</c> 同一条规矩。
/// 调用方造消息时显式写 <c>Timestamp</c>（见 BotAgentHost 的代发那一路）。
/// </summary>
public sealed class ChatMessage
{
    public required MessageRole Role { get; init; }

    /// <summary>
    /// 正文。可写：引用原文的兜底补写要把“更早的一条”改成真实原文（OneBot get_msg 查回来之后），
    /// 而消息是就地存在会话列表里的，只能改它自己；其它字段保持 init-only。
    /// </summary>
    public required string Text { get; set; }

    /// <summary>会话内自增序号（持久化不保留，仅在进程内用于前端增量同步）。</summary>
    public long Seq { get; internal set; }

    public DateTimeOffset Timestamp { get; init; }

    public string? SenderName { get; init; }

    /// <summary>发送者 QQ 号（群成员/好友），用于建立人物档案。</summary>
    public long? SenderId { get; init; }

    /// <summary>消息中的图片 URL（供模型识图）。</summary>
    public IReadOnlyList<string>? ImageUrls { get; init; }

    /// <summary>
    /// 这条消息是**直接跟机器人说话**：@ 了机器人自己，或者引用了机器人发的那条。
    /// 为什么单独记：被点名却沉默看着像坏了（阈值是给“要不要插嘴”用的，不该压掉直接问你的话）；
    /// 引用也要优先挂给点名的那个人 —— 否则群里看到的是“机器人在回别人”（管理员 2026-09-16 反馈）。
    /// </summary>
    /// <summary>
    /// 这条是不是“在跟机器人说话”（@ 了它 / 引用了它的话）。默认 init；
    /// 但**引用补查**可能在消息落库之后才从协议端确认“引的是机器人自己”，那时需要回填（V3 §8.3）。
    /// </summary>
    public bool DirectToBot { get; set; }

    /// <summary>QQ 原始消息 ID（用于历史去重）。</summary>
    public long? QqMessageId { get; init; }

    /// <summary>
    /// 这条消息后来被撤回了（OneBot 的 group_recall / friend_recall 事件）。
    /// 为什么要记：撤回后群友就看不到内容了，但机器人手里还有 —— 不标记的话它下一轮会
    /// 引用一句“已经不存在的消息”，或者把撤回前看到的内容当成公共信息继续用。
    /// 标记之后：送给模型的正文变成 <c>[已撤回]</c>（它知道“这里有过一条、被撤了”），
    /// 同时不能再被选作回复引用目标。
    /// 原始正文仍留在记录里（面板/日志是给运维看的），但**永远不会**再发给模型。
    /// </summary>
    public bool Recalled { get; set; }
}

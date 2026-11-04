namespace BotAgent.Domain.Conversation;

/// <summary>一条归档消息（面板展示用）。</summary>
public readonly record struct ArchivedMessage(
    long Seq,
    string Role,
    string Text,
    long TimeUnix,
    string? SenderName,
    long? SenderId,
    long? QqMessageId,
    bool Recalled);

/// <summary>磁盘上的会话快照（与 UI 模型解耦）。</summary>
public sealed class ConversationRecord
{
    public string? Id { get; set; }

    /// <summary>QQ 映射键："private:{QQ号}" 或 "group:{群号}"。null = 本地会话。</summary>
    public string? SourceKey { get; set; }

    /// <summary>
    /// 会话属于哪条通道（<c>private</c> = 私域 NapCat；<c>official</c> = QQ 开放平台）。
    /// 这个字段是**冗余记录**：真正管隔离的是 <see cref="SourceKey"/> 的 <c>official:</c> 前缀，
    /// 读回来时一律以它为准（老库没有这个字段，那就是私域）。
    /// 留字段是为了“看库就能分区”，以及日后要按通道做统计/导出时不必再解析 key。
    /// </summary>
    public string? Channel { get; set; }

    public string Kind { get; set; } = "LocalTest";

    public string Name { get; set; } = string.Empty;

    public string AvatarText { get; set; } = "?";

    public string? AvatarUrl { get; set; }

    public int AvatarIndex { get; set; }

    public List<MessageRecord> Messages { get; set; } = new();

    public long LastTimeUnix { get; set; }

    public int UnreadCount { get; set; }

    /// <summary>
    /// 下一个待分配的会话内序号（由 <c>_nextSeq</c> 导出）。
    /// 必须持久化：否则重启后序号从 0 重新计数，会同时搞坏两件事 ——
    ///   ① 画像的折叠边界（through_seq）比新序号大 → 记忆静默冻结；
    ///   ② (source_key, seq) 主键重号 → 新消息把老消息覆盖掉。
    /// </summary>
    public long NextSeq { get; set; }
}

/// <summary>会话里的一条消息（落库用的形状）。</summary>
public sealed class MessageRecord
{
    public string Role { get; set; } = "Peer";

    public string Text { get; set; } = string.Empty;

    public long TimeUnix { get; set; }

    public string? SenderName { get; set; }

    /// <summary>发送者 QQ 号（headless 新增：重启后仍能建人物档案）。</summary>
    public long? SenderId { get; set; }

    /// <summary>消息图片 URL（headless 新增：重启后仍可识图）。</summary>
    public List<string>? ImageUrls { get; set; }

    /// <summary>QQ 原始消息 ID（headless 新增：重启后历史去重仍生效）。</summary>
    public long? QqMessageId { get; set; }

    /// <summary>这条消息后来被撤回了（内容保留，只是标记）。</summary>
    public bool Recalled { get; set; }

    /// <summary>
    /// 会话内单调序号（同时被人物档案/画像用作“这条是否已在上下文里”的判据）。
    /// **必须持久化**：不存的话重启后序号从头计数，会小于画像里已记录的 ThroughSeq，
    /// 导致该成员的记忆静默停止更新（既不注入也不再摘要）。
    /// 可为负数 —— 拉取到的群历史会插到现有序号之前。
    /// </summary>
    public long Seq { get; set; }
}

using QQChatAgent.Services.OneBot;

namespace QQChatAgent.Services.Qq;

/// <summary>发一条消息的结果。</summary>
/// <param name="Ok">到底发出去没有。</param>
/// <param name="MessageId">协议端给的消息 id；拿不到时为 0（有的协议端不回 id，不是错误）。</param>
public readonly record struct SendResult(bool Ok, long MessageId = 0);

/// <summary>
/// QQ 消息源统一抽象：上层（会话/Agent）只依赖此接口收发消息，
/// 不关心背后是内置账号登录（Lagrange.Core）还是外部 OneBot 协议端。
/// </summary>
public interface IQqChatSource
{
    /// <summary>收到一条 QQ 入站消息（在后台线程触发，上层负责回 UI 线程）。</summary>
    event Action<QqChatMessage>? MessageReceived;

    /// <summary>收到戳一戳（后台线程触发）。别人互戳也会报上来，是否回应由上层决定。</summary>
    event Action<QqPokeEvent>? Poked;

    /// <summary>
    /// 有人撤回了消息（后台线程触发）。
    /// 撤回不是消息，无法从正文里看出来 —— 只能靠 notice 事件同步给上层，
    /// 否则会话里会一直留着一条“群里已经看不到”的消息（模型还会拿它接话）。
    /// </summary>
    event Action<QqRecallEvent>? MessageRecalled;

    /// <summary>连接状态变化（true=在线）。</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>
    /// 这条路属于哪个通道：<see cref="Channels.Private"/>（私域 NapCat）或
    /// <see cref="Channels.Official"/>（官方商用平台）。
    /// 默认是私域 —— 新加的协议端实现要显式写清楚，写错就等于把两个场景串到一起。
    /// </summary>
    string Channel => Channels.Private;

    bool IsConnected { get; }

    /// <summary>
    /// 登记「这个会话属于哪条通道」——多通道聚合（<see cref="ChannelRouter"/>）时用，
    /// 单通道实现忽略即可（默认空实现）。上层从库里恢复会话后调一次，
    /// 这样面板代发这类“没有入站消息可参考”的场景也能把消息发到正确的通道上。
    /// </summary>
    void RegisterTarget(string channel, bool isGroup, long id)
    {
    }

    /// <summary>
    /// 发一张音乐分享卡片（OneBot 的 music 段）。
    /// platform=163 就是网易云：QQ 客户端会渲染成可点开播放的音乐卡片。
    /// title = 歌名（官方那条通道没有音乐卡片、链接又必须报备，_title_ 就是它降级后的全部价值：
    /// 发一条带歌名的文字，让人搜得到）；OneBot 那条路用不到它，忽略即可。
    /// 默认实现返回 false —— 不是每个协议端都支持，上层要能优雅降级。
    /// </summary>
    Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// 向群聊/私聊发送纯文本。replyToMessageId 用于触发 QQ 的“回复”引用。
    /// 返回 <see cref="SendResult.Ok" />（发出去没有）+ <see cref="SendResult.MessageId" />：
    /// 拿得到消息 id 时上层会把它记到会话里 —— 这样**别人回复机器人那句话**时，
    /// 我们能认出“他在回你”，并把原话给模型看（以前 reply 段是被丢掉的）。
    /// </summary>
    Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null);

    /// <summary>
    /// 按消息 id 取回（纯文本、发送者 QQ）—— 引用原文**本地找不到**时的兜底（OneBot <c>get_msg</c>）。
    /// 典型场景：重启后别人引用了上一条进程发的消息（本地表空了），或那条早被清出上下文。
    /// 调用方不要在“接收循环里同步跑”的路径上等它（会死锁）：机器人是查完再补写到那条消息上的。
    /// </summary>
    Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default);

    /// <summary>
    /// 发一条语音（OneBot 的 record 段）。
    /// audioUrl 指向一个**协议端自己能访问**的音频地址（如 TTS 旁路容器的 /speak?text=…）：
    /// 机器人不下载音频，把 URL 交给协议端去下载、转 silk、上传 —— 这样这边就不用碰 silk 编码。
    /// 默认实现返回 false —— 不是每个协议端都支持，上层要能优雅降级成发文字。
    /// </summary>
    Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>发送一张图片（表情包）。data 为图片原始字节，协议端用 base64:// 形式接收。</summary>
    Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null);

    /// <summary>
    /// 戳一戳某人（群聊走 group_poke，私聊走 friend_poke）。
    /// 默认实现返回 false —— 不是每个协议端都支持，上层要能优雅降级。
    /// </summary>
    Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>获取群名称（用于会话列表）。拿不到时返回 null。</summary>
    Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default);

    /// <summary>
    /// 拉一个群成员的资料（群主/管理员/成员、群头衔、群名片、等级）。
    /// 为什么要专门问它：群消息事件里只有 role（身份），**群头衔**（自定义头衔）只有这个动作才有。
    /// 默认实现返回 null —— 不是每个协议端都支持，上层要能优雅降级（降级后至少还有 role）。
    /// </summary>
    Task<GroupMemberInfo?> GetGroupMemberInfoAsync(long groupId, long userId, CancellationToken ct = default)
        => Task.FromResult<GroupMemberInfo?>(null);

    /// <summary>
    /// 拉取登录账号在 QQ 里的“收藏表情”图片地址（协议端支持时）。
    /// 默认实现返回空列表 —— 有的协议端没有这个扩展动作，上层要能优雅降级。
    /// </summary>
    Task<List<string>> FetchCustomFacesAsync(int count = 48, CancellationToken ct = default)
        => Task.FromResult(new List<string>());

    /// <summary>
    /// 按消息 id 拿回这条消息里所有图片的**当前**地址。
    ///
    /// 为什么要它：QQ 的图片地址是带时效 rkey 的临时链（<c>multimedia.nt.qq.com.cn/download?...&amp;rkey=…</c>），
    /// 过期后 CDN 一律回 <b>400</b>；而协议端手里的消息记录能重新签发一份（实测：同一条消息重新签发后 200）。
    /// 上层在图片下载失败时调它兑底 —— 默认实现返回空，不支持的协议端就退化成“这张图这轮看不到”。
    /// </summary>
    Task<IReadOnlyList<string>> RefreshImageUrlsAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
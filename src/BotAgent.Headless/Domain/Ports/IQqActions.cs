using BotAgent.Services.Qq;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 协议端的**动作面**端口（由 <c>Services/OneBot/OneBotGateway</c> 实现，见 §6.4）。
///
/// 为什么要有它：<c>//</c> 任务里的 QQ 动作工具（点赞 / 戳一戳 / 表情回应 / 撤回 / 禁言 / 踢人 /
/// 改名片 / 改群名 / 退群 / 发文本）以前是"把 source 强转成 OneBotGateway"拿到手的 ——
/// 那等于用例层**靠具体类型做能力发现**：换一个协议端（官方通道）就悄悄失效，而且转发失败没有编译期提示。
/// 换成端口之后：能力是显式声明的（谁实现谁有），编不过就会当场发现。
/// </summary>
public interface IQqActions
{
    /// <summary>往会话里发一条文本（<c>//</c> 的产物也走它）。</summary>
    Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null);

    /// <summary>戳一戳。</summary>
    Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default);

    /// <summary>给某人点赞。</summary>
    Task<bool> SendLikeAsync(long userId, int times, CancellationToken ct = default);

    /// <summary>给一条消息加表情回应。</summary>
    Task<bool> SetMessageEmojiLikeAsync(long messageId, string emojiId, CancellationToken ct = default);

    /// <summary>撤回一条消息。</summary>
    Task<bool> DeleteMessageAsync(long messageId, CancellationToken ct = default);

    /// <summary>禁言。</summary>
    Task<bool> SetGroupBanAsync(long groupId, long userId, int seconds, CancellationToken ct = default);

    /// <summary>踢人（<paramref name="rejectAdd" /> = 拒绝再加群）。</summary>
    Task<bool> SetGroupKickAsync(long groupId, long userId, bool rejectAdd, CancellationToken ct = default);

    /// <summary>改群名片。</summary>
    Task<bool> SetGroupCardAsync(long groupId, long userId, string card, CancellationToken ct = default);

    /// <summary>改群名。</summary>
    Task<bool> SetGroupNameAsync(long groupId, string groupName, CancellationToken ct = default);

    /// <summary>退群（<paramref name="dismiss" /> = 解散）。</summary>
    Task<bool> SetGroupLeaveAsync(long groupId, bool dismiss, CancellationToken ct = default);
}

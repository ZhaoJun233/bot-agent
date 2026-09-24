namespace BotAgent.Domain.Conversation;

/// <summary>
/// 机器人自己发出去的一条消息（id → 原话 + 时间）。
///
/// 为什么在 Domain：它**是端口签名的一部分**（<c>IOwnMessageRepository</c> 的返回类型），
/// 而端口不能引用适配层类型（§3.2 的 <c>db → service</c>）—— 所以随端口一起下沉。
/// </summary>
public sealed record OwnMessage(long Id, string Text, DateTimeOffset At);

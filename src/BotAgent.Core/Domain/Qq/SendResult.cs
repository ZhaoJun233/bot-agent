namespace BotAgent.Services.Qq;

/// <summary>发一条消息的结果。</summary>
/// <param name="Ok">到底发出去没有。</param>
/// <param name="MessageId">协议端给的消息 id；拿不到时为 0（有的协议端不回 id，不是错误）。</param>
public readonly record struct SendResult(bool Ok, long MessageId = 0);

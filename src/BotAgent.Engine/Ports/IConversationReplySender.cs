using BotAgent.Domain.Platforms;

namespace BotAgent.Services.Ports;

/// <summary>
/// 平台中立的会话回复发送端口别名；继承完整 sourceKey 记账重载，旧上下文重载保留兼容。
/// </summary>
public interface IConversationReplySender : IQqMessageSender
{
    /// <summary>支持显式平台上下文的出站分句发送重载，优先使用显式上下文判定策略。</summary>
    Task<CadenceSendReport> SendWithCadenceAsync(
        PlatformContext context,
        bool isGroup,
        long targetId,
        string reply,
        long? replyTo,
        bool directAddress = false);
}

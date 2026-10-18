using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Platforms;

/// <summary>
/// 平台中立契约：真实适配器向上层发出的富入站事件（保留原生消息与归一化消息）。
/// </summary>
public interface IPlatformInboundChatSource
{
    event Action<QqChatMessage, InboundMessage>? RichInboundReceived;
}

/// <summary>
/// 平台中立契约：把一个上行源绑定到注册表中的适配器，而不向上层暴露具体适配器类型。
/// </summary>
public interface IPlatformSourceBinding
{
    IQqChatSource Source { get; }
}

/// <summary>
/// 平台中立契约：飞书别名目标与原生会话之间的既有映射（只投影既有绑定，不分配新身份）。
/// </summary>
public interface IFeishuTargetMap
{
    bool AllowsMappedTarget(bool isGroup, long alias);

    bool TryGetTargetConversation(bool isGroup, long alias, out ConversationId? target);

    bool TryGetTargetAlias(ConversationId conversation, out long alias);
}

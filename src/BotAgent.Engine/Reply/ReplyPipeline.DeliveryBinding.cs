using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Services.Conversations;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    private static IDisposable BindReplyDelivery(BotConversation conversation, long? messageId, bool proactive)
    {
        var source = PlatformSourceExecution.Current;
        MessageRef? trigger = null;
        if (source?.Origin.Driver.PlatformId == PlatformId.QqOfficial
            && !proactive && messageId is > 0 && source.Origin.Trigger is { } original
            && ConversationIdCodec.TryParse(conversation.SourceKey, out var target)
            && original.Conversation == target)
            trigger = new(target, messageId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return ReplyDeliveryExecution.Enter(source, trigger);
    }
}

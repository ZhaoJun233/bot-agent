namespace BotAgent.Domain.Conversation;

public enum ConversationKind
{
    LocalTest,
    PrivateChat,
    GroupChat,
    ChannelChat
}

public enum MessageRole
{
    Peer,
    Self,
    System
}

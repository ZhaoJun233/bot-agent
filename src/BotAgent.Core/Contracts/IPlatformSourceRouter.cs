using BotAgent.Services.OneBot;
namespace BotAgent.Services.Qq;

/// <summary>Router-backed source used by Engine to resolve conversation identity and native targets.</summary>
public interface IPlatformSourceRouter : IQqChatSource
{
    string? ConversationKey(QqChatMessage message, Func<string, bool> exists);
    (bool IsGroup, long Id) CompatibilityTarget(string sourceKey);
    PlatformSourceLease? CaptureForConversation(string sourceKey);
}

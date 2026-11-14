using BotAgent.Adapters.Official;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

internal static class OfficialProbeContext
{
    public static OfficialDeliveryLedger OpenLedger(string identityPath, string app = "app-A")
        => new(new OfficialDeliverySnapshotFile(identityPath + ".delivery"), app);

    public static PlatformSourceLease Source(IQqChatSource gateway, bool group, long target, long message)
        => new(new Owner(), gateway, new(new(PlatformId.QqOfficial, AccountScope.Legacy, "synthetic-driver"),
            new MessageRef(new(PlatformId.QqOfficial, AccountScope.Legacy,
                group ? ConversationKind.GroupChat : ConversationKind.PrivateChat, target.ToString()), message.ToString())),
            CancellationToken.None, () => true);

    private sealed class Owner : IPlatformSourceOwner
    { public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => null; }
}

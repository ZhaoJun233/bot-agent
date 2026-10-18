namespace BotAgent.Services.Qq;

/// <summary>Optional history capability of a chat source.</summary>
public interface IPlatformHistorySource : IQqChatSource
{
    Task<List<BotAgent.Services.OneBot.QqChatMessage>> GetGroupMsgHistoryAsync(
        long groupId, int count = 20, CancellationToken ct = default);
}

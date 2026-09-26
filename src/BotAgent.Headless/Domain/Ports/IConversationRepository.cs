using BotAgent.Domain.Conversation;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 会话与消息的存取端口（由 <c>Adapters/Persistence/ConversationStore</c> 实现）。
///
/// 为什么要有它：用例层（回复流程、面板、归档巡检）只该说"把这几条会话存下来""把这个会话的归档读出来"，
/// 不该知道 SQL 长什么样、表有几张。有了端口，测试可以塞假仓储，换库也不必动用例。
/// 形状与语义**与实现一一对应**（记录类型在 <c>Domain/Conversation</c>，聚合边界 = 一个会话）。
/// </summary>
public interface IConversationRepository
{
    /// <summary>读出全部会话（含最近消息）。</summary>
    List<ConversationRecord> LoadAsync();

    /// <summary>请求保存（实现可以合并/防抖，不保证立刻落盘）。</summary>
    void RequestSave(IEnumerable<ConversationRecord> records);

    /// <summary>删掉一个会话及其消息。</summary>
    void DeleteConversation(string sourceKey);

    /// <summary>把被挤出上下文的消息追加进归档。</summary>
    void AppendArchive(string sourceKey, IReadOnlyList<ChatMessage> evicted);

    /// <summary>读归档里最近的若干条。</summary>
    List<ArchivedMessage> ReadArchive(string sourceKey, int limit);

    /// <summary>归档条数（面板展示与"要不要继续翻旧账"用）。</summary>
    long ArchiveCount(string sourceKey);

    /// <summary>清空某会话的活动与归档消息，但保留会话入口。</summary>
    void DeleteMessages(string sourceKey);
}

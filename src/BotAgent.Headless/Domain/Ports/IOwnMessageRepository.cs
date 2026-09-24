using BotAgent.Domain.Conversation;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 「机器人自己发出去的消息」台账端口（由 <c>Adapters/Persistence/OwnMessageStore</c> 实现，见 §6.4）。
///
/// 它存在的原因很具体：别人**引用回复**机器人上一句时，得认出"这是我说的哪一句"（2026-09-19 反馈被吞）。
/// 用例（<c>OwnMessageLedger</c>）只该说"我发过哪些""记一条""剪一下"，不该知道它落在哪张表、怎么导入旧 JSON。
/// </summary>
public interface IOwnMessageRepository
{
    /// <summary>台账最多留多少条（引旧消息的情况极少，两百条足够）。</summary>
    int MaxEntries { get; }

    /// <summary>读出最近若干条（按时间倒序）。首次调用会把老的 JSON 导进来（幂等）。</summary>
    List<OwnMessage> LoadRecent(int max);

    /// <summary>记一条（已存在就覆盖）。</summary>
    void Upsert(long id, string text, DateTimeOffset at);

    /// <summary>只留最近 <paramref name="max" /> 条（按时间）。</summary>
    void PruneTo(int max);
}

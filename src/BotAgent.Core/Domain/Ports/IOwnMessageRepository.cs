using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;

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

    /// <summary>读取保留的 legacy 数据供迁移/检查；裸 id 不能参与 scoped 查询。</summary>
    List<OwnMessage> LoadRecent(int max);

    /// <summary>仅兼容旧签名；裸 id 缺 scope，不得新增模糊身份。</summary>
    void Upsert(long id, string text, DateTimeOffset at);

    /// <summary>Read only fully scoped identities; legacy implementations fail closed.</summary>
    List<OwnMessage> LoadRecentScoped(int max) => new();

    /// <summary>Persist the full platform/account/conversation/native-message identity.</summary>
    void Upsert(MessageRef messageRef, string text, DateTimeOffset at)
        => throw new NotSupportedException("Scoped own-message storage is not supported.");

    /// <summary>只剪枝 scoped 台账，不删除缺 scope 的 legacy 保留数据。</summary>
    void PruneTo(int max);
}

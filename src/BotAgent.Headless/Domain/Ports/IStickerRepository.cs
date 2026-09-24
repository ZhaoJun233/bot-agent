using BotAgent.Domain.Stickers;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 表情包库的存取端口（由 <c>Adapters/Persistence/StickerStore</c> 实现，见 §6.4）。
///
/// 为什么要有它：用例层（收集/描述/策展/发送表情、回复链挑图）只该说"库里有哪些""这张标成用过了"，
/// 不该知道索引怎么落库、图片放在哪个目录。有了端口，表情包那几条规则可以**不连库**用假仓储测。
/// 图片本体是 IO（读字节），所以它在端口里；**描述文字是纯规则**，在 <see cref="StickerText" />。
/// </summary>
public interface IStickerRepository
{
    /// <summary>库里现在有多少张。</summary>
    int Count { get; }

    /// <summary>已生成说明的张数（没说明的没法按语境检索，只能随机兜底）。</summary>
    int DescribedCount { get; }

    /// <summary>从数据目录载入索引（启动一次）。</summary>
    void Load(string dataRoot);

    /// <summary>当前全部条目（只读快照）。</summary>
    List<StickerRecord> Snapshot();

    /// <summary>按短 id 取一张。</summary>
    StickerRecord? Find(string id);

    /// <summary>收藏一张图；同一张内容已存在时返回 null（不重复存）。</summary>
    StickerRecord? Add(byte[] data, string ext, string? fromUid, long fromGroup);

    /// <summary>记一次使用（挑中并发出去了）。</summary>
    void MarkUsed(string id);

    /// <summary>写入模型给出的说明 / 关键词 / 是否表情包。</summary>
    void SetDescription(string id, string? desc, IEnumerable<string>? tags, bool? isSticker = null);

    /// <summary>识别失败一次（累计几次之后不再重试）。</summary>
    void MarkDescribeFailed(string id);

    /// <summary>删掉一张（返回是否真的删掉了）。</summary>
    bool Remove(string id, string? reason = null);

    /// <summary>按容量上限淘汰（用得少 + 最久没用优先），返回被淘汰的条目。</summary>
    List<StickerRecord> EnforceLimit(int max);

    /// <summary>按语境挑候选（关键词命中 + 随机兜底）。</summary>
    List<StickerRecord> PickCandidates(string query, int count, int excludeUsedWithinSeconds = -1);

    /// <summary>读图片字节（发送 / 面板取图都走它）。文件不在时抛 —— 由调用方决定怎么降级。</summary>
    Task<byte[]> ReadBytesAsync(StickerRecord item, CancellationToken ct = default);
}

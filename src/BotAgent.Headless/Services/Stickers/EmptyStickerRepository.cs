using BotAgent.Domain.Ports;
using BotAgent.Domain.Stickers;

namespace BotAgent.Services.Stickers;

/// <summary>
/// 空表情包库：功能不存在时用它顶替真实库。
///
/// 为什么要有它：只把"发送"短路是不够的 —— 回复流程还会从 <c>Store.PickCandidates</c>
/// 挑候选塞进提示词。库是空的，模型就选不出表情包，功能才算真的不存在。
/// 全部写操作 no-op、全部读操作返回空，不碰磁盘。
/// </summary>
public sealed class EmptyStickerRepository : IStickerRepository
{
    public static readonly EmptyStickerRepository Instance = new();

    public int Count => 0;

    public int DescribedCount => 0;

    public void Load(string dataRoot) { }

    public List<StickerRecord> Snapshot() => new();

    public StickerRecord? Find(string id) => null;

    public StickerRecord? Add(byte[] data, string ext, string? fromUid, long fromGroup) => null;

    public void MarkUsed(string id) { }

    public void SetDescription(string id, string? desc, IEnumerable<string>? tags, bool? isSticker = null) { }

    public void MarkDescribeFailed(string id) { }

    public bool Remove(string id, string? reason = null) => false;

    public List<StickerRecord> EnforceLimit(int max) => new();

    public List<StickerRecord> PickCandidates(string query, int count, int excludeUsedWithinSeconds = -1) => new();

    public Task<byte[]> ReadBytesAsync(StickerRecord item, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());
}
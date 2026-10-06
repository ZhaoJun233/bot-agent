namespace BotAgent.Domain.Stickers;

/// <summary>
/// 表情包的**纯文本规则**：给模型 / 巡检看的一行摘要。
///
/// 为什么单独在 Domain：它原来是 <c>StickerStore</c> 上的一个 <c>static</c> 方法，
/// 而用例层（回复链选表情、表情包巡检）要用它 —— 静态方法过不了端口，于是按"纯函数下沉"的老规矩搬到 Domain，
/// 顺带把它变成**不连库就能测**的一条规则。
/// </summary>
public static class StickerText
{
    /// <summary>给模型看的一行摘要（提示词与巡检都用它）。</summary>
    public static string Describe(StickerRecord item)
    {
        var desc = string.IsNullOrWhiteSpace(item.Desc) ? "（还没识别）" : item.Desc;
        var tags = item.Tags.Count > 0 ? "｜" + string.Join("/", item.Tags) : string.Empty;
        return desc + tags;
    }
}

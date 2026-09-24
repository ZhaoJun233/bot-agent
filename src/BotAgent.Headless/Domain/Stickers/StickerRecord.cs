using System.Text.Json.Serialization;

namespace BotAgent.Domain.Stickers;

/// <summary>表情包库里的一张图。</summary>
public sealed class StickerRecord
{
    /// <summary>短 id（内容哈希前 8 位）：模型和面板都用它引用这张图。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>内容 SHA-256（去重依据：同一张图被转发多少次都只存一份）。</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>文件名（相对 stickers/ 目录）。</summary>
    public string File { get; set; } = string.Empty;

    public string Ext { get; set; } = "png";

    public long Bytes { get; set; }

    public long AddedAt { get; set; }

    public long LastUsedAt { get; set; }

    public int Uses { get; set; }

    /// <summary>来源：哪个群、谁发的（仅作展示与排障，表情包库是全局共用的）。</summary>
    public string? FromUid { get; set; }

    public long FromGroup { get; set; }

    /// <summary>模型生成的一句话说明（用它做语境检索）。</summary>
    public string? Desc { get; set; }

    /// <summary>模型生成的情绪/场景关键词。</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 模型判定“这是不是一张可以用来说话的表情包”。
    /// null = 还没识别；false = 聊天截图/广告/纯文字图之类，不入库。
    /// 为什么需要它：线上实测把群友发的**聊天截图**也当成表情包收进去了（描述里写着“文字写确认是旧版本…”），
    /// 这种图发出去只会尴尬 —— 得让看过图的那个模型先把关。
    /// </summary>
    public bool? IsSticker { get; set; }

    public bool Described { get; set; }

    public int DescribeAttempts { get; set; }

    [JsonIgnore]
    public string AbsolutePath { get; set; } = string.Empty;
}

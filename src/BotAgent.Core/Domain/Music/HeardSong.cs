using System.Text.Json.Serialization;

namespace BotAgent.Domain.Music;

/// <summary>
/// 一首“听过的歌”（纯数据）。第一次听到时记下歌名 / 歌手 / 时长 / 歌词摘录与波形特征，
/// 下一轮就能直接引用（不必再搜一次）—— “上次那首歌”这种上下文就靠它）。
///
/// 为什么在 Domain：它**是端口签名的一部分**（<c>IMusicRepository</c> 的出入参），
/// 而端口不能引用适配层类型（§3.2 的 <c>db → service</c>），所以随端口一起下沉。
/// </summary>
public sealed class HeardSong
{
    /// <summary>平台 + 歌曲 id（例如 <c>163:12345</c>）：跨平台的稳定主键。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>平台（<c>163</c> = 网易云）。</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>平台内的歌曲 id。</summary>
    public string SongId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    /// <summary>波形特征（低码率音频算出来的，用于“听起来像不像”的粗判）。</summary>
    public string Features { get; set; } = string.Empty;

    /// <summary>歌词摘录（给模型看的那一小段）。</summary>
    public string LyricExcerpt { get; set; } = string.Empty;

    public DateTimeOffset FirstHeard { get; set; }

    public DateTimeOffset LastHeard { get; set; }

    public int HeardCount { get; set; }

    /// <summary>有没有波形特征（没有就只能靠歌名/歌词聊）。</summary>
    [JsonIgnore]
    public bool HasWaveform => !string.IsNullOrWhiteSpace(Features);

    /// <summary>这条记录还算不算“新”（<paramref name="ttlDays" /> 天内听过）。</summary>
    public bool IsFresh(DateTimeOffset now, int ttlDays)
        => now - LastHeard < TimeSpan.FromDays(Math.Max(1, ttlDays));
}

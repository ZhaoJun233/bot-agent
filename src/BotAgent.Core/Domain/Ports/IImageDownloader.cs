namespace BotAgent.Domain.Ports;

/// <summary>
/// 图片下载端口（由 <c>Adapters/Model/ImageDownloader</c> 实现）。
///
/// 为什么要有它：QQ 的图片地址是**带时效 rkey 的临时链**，过期后 CDN 一律回 400，所以下载器里带着
/// 缓存 + "过期就让上层重签一次再下"的回调；模型客户端与表情包库都要用同一份缓存。
/// 这些细节（HTTP、base64、mime 嗅探、缓存键）不该长在用例里。
/// </summary>
public interface IImageDownloader
{
    /// <summary>rkey 过期时让上层重签图片地址（实现会在下载失败后调它一次）。</summary>
    Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshUrls { get; set; }

    /// <summary>命中缓存的次数（面板/日志如实显示）。</summary>
    int CacheHits { get; }

    /// <summary>重签后重下成功的次数。</summary>
    int RefreshedCount { get; }

    /// <summary>按 URL 取原始字节（失败 = null）。</summary>
    Task<(byte[] Data, string Mime, string Ext)?> DownloadBytesAsync(string url, CancellationToken ct, long? messageId = null);
}

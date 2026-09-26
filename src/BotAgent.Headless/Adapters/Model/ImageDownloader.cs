using System.Text;
using System.Net;
using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Model;

/// <summary>图片下载器：把图片 URL 下载并转成 base64 data URL（供多模态模型识图），
/// 也给表情包库提供原始字节。</summary>
internal sealed class ImageDownloader : IImageDownloader
{
    /// <summary>出网（由装配点给的那个"图片专用"客户端，超时 8 秒）。</summary>
    private readonly IHttpFetcher _http;

    public ImageDownloader(IHttpFetcher http) => _http = http;

    private const int MaxImageBytes = 6 * 1024 * 1024;

    // ── 缓存 ──
    // 为什么要缓存：QQ 的图片地址是**带时效 rkey 的临时链**，过期后 CDN 一律回 400。
    // 而图片会一直留在会话上下文里（几百条），每生成一次就会重新去下一遍 ——
    // 实测线上：同一张图在 5 分钟内被反复重试、全部 400，日志刷屏且模型看不到图。
    // 缓存按 **URL 作键**（同一张图事件里的 URL 不变），容量/字节双上限，满了挑最旧的逐出。
    private const int MaxCacheEntries = 48;
    private const long MaxCacheBytes = 32L * 1024 * 1024;

    private readonly object _cacheGate = new();
    private readonly Dictionary<string, (byte[] Data, string Mime, string Ext)> _cache = new();
    private readonly Queue<string> _cacheOrder = new();
    private long _cacheBytes;

    /// <summary>已失败过的 URL（→ 下次重试不早于这个时间）：避免每轮生成都对着一张过期图重试+刷日志。</summary>
    private readonly Dictionary<string, DateTimeOffset> _failedUntil = new();

    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 图片地址过期（400）时去找协议端**重新签发**地址的回调：入参是消息 id，返回该消息里所有图片的当前地址。
    /// 由 BotAgentHost 接上 `IQqChatSource.RefreshImageUrlsAsync`；没接上就只能认赔（这张图这轮看不到）。
    /// </summary>
    public Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshUrls { get; set; }

    /// <summary>缓存命中数（测试用）。</summary>
    public int CacheHits { get; private set; }

    /// <summary>“地址过期 → 重新签发”成功的次数（测试用）。</summary>
    public int RefreshedCount { get; private set; }

    /// <summary>
    /// 是否允许从内网/回环地址下载（QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1）。
    /// 默认关闭：图片 URL 来自 QQ 事件，属不可信输入，SSRF 防护必须默认生效。
    /// 只在“自建 NapCat 用内网地址、或集成测试用本地图片服务器”时手动打开，
    /// 官方镜像与远程部署都不应该打开它。
    /// </summary>
    public bool AllowPrivateHosts { get; init; } =
        Environment.GetEnvironmentVariable("QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS") == "1";

    /// <summary>
    /// 下载图片字节（失败返回 null）。供多模态识图与表情包库共用。
    /// 流程：缓存 → 直接下 → 400（rkey 过期）时让协议端重新签发地址再下一次。
    /// </summary>
    /// <param name="messageId">这条图片来自哪条消息（可选）：地址过期时用它能找协议端重新签发。</param>
    public async Task<(byte[] Data, string Mime, string Ext)?> DownloadBytesAsync(string url, CancellationToken ct, long? messageId = null)
    {
        try
        {
            if (TryGetCached(url) is { } cached)
            {
                return cached;
            }

            lock (_cacheGate)
            {
                if (_failedUntil.TryGetValue(url, out var until) && Clock.Now < until)
                {
                    return null;   // 刚失败过，别对着一张取不到的图每轮重试
                }
            }

            var outcome = await FetchAsync(url, ct);
            var result = outcome.Image;
            if (result is null && outcome.CanRetryWithFreshUrl && messageId is long mid && RefreshUrls is not null)
            {
                // 失败时让协议端换一份**当前有效**的地址再试一次（绝大多数情况是 rkey 过期 → 400）。
                // 只重试一次、失败后记 10 分钟退避：不会变成“对着一张取不到的图每轮重试”。
                // ★ 这里**不**打“下载失败”：地址过期→重签是设计好的正常路径，不是事故。
                //   以前每张图都先刷一行红色“下载失败（HTTP 400）”，面板上看着像一直在出错
                //   （管理员 2026-09-18 报的）；现在只在“重签也拿不到”时才报失败。
                result = await FetchRefreshedAsync(url, mid, outcome.Status, ct);
                if (result is null)
                {
                    Services.FileLog.Write("Vision", $"图片取不到：原地址 HTTP {outcome.Status}，协议端重签后仍然失败（消息 {mid}）");
                }
            }
            else if (result is null && outcome.Status > 0)
            {
                Services.FileLog.Write("Vision", $"图片下载失败（HTTP {outcome.Status}）: {Truncate(url, 120)}");
            }

            if (result is not { } ok)
            {
                lock (_cacheGate)
                {
                    _failedUntil[url] = Clock.Now + FailureBackoff;
                }

                return null;
            }

            Remember(url, ok);
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.FileLog.Write("Vision", $"图片下载异常（{ex.GetType().Name}: {ex.Message}）: {Truncate(url, 120)}");
            return null;
        }
    }

    /// <summary>一次抓取的结果：拿到了图，或者“失败了但值得换个新地址再试”。</summary>
    private readonly record struct FetchOutcome((byte[] Data, string Mime, string Ext)? Image, bool CanRetryWithFreshUrl, int Status);

    /// <summary>用协议端重新签发的地址下同一张图。对不上同一张（fileid 不同）时退而用第一个地址。</summary>
    private async Task<(byte[] Data, string Mime, string Ext)?> FetchRefreshedAsync(string staleUrl, long messageId, int status, CancellationToken ct)
    {
        IReadOnlyList<string> fresh;
        try
        {
            fresh = await RefreshUrls!(messageId, ct);
        }
        catch (Exception ex)
        {
            Services.FileLog.Write("Vision", $"重新签发图片地址失败（消息 {messageId}）: {ex.GetType().Name} {ex.Message}");
            return null;
        }

        if (fresh.Count == 0)
        {
            Services.FileLog.Write("Vision", $"图片地址已过期，协议端也拿不到新地址（消息 {messageId}）");
            return null;
        }

        var wanted = Param(staleUrl, "fileid");
        var pick = wanted is not null
            ? fresh.FirstOrDefault(u => string.Equals(Param(u, "fileid"), wanted, StringComparison.Ordinal))
            : null;
        pick ??= fresh[0];

        var bytes = await FetchAsync(pick, ct);
        if (bytes.Image is not null)
        {
            RefreshedCount++;
            // 信息性一行（不是错误）：QQ 的图片地址本来就短命，重签取回是正常路径
            Services.FileLog.Write("Vision", $"图片地址已过期（HTTP {status}）→ 已用协议端重签的地址取回（消息 {messageId}）");
        }

        return bytes.Image;
    }

    /// <summary>取 URL 里的某个查询参数（用来认“同一张图”：fileid 不变，rkey 变）。</summary>
    private static string? Param(string url, string name)
    {
        var marker = name + "=";
        var at = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var start = at + marker.Length;
        var end = url.IndexOf('&', start);
        return end < 0 ? url[start..] : url[start..end];
    }

    private async Task<FetchOutcome> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!IsSafeImageUrl(url, out var uri))
            {
                Services.FileLog.Write("Vision", $"图片地址被拒（SSRF 防护）: {Truncate(url, 120)}");
                return new FetchOutcome(null, false, 0);
            }

            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                // 4xx/5xx ⇒ 值得拿协议端重新签发的地址再试一次（最常见的是 rkey 过期回 400）。
                // 日志由调用方写（它才知道有没有“重新签发”这条路）。
                return new FetchOutcome(null, true, (int)response.StatusCode);
            }

            // 有 Content-Length 时先拦，避免把超大响应当进内存
            if (response.Content.Headers.ContentLength is long declared &&
                (declared <= 0 || declared > MaxImageBytes))
            {
                Services.FileLog.Write("Vision", $"图片声明大小异常 {declared}: {Truncate(url, 120)}");
                return new FetchOutcome(null, false, 0);
            }

            var bytes = await ReadCappedAsync(response.Content, MaxImageBytes, ct);
            if (bytes is null || bytes.Length == 0)
            {
                Services.FileLog.Write("Vision", $"图片超限或为空: {Truncate(url, 120)}");
                return new FetchOutcome(null, false, 0);
            }

            var mime = DetectMime(bytes);
            var ext = mime switch
            {
                "image/jpeg" => "jpg",
                "image/gif" => "gif",
                "image/webp" => "webp",
                _ => "png"
            };
            return new FetchOutcome((bytes, mime, ext), false, 200);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.FileLog.Write("Vision", $"图片下载异常: {ex.Message} | {Truncate(url, 120)}");
            return new FetchOutcome(null, false, 0);
        }
    }

    /// <summary>
    /// 下载图片并转为 data:image/...;base64,xxx；失败返回 null。
    /// 图片 URL 来自 QQ 事件，属**不可信输入** → 先做 SSRF 防护再下载。
    /// </summary>
    public async Task<string?> DownloadAsDataUrl(string url, CancellationToken ct, long? messageId = null)
    {
        var downloaded = await DownloadBytesAsync(url, ct, messageId);
        return downloaded is { } d ? $"data:{d.Mime};base64,{Convert.ToBase64String(d.Data)}" : null;
    }

    // ── 缓存读写 ──

    private (byte[] Data, string Mime, string Ext)? TryGetCached(string url)
    {
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(url, out var hit))
            {
                return null;
            }

            CacheHits++;
            return hit;
        }
    }

    private void Remember(string url, (byte[] Data, string Mime, string Ext) value)
    {
        lock (_cacheGate)
        {
            if (_cache.ContainsKey(url))
            {
                return;
            }

            _cache[url] = value;
            _cacheOrder.Enqueue(url);
            _cacheBytes += value.Data.Length;

            while ((_cache.Count > MaxCacheEntries || _cacheBytes > MaxCacheBytes) && _cacheOrder.Count > 0)
            {
                var oldest = _cacheOrder.Dequeue();
                if (_cache.Remove(oldest, out var dropped))
                {
                    _cacheBytes -= dropped.Data.Length;
                }
            }

            _failedUntil.Remove(url);
        }
    }

    /// <summary>
    /// SSRF 防护：只允许公网 http(s) 图片地址。
    /// 拦的是这类被构造出来的地址： http://127.0.0.1:6099/...（NapCat WebUI 自身）、
    /// http://169.254.169.254/...（云元数据）、http://napcat:3001/...（容器内服务）。
    /// 局限：不做 DNS 解析，因此无法拦截“解析到内网 IP 的公网域名”（需要出站防火墙）。
    /// </summary>
    private bool IsSafeImageUrl(string? url, out Uri uri)
    {
        uri = null!;

        // 显式放行（QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1，仅供自建/测试）
        if (AllowPrivateHosts)
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url.Trim(), UriKind.Absolute, out var permissive) &&
                (permissive.Scheme == Uri.UriSchemeHttp || permissive.Scheme == Uri.UriSchemeHttps))
            {
                uri = permissive;
                return true;
            }

            return false;
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = parsed.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // 单标签主机名（localhost / napcat / redis …）一律拒绝：
        // 真实图片域名必定带点（gchat.qpic.cn 等）
        if (!host.Contains('.'))
        {
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // IP 字面量：拒绝回环 / 私有 / 链路本地 / 未指定
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal ||
                ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            {
                return false;
            }

            if (IsPrivateV4(ip))
            {
                return false;
            }

            // IPv4-mapped IPv6（::ffff:127.0.0.1）
            if (ip.IsIPv4MappedToIPv6 && IsPrivateV4(ip.MapToIPv4()))
            {
                return false;
            }
        }

        uri = parsed;
        return true;
    }

    private static bool IsPrivateV4(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10                                 // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)               // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254)               // 169.254.0.0/16 链路本地（云元数据）
            || b[0] == 127                                // 127.0.0.0/8
            || b[0] == 0;                                 // 0.0.0.0/8
    }

    /// <summary>流式读取并限制总字节数：超过上限立即返回 null，不会把超大响应全量读进内存。</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int cap, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();

        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > cap)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string DetectMime(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return "image/png";
    }
}

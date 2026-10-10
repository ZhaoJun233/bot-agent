using System.Net;

namespace BotAgent.Services.Net;

/// <summary>
/// 出站 URL 的安全闸门（SSRF 防护）：图片、链接与页面抓取共用。
/// 此处校验 URL 和跳转；DNS 地址在安全客户端的连接回调中校验并固定。
///
/// 为什么必须拦：这些 URL 来自群消息，任何人都能发。
/// 不拦的话群友发一个 http://169.254.169.254/latest/meta-data/（云元数据）
/// 或 http://napcat:3001/（容器内服务）就能让服务器替他去读内网。
/// </summary>
public static class SafeUrl
{
    /// <summary>校验一个出站 URL；不通过时返回 false 并给出中文原因。</summary>
    public static bool TryValidate(string? url, bool allowPrivate, out Uri uri, out string reason)
    {
        uri = null!;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            reason = "不是 http/https 地址";
            return false;
        }

        if (allowPrivate)
        {
            uri = parsed;
            return true;
        }

        var host = parsed.DnsSafeHost.Trim('[', ']').TrimEnd('.');
        if (host.Length == 0)
        {
            reason = "没有主机名";
            return false;
        }

        // 单标签主机名（localhost / napcat / redis …）一律拒绝：真实站点必定带点
        if (!IPAddress.TryParse(host, out _) && !host.Contains('.'))
        {
            reason = "单标签主机名（疑似内网服务）";
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            reason = "内网域名";
            return false;
        }

        if (IPAddress.TryParse(host, out var ip) && OutboundAddressPolicy.IsBlocked(ip))
        {
            reason = "回环/私有/链路本地地址";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// 在底层客户端已关闭自动重定向时，逐跳跟随并重新校验目标。
    /// 返回的响应由调用方负责释放；中间响应在跳转时立即释放。
    /// </summary>
    public static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        IHttpFetcher http,
        Uri start,
        Func<Uri, HttpRequestMessage> createRequest,
        bool allowPrivate,
        CancellationToken ct,
        int maxRedirects = 5)
    {
        if (maxRedirects < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRedirects));
        }

        var current = start;
        for (var hop = 0; hop <= maxRedirects; hop++)
        {
            if (!TryValidate(current.ToString(), allowPrivate, out _, out var invalidReason))
            {
                throw new HttpRequestException($"出站目标被拒绝（{invalidReason}）");
            }

            using var request = createRequest(current);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var location = response.Headers.Location;
            if (!IsRedirect(response.StatusCode) || location is null)
            {
                return response;
            }

            if (hop == maxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("重定向超过上限");
            }

            if (!Uri.TryCreate(current, location, out var next))
            {
                response.Dispose();
                throw new HttpRequestException("重定向 Location 无效");
            }

            if (!TryValidate(next.ToString(), allowPrivate, out _, out var reason))
            {
                response.Dispose();
                throw new HttpRequestException($"重定向目标被拒绝（{reason}）");
            }

            response.Dispose();
            current = next;
        }

        throw new HttpRequestException("重定向超过上限");
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}

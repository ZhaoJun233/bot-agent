using BotAgent.Plugins.Net;
using BotAgent.Services.Net;

namespace BotAgent.Adapters.Net;

/// <summary>Public page capability; its client must use the host's public-destination connection policy.</summary>
public sealed class WebPageTransport(IHttpFetcher http, Func<bool> allowPrivate) : IWebPageTransport
{
    private const int DefaultReadLimitBytes = 2 * 1024 * 1024;
    private const int MaxReadLimitBytes = 4 * 1024 * 1024;

    public const string WikimediaBotUserAgent = "BotAgent/1.5 (+https://github.com/ZhaoJun233/bot-agent)";

    public static string? ResolveUserAgent(Uri target, string? requested)
    {
        if ((target.Host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase)
             || target.Host.EndsWith(".wikimedia.org", StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(requested) || requested.StartsWith("Mozilla/", StringComparison.OrdinalIgnoreCase)))
        {
            return WikimediaBotUserAgent;
        }
        return requested;
    }

    public bool TryValidate(string? url, out Uri uri, out string reason)
        => SafeUrl.TryValidate(url, allowPrivate(), out uri, out reason);

    public async Task<WebPageResponse> GetAsync(WebPageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReadLimitBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        var limit = Math.Min(request.ReadLimitBytes ?? DefaultReadLimitBytes, MaxReadLimitBytes);
        using var response = await SafeUrl.SendFollowingRedirectsAsync(http, request.Url, target =>
        {
            var message = new HttpRequestMessage(HttpMethod.Get, target);
            var ua = ResolveUserAgent(target, request.UserAgent);
            if (ua is not null) message.Headers.TryAddWithoutValidation("User-Agent", ua);
            if (request.AcceptLanguage is not null) message.Headers.TryAddWithoutValidation("Accept-Language", request.AcceptLanguage);
            return message;
        }, allowPrivate(), ct).ConfigureAwait(false);
        var body = Array.Empty<byte>();
        if (response.IsSuccessStatusCode)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[32 * 1024];
            while (buffer.Length < limit)
            {
                var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read <= 0) break;
                buffer.Write(chunk, 0, read);
            }
            body = buffer.ToArray();
        }
        return new WebPageResponse(response.RequestMessage?.RequestUri ?? request.Url, (int)response.StatusCode, body);
    }
}

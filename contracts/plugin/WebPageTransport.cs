namespace BotAgent.Plugins.Net;

/// <summary>A public GET, with no credentials or arbitrary request headers.</summary>
public sealed record WebPageRequest(Uri Url, int? ReadLimitBytes = null,
    string? UserAgent = null, string? AcceptLanguage = null);

/// <summary>Fully materialized response; no live connection or stream crosses the package boundary.</summary>
public sealed record WebPageResponse(Uri Url, int StatusCode, byte[] Body)
{
    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;
}

/// <summary>Host-owned URL policy and redirect handling. The complete body read owns the operation lifetime.</summary>
public interface IWebPageTransport
{
    public const string Permission = "network.public-read";

    bool TryValidate(string? url, out Uri uri, out string reason);

    /// <summary>ReadLimitBytes stops after a 32-KiB read crosses the limit; null reads the complete body.
    /// Non-success responses have an empty body. The host disposes the response before returning.</summary>
    Task<WebPageResponse> GetAsync(WebPageRequest request, CancellationToken ct);
}

using System.Net.Http;

namespace BotAgent.Platforms.Net;

/// <summary>
/// 平台网络出网端口抽象。
/// </summary>
public interface IPlatformHttpFetcher
{
    TimeSpan Timeout { get; }
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default);
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct = default);
    Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default);
    Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completionOption, CancellationToken ct = default);
    Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default);
    Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption, CancellationToken ct = default);
    Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default);
}

public interface IHttpFetcher : IPlatformHttpFetcher
{
}

using System.Net.Http;

namespace BotAgent.Adapters.Net;

public interface IHttpFetcher
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

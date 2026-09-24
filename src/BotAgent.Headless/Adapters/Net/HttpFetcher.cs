using System.Net.Http.Headers;

namespace BotAgent.Adapters.Net;

/// <summary>
/// 出网端口（§6.3 的 <c>IHttpFetcher</c>）。
///
/// **为什么放在 <c>Adapters/Net/</c> 而不是 <c>Domain/Ports/</c>**（对原方案的一处偏离，理由写在这里）：
/// 它的签名要用 <c>HttpRequestMessage</c> / <c>HttpResponseMessage</c> 这些**框架类型**，
/// 而硬规则 R1 明确禁止 <c>Domain/**</c> 出现 <c>System.Net.Http</c>（连 <c>using</c> 都不许）。
/// 把端口放在适配器这一侧：R1 保住，同时拿到这个端口真正值钱的两点 ——
///   ① **socket 只有一个构造点**（就是本文件；服务里再出现 <c>new HttpClient</c> 会被探针抓）；
///   ② 出网可替身（要假 HTTP 时换一个 <see cref="IHttpFetcher" /> 实现即可，不必真开 socket）。
///
/// **为什么不做成"URL 进 / 字节出"的窄口**：这里的调用方要用**请求头**（鉴权、Accept、Content-Type）、
/// **多段上传**（语音克隆是 multipart）与**流式读**（音频/图片边下边判大小）。
/// 窄口要么丢掉这些，要么自己重写一个 HttpClient —— 后者更糟。所以端口刻意**镜像 HttpClient 的
/// 被用到的那一小撮 API**（见下面 5 个方法），换掉实现时调用点一行都不用改。
/// </summary>
public interface IHttpFetcher
{
    /// <summary>这个客户端的超时（面板/日志要能如实显示）。</summary>
    TimeSpan Timeout { get; }

    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default);

    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct = default);

    Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default);

    Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completionOption, CancellationToken ct = default);

    Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default);

    Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption, CancellationToken ct = default);

    Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default);
}

/// <summary>
/// 出网端口的唯一实现：**一个 <see cref="HttpClient" /> 一个实例**（超时在构造时定），
/// 全仓的 <c>new HttpClient</c> 只允许出现在这里（探针硬规则；面板/部署/协议端那几处是具名例外）。
///
/// 超时口径**沿用原来的数字**（语音 30s / 音乐与链接 45s / 搜索 60s / 官方 30s / 登录二维码 8s /
/// 图片下载 8s / 辅助模型调用 60s / 聊天模型 = <c>QQCHAT_MODEL_TIMEOUT_SECONDS</c>）；
/// 这里只是把"谁来 new"从各个服务里搬到装配点，数字一个没改。
/// </summary>
public sealed class HttpFetcher : IHttpFetcher, IDisposable
{
    private readonly HttpClient _client;

    public HttpFetcher(TimeSpan timeout, Action<string>? log = null, string? name = null)
    {
        Timeout = timeout;
        _client = new HttpClient { Timeout = timeout };

        // 默认请求头与"这个客户端是干什么的"绑在一起（原来分散在各服务里）；
        // 需要按请求改头的调用方仍然自己塞 HttpRequestMessage（方法签名与 HttpClient 一致）。
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("BotAgent/0.1");
        Name = name ?? "http";
        _log = log;
    }

    private readonly Action<string>? _log;

    /// <summary>给日志用的名字（"voice" / "media" / "search" …），出错时能看出是哪条链路。</summary>
    public string Name { get; }

    public TimeSpan Timeout { get; }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct = default)
    {
        try
        {
            return await _client.SendAsync(request, completionOption, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 出网失败在**这里**记一次（带链路名）：以前每个服务的 catch 各写一遍，找问题时要翻好几处
            _log?.Invoke($"[Net/{Name}] {request.Method} {SafeUrl(request.RequestUri)} 失败：{ex.GetType().Name} {ex.Message}");
            throw;
        }
    }

    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
        => _client.GetAsync(url, ct);

    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completionOption, CancellationToken ct = default)
        => _client.GetAsync(url, completionOption, ct);

    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default)
        => _client.GetAsync(url, ct);

    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption, CancellationToken ct = default)
        => _client.GetAsync(url, completionOption, ct);

    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        => _client.PostAsync(url, content, ct);

    /// <summary>日志里只印 host + path（查询串里可能有 token / cookie）。</summary>
    private static string SafeUrl(Uri? uri)
        => uri is null ? "(null)" : $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

    public void Dispose() => _client.Dispose();
}

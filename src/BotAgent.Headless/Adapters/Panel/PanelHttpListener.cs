using System.Net;

namespace BotAgent.Adapters.Panel;

/// <summary>Owns one HTTP listener without constructing a business service or an authentication policy.</summary>
internal sealed class PanelHttpListener(
    int port, string bindAddress, Func<HttpListenerContext, Task> dispatch, Action<string> log) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private HttpListener? _listener;
    private int _disposed;

    internal string? ListeningOn { get; private set; }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listener is not null) return;
        var candidates = bindAddress == "+" && OperatingSystem.IsWindows()
            ? new[] { "+", "127.0.0.1" }
            : new[] { bindAddress };
        foreach (var bind in candidates)
        {
            var prefix = $"http://{bind}:{port}/";
            var candidate = new HttpListener();
            try
            {
                candidate.Prefixes.Add(prefix);
                candidate.Start();
                _listener = candidate;
                ListeningOn = prefix;
                _ = AcceptAsync(candidate, _shutdown.Token);
                log($"面板已监听 {prefix}（/ 、/healthz、/api/*）");
                return;
            }
            catch (Exception exception)
            {
                candidate.Close();
                log($"绑定 {prefix} 失败：{exception.Message}");
            }
        }
        log($"面板无法启动（端口 {port}）。可设 QQCHAT_HEALTH_BIND=127.0.0.1，或 QQCHAT_HEALTH_PORT=0 关闭。");
    }

    private async Task AcceptAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (HttpListenerException) when (ct.IsCancellationRequested || !listener.IsListening) { break; }
            catch (HttpListenerException) { continue; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => DispatchAsync(context));
        }
    }

    private async Task DispatchAsync(HttpListenerContext context)
    {
        try { await dispatch(context).ConfigureAwait(false); }
        catch (Exception exception)
        {
            log($"HTTP 请求处理失败（{exception.GetType().Name}）");
            try { context.Response.StatusCode = 500; context.Response.Close(); }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _listener?.Close();
        _shutdown.Dispose();
    }
}

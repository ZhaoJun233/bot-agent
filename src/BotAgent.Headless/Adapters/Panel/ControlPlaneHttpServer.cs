using System.Net;
using System.Text;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Panel;
using BotAgent.Services;

namespace BotAgent.Adapters.Panel;

/// <summary>The listener and ZBA authentication exist before a business request handler can be attached.</summary>
internal sealed class ControlPlaneHttpServer : IDisposable
{
    private readonly ZbaEndpoint _endpoint;
    private readonly PanelHttpListener _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private Func<HttpListenerContext, Task>? _business;
    private int _disposed;

    internal ControlPlaneHttpServer(int port, ZbaEndpoint endpoint)
    {
        _endpoint = endpoint;
        var password = new PanelPasswordStore(Path.Combine(AppPaths.DataDir, "panel-password"));
        var bind = !password.IsConfigured ? "127.0.0.1"
            : EnvironmentAliases.Get("BOTAGENT_HEALTH_BIND", "QQCHAT_HEALTH_BIND")?.Trim() is { Length: > 0 } custom ? custom : "+";
        _transport = new(port, bind, DispatchAsync, message => FileLog.Write("Web", message));
    }

    internal string? ListeningOn => _transport.ListeningOn;
    internal void Start() => _transport.Start();

    internal void AttachBusiness(WebUiServer web)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        web.StartRoutes();
        if (Interlocked.CompareExchange(ref _business, web.DispatchAsync, null) is not null)
            throw new InvalidOperationException("business_http_already_attached");
    }

    internal void DetachBusiness() => Interlocked.Exchange(ref _business, null);

    private async Task DispatchAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (path.StartsWith("/zba/", StringComparison.OrdinalIgnoreCase))
        {
            var maxBodyBytes = _endpoint.MaxBodyBytesFor(context.Request.HttpMethod, path);
            try { await ZbaHttpListenerAdapter.HandleAsync(context, _endpoint, maxBodyBytes, _shutdown.Token); }
            finally { context.Response.Close(); }
            return;
        }
        var business = Volatile.Read(ref _business);
        if (business is not null) { await business(context); return; }
        var health = path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "GET";
        var body = health ? "{\"status\":\"degraded\",\"ready\":false,\"managementReady\":true}"
            : "{\"error\":\"business_unavailable\"}";
        context.Response.StatusCode = health ? 200 : 503;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        try { await context.Response.OutputStream.WriteAsync(bytes, _shutdown.Token); }
        finally { context.Response.Close(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DetachBusiness();
        _shutdown.Cancel();
        _transport.Dispose();
        _shutdown.Dispose();
    }
}

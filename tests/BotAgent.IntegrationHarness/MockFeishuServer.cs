using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// 假飞书开放平台（Feishu Bot REST API）：提供 tenant access token 与出站消息接收。
/// </summary>
public sealed class MockFeishuServer : IDisposable
{
    private readonly int _port;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public MockFeishuServer(int port)
    {
        _port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    public string? BotOpenId { get; set; }

    public IReadOnlyList<string> Messages => _messages.ToArray();

    public void ClearSent() => _messages.Clear();

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public async Task<bool> WaitForMessageAsync(int expectedCount, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (_messages.Count >= expectedCount)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return _messages.Count >= expectedCount;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(context));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            if (path.Contains("/auth/v3/tenant_access_token/internal", StringComparison.OrdinalIgnoreCase))
            {
                var tokenResp = "{\"code\":0,\"tenant_access_token\":\"t-synthetic-feishu-token\",\"expire\":7200}";
                await WriteAsync(context, 200, tokenResp);
                return;
            }

            if (path.Contains("/bot/v3/info", StringComparison.OrdinalIgnoreCase))
            {
                var botResp = string.IsNullOrEmpty(BotOpenId)
                    ? "{\"code\":0}"
                    : $"{{\"code\":0,\"bot\":{{\"open_id\":\"{BotOpenId}\"}}}}";
                await WriteAsync(context, 200, botResp);
                return;
            }

            if (path.Contains("/im/v1/messages", StringComparison.OrdinalIgnoreCase))
            {
                _messages.Enqueue(body);
                var sendResp = "{\"code\":0,\"data\":{\"message_id\":\"om_synthetic_feishu_sent\"}}";
                await WriteAsync(context, 200, sendResp);
                return;
            }

            await WriteAsync(context, 200, "{\"code\":0}");
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, int code, string json)
    {
        context.Response.StatusCode = code;
        context.Response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }
    }
}

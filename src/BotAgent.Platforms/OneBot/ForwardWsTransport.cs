using System.Net.WebSockets;
using System.Text;

namespace BotAgent.Services.OneBot;

/// <summary>正向 WebSocket：本程序作 WS 客户端连接协议端（推荐，NapCat/Lagrange/LLOneBot 默认支持）。</summary>
public sealed class ForwardWsTransport : IOneBotTransport
{
    private readonly Uri _uri;
    private readonly string? _token;
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _socket;
    private Task? _loopTask;
    private bool _stopped;
    private int _stopRequested;
    private int _disposed;

    public event Action<string>? OnText;
    public event Action<bool>? OnStateChanged;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public ForwardWsTransport(string address, string? token)
    {
        _uri = new Uri(address);
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public Task StartAsync()
    {
        if (_stopped) throw new InvalidOperationException("transport_stopped");
        if (_loopTask is null || _loopTask.IsCompleted)
        {
            _loopTask = Task.Run(() => RunWithReconnectAsync(_cts.Token));
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
        _stopped = true;
        _cts.Cancel();
        try
        {
            _socket?.Abort();
            _socket?.Dispose();
        }
        catch
        {
            // 忽略
        }

        OnStateChanged?.Invoke(false);
    }

    public void Dispose() => Stop();

    public async Task StopAsync(CancellationToken ct = default)
    {
        Stop();
        if (_loopTask is { } loop)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _cts.Dispose();
    }

    public async Task SendActionAsync(string action, string paramsJson, string echo, CancellationToken ct = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("WebSocket 未连接");
        if (socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket 未连接");
        }

        var json = "{\"action\":" + Json(action) + ",\"params\":" + (paramsJson.Length == 0 ? "{}" : paramsJson) +
                   ",\"echo\":" + Json(echo) + "}";
        var bytes = Encoding.UTF8.GetBytes(json);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, linked.Token);
    }

    private async Task RunWithReconnectAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested && !_stopped)
        {
            OnStateChanged?.Invoke(false);
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("User-Agent", "BotAgent/0.2.0");
            if (_token is not null)
            {
                socket.Options.SetRequestHeader("Authorization", "Bearer " + _token);
            }

            try
            {
                await socket.ConnectAsync(_uri, ct);
                _socket = socket;
                OnStateChanged?.Invoke(true);
                delay = TimeSpan.FromSeconds(1);
                await ReceiveLoopAsync(socket, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 连接失败/断开 -> 退避后重连
            }
            finally
            {
                _socket = null;
                OnStateChanged?.Invoke(false);
            }

            try
            {
                await Clock.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var message = new WsMessageBuffer();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.Reset();
            WebSocketMessageType? messageType = null;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                messageType ??= result.MessageType;
                if (result.MessageType != messageType)
                    throw new InvalidDataException("websocket_message_type_changed");
                message.Append(buffer, result.Count);
            }
            while (!result.EndOfMessage);

            // Binary messages are bounded and discarded; never publish them as OneBot text.
            if (messageType == WebSocketMessageType.Text) OnText?.Invoke(message.DecodeText());
        }
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}

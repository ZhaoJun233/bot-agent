using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// 假「QQ 官方机器人开放平台」（Bot API v2）：HTTP 接口 + 一个 WebSocket 网关。
///
/// 为什么需要它：官方通道的凭据是号主自己在开放平台申请的，测试里不可能有；
/// 而这条链路上最危险的恰恰是协议细节（token 头是 <c>QQBot</c> 不是 <c>Bearer</c>、
/// identify 的 intents、被动回复的 msg_id/msg_seq、语音要走富媒体 file_type=3 + msg_type=7）。
/// 用假网关把这些细节**钉死**，真上线时只剩“凭据 + IP 白名单”两件事要人工确认。
///
/// 端口由场景传入（<see cref="Program.FreePort"/>），两个监听器：
///   • HTTP：<c>/app/getAppAccessToken</c>、<c>/gateway/bot</c>、
///     <c>/v2/{groups|users}/{openid}/messages</c>、<c>/v2/{groups|users}/{openid}/files</c>
///   • WS：说 op10 hello，收 op2 identify 回 READY，收 op6 resume 回 RESUMED，收 op1 心跳回 op11
/// </summary>
public sealed class MockOfficialGateway : IDisposable
{
    private readonly int _httpPort;
    private readonly int _wsPort;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<JsonObject> _httpRequests = new();
    private readonly ConcurrentQueue<JsonObject> _messages = new();
    private readonly ConcurrentQueue<JsonObject> _uploads = new();
    private readonly ConcurrentQueue<JsonObject> _identifies = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private WebSocket? _socket;
    private int _tokenRequests;
    private int _identifyCount;
    private int _resumeCount;
    private int _replySeq;

    public MockOfficialGateway(int httpPort, int wsPort)
    {
        _httpPort = httpPort;
        _wsPort = wsPort;
    }

    /// <summary>机器人侧要配的 REST 根地址（<c>QQCHAT_OFFICIAL_API_BASE</c>）。</summary>
    public string HttpBase => $"http://127.0.0.1:{_httpPort}";

    /// <summary>取 token 的地址（<c>QQCHAT_OFFICIAL_TOKEN_URL</c>）——真实平台是与 REST 分开的域名。</summary>
    public string TokenUrl => $"{HttpBase}/app/getAppAccessToken";

    /// <summary>网关地址：<c>GET /gateway/bot</c> 会把这个回给机器人（机器人不该硬编码 wss）。</summary>
    public string WsUrl => $"ws://127.0.0.1:{_wsPort}/websocket/";

    public string AccessToken { get; set; } = "test-token-not-real";

    /// <summary>取 token 了几次（验证“过期前刷新”用）。</summary>
    public int TokenRequests => _tokenRequests;

    /// <summary>identify 了几次（断线重连后应当 ≥2）。</summary>
    public int IdentifyCount => _identifyCount;

    /// <summary>resume 了几次（带着 session_id 恢复）。</summary>
    public int ResumeCount => _resumeCount;

    /// <summary>收到的所有 HTTP 请求（path + body + Authorization 头）。</summary>
    public List<JsonObject> HttpRequests => _httpRequests.ToList();

    /// <summary>收到的发消息请求（只含 <c>/messages</c> 那两条路）。</summary>
    public List<JsonObject> Messages => _messages.ToList();

    /// <summary>收到的富媒体上传请求（<c>/files</c>）。</summary>
    public List<JsonObject> Uploads => _uploads.ToList();

    /// <summary>收到的 identify/resume 报文（验证 token 头写法与 intents 用）。</summary>
    public List<JsonObject> Identifies => _identifies.ToList();

    /// <summary>让接下来 N 次发消息失败（验证“被动失败退回主动消息”这类降级）。</summary>
    public int FailNextMessages { get; set; }

    /// <summary>socket 是否还连着（场景用它判断“真的重连了”）。</summary>
    public bool IsSocketOpen => _socket?.State == WebSocketState.Open;

    public void Start()
    {
        // 两个前缀都要监听：HTTP 接口在 _httpPort，WebSocket 网关在 _wsPort
        // （真实平台也是两个地址：REST 与 wss 不同域，所以 /gateway/bot 才要专门问一次）
        _listener.Prefixes.Add($"http://127.0.0.1:{_httpPort}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{_wsPort}/");
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>推一条群 @ 机器人 的消息（<c>GROUP_AT_MESSAGE_CREATE</c>）。</summary>
    public Task PushGroupMessageAsync(string groupOpenId, string memberOpenId, string text, string msgId)
        => PushEventAsync("GROUP_AT_MESSAGE_CREATE", new JsonObject
        {
            ["id"] = msgId,
            ["group_openid"] = groupOpenId,
            ["author"] = new JsonObject { ["member_openid"] = memberOpenId },
            ["content"] = text,
            ["timestamp"] = DateTimeOffset.Now.ToString("o"),
        });

    /// <summary>推一条单聊消息（<c>C2C_MESSAGE_CREATE</c>）。</summary>
    public Task PushC2CMessageAsync(string userOpenId, string text, string msgId)
        => PushEventAsync("C2C_MESSAGE_CREATE", new JsonObject
        {
            ["id"] = msgId,
            ["author"] = new JsonObject { ["user_openid"] = userOpenId },
            ["content"] = text,
            ["timestamp"] = DateTimeOffset.Now.ToString("o"),
        });

    /// <summary>推一个任意事件（凑协议细节时用）。</summary>
    public async Task PushEventAsync(string type, JsonObject data)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("假官方网关还没有连上（机器人没 identify 成功？）");
        }

        var payload = new JsonObject
        {
            ["op"] = 0,
            ["s"] = 1,
            ["t"] = type,
            ["d"] = data,
        };

        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>把 socket 掐掉（模拟官方网关掉线 / op7），验证机器人会重连并重新 identify。</summary>
    public async Task CloseSocketAsync()
    {
        var socket = _socket;
        if (socket is not null)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "mock-close", _cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 已经断了
            }
        }
    }

    public async Task<bool> WaitForIdentifyAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (_identifyCount >= count)
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return _identifyCount >= count;
    }

    public async Task<bool> WaitForMessageAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (_messages.Count >= count)
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return _messages.Count >= count;
    }

    public async Task<bool> WaitForUploadAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (_uploads.Count >= count)
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return _uploads.Count >= count;
    }

    // ═══════════════ HTTP ═══════════════

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (context.Request.IsWebSocketRequest)
            {
                _ = Task.Run(() => HandleSocketAsync(context, ct), ct);
            }
            else
            {
                _ = Task.Run(() => HandleHttpAsync(context), ct);
            }
        }
    }

    private async Task HandleHttpAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;
        var body = string.Empty;

        if (method == "POST")
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        JsonObject? parsed = null;
        try
        {
            parsed = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject;
        }
        catch (Exception)
        {
            // 记原始文本就够了
        }

        _httpRequests.Enqueue(new JsonObject
        {
            ["method"] = method,
            ["path"] = path,
            ["auth"] = context.Request.Headers["Authorization"] ?? string.Empty,
            ["appid"] = context.Request.Headers["X-Union-Appid"] ?? string.Empty,
            ["body"] = parsed?.DeepClone(),
            ["raw"] = body,
        });

        if (path.EndsWith("/app/getAppAccessToken", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _tokenRequests);
            var appId = parsed?["appId"]?.GetValue<string>() ?? string.Empty;
            var secret = parsed?["clientSecret"]?.GetValue<string>() ?? string.Empty;
            if (appId.Length == 0 || secret.Length == 0)
            {
                await WriteAsync(context, 400, new JsonObject { ["code"] = 100007, ["message"] = "appId/clientSecret 缺失" }).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, 200, new JsonObject
            {
                ["access_token"] = AccessToken,
                ["expires_in"] = 7200,
            }).ConfigureAwait(false);
            return;
        }

        if (path.EndsWith("/gateway/bot", StringComparison.Ordinal))
        {
            await WriteAsync(context, 200, new JsonObject
            {
                ["url"] = WsUrl,
                ["shards"] = 1,
                ["session_start_limit"] = new JsonObject { ["total"] = 1000, ["remaining"] = 999, ["max_concurrency"] = 1 },
            }).ConfigureAwait(false);
            return;
        }

        if (path.Contains("/messages", StringComparison.Ordinal))
        {
            _messages.Enqueue(new JsonObject
            {
                ["path"] = path,
                ["body"] = parsed?.DeepClone(),
                ["auth"] = context.Request.Headers["Authorization"] ?? string.Empty,
            });

            if (FailNextMessages > 0)
            {
                FailNextMessages--;
                await WriteAsync(context, 400, new JsonObject { ["code"] = 304016, ["message"] = "SEND_ERROR（假网关让它失败）" }).ConfigureAwait(false);
                return;
            }

            var id = "official_reply_" + Interlocked.Increment(ref _replySeq);
            await WriteAsync(context, 200, new JsonObject
            {
                ["id"] = id,
                ["timestamp"] = DateTimeOffset.Now.ToString("o"),
            }).ConfigureAwait(false);
            return;
        }

        if (path.Contains("/files", StringComparison.Ordinal))
        {
            _uploads.Enqueue(new JsonObject
            {
                ["path"] = path,
                ["file_type"] = parsed?["file_type"]?.DeepClone(),
                ["srv_send_msg"] = parsed?["srv_send_msg"]?.DeepClone(),
                ["file_data_len"] = parsed?["file_data"]?.GetValue<string>()?.Length ?? 0,
                ["file_data_head"] = parsed?["file_data"]?.GetValue<string>() is { Length: > 16 } head ? head[..16] : string.Empty,
                ["url"] = parsed?["url"]?.DeepClone(),
            });

            await WriteAsync(context, 200, new JsonObject
            {
                ["file_uuid"] = "uuid_" + (_uploads.Count),
                ["file_info"] = "file_info_" + (_uploads.Count),
                ["ttl"] = 0,
            }).ConfigureAwait(false);
            return;
        }

        await WriteAsync(context, 404, new JsonObject { ["code"] = 404, ["message"] = "假官方网关不认识这个路径" }).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, JsonObject payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    // ═══════════════ WebSocket 网关 ═══════════════

    private async Task HandleSocketAsync(HttpListenerContext context, CancellationToken ct)
    {
        var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var socket = wsContext.WebSocket;
        _socket = socket;

        await SendAsync(socket, new JsonObject { ["op"] = 10, ["d"] = new JsonObject { ["heartbeat_interval"] = 45000 } }).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var frame = await ReceiveAsync(socket, buffer, ct).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            var op = frame["op"]?.GetValue<int>() ?? -1;
            switch (op)
            {
                case 2: // identify
                    Interlocked.Increment(ref _identifyCount);
                    _identifies.Enqueue(frame.DeepClone() as JsonObject ?? new JsonObject());
                    await SendAsync(socket, new JsonObject
                    {
                        ["op"] = 0,
                        ["s"] = 1,
                        ["t"] = "READY",
                        ["d"] = new JsonObject
                        {
                            ["version"] = 1,
                            ["session_id"] = "mock_session_" + _identifyCount,
                            ["user"] = new JsonObject { ["id"] = "100000001", ["username"] = "mock-bot", ["bot"] = true },
                            ["shard"] = new JsonArray(0, 1),
                        },
                    }).ConfigureAwait(false);
                    break;

                case 6: // resume
                    Interlocked.Increment(ref _resumeCount);
                    _identifies.Enqueue(frame.DeepClone() as JsonObject ?? new JsonObject());
                    await SendAsync(socket, new JsonObject
                    {
                        ["op"] = 0,
                        ["s"] = 2,
                        ["t"] = "RESUMED",
                        ["d"] = new JsonObject { ["session_id"] = "mock_session_resumed" },
                    }).ConfigureAwait(false);
                    break;

                case 1: // 心跳
                    await SendAsync(socket, new JsonObject { ["op"] = 11, ["d"] = null }).ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }
    }

    private static async Task SendAsync(WebSocket socket, JsonObject payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<JsonObject?> ReceiveAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray())) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (Exception)
        {
            // 收尾失败无所谓
        }

        try
        {
            _socket?.Dispose();
        }
        catch (Exception)
        {
            // 同上
        }

        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
        _sendGate.Dispose();
        _cts.Dispose();
    }
}

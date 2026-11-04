using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.McpProbe;

/// <summary>
/// 进程内回环的 Streamable HTTP MCP 服务器（协议版本可配置，默认 2025-11-25）。
///
/// 只实现本探针需要的 JSON-RPC 子集：initialize / notifications/initialized / tools/list /
/// tools/call，外加 DELETE（204）与 GET（405，证明客户端不需要独立的 GET 流）。
/// 鉴权固定为 <c>Authorization: Bearer synthetic-token</c>；缺少/不匹配一律 401。
/// 所有 count 与 header 记录都是给断言用的观测值，不含任何真实数据。
/// </summary>
public sealed class SyntheticMcpServer : IAsyncDisposable
{
    public const string Token = "synthetic-token";

    public const string EchoSchema =
        """{"type":"object","properties":{"message":{"type":"string","maxLength":100}},"required":["message"],"additionalProperties":false}""";

    /// <summary>合成图片块的数据（不是任何真实图片）。</summary>
    public const string ImageData = "c3ludGhldGljLWltYWdl";

    private readonly HttpListener _listener;
    private readonly Task _acceptLoop;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, int> _requestCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Task> _handlers = new();
    private readonly TaskCompletionSource _initObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _listObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _callObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string _lastAuthorization = string.Empty;
    private string? _lastProtocolHeader;
    private int _sessionDeleteCount;
    private long _toolCallCount;
    private int _expireFirstCall;
    private int _getRequestCount;
    private int _postRequestCount;
    private int _disposed;
    private int _handlerId;

    public SyntheticMcpServer()
    {
        HttpListener? listener = null;
        var port = 0;
        for (var attempt = 0; attempt < 12 && listener is null; attempt++)
        {
            port = GetFreeLoopbackPort();
            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                candidate.Start();
                listener = candidate;
            }
            catch (HttpListenerException)
            {
                // Windows 偶尔会把临时选中的端口划进保留区间（拒绝访问）；换一个端口重试。
                try
                {
                    candidate.Close();
                }
                catch
                {
                    // 忽略关闭失败。
                }

                listener = null;
            }
        }

        if (listener is null)
        {
            throw new InvalidOperationException("could not bind a loopback HttpListener port");
        }

        Endpoint = $"http://127.0.0.1:{port}/mcp";
        _listener = listener;
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>完整端点（/mcp）。</summary>
    public string Endpoint { get; }

    /// <summary>initialize 响应里返回的协议版本（改成别的值可测协商不匹配）。</summary>
    public string ProtocolVersion { get; set; } = "2025-11-25";

    /// <summary>tools/list 返回的工具定义。</summary>
    public IReadOnlyList<SyntheticTool> Tools { get; set; } = DefaultTools();

    /// <summary>下一次 tools/call 返回 404（模拟会话/路由失效），只生效一次。</summary>
    public bool ExpireFirstCall
    {
        get => Volatile.Read(ref _expireFirstCall) != 0;
        set => Volatile.Write(ref _expireFirstCall, value ? 1 : 0);
    }

    /// <summary>模拟只支持旧版 HTTP+SSE 的服务器：所有 POST 都返回 405。</summary>
    public bool RejectPostWith405 { get; set; }

    /// <summary>首次收到 initialize 时完成；断言“请求已到达服务器”的确定性时点。</summary>
    public Task InitObserved => _initObserved.Task;

    /// <summary>首次收到 tools/list 时完成。</summary>
    public Task ListObserved => _listObserved.Task;

    /// <summary>首次收到 tools/call 时完成。</summary>
    public Task CallObserved => _callObserved.Task;

    /// <summary>设置后 initialize 响应会等待该门释放（用于释放/初始化竞态测试）。</summary>
    public TaskCompletionSource? InitGate { get; set; }

    /// <summary>设置后 tools/list 响应会等待该门释放。</summary>
    public TaskCompletionSource? ListGate { get; set; }

    /// <summary>设置后 tools/call 响应会等待该门释放。</summary>
    public TaskCompletionSource? CallGate { get; set; }

    public string LastAuthorization => Volatile.Read(ref _lastAuthorization);

    public string? LastProtocolHeader => Volatile.Read(ref _lastProtocolHeader);

    public int SessionDeleteCount => Volatile.Read(ref _sessionDeleteCount);

    public long ToolCallCount => Interlocked.Read(ref _toolCallCount);

    /// <summary>收到的 GET 请求数（用来证明没有静默回退到 SSE）。</summary>
    public int GetRequestCount => Volatile.Read(ref _getRequestCount);

    /// <summary>收到的 POST 请求数（含被 405 拒绝的 initialize）。</summary>
    public int PostRequestCount => Volatile.Read(ref _postRequestCount);

    public int RequestCount(string method)
        => _requestCounts.TryGetValue(method, out var count) ? count : 0;

    private async Task AcceptLoopAsync()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            var task = HandleAsync(context);
            var id = Interlocked.Increment(ref _handlerId);
            _handlers[id] = task;
            _ = task.ContinueWith(completedTask =>
            {
                _handlers.TryRemove(id, out _);
                return completedTask.IsCompleted;
            }, TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            Volatile.Write(ref _lastAuthorization, request.Headers["Authorization"] ?? string.Empty);
            var protocolHeader = request.Headers["MCP-Protocol-Version"]
                                 ?? request.Headers["Mcp-Protocol-Version"];
            if (!string.IsNullOrEmpty(protocolHeader))
            {
                Volatile.Write(ref _lastProtocolHeader, protocolHeader);
            }

            if (!string.Equals(request.Headers["Authorization"], "Bearer " + Token, StringComparison.Ordinal))
            {
                WriteStatus(response, 401);
                return;
            }

            if (request.HttpMethod == "GET")
            {
                Interlocked.Increment(ref _getRequestCount);
                WriteStatus(response, 405);
                return;
            }

            if (request.HttpMethod == "DELETE")
            {
                Interlocked.Increment(ref _sessionDeleteCount);
                WriteStatus(response, 204);
                return;
            }

            if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/mcp")
            {
                WriteStatus(response, 404);
                return;
            }

            Interlocked.Increment(ref _postRequestCount);
            if (RejectPostWith405)
            {
                WriteStatus(response, 405);
                return;
            }

            var body = await ReadBodyAsync(request).ConfigureAwait(false);
            if (JsonNode.Parse(body) is not JsonObject message)
            {
                WriteStatus(response, 400);
                return;
            }

            var method = message["method"]?.GetValue<string>() ?? string.Empty;
            _requestCounts.AddOrUpdate(method, 1, (_, count) => count + 1);

            var sessionHeader = request.Headers["Mcp-Session-Id"];
            if (method != "initialize" && string.IsNullOrEmpty(sessionHeader))
            {
                WriteStatus(response, 400);
                return;
            }

            switch (method)
            {
                case "initialize":
                {
                    _initObserved.TrySetResult();
                    if (InitGate is { } initGate)
                    {
                        await initGate.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }

                    var sessionId = Guid.NewGuid().ToString("N");
                    var result = new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject(),
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "synthetic-mcp",
                            ["version"] = "1.0.0",
                        },
                    };
                    WriteJson(response, Response(message, result), sessionId);
                    return;
                }

                case "notifications/initialized":
                    WriteStatus(response, 202);
                    return;

                case "tools/list":
                {
                    _listObserved.TrySetResult();
                    if (ListGate is { } listGate)
                    {
                        await listGate.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }

                    var tools = new JsonArray();
                    foreach (var tool in Tools)
                    {
                        tools.Add(new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["inputSchema"] = JsonNode.Parse(tool.InputSchemaJson),
                            ["annotations"] = new JsonObject { ["readOnlyHint"] = tool.ReadOnlyHint },
                        });
                    }

                    WriteJson(response, Response(message, new JsonObject { ["tools"] = tools }));
                    return;
                }

                case "tools/call":
                {
                    Interlocked.Increment(ref _toolCallCount);
                    _callObserved.TrySetResult();
                    if (CallGate is { } callGate)
                    {
                        await callGate.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }

                    if (Interlocked.Exchange(ref _expireFirstCall, 0) == 1)
                    {
                        WriteStatus(response, 404);
                        return;
                    }

                    var parameters = message["params"] as JsonObject;
                    var name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
                    var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

                    switch (name)
                    {
                        case "echo":
                        {
                            var text = "echo:" + (arguments["message"]?.GetValue<string>() ?? string.Empty);
                            WriteTool(response, message, text, isError: false);
                            return;
                        }

                        case "slow":
                            await Task.Delay(TimeSpan.FromSeconds(2), _shutdown.Token).ConfigureAwait(false);
                            WriteTool(response, message, "slow:done", isError: false);
                            return;

                        case "huge":
                            WriteTool(response, message, new string('h', 4000), isError: false);
                            return;

                        case "image":
                            WriteJson(response, Response(message, new JsonObject
                            {
                                ["content"] = new JsonArray(new JsonObject
                                {
                                    ["type"] = "image",
                                    ["data"] = ImageData,
                                    ["mimeType"] = "image/png",
                                }),
                                ["isError"] = false,
                            }));
                            return;

                        case "fail":
                            WriteTool(response, message, "synthetic failure", isError: true);
                            return;

                        case "unauthorized":
                            WriteTool(response, message, "unauthorized", isError: false);
                            return;

                        case "malformed":
                            WriteRaw(response, "{this is not valid json");
                            return;

                        default:
                            WriteJson(response, Error(message, -32602, "unknown tool"));
                            return;
                    }
                }

                default:
                    WriteJson(response, Error(message, -32601, "method not found"));
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // 停机/客户端断开：不写响应，等 Dispose 收口。
        }
        catch (Exception)
        {
            try
            {
                WriteStatus(response, 500);
            }
            catch
            {
                // 客户端已经走了。
            }
        }
        finally
        {
            try
            {
                response.OutputStream.Close();
            }
            catch
            {
                // 忽略已断开的连接。
            }

            try
            {
                response.Close();
            }
            catch
            {
                // 忽略已断开的连接。
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // 停机不阻塞测试。
        }

        var pending = _handlers.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // 在途 handler 已被取消/断开，不再等待。
            }
        }

        _shutdown.Dispose();
    }

    private static JsonObject Response(JsonObject request, JsonNode result)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]?.DeepClone(),
            ["result"] = result,
        };

    private static JsonObject Error(JsonObject request, int code, string message)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

    private static void WriteTool(HttpListenerResponse response, JsonObject request, string text, bool isError)
        => WriteJson(response, Response(request, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            }),
            ["isError"] = isError,
        }));

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static void WriteJson(HttpListenerResponse response, JsonNode payload, string? sessionId = null)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        if (sessionId is not null)
        {
            response.Headers["Mcp-Session-Id"] = sessionId;
        }

        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteRaw(HttpListenerResponse response, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteStatus(HttpListenerResponse response, int status)
    {
        response.StatusCode = status;
        response.ContentLength64 = 0;
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static IReadOnlyList<SyntheticTool> DefaultTools() => new[]
    {
        new SyntheticTool("echo", "Echo the message back.", EchoSchema, ReadOnlyHint: true),
        new SyntheticTool("slow", new string('d', 1000), EchoSchema, ReadOnlyHint: true),
        new SyntheticTool("huge", "Return 4000 synthetic characters.", EchoSchema, ReadOnlyHint: false),
        new SyntheticTool("image", "Return one synthetic image block.", EchoSchema, ReadOnlyHint: false),
        new SyntheticTool("fail", "Return a synthetic tool error.", EchoSchema, ReadOnlyHint: false),
        new SyntheticTool("unauthorized", "Discovered by the server but not authorized by config.", EchoSchema, ReadOnlyHint: false),
        new SyntheticTool("malformed", "Return invalid JSON.", EchoSchema, ReadOnlyHint: false),
    };
}

/// <summary>tools/list 里的一条合成工具定义。</summary>
public sealed record SyntheticTool(string Name, string Description, string InputSchemaJson, bool ReadOnlyHint);

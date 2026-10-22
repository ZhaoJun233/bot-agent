using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S53 用的最小 Streamable HTTP MCP 2025-11-25 服务器（进程内回环）。
///
/// 只有两个工具：<c>echo</c>（配置里允许）与 <c>unauthorized</c>（服务器会列出，但配置不授权）。
/// 无鉴权要求；仍记录 <see cref="LastAuthorization" /> 便于断言。initialize 之后必须带
/// <c>Mcp-Session-Id</c>。所有值都是合成的，不接任何真实 MCP 服务器。
/// </summary>
public sealed class MockMcpServer : IDisposable
{
    private const string ProtocolVersion = "2025-11-25";

    private const string EchoSchema =
        """{"type":"object","properties":{"message":{"type":"string","maxLength":100}},"required":["message"],"additionalProperties":false}""";

    private readonly HttpListener _listener;
    private readonly Task _acceptLoop;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, Task> _handlers = new();
    private readonly object _sessionGate = new();

    private string? _sessionId;
    private string _lastProtocolVersion = string.Empty;
    private string _lastAuthorization = string.Empty;
    private string? _lastToolName;
    private int _toolCallCount;
    private int _disposed;
    private int _handlerId;

    /// <summary>启动监听（优先使用 preferredPort；被 Windows 保留区间拒绝时换临时端口重试）。</summary>
    public MockMcpServer(int preferredPort)
    {
        HttpListener? listener = null;
        var port = preferredPort;
        for (var attempt = 0; attempt < 12 && listener is null; attempt++)
        {
            if (attempt > 0)
            {
                port = FreeLoopbackPort();
            }

            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                candidate.Start();
                listener = candidate;
            }
            catch (HttpListenerException)
            {
                try
                {
                    candidate.Close();
                }
                catch
                {
                    // 忽略关闭失败。
                }
            }
        }

        if (listener is null)
        {
            throw new InvalidOperationException("could not bind a loopback HttpListener port for MockMcpServer");
        }

        Port = port;
        Endpoint = $"http://127.0.0.1:{port}/mcp";
        _listener = listener;
        _acceptLoop = AcceptLoopAsync();
        Console.WriteLine($"      [mock] MCP {Endpoint}");
    }

    public int Port { get; }

    /// <summary>配置里填的完整地址（/mcp）。</summary>
    public string Endpoint { get; }

    public int ToolCallCount => Volatile.Read(ref _toolCallCount);

    /// <summary>initialize 请求里的 protocolVersion（缺失时用服务器响应版本）。</summary>
    public string LastProtocolVersion => Volatile.Read(ref _lastProtocolVersion);

    /// <summary>最近一次请求的 Authorization 头（本场景无鉴权，通常为空）。</summary>
    public string LastAuthorization => Volatile.Read(ref _lastAuthorization);

    public string? LastToolName => Volatile.Read(ref _lastToolName);

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

            if (request.HttpMethod == "GET")
            {
                WriteStatus(response, 405);
                return;
            }

            if (request.HttpMethod == "DELETE")
            {
                WriteStatus(response, 204);
                return;
            }

            if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/mcp")
            {
                WriteStatus(response, 404);
                return;
            }

            var body = await ReadBodyAsync(request).ConfigureAwait(false);
            if (JsonNode.Parse(body) is not JsonObject message)
            {
                WriteStatus(response, 400);
                return;
            }

            var method = message["method"]?.GetValue<string>() ?? string.Empty;

            if (method == "initialize")
            {
                var requested = message["params"]?["protocolVersion"]?.GetValue<string>();
                Volatile.Write(ref _lastProtocolVersion, string.IsNullOrWhiteSpace(requested)
                    ? ProtocolVersion
                    : requested);

                var sessionId = Guid.NewGuid().ToString("N");
                lock (_sessionGate)
                {
                    _sessionId = sessionId;
                }

                WriteJson(response, Response(message, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "synthetic-mcp",
                        ["version"] = "1.0.0",
                    },
                }), sessionId);
                return;
            }

            var sessionHeader = request.Headers["Mcp-Session-Id"];
            string? currentSession;
            lock (_sessionGate)
            {
                currentSession = _sessionId;
            }

            if (string.IsNullOrEmpty(sessionHeader) ||
                !string.Equals(sessionHeader, currentSession, StringComparison.Ordinal))
            {
                WriteStatus(response, 400);
                return;
            }

            switch (method)
            {
                case "notifications/initialized":
                    WriteStatus(response, 202);
                    return;

                case "tools/list":
                {
                    var tools = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "echo",
                            ["description"] = "Echo a synthetic message.",
                            ["inputSchema"] = JsonNode.Parse(EchoSchema),
                        },
                        new JsonObject
                        {
                            ["name"] = "unauthorized",
                            ["description"] = "Listed but not authorized by server config.",
                            ["inputSchema"] = JsonNode.Parse(EchoSchema),
                        },
                    };
                    WriteJson(response, Response(message, new JsonObject { ["tools"] = tools }));
                    return;
                }

                case "tools/call":
                {
                    Interlocked.Increment(ref _toolCallCount);
                    var parameters = message["params"] as JsonObject;
                    var name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
                    var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
                    Volatile.Write(ref _lastToolName, name);

                    switch (name)
                    {
                        case "echo":
                        {
                            var text = "echo:" + (arguments["message"]?.GetValue<string>() ?? string.Empty);
                            WriteTool(response, message, text);
                            return;
                        }

                        case "unauthorized":
                            WriteTool(response, message, "unauthorized:called");
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
            // 停机/断开：不写响应。
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

    public void Dispose()
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
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
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
                Task.WaitAll(pending, TimeSpan.FromSeconds(5));
            }
            catch
            {
                // 在途 handler 已被取消/断开。
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

    private static void WriteTool(HttpListenerResponse response, JsonObject request, string text)
        => WriteJson(response, Response(request, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            }),
            ["isError"] = false,
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

    private static void WriteStatus(HttpListenerResponse response, int status)
    {
        response.StatusCode = status;
        response.ContentLength64 = 0;
    }

    private static int FreeLoopbackPort()
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
}

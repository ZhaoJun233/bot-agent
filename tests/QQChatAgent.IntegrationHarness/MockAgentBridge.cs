using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// 测试用的“本机桥”：像 pi-bridge.py 那样连到机器人的 /agent-bridge，接收任务、回传结果。
/// 有了它才能把 §31 的边界（令牌 / 白名单 / 任务细节 / 取消 / 长输出）在测试里钉住。
/// </summary>
internal sealed class MockAgentBridge : IDisposable
{
    private readonly string _url;
    private readonly JsonObject? _hello;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MockAgentBridge(string url, JsonObject? hello = null)
    {
        _url = url;
        _hello = hello;
    }

    /// <summary>收到的任务（JSON）。</summary>
    public List<JsonObject> Tasks { get; } = new();

    /// <summary>收到的取消请求里的任务 id。</summary>
    public List<string> Cancels { get; } = new();

    /// <summary>被问过几次 pi 会话列表（面板/`//pi` 用）。</summary>
    public int PiSessionsAsked { get; private set; }

    /// <summary>收到的“删会话”请求（forget）里的 pi 会话 id。</summary>
    public List<string> Forgotten { get; } = new();

    /// <summary>是否连接成功（令牌不对时会失败）。</summary>
    public bool Connected { get; private set; }

    public async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri(_url), ct);
        }
        catch (Exception)
        {
            socket.Dispose();
            return false;   // 握手被拒（401/403）时 ConnectAsync 抛异常
        }

        _socket = socket;
        Connected = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        if (_hello is not null)
        {
            // 必须带 type（真实桥 pi-bridge.py 也是这么发的）：服务端靠它分派
            var hello = new JsonObject { ["type"] = "hello" };
            foreach (var (key, value) in _hello)
            {
                hello[key] = value?.DeepClone();
            }

            Send(hello);
        }

        return true;
    }

    public void Send(JsonObject payload)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open })
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        // 测试里同步发（量很小）；失败就当桥断了
        socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var pending = new MemoryStream();
        try
        {
            while (_socket is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                pending.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(pending.ToArray());
                pending.SetLength(0);
                var node = JsonNode.Parse(json);
                switch (node?["type"]?.GetValue<string>())
                {
                    case "task":
                        Tasks.Add(node!.AsObject());
                        break;
                    case "cancel":
                        Cancels.Add(node?["id"]?.GetValue<string>() ?? string.Empty);
                        break;
                    case "sessions":
                        PiSessionsAsked++;
                        Send(new JsonObject
                        {
                            ["type"] = "sessions",
                            ["list"] = new JsonArray(
                                new JsonObject { ["id"] = "pi-sess-aaa", ["file"] = "pi-sess-aaa.jsonl", ["title"] = "外部会话甲的标题", ["cwd"] = "E:/bot", ["size"] = 123, ["mtime"] = 1789600000 },
                                new JsonObject { ["id"] = "pi-sess-bbb", ["file"] = "pi-sess-bbb.jsonl", ["title"] = "外部会话乙的标题", ["cwd"] = "E:/bot", ["size"] = 456, ["mtime"] = 1789500000 },
                                new JsonObject { ["id"] = "pi-sess-ccc", ["file"] = "pi-sess-ccc.jsonl", ["title"] = "", ["cwd"] = "E:/work", ["size"] = 789, ["mtime"] = 1789400000 })
                        });
                        break;
                    case "forget":
                        Forgotten.Add(node?["session"]?.GetValue<string>() ?? string.Empty);
                        Send(new JsonObject { ["type"] = "forgot", ["session"] = node?["session"]?.GetValue<string>(), ["removed"] = 1 });
                        break;
                }
            }
        }
        catch (Exception)
        {
            // 断开就是断开，测试里不关心原因
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _socket?.Dispose();
        }
        catch
        {
        }

        _cts?.Dispose();
    }
}

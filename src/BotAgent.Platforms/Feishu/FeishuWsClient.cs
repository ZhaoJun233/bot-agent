using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BotAgent.Adapters.Platforms.Feishu;

public interface IFeishuWsSession : IDisposable
{
    bool IsOpen { get; }
    Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
    Task<(WebSocketMessageType MessageType, byte[] Data, bool Closed)> ReceiveMessageAsync(CancellationToken ct);
    Task CloseGracefullyAsync(CancellationToken ct);
}

internal sealed class ClientWebSocketFeishuSession(ClientWebSocket socket) : IFeishuWsSession
{
    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => await socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);

    public async Task<(WebSocketMessageType MessageType, byte[] Data, bool Closed)> ReceiveMessageAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        while (true)
        {
            var res = await socket.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (res.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, [], true);
            if (ms.Length + res.Count > FeishuWsFrameCodec.MaxFrameBytes)
                throw new InvalidOperationException("feishu_ws_frame_too_large");
            ms.Write(buf, 0, res.Count);
            if (res.EndOfMessage)
                return (res.MessageType, ms.ToArray(), false);
        }
    }

    public async Task CloseGracefullyAsync(CancellationToken ct)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            closeCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                try { socket.Abort(); } catch { }
            }
        }
    }

    public void Dispose() => socket.Dispose();
}

public sealed partial class FeishuBotGateway
{
    private CancellationTokenSource? _wsLoopCts;
    private Task? _wsLoopTask;
    private volatile bool _wsConnected;
    private volatile bool _wsLoopActive;
    private int _reconcileRequested;
    private int _reconcileActive;
    private readonly SemaphoreSlim _wsLifecycleGate = new(1, 1);

    public bool IsLongConnectionMode
    {
        get
        {
            var values = _readPluginSettings?.Invoke();
            if (values is not null && values.TryGetValue("transportMode", out var el) && el.ValueKind == JsonValueKind.String)
                return string.Equals(el.GetString()?.Trim(), "long_connection", StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    public bool EnableInteractiveCards
    {
        get
        {
            var values = _readPluginSettings?.Invoke();
            if (values is not null && values.TryGetValue("enableInteractiveCards", out var el))
            {
                if (el.ValueKind == JsonValueKind.False) return false;
                if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
            }
            return true;
        }
    }

    public void ReconcileTransportMode()
    {
        if (_disposed) return;
        Volatile.Write(ref _reconcileRequested, 1);
        if (Interlocked.CompareExchange(ref _reconcileActive, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _reconcileRequested, 0) != 0)
                {
                    if (_disposed) break;
                    await _wsLifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (_disposed || !_operations.IsActive) break;
                        if (IsLongConnectionMode)
                            StartWsIfConfigured();
                        else
                            await StopWsAsync().ConfigureAwait(false);
                        ConnectionChanged?.Invoke(IsConnected);
                    }
                    finally
                    {
                        _wsLifecycleGate.Release();
                    }
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _reconcileActive, 0);
                if (Volatile.Read(ref _reconcileRequested) != 0 && !_disposed)
                    ReconcileTransportMode();
            }
        }, CancellationToken.None);
    }

    private void StartWsIfConfigured()
    {
        if (!IsLongConnectionMode || (_wsLoopTask is { IsCompleted: false } && _wsLoopActive))
            return;

        _wsLoopCts?.Cancel();
        _wsLoopCts?.Dispose();
        _wsLoopCts = new CancellationTokenSource();
        var token = _wsLoopCts.Token;
        _wsLoopActive = true;
        _wsLoopTask = Task.Run(() => RunLongConnectionLoopAsync(token), CancellationToken.None);
    }

    private async Task StopWsAsync()
    {
        _wsLoopActive = false;
        var cts = Interlocked.Exchange(ref _wsLoopCts, null);
        var task = Interlocked.Exchange(ref _wsLoopTask, null);
        if (cts is not null)
        {
            cts.Cancel();
            if (task is not null)
            {
                try { await task.ConfigureAwait(false); }
                catch { }
            }
            cts.Dispose();
        }
        SetWsConnected(false);
    }

    private void SetWsConnected(bool connected)
    {
        if (_wsConnected == connected) return;
        _wsConnected = connected;
        ConnectionChanged?.Invoke(IsConnected);
    }

    private async Task RunLongConnectionLoopAsync(CancellationToken ct)
    {
        var backoffSeconds = 3;
        while (!ct.IsCancellationRequested && !_disposed && IsLongConnectionMode)
        {
            try
            {
                var settings = Snapshot(_box.Current);
                if (string.IsNullOrWhiteSpace(settings.AppId) || string.IsNullOrWhiteSpace(settings.AppSecret))
                {
                    LastErrorCode = "feishu_not_configured";
                    await Clock.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    continue;
                }

                var endpoint = await FetchWsEndpointAsync(settings, ct).ConfigureAwait(false);
                if (endpoint is null)
                {
                    await Clock.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
                    backoffSeconds = Math.Min(60, backoffSeconds * 2);
                    continue;
                }

                backoffSeconds = Math.Max(3, endpoint.Value.ReconnectSeconds);
                await RunSingleWsSessionAsync(endpoint.Value.Uri, endpoint.Value.ServiceId, endpoint.Value.PingSeconds, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastErrorCode = "ws_connection_failed";
                _log?.Invoke($"[飞书] 长连接异常断开（type={ex.GetType().Name}），{backoffSeconds}s 后重连");
                SetWsConnected(false);
                try { await Clock.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoffSeconds = Math.Min(60, backoffSeconds * 2);
            }
        }
        _wsLoopActive = false;
        SetWsConnected(false);
    }

    private async Task<(Uri Uri, int ServiceId, int PingSeconds, int ReconnectSeconds)?> FetchWsEndpointAsync(
        FeishuSettingsSnapshot settings,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBase}/callback/ws/endpoint")
        {
            Content = new StringContent(
                new JsonObject { ["AppID"] = settings.AppId, ["AppSecret"] = settings.AppSecret }.ToJsonString(),
                Encoding.UTF8,
                "application/json")
        };
        req.Headers.TryAddWithoutValidation("locale", "zh");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            LastErrorCode = $"ws_endpoint_http_{(int)resp.StatusCode}";
            return null;
        }

        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
        var code = root?["code"]?.GetValue<int>() ?? -1;
        var data = root?["data"] as JsonObject;
        var rawUrl = data?["URL"]?.GetValue<string>() ?? data?["url"]?.GetValue<string>();
        if (code != 0 || string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var wsUri)
            || wsUri.Scheme is not ("ws" or "wss"))
        {
            LastErrorCode = $"ws_endpoint_err_{code}";
            return null;
        }

        var cfg = data?["ClientConfig"] as JsonObject;
        var pingSec = Math.Clamp(cfg?["PingInterval"]?.GetValue<int>() ?? 120, 5, 300);
        var reconnSec = Math.Clamp(cfg?["ReconnectInterval"]?.GetValue<int>() ?? 10, 2, 120);
        var serviceId = ParseQueryInt(wsUri.Query, "service_id");
        return (wsUri, serviceId, pingSec, reconnSec);
    }

    private async Task RunSingleWsSessionAsync(Uri wsUri, int serviceId, int initialPingSeconds, CancellationToken ct)
    {
        using var session = _wsConnect is not null
            ? await _wsConnect(wsUri, ct).ConfigureAwait(false)
            : await ConnectDefaultWebSocketAsync(wsUri, ct).ConfigureAwait(false);

        using var sendLock = new SemaphoreSlim(1, 1);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingSeconds = initialPingSeconds;
        var reassembler = new FeishuWsFragmentReassembler();
        LastErrorCode = null;
        SetWsConnected(true);
        _ = Task.Run(async () =>
        {
            try
            {
                using var warmCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                warmCts.CancelAfter(TimeSpan.FromSeconds(5));
                await ResolveBotOpenIdAsync(Snapshot(_box.Current), warmCts.Token).ConfigureAwait(false);
            }
            catch { }
        }, CancellationToken.None);
        _log?.Invoke($"[飞书] pbbp2 长连接已建立（service_id={serviceId}，ping={pingSeconds}s）");

        var pingTask = Task.Run(async () =>
        {
            try
            {
                while (!sessionCts.Token.IsCancellationRequested && session.IsOpen)
                {
                    await Clock.Delay(TimeSpan.FromSeconds(Math.Clamp(Volatile.Read(ref pingSeconds), 5, 300)), sessionCts.Token).ConfigureAwait(false);
                    var pingFrame = new FeishuWsFrame(
                        0, 0, serviceId, FeishuWsFrameCodec.MethodControl,
                        [new FeishuWsHeader("type", "ping")],
                        null, null, []);
                    await SendFrameLockedAsync(session, sendLock, pingFrame, sessionCts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                try { sessionCts.Cancel(); } catch { }
            }
        }, CancellationToken.None);

        try
        {
            while (!sessionCts.Token.IsCancellationRequested && session.IsOpen)
            {
                var (msgType, rawBytes, closed) = await session.ReceiveMessageAsync(sessionCts.Token).ConfigureAwait(false);
                if (closed) break;
                if (msgType != WebSocketMessageType.Binary || !FeishuWsFrameCodec.TryDecode(rawBytes, out var frame) || frame is null)
                    continue;

                if (frame.Method == FeishuWsFrameCodec.MethodControl)
                {
                    if (string.Equals(frame.GetHeader("type"), "pong", StringComparison.OrdinalIgnoreCase) && frame.Payload.Length > 0)
                    {
                        try
                        {
                            var pongCfg = JsonNode.Parse(Encoding.UTF8.GetString(frame.Payload)) as JsonObject;
                            if (pongCfg?["PingInterval"]?.GetValue<int>() is { } nextPing && nextPing >= 5)
                                Volatile.Write(ref pingSeconds, nextPing);
                        }
                        catch { }
                    }
                    continue;
                }

                if (frame.Method == FeishuWsFrameCodec.MethodData)
                {
                    if (!reassembler.TryCombine(frame, out var fullPayload) || fullPayload is null)
                        continue;
                    await HandleWsDataFrameAsync(session, sendLock, frame, fullPayload, sessionCts.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            sessionCts.Cancel();
            SetWsConnected(false);
            try { await pingTask.ConfigureAwait(false); } catch { }
            await session.CloseGracefullyAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleWsDataFrameAsync(
        IFeishuWsSession session,
        SemaphoreSlim sendLock,
        FeishuWsFrame frame,
        byte[] payloadBytes,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ackCode = 200;
        var frameType = frame.GetHeader("type") ?? "event";
        string? cardResponseBody = null;
        if ((string.Equals(frameType, "event", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "card", StringComparison.OrdinalIgnoreCase)) && payloadBytes.Length > 0)
        {
            try
            {
                var eventNode = JsonNode.Parse(Encoding.UTF8.GetString(payloadBytes)) as JsonObject;
                if (eventNode is not null)
                {
                    if (string.Equals(frameType, "card", StringComparison.OrdinalIgnoreCase)
                        && eventNode["header"] is null)
                    {
                        var wsMessageId = frame.GetHeader("message_id")?.Trim();
                        TryGetOptionalString(eventNode, "token", out var wsCardToken);
                        var resolvedEventId = !string.IsNullOrWhiteSpace(wsMessageId)
                            ? wsMessageId
                            : !string.IsNullOrWhiteSpace(wsCardToken) ? wsCardToken!.Trim() : null;
                        var syntheticHeader = new JsonObject
                        {
                            ["event_type"] = "card.action.trigger"
                        };
                        if (!string.IsNullOrWhiteSpace(resolvedEventId))
                            syntheticHeader["event_id"] = resolvedEventId;
                        eventNode = new JsonObject
                        {
                            ["header"] = syntheticHeader,
                            ["event"] = eventNode.DeepClone()
                        };
                    }
                    var (handled, statusCode, respBody) = await DispatchVerifiedEventAsync(
                        eventNode, _box.Current, nonce: null, signed: false, ct).ConfigureAwait(false);
                    if (statusCode >= 500)
                        ackCode = 500;
                    else if (handled && string.Equals(frameType, "card", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(respBody))
                        cardResponseBody = respBody;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (JsonException)
            {
                ackCode = 200;
            }
            catch
            {
                ackCode = 500;
            }
        }

        sw.Stop();
        var headers = new List<FeishuWsHeader>(frame.Headers.Count + 1);
        foreach (var h in frame.Headers)
        {
            if (!string.Equals(h.Key, "biz_rt", StringComparison.OrdinalIgnoreCase))
                headers.Add(h);
        }
        headers.Add(new FeishuWsHeader("biz_rt", Math.Max(1L, sw.ElapsedMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var ackPayloadObj = new JsonObject { ["code"] = ackCode };
        if (ackCode == 200 && !string.IsNullOrWhiteSpace(cardResponseBody))
            ackPayloadObj["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(cardResponseBody));
        var ackBody = Encoding.UTF8.GetBytes(ackPayloadObj.ToJsonString());
        var ackFrame = frame with { Headers = headers, Payload = ackBody };
        await SendFrameLockedAsync(session, sendLock, ackFrame, ct).ConfigureAwait(false);
    }

    private static async Task SendFrameLockedAsync(
        IFeishuWsSession session,
        SemaphoreSlim sendLock,
        FeishuWsFrame frame,
        CancellationToken ct)
    {
        var encoded = FeishuWsFrameCodec.Encode(frame);
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.IsOpen)
                await session.SendBinaryAsync(encoded, ct).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<IFeishuWsSession> ConnectDefaultWebSocketAsync(Uri uri, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            return new ClientWebSocketFeishuSession(ws);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private static int ParseQueryInt(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var trimmed = query.TrimStart('?');
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && string.Equals(part[..eq], key, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(Uri.UnescapeDataString(part[(eq + 1)..]), out var val))
            {
                return val;
            }
        }
        return 0;
    }
}

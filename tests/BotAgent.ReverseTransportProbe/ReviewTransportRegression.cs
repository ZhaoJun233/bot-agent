using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BotAgent.Services.OneBot;

// Parent wiring: await ReviewTransportRegression.RunAsync(Check); source-link the transports/helpers.
// Every payload is synthetic; no application startup, configuration, logs or storage are used.
internal static partial class ReviewTransportRegression
{
    private const int MessageLimit = 4 * 1024 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public static async Task RunAsync(Action<string, bool> check)
    {
        await CheckHttpAsync(check);
        await CheckForwardAsync(check);
        await CheckReverseAsync(check);
    }

    private static async Task CheckHttpAsync(Action<string, bool> check)
    {
        foreach (var body in new[]
        {
            "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"message_id\":10001}}",
            "{\"status\":\"failed\",\"retcode\":1,\"data\":null,\"echo\":\"wrong-synthetic-echo\"}"
        })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var deadline = new CancellationTokenSource(Budget);
            var serve = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                var stream = peer.GetStream();
                var header = new List<byte>();
                var one = new byte[1];
                while (true)
                {
                    await stream.ReadExactlyAsync(one, deadline.Token);
                    header.Add(one[0]);
                    if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                }
                var text = Encoding.ASCII.GetString(header.ToArray());
                var lengthLine = text.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                check("B05 HTTP still sends bearer authentication and action path", text.StartsWith("POST /get_status HTTP/1.1\r\n") && text.Contains("Authorization: Bearer synthetic-token\r\n"));
                int count = int.Parse(lengthLine.Split(':')[1].Trim());
                await stream.ReadExactlyAsync(new byte[count], deadline.Token);
                var bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
                await stream.WriteAsync(bytes, deadline.Token);
            });
            using var transport = new HttpTransport($"http://127.0.0.1:{port}", "synthetic-token");
            var received = new List<string>();
            transport.OnText += received.Add;
            await transport.SendActionAsync("get_status", "{}", "synthetic-echo", deadline.Token);
            await serve;
            check("B05 HTTP action emits exactly one completion callback", received.Count == 1);
            if (received.Count == 1)
            {
                using var original = JsonDocument.Parse(body);
                using var actual = JsonDocument.Parse(received[0]);
                check("B05 HTTP completion uses caller echo, including conflicting remote echo", actual.RootElement.GetProperty("echo").GetString() == "synthetic-echo");
                check("B05 HTTP success/failure result data survives completion",
                    actual.RootElement.GetProperty("data").GetRawText() == original.RootElement.GetProperty("data").GetRawText() &&
                    actual.RootElement.GetProperty("retcode").GetInt32() == original.RootElement.GetProperty("retcode").GetInt32());
            }
            await transport.StopAsync();
        }
    }

    private static async Task CheckForwardAsync(Action<string, bool> check)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"synthetic\":\"群友A😀\"}");
        var fragments = bytes.Select((b, index) => (new[] { b }, WebSocketMessageType.Text, index == bytes.Length - 1)).ToArray();
        using var transport = new ForwardWsTransport("ws://localhost:1/synthetic", null);
        var received = new List<string>();
        transport.OnText += received.Add;
        await ReceiveForwardAsync(transport, new FragmentSocket(fragments));
        check("B19 forward decodes UTF8 once across byte-sized receive fragments", received.SequenceEqual(new[] { Encoding.UTF8.GetString(bytes) }));

        received.Clear();
        await ReceiveForwardAsync(transport, new FragmentSocket(new[] { (new byte[] { 1, 2 }, WebSocketMessageType.Binary, true) }));
        check("B07 forward binary messages never become text callbacks", received.Count == 0);

        var chunk = Enumerable.Repeat((byte)'x', 16384).ToArray();
        await ReceiveForwardAsync(transport, new FragmentSocket(Enumerable.Range(0, MessageLimit / chunk.Length)
            .Select(i => (chunk, WebSocketMessageType.Text, i == MessageLimit / chunk.Length - 1)).ToArray()));
        check("B07 forward accepts exactly 4 MiB and resets completed messages", received.Count == 1 && received[0].Length == MessageLimit);
        received.Clear();

        var invalidUtf8 = new FragmentSocket(new[] { (new byte[] { 0xF0, 0x9F }, WebSocketMessageType.Text, true) });
        check("B19 forward rejects incomplete UTF8 without publishing replacement characters", await RejectForwardAsync(transport, invalidUtf8) && received.Count == 0);

        var oversized = new FragmentSocket(Enumerable.Range(0, MessageLimit / chunk.Length + 1)
            .Select(i => (chunk, WebSocketMessageType.Text, i == MessageLimit / chunk.Length)).ToArray());
        check("B07 forward rejects aggregate message beyond 4 MiB before callback", await RejectForwardAsync(transport, oversized) && received.Count == 0);
        await transport.StopAsync();
    }

    private static async Task ReceiveForwardAsync(ForwardWsTransport transport, WebSocket socket)
    {
        using (socket)
        using (var deadline = new CancellationTokenSource(Budget))
        {
            var method = typeof(ForwardWsTransport).GetMethod("ReceiveLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(transport, new object[] { socket, deadline.Token })!;
        }
    }

    private static async Task<bool> RejectForwardAsync(ForwardWsTransport transport, WebSocket socket)
    {
        try { await ReceiveForwardAsync(transport, socket); return false; }
        catch (InvalidDataException) { return true; }
        catch (DecoderFallbackException) { return true; }
    }

    private static async Task CheckReverseAsync(Action<string, bool> check)
    {
        using var transport = new ReverseWsTransport("http://localhost:0/synthetic", "synthetic-token");
        await transport.StartAsync();
        var listener = (TcpListener)typeof(ReverseWsTransport).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        check("B06 actual localhost listener binds loopback only", IPAddress.IsLoopback(endpoint.Address));
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        transport.OnText += received.Enqueue;
        using (var peer = await ConnectReverseAsync(endpoint.Port))
        {
            var bytes = Encoding.UTF8.GetBytes("{\"synthetic\":\"群友A😀\"}");
            int split = Array.IndexOf(bytes, (byte)0xF0) + 2;
            await peer.GetStream().WriteAsync(Frame(1, false, bytes[..split]));
            var ping = Enumerable.Range(0, 125).Select(i => (byte)i).ToArray();
            await peer.GetStream().WriteAsync(Frame(9, true, ping));
            using var deadline = new CancellationTokenSource(Budget);
            var pong = new byte[127];
            await peer.GetStream().ReadExactlyAsync(pong, deadline.Token);
            check("B27 no text callback until FIN and bounded ping can interleave", received.IsEmpty && pong[0] == 0x8A && pong[1] == 125 && pong[2..].SequenceEqual(ping));
            await peer.GetStream().WriteAsync(Frame(0, true, bytes[split..]));
            // Ping/pong orders the receive loop after the completed message without timing sleeps.
            await peer.GetStream().WriteAsync(Frame(9, true, Array.Empty<byte>()));
            await peer.GetStream().ReadExactlyAsync(new byte[2], deadline.Token);
            check("B27 reverse publishes exactly one complete UTF8 message", received.ToArray().SequenceEqual(new[] { Encoding.UTF8.GetString(bytes) }));
        }
        received.Clear();
        using (var peer = await ConnectReverseAsync(endpoint.Port))
        {
            using var deadline = new CancellationTokenSource(Budget);
            await peer.GetStream().WriteAsync(Frame(2, false, new byte[] { 1, 2 }), deadline.Token);
            await peer.GetStream().WriteAsync(Frame(0, true, new byte[] { 3, 4 }), deadline.Token);
            await peer.GetStream().WriteAsync(Frame(9, true, Array.Empty<byte>()), deadline.Token);
            await peer.GetStream().ReadExactlyAsync(new byte[2], deadline.Token);
            check("B27 fragmented reverse binary is discarded without text callback", received.IsEmpty);
            await peer.GetStream().WriteAsync(Frame(1, true, Enumerable.Repeat((byte)'x', MessageLimit).ToArray()), deadline.Token);
            await peer.GetStream().WriteAsync(Frame(1, true, Array.Empty<byte>()), deadline.Token);
            await peer.GetStream().WriteAsync(Frame(9, true, Array.Empty<byte>()), deadline.Token);
            await peer.GetStream().ReadExactlyAsync(new byte[2], deadline.Token);
            var texts = received.ToArray();
            check("B07/B27 reverse accepts exactly 4 MiB and resets before next message", texts.Length == 2 && texts[0].Length == MessageLimit && texts[1] == string.Empty);
        }
        received.Clear();

        var badFrames = new (string Name, byte[] Header)[]
        {
            ("over-limit 64-bit frame", AdvertisedFrame(1, true, MessageLimit + 1UL)),
            ("64-bit high bit", AdvertisedFrame(1, true, 1UL << 63)),
            ("oversized ping", AdvertisedFrame(9, true, 126)),
            ("fragmented ping", Frame(9, false, Array.Empty<byte>())),
            ("orphan continuation", Frame(0, true, new byte[] { 1 })),
            ("unmasked client frame", new byte[] { 0x81, 0x00 }),
            ("reserved bits", new byte[] { 0xC1, 0x80, 1, 2, 3, 4 }),
            ("reserved opcode", Frame(3, true, Array.Empty<byte>())),
            ("nonminimal length", new byte[] { 0x81, 0xFE, 0, 1 }),
            ("invalid text UTF8", Frame(1, true, new byte[] { 0xF0, 0x9F })),
            ("one-byte close", Frame(8, true, new byte[] { 1 }))
        };
        foreach (var (name, header) in badFrames)
        {
            using var peer = await ConnectReverseAsync(endpoint.Port);
            await peer.GetStream().WriteAsync(header);
            check($"B07/B27 rejects {name} without callback or waiting for advertised body", await ClosedAsync(peer) && received.IsEmpty);
            received.Clear();
        }

        using (var peer = await ConnectReverseAsync(endpoint.Port))
        {
            await peer.GetStream().WriteAsync(Frame(1, false, new byte[MessageLimit / 2]));
            await peer.GetStream().WriteAsync(AdvertisedFrame(0, true, MessageLimit / 2 + 1UL));
            check("B07 reverse rejects aggregate overflow before second payload read", await ClosedAsync(peer) && received.IsEmpty);
        }
        using (var peer = await ConnectReverseAsync(endpoint.Port))
        {
            await peer.GetStream().WriteAsync(Frame(1, false, new byte[] { (byte)'x' }));
            await peer.GetStream().WriteAsync(Frame(1, true, new byte[] { (byte)'y' }));
            check("B27 rejects new text frame during continuation sequence", await ClosedAsync(peer) && received.IsEmpty);
        }
        await transport.StopAsync();
    }

    private static async Task<TcpClient> ConnectReverseAsync(int port)
    {
        var peer = new TcpClient();
        try
        {
            using var deadline = new CancellationTokenSource(Budget);
            await peer.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /synthetic HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nAuthorization: Bearer synthetic-token\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: c3ludGhldGljLWtleS0xMg==\r\n\r\n"), deadline.Token);
            var response = new List<byte>();
            var one = new byte[1];
            while (response.Count < 4096)
            {
                await peer.GetStream().ReadExactlyAsync(one, deadline.Token);
                response.Add(one[0]);
                if (response.Count >= 4 && Encoding.ASCII.GetString(response.TakeLast(4).ToArray()) == "\r\n\r\n") return peer;
            }
            throw new InvalidDataException("synthetic_upgrade_too_large");
        }
        catch { peer.Dispose(); throw; }
    }

    private static byte[] Frame(int opcode, bool final, byte[] payload)
    {
        var header = AdvertisedFrame(opcode, final, (ulong)payload.Length);
        var result = new byte[header.Length + 4 + payload.Length];
        header.CopyTo(result, 0);
        byte[] mask = { 1, 2, 3, 4 };
        mask.CopyTo(result, header.Length);
        for (int i = 0; i < payload.Length; i++) result[header.Length + 4 + i] = (byte)(payload[i] ^ mask[i & 3]);
        return result;
    }

    private static byte[] AdvertisedFrame(int opcode, bool final, ulong length)
    {
        var header = new List<byte> { (byte)(opcode | (final ? 0x80 : 0)) };
        if (length <= 125) header.Add((byte)(0x80 | length));
        else if (length <= ushort.MaxValue)
        {
            header.Add(0xFE);
            header.Add((byte)(length >> 8));
            header.Add((byte)length);
        }
        else
        {
            header.Add(0xFF);
            for (int shift = 56; shift >= 0; shift -= 8) header.Add((byte)(length >> shift));
        }
        return header.ToArray();
    }

    private static async Task<bool> ClosedAsync(TcpClient peer)
    {
        using var deadline = new CancellationTokenSource(Budget);
        try { return await peer.GetStream().ReadAsync(new byte[1], deadline.Token) == 0; }
        catch (IOException) { return true; }
        catch (OperationCanceledException) { return false; }
    }

    private sealed class FragmentSocket : WebSocket
    {
        private readonly Queue<(byte[] Bytes, WebSocketMessageType Type, bool Final)> _fragments;
        public FragmentSocket(IEnumerable<(byte[], WebSocketMessageType, bool)> fragments) => _fragments = new(fragments);
        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool final, CancellationToken ct) => throw new NotSupportedException();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_fragments.Count == 0) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var fragment = _fragments.Dequeue();
            if (fragment.Bytes.Length > buffer.Count) throw new InvalidOperationException("synthetic_fragment_exceeds_receive_buffer");
            fragment.Bytes.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(fragment.Bytes.Length, fragment.Type, fragment.Final));
        }
    }
}

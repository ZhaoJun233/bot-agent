using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using BotAgent.Services.OneBot;

// Only synthetic loopback traffic; source-linking excludes application wiring/data.
int failures = 0;
void Check(string name, bool condition)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    if (!condition) failures++;
}
async Task<bool> Closed(TcpClient client, TimeSpan timeout)
{
    using var deadline = new CancellationTokenSource(timeout);
    try { return await client.GetStream().ReadAsync(new byte[1], deadline.Token) == 0; }
    catch (OperationCanceledException) { return false; }
    catch (IOException) { return true; }
}
using var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
using var transport = new ReverseWsTransport($"http://127.0.0.1:{port}", "synthetic-token");
int connectedEvents = 0;
var replacementPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
transport.OnStateChanged += connected =>
{
    if (connected && Interlocked.Increment(ref connectedEvents) == 2) replacementPublished.TrySetResult();
};
var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
transport.OnText += text => received.TrySetResult(text);
await transport.StartAsync();

using var silent = new TcpClient();
await silent.ConnectAsync(IPAddress.Loopback, port);
using var partial = new TcpClient();
await partial.ConnectAsync(IPAddress.Loopback, port);
await partial.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /synthetic HTTP/1.1\r\nUpgrade: websocket\r\n"));
using var dripCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(11));
int dripWrites = 0;
var drip = Task.Run(async () =>
{
    try
    {
        while (!dripCancellation.IsCancellationRequested)
        {
            await partial.GetStream().WriteAsync(new byte[] { (byte)'X' }, dripCancellation.Token);
            Interlocked.Increment(ref dripWrites);
            await Task.Delay(100, dripCancellation.Token);
        }
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
});
Check("pending handshake is not connected", !transport.IsConnected && connectedEvents == 0);

using var valid = new ClientWebSocket();
valid.Options.SetRequestHeader("Authorization", "Bearer synthetic-token");
try
{
    using var connectBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await valid.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/synthetic"), connectBudget.Token);
    Check("valid later client is not blocked by pending handshakes", valid.State == WebSocketState.Open);
    await valid.SendAsync(Encoding.UTF8.GetBytes("{\"synthetic\":true}"), WebSocketMessageType.Text, true, CancellationToken.None);
    Check("valid client text still reaches OneBot", await received.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "{\"synthetic\":true}");
}
catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or TimeoutException)
{
    Check("valid later client is not blocked by pending handshakes", false);
}

Check("silent handshake has a finite deadline and closes socket", await Closed(silent, TimeSpan.FromSeconds(12)));
Check("partial handshake has a finite deadline and closes socket", await Closed(partial, TimeSpan.FromSeconds(2)));
dripCancellation.Cancel();
await drip;
Check("continuous header trickle cannot extend absolute deadline", dripWrites > 3 && transport.IsConnected);

using var unauthorized = new TcpClient();
await unauthorized.ConnectAsync(IPAddress.Loopback, port);
await unauthorized.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nUpgrade: websocket\r\nAuthorization: Bearer synthetic-invalid\r\nSec-WebSocket-Key: c3ludGhldGljLWtleS0xMg==\r\n\r\n"));
Check("unauthorized upgrade closes without replacing active client", await Closed(unauthorized, TimeSpan.FromSeconds(2)) && transport.IsConnected);

using var oversized = new TcpClient();
await oversized.ConnectAsync(IPAddress.Loopback, port);
await oversized.GetStream().WriteAsync(Encoding.ASCII.GetBytes(new string('X', 16 * 1024 + 1)));
Check("oversized headers close without replacing active client", await Closed(oversized, TimeSpan.FromSeconds(2)) && transport.IsConnected);

using var replacement = new ClientWebSocket();
replacement.Options.SetRequestHeader("Authorization", "Bearer synthetic-token");
using (var connectBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    try
    {
        await replacement.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/synthetic"), connectBudget.Token);
        await replacementPublished.Task.WaitAsync(connectBudget.Token);
    }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
}
Check("authenticated replacement is accepted while old receive waits", replacement.State == WebSocketState.Open && transport.IsConnected);
if (replacementPublished.Task.IsCompletedSuccessfully)
{
    using var actionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await transport.SendActionAsync("get_status", "{}", "synthetic-echo", actionBudget.Token);
    var bytes = new byte[1024];
    var result = await replacement.ReceiveAsync(bytes, actionBudget.Token);
    Check("old cleanup cannot remove replacement action route", Encoding.UTF8.GetString(bytes, 0, result.Count).Contains("synthetic-echo"));
}

// A flood cannot leave unlimited pending sockets alive. Existing authorized client survives.
var stalledClients = new List<TcpClient>();
try
{
    for (int i = 0; i < 16; i++)
    {
        var stalled = new TcpClient();
        stalledClients.Add(stalled);
        await stalled.ConnectAsync(IPAddress.Loopback, port);
        await stalled.GetStream().WriteAsync(new byte[] { (byte)'G' });
    }

    using var overflow = new TcpClient();
    await overflow.ConnectAsync(IPAddress.Loopback, port);
    Check("pending handshake count is bounded and active client survives", await Closed(overflow, TimeSpan.FromSeconds(2)) && transport.IsConnected);
}
finally
{
    foreach (var stalled in stalledClients) stalled.Dispose();
}

using var pendingAtStop = new TcpClient();
await pendingAtStop.ConnectAsync(IPAddress.Loopback, port);
await pendingAtStop.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"));
transport.Stop();
Check("stop closes pending sockets promptly", await Closed(pendingAtStop, TimeSpan.FromSeconds(2)));
Check("stop reports disconnected", !transport.IsConnected);
await ReviewTransportRegression.RunAsync(Check);
Console.WriteLine($"failures={failures}");
return failures == 0 ? 0 : 1;

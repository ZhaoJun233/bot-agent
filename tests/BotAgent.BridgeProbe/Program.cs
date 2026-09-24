using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BotAgent.Services;
using BotAgent.Services.Agent;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (!ok) failures++;
}
async Task Wait(Func<bool> predicate)
{
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    while (!predicate()) await Task.Delay(10, budget.Token);
}
JsonObject Hello(string dir = "C:/synthetic/bridge") => new()
{
    ["type"] = "hello", ["host"] = "WORKER-TEST", ["cwd"] = dir, ["pi"] = "mock"
};
var settings = new AppSettings { EnableAgentBridge = true, AgentWorkDir = "C:/synthetic/global" };
    var bridge = new AgentBridgeServer(new SettingsBox(settings), _ => { });
var finished = new ConcurrentQueue<AgentTask>();
bridge.Finished += finished.Enqueue;
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));

using var first = new TestSocket();
var firstLoop = bridge.HandleAsync(first, budget.Token);
Check("pending handshake is not an online device", !bridge.Connected);
first.Incoming.Writer.TryWrite(Hello());
await Wait(() => bridge.BridgeNames.Contains("WORKER-TEST"));
Check("summary follows global directory", bridge.Describe().Contains("C:/synthetic/global"));
settings.AgentDevices = "[{\"name\":\"WORKER-TEST\",\"workdir\":\"C:/synthetic/device\"}]";
Check("summary follows device override", bridge.Describe().Contains("C:/synthetic/device"));

first.HoldSends = true;
var models = bridge.RequestModelsAsync("WORKER-TEST");
await Wait(() => first.ActiveSends == 1);
var sessions = bridge.RequestPiSessionsAsync("WORKER-TEST");
await Task.Delay(80);
Check("parallel callers serialize websocket sends", first.MaxActiveSends == 1);
first.ReleaseSends.TrySetResult();
await Task.WhenAll(models, sessions).WaitAsync(budget.Token);
first.HoldSends = false;

var task = bridge.NewTask("group:10001", "synthetic task", "synthetic-session", "WORKER-TEST");
bridge.TryEnqueue(task);
await Wait(() => first.Sent.Any(p => p["type"]?.GetValue<string>() == "task"));
Check("task uses the directory shown in summary", first.Sent.Last(p => p["type"]?.GetValue<string>() == "task")["cwd"]?.GetValue<string>() == "C:/synthetic/device");
first.Incoming.Writer.TryWrite(new JsonObject { ["testClose"] = true });
await firstLoop.WaitAsync(budget.Token);
Check("disconnect settles current task immediately", task.Done && !task.Ok && bridge.Current is null);
Check("disconnect finishes a task once", finished.Count(t => ReferenceEquals(t, task)) == 1);

// Old receive stays pending even after Abort, to force cleanup after replacement has started a task.
using var old = new TestSocket { IgnoreReceiveCancellation = true };
var oldLoop = bridge.HandleAsync(old, budget.Token);
old.Incoming.Writer.TryWrite(Hello());
await Wait(() => bridge.Connected);
var oldTask = bridge.NewTask("group:10002", "old synthetic task", "old-session", "WORKER-TEST");
bridge.TryEnqueue(oldTask);
await Wait(() => old.Sent.Any(p => p["type"]?.GetValue<string>() == "task"));
var queued = bridge.NewTask("group:10003", "queued synthetic task", "queued-session", "WORKER-TEST");
bridge.TryEnqueue(queued);
using var replacement = new TestSocket();
var newLoop = bridge.HandleAsync(replacement, budget.Token);
replacement.Incoming.Writer.TryWrite(Hello("C:/synthetic/replacement"));
await Wait(() => replacement.Sent.Any(p => p["id"]?.GetValue<string>() == queued.Id));
Check("same-name replacement settles only old task", oldTask.Done && !queued.Done);
old.Incoming.Writer.TryWrite(new JsonObject { ["type"] = "done", ["id"] = queued.Id, ["text"] = "stale result", ["exitCode"] = 0 });
old.Incoming.Writer.TryWrite(new JsonObject { ["testClose"] = true });
await oldLoop.WaitAsync(budget.Token);
Check("old cleanup cannot remove replacement or its task", bridge.IsDeviceOnline("WORKER-TEST") && ReferenceEquals(bridge.Current, queued) && !queued.Done);
replacement.Incoming.Writer.TryWrite(new JsonObject { ["type"] = "done", ["id"] = queued.Id, ["text"] = "synthetic result", ["exitCode"] = 0 });
await Wait(() => queued.Done);
Check("replacement result is accepted exactly once", queued.Ok && finished.Count(t => ReferenceEquals(t, queued)) == 1);

replacement.FailSend = true;
Check("send failure is reported", !await bridge.RequestModelsAsync("WORKER-TEST"));
await newLoop.WaitAsync(budget.Token);
Check("failed transport is removed from online list", !bridge.Connected);

using var stalled = new TestSocket { HoldSends = true };
var stalledLoop = bridge.HandleAsync(stalled, budget.Token);
stalled.Incoming.Writer.TryWrite(Hello());
await Wait(() => bridge.Connected);
var stalledSend = bridge.RequestModelsAsync("WORKER-TEST");
Check("stalled send has a finite deadline", !await stalledSend.WaitAsync(TimeSpan.FromSeconds(13)));
await stalledLoop.WaitAsync(budget.Token);
Check("stalled send ends its connection", !bridge.Connected);

Console.WriteLine($"failures={failures}");
return failures == 0 ? 0 : 1;

sealed class TestSocket : WebSocket
{
    public Channel<JsonObject> Incoming { get; } = Channel.CreateUnbounded<JsonObject>();
    public ConcurrentQueue<JsonObject> Sent { get; } = new();
    public TaskCompletionSource ReleaseSends { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HoldSends, FailSend, IgnoreReceiveCancellation;
    public int ActiveSends, MaxActiveSends;
    private WebSocketState state = WebSocketState.Open;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public override void Abort() => state = WebSocketState.Aborted;
    public override void Dispose() => state = WebSocketState.Closed;
    public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => CloseOutputAsync(s, d, ct);
    public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct)
    {
        state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        var p = await Incoming.Reader.ReadAsync(IgnoreReceiveCancellation ? CancellationToken.None : ct);
        if (p["testClose"]?.GetValue<bool>() == true)
        {
            state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
        var bytes = Encoding.UTF8.GetBytes(p.ToJsonString());
        Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
        return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
    }
    public override async Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType t, bool end, CancellationToken ct)
    {
        if (FailSend) throw new WebSocketException("synthetic send failure");
        var active = Interlocked.Increment(ref ActiveSends);
        MaxActiveSends = Math.Max(MaxActiveSends, active);
        try
        {
            Sent.Enqueue(JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject());
            if (HoldSends) await ReleaseSends.Task.WaitAsync(ct);
        }
        finally { Interlocked.Decrement(ref ActiveSends); }
    }
}

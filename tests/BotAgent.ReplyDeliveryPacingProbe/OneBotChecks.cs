using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Reply;
using BotAgent.Services.OneBot;

static class OneBotChecks
{
    public static async Task Run(Action<string, bool> check, Func<string, Func<Task>, Task> canceled)
    {
        await CheckActionClassification(check, canceled);
        await CheckWriteLock(check, canceled);
    }

    private static async Task CheckActionClassification(Action<string, bool> check, Func<string, Func<Task>, Task> canceled)
    {
        var assembly = typeof(OneBotGateway).Assembly;
        var clientType = assembly.GetType("BotAgent.Services.OneBot.OneBotActionClient", true)!;
        var lifetimeType = assembly.GetType("BotAgent.Services.Platforms.PlatformOperationLifetime", true)!;
        using var lifetime = (IDisposable)Activator.CreateInstance(lifetimeType, nonPublic: true)!;
        try
        {
        using var transport = new FakeActionTransport();
        var client = Activator.CreateInstance(clientType, new object[]
        {
            (Func<IOneBotTransport?>)(() => transport), lifetime, (Func<bool>)(() => false), new SyntheticSource(),
        })!;
        transport.OnSend = echo => clientType.GetMethod("Complete")!.Invoke(client, new object[] { echo, new JsonObject() });
        Task<JsonNode?> Send(string action) => (Task<JsonNode?>)clientType.GetMethod("SendAsync")!.Invoke(client, new object[] { action, "{}", CancellationToken.None })!;
        var guard = new TestGuard { OnTry = _ => throw new OperationCanceledException("synthetic_late_input") };
        using (ReplyCommitBoundary.Enter(guard))
        {
            foreach (var action in new[] { "get_msg", "get_login_info", "download_file", "upload_group_file" })
                check("preparation RPC does not start chat", await Send(action) is not null);
            check("read and upload RPCs did not use commit guard", guard.Sends == 0 && guard.Tries == 0 && transport.Calls == 4);
            foreach (var action in new[] { "send_group_msg", "send_private_msg", "send_group_forward_msg", "group_poke", "friend_poke" })
                await canceled("visible OneBot action propagates paced rejection", async () => await Send(action));
            check("rejected actions never reach transport", transport.Calls == 4 && guard.Tries == 5 && guard.Sends == 0);
            var pending = (IDictionary)clientType.GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            check("rejected OneBot action clears pending receipt", pending.Count == 0);
        }
        transport.ThrowCancellation = true;
        check("off OneBot cancellation retains legacy null result", await Send("send_group_msg") is null);
        }
        finally
        {
            await (Task)lifetimeType.GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(lifetime, null)!;
        }
    }

    private static async Task CheckWriteLock(Action<string, bool> check, Func<string, Func<Task>, Task> canceled)
    {
        var type = typeof(ReverseWsTransport).GetNestedType("WsConnection", BindingFlags.NonPublic)!;
        using var stream = new MemoryStream();
        var connection = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { stream }, null)!;
        var gate = (SemaphoreSlim)type.GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        Task Send() => (Task)type.GetMethod("SendTextAsync")!.Invoke(connection, new object[] { "synthetic", CancellationToken.None, true })!;
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guard = new TestGuard
        {
            OnPrepare = call =>
            {
                prepared.TrySetResult();
                if (call > 1)
                {
                    check("reverse WS pacing resumes outside write lock", gate.Wait(0));
                    gate.Release();
                }
            },
            OnTry = call => call > 1,
        };
        await gate.WaitAsync();
        using (ReplyCommitBoundary.Enter(guard))
        {
            var sending = Send();
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check("blocked reverse WS has no first emission", stream.Length == 0 && guard.Tries == 0);
            gate.Release();
            await sending.WaitAsync(TimeSpan.FromSeconds(2));
            check("reverse WS rechecks after write lock without duplicate bytes", guard.Prepares == 2 && guard.Tries == 2 && Encoding.UTF8.GetString(stream.ToArray()[2..]) == "synthetic");
        }
        stream.SetLength(0);
        prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guard = new TestGuard
        {
            OnPrepare = _ => prepared.TrySetResult(),
            OnTry = _ => throw new OperationCanceledException("synthetic_late_input"),
        };
        await gate.WaitAsync();
        using (ReplyCommitBoundary.Enter(guard))
        {
            var sending = Send();
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(2));
            gate.Release();
            await canceled("late input at reverse write lock cancels pending bytes", () => sending);
            check("late reverse rejection emits nothing and releases lock", stream.Length == 0 && gate.Wait(0));
            gate.Release();
        }
    }
}

sealed class FakeActionTransport : IOneBotTransport
{
#pragma warning disable CS0067
    public event Action<string>? OnText;
    public event Action<bool>? OnStateChanged;
#pragma warning restore CS0067
    public bool IsConnected => true;
    public int Calls { get; private set; }
    public Action<string>? OnSend { get; set; }
    public bool ThrowCancellation { get; set; }
    public Task StartAsync() => Task.CompletedTask;
    public Task SendActionAsync(string action, string paramsJson, string echo, CancellationToken ct = default)
    {
        Calls++;
        if (ThrowCancellation) throw new OperationCanceledException();
        OnSend?.Invoke(echo);
        return Task.CompletedTask;
    }
    public void Stop() { }
    public void Dispose() { }
}

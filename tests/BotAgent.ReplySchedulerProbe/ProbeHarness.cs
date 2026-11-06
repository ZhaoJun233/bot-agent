using System.Collections.Concurrent;
using System.Threading.Channels;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Reply;
using BotAgent.Engine.Runtime;
using BotAgent.Services;
using BotAgent.Services.Reply;

namespace BotAgent.ReplySchedulerProbe;

internal sealed class ProbeHarness : IAsyncDisposable
{
    private readonly IClock _previousClock = EngineClock.Current;
    private readonly Channel<ScheduledCall> _calls = Channel.CreateUnbounded<ScheduledCall>();
    private int _sends, _effects, _preflights;
    public ManualSchedulerClock Clock { get; } = new();
    public AppSettings Settings => Pipeline.Settings;
    public ReplyPipeline Pipeline { get; }
    public ConcurrentQueue<ScheduledCall> History { get; } = new();
    public int Sends => Volatile.Read(ref _sends);
    public int Effects => Volatile.Read(ref _effects);
    public int Preflights => Volatile.Read(ref _preflights);

    public ProbeHarness()
    {
        EngineClock.Initialize(Clock);
        Pipeline = new ReplyPipeline { Generate = GenerateAsync };
    }

    public static BotConversation Group(string key = "group:10001") => new() { SourceKey = key };
    public static BotConversation Private() => new() { SourceKey = "private:10001", Kind = ConversationKind.PrivateChat };
    public ChatMessage Input(BotConversation conversation, bool direct = true, long speaker = 10001, bool contextOnly = false)
    {
        var message = conversation.Add(speaker, direct);
        if (contextOnly) Pipeline.Continuation(conversation, message); else Pipeline.Ingress(conversation, message);
        return message;
    }

    private async Task GenerateAsync(BotConversation conversation, CancellationToken token, IPacedReplyAttempt attempt)
    {
        var call = new ScheduledCall(this, conversation, token, attempt);
        History.Enqueue(call);
        await _calls.Writer.WriteAsync(call, token);
        await call.RunAsync();
    }

    public async Task<ScheduledCall> NextAsync() => await _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    public async Task AtAsync(int milliseconds)
    {
        Clock.At(milliseconds);
        // Yield only for thread-pool scheduling; wall time never advances the injected business clock.
        await Task.Delay(10);
    }
    public Task SleepingAsync() => WaitAsync(() => Clock.Pending > 0, "scheduler registered a virtual delay");
    public Task DrainedAsync() => WaitAsync(() => Pipeline.Works == 0, "scheduler workers drained");
    internal void RecordSend() => Interlocked.Increment(ref _sends);
    internal void RecordEffect() => Interlocked.Increment(ref _effects);
    internal void RecordPreflight() => Interlocked.Increment(ref _preflights);
    public void Toggle(bool enabled)
    {
        var settings = Settings.Snapshot();
        settings.ReplyPacingEnabled = enabled;
        Pipeline.ReplaceSettings(settings);
    }
    public void Healthy() => Assert(Pipeline.Errors.Length == 0, "scheduler errors: " + string.Join(",", Pipeline.Errors));

    public static async Task WaitAsync(Func<bool> ready, string reason)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { while (!ready()) await Task.Delay(1, deadline.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException("Timed out: " + reason); }
    }
    public static void Assert(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }
    public static void Equal<T>(T expected, T actual, string reason)
        => Assert(EqualityComparer<T>.Default.Equals(expected, actual), reason + ": expected " + expected + "; got " + actual);

    public async ValueTask DisposeAsync()
    {
        Pipeline.Stop();
        foreach (var call in History) call.Unblock();
        try { await DrainedAsync(); Healthy(); }
        finally { EngineClock.Initialize(_previousClock); }
    }
}

internal sealed class ScheduledCall(ProbeHarness harness, BotConversation conversation,
    CancellationToken token, IPacedReplyAttempt attempt)
{
    private readonly TaskCompletionSource<Func<Task>> _operation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public BotConversation Conversation { get; } = conversation;
    public CancellationToken Token { get; } = token;
    public IPacedReplyAttempt Attempt { get; } = attempt;
    public TaskCompletionSource EffectReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Interrupted { get; private set; }

    public async Task RunAsync()
    {
        try { await (await _operation.Task.WaitAsync(Token))(); }
        catch (OperationCanceledException) { Interrupted = true; throw; }
        finally { Finished.TrySetResult(); }
    }
    public void Send(bool full = true) => _operation.SetResult(() => SendAsync(full));
    public void EffectThenHold() => _operation.SetResult(async () =>
    {
        await ReplyCommitBoundary.BeforeEffectAsync();
        harness.RecordEffect();
        EffectReached.TrySetResult();
        await _continue.Task.WaitAsync(Token);
        await SendAsync(true);
    });
    public void SendPrepared(bool hold = true) => _operation.SetResult(async () =>
    {
        await Attempt.ValidateDraftAsync();
        await ReplyCommitBoundary.BeforePrepareAsync();
        harness.RecordPreflight();
        Prepared.TrySetResult();
        if (hold) await _continue.Task.WaitAsync(Token);
        if (!ReplyCommitBoundary.TryBeforeSend()) await ReplyCommitBoundary.BeforeSendAsync();
        harness.RecordSend();
        Attempt.RecordDelivery(new ChatDeliveryReport(true, true));
    });
    public void Continue() => _continue.TrySetResult();
    public void Unblock()
    {
        _operation.TrySetResult(() => Task.CompletedTask);
        _continue.TrySetResult();
    }
    private async Task SendAsync(bool full)
    {
        await Attempt.ValidateDraftAsync();
        await ReplyCommitBoundary.BeforeSendAsync();
        if (full) harness.RecordSend();
        Attempt.RecordDelivery(new ChatDeliveryReport(full, full));
    }
    public Task DoneAsync() => Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
}

internal sealed class ManualSchedulerClock : IClock
{
    private readonly object _gate = new();
    private readonly List<(long Due, TaskCompletionSource Completion)> _waits = [];
    private long _milliseconds;
    public long TickCount { get { lock (_gate) return _milliseconds; } }
    public DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddDays(20000).AddMilliseconds(TickCount);
    public DateTimeOffset UtcNow => Now;
    public DateTime LocalDateTime => Now.UtcDateTime;
    public int Pending { get { lock (_gate) return _waits.Count(w => !w.Completion.Task.IsCompleted); } }
    public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Delay(TimeSpan.FromMilliseconds(millisecondsDelay), ct);
    public Task Delay(TimeSpan delay, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (delay <= TimeSpan.Zero) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waits.Add((_milliseconds + (long)Math.Ceiling(delay.TotalMilliseconds), completion));
        return WaitAsync(completion, ct);
    }
    private static async Task WaitAsync(TaskCompletionSource completion, CancellationToken token)
    {
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        await completion.Task;
    }
    public void At(long milliseconds)
    {
        List<TaskCompletionSource> ready;
        lock (_gate)
        {
            if (milliseconds < _milliseconds) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            _milliseconds = milliseconds;
            ready = _waits.Where(w => w.Due <= _milliseconds || w.Completion.Task.IsCompleted).Select(w => w.Completion).ToList();
            _waits.RemoveAll(w => w.Due <= _milliseconds || w.Completion.Task.IsCompleted);
        }
        foreach (var completion in ready) completion.TrySetResult();
    }
}

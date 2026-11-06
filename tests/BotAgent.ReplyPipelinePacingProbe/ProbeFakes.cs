using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Reply;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.ReplyPipelinePacingProbe;

// All messages and identifiers in this probe are synthetic. No network or storage adapter is used.
public class EmptyProxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object?>? Handler { get; set; }
    public static T Create<T>() where T : class => DispatchProxy.Create<T, EmptyProxy>();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) throw new InvalidOperationException("Missing method metadata");
        args ??= [];
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
            if (parameters[i].IsOut) args[i] = Default(parameters[i].ParameterType.GetElementType()!);
        return Handler is null ? Default(method.ReturnType) : Handler(method, args);
    }
    public static object? Default(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(string)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GenericTypeArguments[0])
                .Invoke(null, [Default(type.GenericTypeArguments[0])]);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return Activator.CreateInstance(type);
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            || type.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
            return Array.CreateInstance(type.GenericTypeArguments[0], 0);
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            || type.GetGenericTypeDefinition() == typeof(List<>))) return Activator.CreateInstance(type);
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

internal sealed class ManualClock : IClock
{
    private readonly object _gate = new();
    private readonly List<(long Due, TaskCompletionSource Completion)> _waits = [];
    private readonly List<(TimeSpan Delay, TaskCompletionSource<RegisteredDelay> Completion)> _registrations = [];
    internal sealed record RegisteredDelay(long RegisteredAt, long DueAt);
    private long _ticks;
    public long TickCount { get { lock (_gate) return _ticks; } }
    public DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddDays(20000).AddMilliseconds(TickCount);
    public DateTimeOffset UtcNow => Now;
    public DateTime LocalDateTime => Now.LocalDateTime;
    public int Pending { get { lock (_gate) return _waits.Count(wait => !wait.Completion.Task.IsCompleted); } }
    // Arm before releasing the action under test; the signal identifies its newly registered
    // timer, unlike Pending, which can include unrelated scheduler waits. This never advances time.
    public Task<RegisteredDelay> WaitForDelayRegistration(TimeSpan delay)
    {
        var completion = new TaskCompletionSource<RegisteredDelay>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _registrations.Add((delay, completion));
        return completion.Task;
    }
    public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Delay(TimeSpan.FromMilliseconds(millisecondsDelay), ct);
    public Task Delay(TimeSpan delay, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (delay <= TimeSpan.Zero) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredDelay registration;
        TaskCompletionSource<RegisteredDelay>[] observers;
        lock (_gate)
        {
            registration = new(_ticks, _ticks + (long)Math.Ceiling(delay.TotalMilliseconds));
            _waits.Add((registration.DueAt, completion));
            observers = _registrations.Where(wait => wait.Delay == delay).Select(wait => wait.Completion).ToArray();
            _registrations.RemoveAll(wait => wait.Delay == delay);
        }
        if (ct.CanBeCanceled) ct.Register(() => completion.TrySetCanceled(ct));
        foreach (var observer in observers) observer.TrySetResult(registration);
        return completion.Task;
    }
    public void Advance(int milliseconds)
    {
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        List<TaskCompletionSource> due;
        lock (_gate)
        {
            _ticks += milliseconds;
            due = _waits.Where(w => w.Due <= _ticks || w.Completion.Task.IsCompleted).Select(w => w.Completion).ToList();
            _waits.RemoveAll(w => w.Due <= _ticks || w.Completion.Task.IsCompleted);
        }
        foreach (var completion in due) completion.TrySetResult();
    }
}

internal sealed class ModelCall(IReadOnlyList<ChatMessage> context, bool toolsEnabled, CancellationToken cancellation, string? searchText)
{
    public IReadOnlyList<ChatMessage> Context { get; } = context.ToArray();
    public bool ToolsEnabled { get; } = toolsEnabled;
    public string? SearchText { get; } = searchText;
    public CancellationToken Cancellation { get; } = cancellation;
    public TaskCompletionSource<CompletionResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Reply(string? text, string? search = null) => Result.TrySetResult(new CompletionResult(100, text, "synthetic", Search: search));
}

internal sealed class ControlledModel
{
    private readonly Channel<ModelCall> _calls = Channel.CreateUnbounded<ModelCall>();
    public ConcurrentQueue<ModelCall> History { get; } = new();
    public IModelClient Client { get; }
    public ControlledModel()
    {
        Client = EmptyProxy.Create<IModelClient>();
        ((EmptyProxy)(object)Client).Handler = (method, args) =>
        {
            if (method.Name == "get_ChatTimeout") return TimeSpan.FromMinutes(1);
            if (method.Name != "CompleteAsync") return EmptyProxy.Default(method.ReturnType);
            var parameters = method.GetParameters();
            object? Argument(string name) => args[Array.FindIndex(parameters, p => p.Name == name)];
            var call = new ModelCall((IReadOnlyList<ChatMessage>)args[0]!,
                (bool)Argument("enableWebSearch")!, (CancellationToken)Argument("ct")!, (string?)Argument("searchText"));
            History.Enqueue(call);
            _calls.Writer.TryWrite(call);
            // Deliberately ignores provider cancellation: the product must still reject a late stale result.
            return call.Result.Task;
        };
    }
    public Task<ModelCall> Next() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
}

internal sealed class SyntheticSource : IQqChatSource, IStagedReplyChatSource
{
    private readonly Channel<(long Target, string Text)> _sent = Channel.CreateUnbounded<(long, string)>();
    public ConcurrentQueue<(long Target, string Text)> Sends { get; } = new();
    public Func<int, Task<SendResult>>? OnSend { get; set; }
    public Func<int, Task>? OnPrepare { get; set; }
    public int Preparations { get; private set; }
    public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
    public event Action<QqPokeEvent>? Poked { add { } remove { } }
    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
    public event Action<bool>? ConnectionChanged { add { } remove { } }
    public bool IsConnected => true;
    public async Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default,
        long? replyToMessageId = null, bool directAddress = false)
    {
        var preparation = ++Preparations;
        if (OnPrepare is not null) await OnPrepare(preparation);
        await ReplyCommitBoundary.BeforeSendAsync();
        ct.ThrowIfCancellationRequested();
        Sends.Enqueue((targetId, text));
        _sent.Writer.TryWrite((targetId, text));
        return OnSend is null ? new(true) : await OnSend(Sends.Count);
    }
    public Task<(long Target, string Text)> NextSend() => _sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null) => Task.FromResult(true);
    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default) => Task.FromResult<(string?, long)>((null, 0));
    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => Task.FromResult<string?>("synthetic");
}

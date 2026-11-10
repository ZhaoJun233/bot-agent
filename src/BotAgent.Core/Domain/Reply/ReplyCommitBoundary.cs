using BotAgent.Services.Qq;

namespace BotAgent.Domain.Reply;

/// <summary>Revalidates a paced reply at its last boundary before an outward commit.</summary>
public interface IReplyCommitGuard
{
    Task BeforeSendAsync();
    Task BeforeEffectAsync();
    Task BeforePrepareAsync();
    bool TryBeforeSend();
}

/// <summary>A source that re-enters the send boundary after asynchronous transport preparation.</summary>
public interface IStagedReplyChatSource { }

/// <summary>Per-async-flow guard; command and legacy paths have no guard and never wait.</summary>
public static class ReplyCommitBoundary
{
    private static readonly AsyncLocal<IReplyCommitGuard?> Current = new();
    private static readonly AsyncLocal<Action?> Validation = new();
    public static bool IsActive => Current.Value is not null;

    public static IDisposable Enter(IReplyCommitGuard? guard)
    {
        var previous = Current.Value;
        Current.Value = guard;
        return new Scope(() => Current.Value = previous);
    }

    public static IDisposable EnterValidation(Action validate)
    {
        var previous = Validation.Value;
        Validation.Value = previous + validate;
        return new Scope(() => Validation.Value = previous);
    }

    public static async Task BeforeSendAsync()
    {
        if (Current.Value is not { } guard) return;
        while (true)
        {
            await guard.BeforePrepareAsync();
            Validation.Value?.Invoke();
            if (guard.TryBeforeSend()) return;
        }
    }

    public static async Task BeforePrepareAsync()
    {
        if (Current.Value is not { } guard) return;
        await guard.BeforePrepareAsync();
        Validation.Value?.Invoke();
    }

    public static Task BeforeEffectAsync() => Current.Value?.BeforeEffectAsync() ?? Task.CompletedTask;

    /// <summary>Never waits while a transport lock is held. False asks the caller to release and prepare again.</summary>
    public static bool TryBeforeSend()
    {
        if (Current.Value is not { } guard) return true;
        Validation.Value?.Invoke();
        return guard.TryBeforeSend();
    }

    public static Task BeforeSourceSendAsync(IQqChatSource source)
        => source is IStagedReplyChatSource || PlatformSourceExecution.Current?.Source is IStagedReplyChatSource
            ? BeforePrepareAsync() : BeforeSendAsync();

    private sealed class Scope(Action restore) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            restore();
        }
    }
}

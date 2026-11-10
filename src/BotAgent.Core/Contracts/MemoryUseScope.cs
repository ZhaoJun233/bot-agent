using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Messaging;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Memory;

/// <summary>Tracks all memory consumed by an execution, including its derived work.
/// Receipt callbacks belong to the authorization core: they validate and synchronously
/// invoke the supplied commit under the same lock used by invalidation. No lock is held
/// while awaiting the returned task. A started external request cannot be recalled.
/// </summary>
public sealed class MemoryUseScope : IDisposable
{
    private static readonly AsyncLocal<MemoryUseScope?> Ambient = new();
    private readonly MemoryUseScope? _parent;
    private readonly object _gate;
    private readonly List<Func<Action, bool>> _receipts = new();
    private readonly List<Destination> _destinations = new();
    private static readonly AsyncLocal<Destination?> Outbound = new();
    private sealed record Destination(DriverKey Driver, ConversationId Conversation);
    private bool _disposed;

    private MemoryUseScope(MemoryUseScope? parent)
    {
        _parent = parent;
        _gate = parent?._gate ?? new object();
    }

    /// <summary>Start before retrieval or scheduling derived work. Nested scopes retain
    /// their parent's receipts; starting a child must never wash away consumed memory.</summary>
    public static MemoryUseScope Begin()
    {
        var scope = new MemoryUseScope(Ambient.Value);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>Capture the shared receipt set, not a validity snapshot. Task.Run flows
    /// it automatically; custom queues must capture/enter it or use Bind.</summary>
    public static MemoryUseScope? Capture() => Ambient.Value;

    public static IDisposable Enter(MemoryUseScope? scope)
    {
        var previous = Ambient.Value;
        Ambient.Value = scope;
        return new Restore(previous);
    }

    /// <summary>Register before providing facts to a model. The callback must invoke
    /// commit synchronously and exactly once if authorized, otherwise not at all.
    /// Registering without a root scope is a wiring error, never an unguarded fallback.</summary>
    public static void Register(Func<Action, bool> tryUse)
    {
        ArgumentNullException.ThrowIfNull(tryUse);
        var scope = Ambient.Value ?? throw new InvalidOperationException("memory_use_scope_required");
        lock (scope._gate) scope._receipts.Add(tryUse);
    }

    public static bool CanUse => TryUse(static () => { });

    /// <summary>Atomically validate all receipts and initiate a synchronous commit.
    /// The commit may return a task through Use; it must not wait for external work.
    /// Authorization failures (including validation exceptions) fail closed.
    /// Exceptions from an already initiated commit retain their original semantics.</summary>
    public static bool TryUse(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var scope = Ambient.Value;
        if (scope is null)
        {
            commit();
            return true;
        }

        var committed = false;
        try
        {
            scope.TryUseCore(() =>
            {
                committed = true;
                commit();
            });
        }
        catch when (!committed)
        {
            return false;
        }
        return committed;
    }

    public static T Use<T>(Func<T> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        T result = default!;
        if (!TryUse(() => result = commit())) throw new MemoryUseInvalidatedException();
        return result;
    }

    public static bool HasMemory => Ambient.Value?.HasReceipts() == true;

    /// <summary>Memory may only leave through the exact conversation and driver in
    /// which it was read. Tool permissions cannot widen this disclosure boundary.</summary>
    public static void RestrictDestination(DriverKey driver, ConversationId conversation)
    {
        var scope = Ambient.Value ?? throw new InvalidOperationException("memory_use_scope_required");
        lock (scope._gate) scope._destinations.Add(new(driver, conversation));
    }

    public static IDisposable EnterDestination(DriverKey driver, ConversationId conversation)
        => SetDestination(new(driver, conversation));

    /// <summary>Legacy numeric IDs need the actual sending source's host lease.
    /// Never infer the destination from the origin, or accept a different driver.</summary>
    public static IDisposable EnterLegacyDestination(IQqChatSource source, bool isGroup, long id)
    {
        var lease = PlatformSourceExecution.Current;
        if (lease is null || !lease.IsCurrent || !ReferenceEquals(lease.Source, source))
            return SetDestination(null);
        var context = lease.Context;
        var conversation = new ConversationId(context.PlatformId, context.AccountScope,
            isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
            id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return EnterDestination(new DriverKey(context.PlatformId, context.AccountScope, context.InstanceId), conversation);
    }

    public static bool TryDeliver(Action commit)
    {
        var delivered = false;
        TryUse(() =>
        {
            var scope = Ambient.Value;
            if (scope?.HasReceipts() == true && !scope.Allows(Outbound.Value)) return;
            delivered = true;
            commit();
        });
        return delivered;
    }

    public static T UseForDelivery<T>(Func<T> commit)
    {
        T result = default!;
        if (!TryDeliver(() => result = commit())) throw new MemoryUseInvalidatedException();
        return result;
    }

    private bool HasReceipts()
    {
        lock (_gate) return _receipts.Count > 0 || _parent?.HasReceipts() == true;
    }

    private bool Allows(Destination? destination)
    {
        if (destination is null) return false;
        lock (_gate)
        {
            if (_receipts.Count > 0 && _destinations.Count == 0) return false;
            return _destinations.All(allowed => allowed == destination) && (_parent?.Allows(destination) ?? true);
        }
    }

    private static IDisposable SetDestination(Destination? destination)
    {
        var previous = Outbound.Value;
        Outbound.Value = destination;
        return new RestoreDestination(previous);
    }

    private sealed class RestoreDestination(Destination? previous) : IDisposable
    {
        public void Dispose() => Outbound.Value = previous;
    }

    public static void ThrowIfInvalid()
    {
        if (!CanUse) throw new MemoryUseInvalidatedException();
    }

    public static Action Bind(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var scope = Capture();
        return () =>
        {
            using var restore = Enter(scope);
            action();
        };
    }

    public static Func<CancellationToken, Task> Bind(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var scope = Capture();
        return async ct =>
        {
            using var restore = Enter(scope);
            await action(ct).ConfigureAwait(false);
        };
    }

    private bool TryUseCore(Action commit)
    {
        // All descendants share this gate: registration cannot race a receipt snapshot
        // or the synchronous commit. The returned external task is awaited outside.
        lock (_gate)
        {
            var receipts = _receipts.ToArray();
            bool Check(int index)
            {
                if (_receipts.Count != receipts.Length) return false;
                if (index == receipts.Length)
                {
                    commit();
                    return true;
                }
                var accepted = false;
                return receipts[index](() => accepted = Check(index + 1)) && accepted;
            }
            if (_parent is null) return Check(0);
            var accepted = false;
            return _parent.TryUseCore(() => accepted = Check(0)) && accepted;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ambient.Value = _parent;
        // Captured/background work retains the receipts after the caller leaves.
    }

    private sealed class Restore(MemoryUseScope? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}

/// <summary>A content-free denial; never include facts, identifiers or grants.</summary>
public sealed class MemoryUseInvalidatedException() : InvalidOperationException("memory_use_invalidated") { }

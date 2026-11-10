using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Engine.Memory;
using BotAgent.Services.Memory;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.MemoryTurnProbe;

internal static class Probe
{
    public static int Passed { get; private set; }
    public static int Failed { get; private set; }
    public static void Check(bool condition, string description)
    {
        if (condition) Passed++; else Failed++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {description}");
    }

    public static bool Model()
    {
        var submitted = false;
        return MemoryUseScope.TryUse(() => submitted = true) && submitted;
    }

    public static bool Send(UnifiedMemoryContext? destination = null)
    {
        using var proof = destination is null ? null
            : MemoryUseScope.EnterDestination(destination.Driver, destination.Conversation);
        var submitted = false;
        return MemoryUseScope.TryDeliver(() => submitted = true) && submitted;
    }
}

internal sealed class Fixture : IDisposable
{
    public static readonly DriverKey Driver = new("test", "account-a", "driver-a");
    public static readonly ParticipantId Actor = new("test", "account-a", "10001");
    public static readonly UnifiedMemoryContext Group = new(Actor, Driver,
        new("test", "account-a", ConversationKind.GroupChat, "20001"), true);
    public static readonly UnifiedMemoryContext OtherGroup = Group with
    { Conversation = Group.Conversation with { NativeTargetId = "20002" } };
    public static readonly UnifiedMemoryContext Private = Group with
    { Conversation = new("test", "account-a", ConversationKind.PrivateChat, "10001") };
    public readonly Clock Time = new();
    public readonly UnifiedMemory Memory;
    public readonly SyntheticSource Source = new();
    public readonly CancellationTokenSource Lifetime = new();
    public bool Current = true;

    public Fixture() => Memory = new(new Store(), Time);

    public UnifiedMemoryEntry Remember(bool confirm = true, params UnifiedMemoryScope[] targets)
    {
        Memory.SetEnabled(true);
        var entry = Memory.RequestRemember(Group, new(new(UnifiedMemoryFactCategory.ReplyLength, "brief"),
            targets.ToImmutableArray(), true)).Entry!;
        if (!confirm) return entry;
        return Memory.Confirm(Private, new(entry.FactId, entry.Version, entry.ConfirmationToken, true, true)).Entry!;
    }

    public PlatformInboundEnvelope Envelope(UnifiedMemoryContext? context = null)
    {
        var c = context ?? Group;
        var reference = new MessageRef(c.Conversation, "synthetic-message");
        return new(new(c.Driver, reference),
            new(reference, c.Actor!, "synthetic-member", "synthetic query", Time.GetUtcNow()));
    }

    public PlatformSourceLease Lease(PlatformMessageOrigin origin)
        => new(new SyntheticOwner(), Source, origin, Lifetime.Token, () => Current);

    public IDisposable Enter(PlatformInboundEnvelope envelope)
        => PlatformSourceExecution.Enter(Lease(envelope.Origin));

    public void Dispose() => Lifetime.Dispose();
}

internal sealed class Store : IUnifiedMemoryPersistence
{
    private UnifiedMemoryState _state = UnifiedMemoryState.Empty;
    public UnifiedMemoryState Load() => _state;
    public void Save(long expectedRevision, UnifiedMemoryState next)
    {
        if (_state.Revision != expectedRevision) throw new InvalidOperationException("synthetic_revision_conflict");
        _state = next;
    }
}

internal sealed class Clock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}

internal sealed class SyntheticOwner : IPlatformSourceOwner
{
    public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => null;
}

internal sealed class SyntheticSource : IQqChatSource
{
    public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
    public event Action<QqPokeEvent>? Poked { add { } remove { } }
    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
    public event Action<bool>? ConnectionChanged { add { } remove { } }
    public bool IsConnected => true;
    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default,
        long? replyToMessageId = null, bool directAddress = false) => throw new NotSupportedException();
    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default,
        long? replyToMessageId = null) => throw new NotSupportedException();
    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => throw new NotSupportedException();
}

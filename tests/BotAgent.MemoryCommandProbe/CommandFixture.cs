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

namespace BotAgent.MemoryCommandProbe;

internal sealed class CommandFixture
{
    public static readonly DriverKey Driver = new("test", "BotAccount-A", "driver-A");
    public static readonly ParticipantId Subject = new("test", "BotAccount-A", "opaque-User-A");
    public static readonly UnifiedMemoryContext Private = Context(ConversationKind.PrivateChat, "opaque-DM-A");
    public static readonly UnifiedMemoryContext Group = Context(ConversationKind.GroupChat, "opaque-Group-A", "Thread-A");
    public static readonly UnifiedMemoryContext Target = Context(ConversationKind.GroupChat, "opaque-Group-B", "Thread-B");
    public static readonly UnifiedMemoryContext Admin = Private with { Actor = Subject with { NativeUserId = "administrator" } };
    public readonly FakeSource Source = new();
    public readonly VolatileStore Store = new();
    public readonly ProbeClock Clock = new();
    public readonly UnifiedMemory Memory;
    public readonly UnifiedMemoryCommandService Commands;

    public CommandFixture(bool enabled = true)
    {
        Memory = new(Store, Clock);
        if (enabled) Memory.SetEnabled(true);
        Commands = new(Source, Memory, c => c.Driver == Admin.Driver && c.Actor == Admin.Actor);
    }

    public static UnifiedMemoryContext Context(ConversationKind kind, string id, string? thread = null)
        => new(Subject, Driver, new(Driver.PlatformId, Driver.AccountScope, kind, id, thread), true);
    public static UnifiedMemoryScope Scope(UnifiedMemoryContext context) => new(context.Driver, context.Conversation);
    public static string Token(UnifiedMemoryContext context) => MemoryCommandSyntax.ScopeToken(Scope(context));
    public static string Confirm(UnifiedMemoryEntry entry, string audience = "accept-group-audience")
        => $"//memory confirm {entry.FactId:N} {entry.Version} {entry.ConfirmationToken:N} allow-source {audience}";

    public QqChatMessage Message(string text, UnifiedMemoryContext? context = null)
    {
        var ctx = context ?? Private;
        var reference = new MessageRef(ctx.Conversation, "synthetic-message");
        var envelope = new PlatformInboundEnvelope(new(ctx.Driver, reference),
            new(reference, ctx.Actor!, "synthetic display only", text, Clock.Now));
        // All compatibility scalar identity fields deliberately disagree with the attested message.
        return new(10001, true, 10002, 10003, "not authoritative", text, Clock.Now, false) { Envelope = envelope };
    }

    public PlatformSourceLease Lease(QqChatMessage message, bool current = true)
        => new(Source, Source, message.Envelope!.Origin, CancellationToken.None, () => current);

    public async Task<string> Run(string text, UnifiedMemoryContext? context = null)
    {
        var message = Message(text, context);
        using var execution = PlatformSourceExecution.Enter(Lease(message));
        var before = Source.Sends.Count;
        Check(await Commands.HandleAsync(message), "reserved command consumed");
        return Source.Sends.Count == before ? "" : Source.Sends[^1];
    }

    private static int _passed;
    public static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + label);
        _passed++;
        Console.WriteLine("PASS: " + label);
    }
    public static void Summary() => Console.WriteLine($"MemoryCommandProbe: {_passed} passed, 0 failed (synthetic only).");
}

internal sealed class VolatileStore : IUnifiedMemoryPersistence
{
    public UnifiedMemoryState State = UnifiedMemoryState.Empty;
    public UnifiedMemoryState Load() => State;
    public void Save(long expectedRevision, UnifiedMemoryState state)
    {
        if (State.Revision != expectedRevision) throw new IOException("synthetic revision mismatch");
        State = state;
    }
}
internal sealed class ProbeClock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeSource : IPlatformSourceRouter, IPlatformSourceOwner
{
    public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
    public event Action<QqPokeEvent>? Poked { add { } remove { } }
    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
    public event Action<bool>? ConnectionChanged { add { } remove { } }
    public bool IsConnected => true;
    public long SelfId => 10001;
    public List<string> Sends { get; } = [];
    public ConversationId? LastTarget;
    public bool ThrowOnSend;
    public Task<SendResult> SendTextAsync(bool isGroup, long id, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
    {
        if (ThrowOnSend) throw new IOException("synthetic transport failure must not escape");
        Sends.Add(text);
        return Task.FromResult(new SendResult(true));
    }
    public Task<bool> SendImageAsync(bool isGroup, long id, byte[] data, CancellationToken ct = default, long? replyToMessageId = null) => throw new NotSupportedException();
    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default) => throw new InvalidOperationException("no history reads");
    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => throw new InvalidOperationException("no identity lookups");
    public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => throw new NotSupportedException();
    public (bool IsGroup, long Id) CompatibilityTarget(string sourceKey)
    {
        if (!ConversationIdCodec.TryParse(sourceKey, out var conversation)) throw new InvalidOperationException("bad native route");
        LastTarget = conversation;
        return (conversation.Kind != ConversationKind.PrivateChat, 10001);
    }
    public PlatformSourceLease? CaptureForConversation(string sourceKey) => throw new InvalidOperationException("no lease reconstruction");
}

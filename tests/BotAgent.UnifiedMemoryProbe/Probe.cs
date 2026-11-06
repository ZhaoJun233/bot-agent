using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Memory;

namespace BotAgent.UnifiedMemoryProbe;

internal static class Probe
{
    private static int _passed;
    internal static readonly DriverKey Driver = new("test", "account-a", "driver-a");
    internal static readonly ParticipantId Subject = new("test", "account-a", "10001");
    internal static readonly UnifiedMemoryContext Private = Context(ConversationKind.PrivateChat, "10001");
    internal static readonly UnifiedMemoryContext Source = Context(ConversationKind.GroupChat, "20001");
    internal static readonly UnifiedMemoryContext Target = Context(ConversationKind.GroupChat, "20002");
    internal static UnifiedMemoryScope Scope(UnifiedMemoryContext context) => new(context.Driver, context.Conversation);
    internal static UnifiedMemoryContext Context(ConversationKind kind, string id) => new(Subject, Driver, new("test", "account-a", kind, id), true);
    internal static UnifiedMemoryRequest Request(string value = "brief", params UnifiedMemoryScope[] targets) =>
        new(new(UnifiedMemoryFactCategory.ReplyLength, value), targets.ToImmutableArray(), true);
    internal static UnifiedMemoryConfirmation Consent(UnifiedMemoryEntry entry) => new(entry.FactId, entry.Version, entry.ConfirmationToken, true, true);
    internal static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
        _passed++;
        Console.WriteLine("PASS: " + label);
    }
    internal static UnifiedMemoryEntry Remember(UnifiedMemory memory, UnifiedMemoryContext? source = null)
    {
        Check(memory.SetEnabled(true).Succeeded, "enable");
        var candidate = memory.RequestRemember(source ?? Source, Request("brief", Scope(Target)));
        Check(candidate.Succeeded, "candidate accepted");
        Check(memory.Confirm(Private, Consent(candidate.Entry!)).Succeeded, "private exact consent");
        return candidate.Entry!;
    }
    internal static void Summary() => Console.WriteLine($"UnifiedMemoryProbe: {_passed} passed, 0 failed (synthetic only).");
}

internal sealed class VolatileStore : IUnifiedMemoryPersistence
{
    public UnifiedMemoryState State = UnifiedMemoryState.Empty;
    public bool FailLoad;
    public bool FailSave;
    public UnifiedMemoryState Load() => FailLoad ? throw new IOException("synthetic load failure") : State;
    public void Save(long expectedRevision, UnifiedMemoryState state)
    {
        if (FailSave || State.Revision != expectedRevision) throw new IOException("synthetic save failure");
        State = state;
    }
}

internal sealed class ProbeClock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

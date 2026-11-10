using BotAgent.Domain.Messaging;
using BotAgent.Services.Memory;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.MemoryTurnProbe;

internal static class AttestationChecks
{
    public static void Run()
    {
        using var f = new Fixture();
        f.Remember();
        var envelope = f.Envelope();
        Denied(f, null, null, "no envelope and no lease");
        Denied(f, envelope, null, "envelope without transport lease");
        Denied(f, null, f.Lease(envelope.Origin), "lease without envelope");
        Denied(f, envelope, f.Lease(new(Fixture.Driver with { InstanceId = "driver-b" }, envelope.Message.Ref)),
            "different driver instance");
        Denied(f, envelope, f.Lease(new(Fixture.Driver with { AccountScope = "account-b" })),
            "different account scope");
        Denied(f, envelope, f.Lease(new(Fixture.Driver with { PlatformId = "elsewhere" })),
            "different platform");
        Denied(f, envelope, f.Lease(new(Fixture.Driver)), "lease without exact trigger");
        Denied(f, envelope, f.Lease(new(Fixture.Driver, envelope.Message.Ref with { NativeMessageId = "different-message" })),
            "different trigger message");
        Denied(f, envelope, f.Lease(new(Fixture.Driver, new(envelope.Message.Ref.Conversation with { ThreadId = "thread-b" },
            envelope.Message.Ref.NativeMessageId))), "different trigger thread");
        var unknown = f.Envelope(Fixture.Group with { Actor = Fixture.Actor with { NativeUserId = " " } });
        Denied(f, unknown, f.Lease(unknown.Origin), "unknown sender identity");
        f.Current = false;
        Denied(f, envelope, f.Lease(envelope.Origin), "retired source lease");
        f.Current = true;
        f.Lifetime.Cancel();
        Denied(f, envelope, f.Lease(envelope.Origin), "cancelled source lifetime");

        using var valid = new Fixture();
        valid.Remember();
        var validEnvelope = valid.Envelope();
        using var execution = valid.Enter(validEnvelope);
        using var turn = UnifiedMemoryTurn.Begin(valid.Memory, validEnvelope);
        Probe.Check(turn.Context == Fixture.Group, "reliable transport envelope attests exact actor and conversation");
        Probe.Check(turn.ReadPrompt("synthetic query").Length > 0, "valid attested turn receives confirmed preference");
        Probe.Check(MemoryUseScope.HasMemory && Probe.Model(), "receipt registered before prompt returns");
        valid.Current = false;
        Probe.Check(!Probe.Model() && !Probe.Send(Fixture.Group), "source retirement blocks consumed memory at both sinks");
    }

    private static void Denied(Fixture f, PlatformInboundEnvelope? envelope, PlatformSourceLease? lease, string description)
    {
        using var execution = PlatformSourceExecution.Enter(lease);
        using var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope);
        var getterCalled = false;
        var profile = turn.ReadLegacyProfile(Fixture.Actor, _ => { getterCalled = true; return "synthetic profile"; });
        Probe.Check(turn.Context is null && turn.ReadPrompt("synthetic query") == ""
            && profile == "" && !getterCalled && !MemoryUseScope.HasMemory, description + " fails closed");
    }
}

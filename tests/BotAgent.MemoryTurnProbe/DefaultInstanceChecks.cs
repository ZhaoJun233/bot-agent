using BotAgent.Domain.Memory;
using BotAgent.Engine.Memory;
using BotAgent.Services.Reply;

namespace BotAgent.MemoryTurnProbe;

internal static class DefaultInstanceChecks
{
    public static void Run()
    {
        using var f = new Fixture();
        var store = new Store();
        var memory = new UnifiedMemory(store, f.Time);
        var source = Fixture.Group with { Driver = Fixture.Driver with { InstanceId = "" } };
        var target = Fixture.OtherGroup with { Driver = source.Driver };
        var owner = Fixture.Private with { Driver = source.Driver };
        var request = new UnifiedMemoryRequest(new(UnifiedMemoryFactCategory.ReplyLength, "brief"),
            [new(target.Driver, target.Conversation)], true);
        Probe.Check(memory.SetEnabled(true).Succeeded, "default instance fixture enables memory");
        var candidate = memory.RequestRemember(source, request);
        Probe.Check(candidate.Succeeded && candidate.Entry is not null, "canonical empty instance creates candidate");
        if (candidate.Entry is not { } pending) return;
        var confirmation = new UnifiedMemoryConfirmation(pending.FactId, pending.Version, pending.ConfirmationToken, true, true);
        Probe.Check(!memory.Confirm(Fixture.Private, confirmation).Succeeded, "named instance cannot confirm default-instance candidate");
        Probe.Check(memory.Confirm(owner, confirmation).Succeeded, "default instance owner confirms exact candidate");
        Probe.Check(memory.Read(source, "synthetic").Receipt is not null && memory.Read(target, "synthetic").Receipt is not null,
            "default instance source and explicit target are readable");
        Probe.Check(memory.Read(Fixture.Group, "synthetic").Entries.IsEmpty, "empty instance never grants to named instance");
        Probe.Check(memory.RequestRemember(source, request with { Targets = [new(Fixture.Driver, target.Conversation)] }).Status
            == UnifiedMemoryStatus.InvalidTarget, "default instance cannot grant a named-instance target");

        var restored = new UnifiedMemory(store, f.Time);
        Probe.Check(restored.Read(source, "synthetic").Receipt is not null, "canonical default instance survives persistence reload validation");
        var envelope = f.Envelope(source);
        using var execution = f.Enter(envelope);
        using var turn = UnifiedMemoryTurn.Begin(memory, envelope);
        Probe.Check(turn.Context == source && turn.ReadPrompt("synthetic").Length > 0, "real turn accepts attested default instance");
        Probe.Check(Probe.Model() && Probe.Send(source), "default instance permits model and exact destination submission");
        Probe.Check(!Probe.Send(Fixture.Group), "default instance derived output rejects named destination proof");
        string? legacyKey = null;
        Probe.Check(turn.ReadLegacyProfile(Fixture.Actor, key => { legacyKey = key; return "synthetic profile"; }) == "synthetic profile",
            "legacy authorization accepts exact default-instance context");
        Probe.Check(legacyKey == LegacyProfileScopeKey.TryCreate(source.Driver, Fixture.Actor, source.Conversation),
            "legacy getter receives the exact canonical default-instance storage key");

        // Null is genuinely absent. Empty is the documented PlatformContext default, not an unknown identity.
        foreach (var invalid in new string?[] { null, " ", "a\n", new('a', 257) })
        {
            var malformed = source with { Driver = source.Driver with { InstanceId = invalid! } };
            Probe.Check(memory.RequestRemember(malformed, request).Status == UnifiedMemoryStatus.UnreliableIdentity
                && memory.Read(malformed, "synthetic").Receipt is null
                && memory.AuthorizeLegacyProfile(malformed, Fixture.Actor) is null,
                "absent whitespace control or oversized instance rejected");
        }

        Probe.Check(memory.Revoke(owner, pending.FactId, new(target.Driver, target.Conversation)).Succeeded
            && memory.Read(target, "synthetic").Entries.IsEmpty
            && memory.Read(source, "synthetic").Receipt is not null, "default-instance revoke removes target while retaining source");
        Probe.Check(!Probe.Model() && !Probe.Send(source), "default-instance mutation invalidates already consumed turn receipts");
        Probe.Check(memory.SetEnabled(false).Succeeded && memory.Read(source, "synthetic").Entries.IsEmpty
            && memory.List(owner).Length == 1, "default-instance stop retains private management but blocks reads");
        Probe.Check(memory.Forget(owner, pending.FactId).Succeeded && memory.List(owner).IsEmpty,
            "default-instance forget works while disabled");
        Probe.Check(memory.IsLegacyProfileSuppressed(Fixture.Actor, new(source.Driver, source.Conversation))
            && !memory.IsLegacyProfileSuppressed(Fixture.Actor, new(Fixture.Driver, source.Conversation)),
            "default-instance suppression never expands to named driver");
        var forgotten = new UnifiedMemory(store, f.Time);
        Probe.Check(forgotten.AuthorizeLegacyProfile(source, Fixture.Actor) is null
            && forgotten.AuthorizeLegacyProfile(Fixture.Group, Fixture.Actor) is not null,
            "default-instance forget remains exact after persistence reload");
    }
}

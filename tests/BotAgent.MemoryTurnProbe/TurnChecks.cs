using BotAgent.Domain.Memory;
using BotAgent.Services.Memory;
using BotAgent.Services.Reply;

namespace BotAgent.MemoryTurnProbe;

internal static class TurnChecks
{
    public static void Run()
    {
        PromptAndDestination();
        UnconfirmedAndDisabled();
        Mutations();
        Legacy();
        EmptyLegacy();
        LegacyGetterInvalidation();
        Disposal();
    }

    private static void PromptAndDestination()
    {
        using var f = new Fixture();
        var entry = f.Remember();
        var envelope = f.Envelope();
        using var execution = f.Enter(envelope);
        using var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope);
        var prompt = turn.ReadPrompt("synthetic query");
        Probe.Check(prompt.Contains("ReplyLength: brief", StringComparison.Ordinal), "prompt contains fixed confirmed preference");
        Probe.Check(new[] { "10001", "20001", "account-a", "driver-a", "synthetic-member", "synthetic-message",
            entry.FactId.ToString(), entry.ConfirmationToken.ToString() }.All(value => !prompt.Contains(value, StringComparison.Ordinal)),
            "prompt excludes identities, source metadata and authorization tokens");
        Probe.Check(Probe.Model(), "valid current receipt permits next model submission");
        Probe.Check(!Probe.Send(), "memory-derived output needs explicit destination proof");
        Probe.Check(Probe.Send(Fixture.Group), "exact read destination is permitted");
        Probe.Check(!Probe.Send(Fixture.OtherGroup), "different conversation is rejected");
        Probe.Check(!Probe.Send(Fixture.Group with { Driver = Fixture.Driver with { InstanceId = "driver-b" } }),
            "different output driver rejected");
        Probe.Check(!Probe.Send(Fixture.Group with { Conversation = Fixture.Group.Conversation with { ThreadId = "thread-b" } }),
            "different output thread rejected");
        Probe.Check(!Probe.Send(Fixture.Group with { Driver = Fixture.Driver with { AccountScope = "account-b" } }),
            "different output account rejected");
        Probe.Check(!Probe.Send(Fixture.Group with { Driver = Fixture.Driver with { PlatformId = "elsewhere" } }),
            "different output platform rejected");
        using var child = MemoryUseScope.Begin();
        f.Memory.SetEnabled(false);
        Probe.Check(!Probe.Model() && !Probe.Send(Fixture.Group), "nested scope cannot remove parent invalidation");
    }

    private static void UnconfirmedAndDisabled()
    {
        using var f = new Fixture();
        f.Remember(false);
        var envelope = f.Envelope();
        using var execution = f.Enter(envelope);
        using (var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope))
            Probe.Check(turn.ReadPrompt("synthetic query") == "" && !MemoryUseScope.HasMemory, "candidate is never exposed to model");
        f.Memory.SetEnabled(false);
        using var disabled = UnifiedMemoryTurn.Begin(f.Memory, envelope);
        Probe.Check(disabled.ReadPrompt("synthetic query") == "" && !MemoryUseScope.HasMemory, "disabled unified memory does not inject prompt");
    }

    private static void Mutations()
    {
        foreach (var change in new[] { "disable", "edit", "forget", "revoke", "reject" })
        {
            using var f = new Fixture();
            var entry = f.Remember(true, new UnifiedMemoryScope(Fixture.Driver, Fixture.OtherGroup.Conversation));
            var envelope = f.Envelope(Fixture.OtherGroup);
            using var execution = f.Enter(envelope);
            using var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope);
            Probe.Check(turn.ReadPrompt("synthetic query").Length > 0 && Probe.Model() && Probe.Send(Fixture.OtherGroup),
                change + " precondition has exact confirmed cross-scope grant");
            var result = change switch
            {
                "disable" => f.Memory.SetEnabled(false),
                "edit" => f.Memory.Edit(Fixture.Private, entry.FactId, entry.Version, new(new(UnifiedMemoryFactCategory.ReplyLength, "detailed"), [], true)),
                "forget" => f.Memory.Forget(Fixture.Private, entry.FactId),
                "revoke" => f.Memory.Revoke(Fixture.Private, entry.FactId, new(Fixture.Driver, Fixture.OtherGroup.Conversation)),
                _ => f.Memory.Reject(Fixture.Private, entry.FactId)
            };
            Probe.Check(result.Succeeded && !Probe.Model() && !Probe.Send(Fixture.OtherGroup), change + " rejects next model and final send");
        }
    }

    private static void Legacy()
    {
        using var f = new Fixture();
        var entry = f.Remember();
        f.Memory.SetEnabled(false);
        var envelope = f.Envelope();
        using var execution = f.Enter(envelope);
        using (var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope))
        {
            Probe.Check(turn.ReadPrompt("synthetic query") == "", "legacy fixture keeps unified memory disabled");
            string? legacyKey = null;
            Probe.Check(turn.ReadLegacyProfile(Fixture.Actor, key => { legacyKey = key; return "synthetic profile"; }) == "synthetic profile" && MemoryUseScope.HasMemory,
                "legacy profile independently authorized while disabled");
            Probe.Check(legacyKey == LegacyProfileScopeKey.TryCreate(Fixture.Driver, Fixture.Actor, Fixture.Group.Conversation),
                "legacy getter receives exact attested provenance rather than scalar UID");
            Probe.Check(Probe.Model() && Probe.Send(Fixture.Group), "legacy permits model and send with exact destination proof");
            Probe.Check(!Probe.Send() && !Probe.Send(Fixture.OtherGroup), "legacy refuses absent or widened destination proof");
            var getterCalled = false;
            string DeniedGetter(string key) { getterCalled = true; return "synthetic profile"; }
            Probe.Check(turn.ReadLegacyProfile(null, DeniedGetter) == ""
                && turn.ReadLegacyProfile(Fixture.Actor with { NativeUserId = " " }, DeniedGetter) == "" && !getterCalled,
                "legacy unknown and empty subject denied before getter");
            f.Memory.Forget(Fixture.Private, entry.FactId);
            Probe.Check(!Probe.Model() && !Probe.Send(Fixture.Group), "forget invalidates old legacy receipt at both sinks");
        }
        using var fresh = UnifiedMemoryTurn.Begin(f.Memory, envelope);
        var suppressedGetterCalled = false;
        Probe.Check(fresh.ReadLegacyProfile(Fixture.Actor, _ => { suppressedGetterCalled = true; return "synthetic profile"; }) == ""
            && !suppressedGetterCalled && !MemoryUseScope.HasMemory, "forgotten subject stays suppressed in fresh turn");
        Probe.Check(fresh.ReadLegacyProfile(Fixture.Actor with { NativeUserId = "10002" }, _ => "other synthetic profile")
            == "other synthetic profile", "forget does not suppress unrelated legacy subject");
    }

    private static void EmptyLegacy()
    {
        using var f = new Fixture();
        var envelope = f.Envelope();
        using var execution = f.Enter(envelope);
        foreach (var summary in new[] { "", " \t\n" })
        {
            using var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope);
            var getterCalled = false;
            var result = turn.ReadLegacyProfile(Fixture.Actor, _ => { getterCalled = true; return summary; });
            Probe.Check(getterCalled && result == "" && !MemoryUseScope.HasMemory && !turn.HasUnifiedFacts,
                "empty legacy getter returns memory-free turn");
            Probe.Check(Probe.Model() && Probe.Send() && Probe.Send(Fixture.OtherGroup),
                "empty legacy profile does not restrict ordinary model or delivery");
        }
    }

    private static void LegacyGetterInvalidation()
    {
        foreach (var change in new[] { "forget", "retire source" })
        {
            using var f = new Fixture();
            var entry = f.Remember();
            var envelope = f.Envelope();
            using var execution = f.Enter(envelope);
            using var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope);
            var getterCalled = false;
            var changed = false;
            var result = turn.ReadLegacyProfile(Fixture.Actor, _ =>
            {
                getterCalled = true;
                if (change == "forget") changed = f.Memory.Forget(Fixture.Private, entry.FactId).Succeeded;
                else { f.Current = false; changed = true; }
                return "synthetic profile captured before invalidation";
            });
            Probe.Check(getterCalled && changed && result == "synthetic profile captured before invalidation" && MemoryUseScope.HasMemory,
                change + " during legacy getter retains the consumed receipt");
            Probe.Check(!Probe.Model() && !Probe.Send(Fixture.Group),
                change + " during legacy getter rejects derived model and send");
        }
    }

    private static void Disposal()
    {
        using var f = new Fixture();
        var entry = f.Remember();
        var envelope = f.Envelope();
        using var execution = f.Enter(envelope);
        var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope);
        turn.ReadPrompt("synthetic query");
        var captured = MemoryUseScope.Capture();
        turn.Dispose();
        using var restored = MemoryUseScope.Enter(captured);
        Probe.Check(Probe.Model(), "captured scope remains valid after lexical disposal");
        f.Memory.Forget(Fixture.Private, entry.FactId);
        Probe.Check(!Probe.Model() && !Probe.Send(Fixture.Group), "disposed but captured scope retains receipt invalidation");
    }

    public static async Task DeferredWork()
    {
        using var f = new Fixture();
        var entry = f.Remember();
        var envelope = f.Envelope();
        using var execution = f.Enter(envelope);
        var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope);
        turn.ReadPrompt("synthetic query");
        var boundAllowed = true;
        var bound = MemoryUseScope.Bind(() => { boundAllowed = Probe.Model() || Probe.Send(Fixture.Group); });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flowed = Task.Run(async () => { await release.Task; return Probe.Model() || Probe.Send(Fixture.Group); });
        turn.Dispose();
        f.Memory.Forget(Fixture.Private, entry.FactId);
        bound();
        Probe.Check(!boundAllowed, "explicitly bound queued work retains revoked receipts after disposal");
        release.SetResult();
        Probe.Check(!await flowed, "async task inherits live receipt set rather than validity snapshot");
    }
}

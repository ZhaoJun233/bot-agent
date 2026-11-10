using BotAgent.ReplyPipelinePacingProbe;

namespace BotAgent.ReplyModeTransitionProbe;

internal static class Program
{
    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] scenarios =
        [
            ("enable discards a legacy ordinary draft", OrdinaryDraft),
            ("rapid enable-disable cannot resurrect a retired draft", RapidToggle),
            ("enable transfers an explicit generation once", ExplicitDraft),
            ("enable preserves an already-started send", CommittedSend),
            ("enable preserves an already-started effect", CommittedEffect),
            ("enable retires an ordinary global-slot waiter", SlotWaiter),
            ("enable removes queued ordinary requests", PendingOrdinary),
            ("enable transfers queued explicit work through quiet", PendingExplicit),
            ("ordinary deferred catch-up is not explicit", IntentScenarios.OrdinaryCatchUp),
            ("null ordinary catch-up is not an accepted result", () => IntentScenarios.NullCatchUp(false)),
            ("null proactive catch-up is not an accepted result", () => IntentScenarios.NullCatchUp(true)),
            ("accepted background result survives mode round-trips", IntentScenarios.AcceptedResultRoundTrip),
            ("null explicit request still observes human quiet", IntentScenarios.NullExplicit),
            ("private input retains explicit intent on enable", IntentScenarios.PrivateExplicit),
            ("stopped deferred request cannot recapture a fresh epoch", EpochScenarios.StoppedDeferred),
            ("stop fences concurrent queue-to-paced migration", StopTransferScenarios.QueueMigration),
            ("stop retires a detached explicit handoff", StopTransferScenarios.DetachedHandoff),
        ];
        var passed = 0;
        foreach (var (name, run) in scenarios)
        {
            try { await run(); passed++; Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"Reply mode transition probe: {passed}/{scenarios.Length} passed.");
        return passed == scenarios.Length ? 0 : 1;
    }

    private static Fixture Legacy() => new(settings => settings.ReplyPacingEnabled = false);
    private static void Toggle(Fixture f, bool enabled)
    {
        f.Settings.ReplyPacingEnabled = enabled;
        f.Pipeline.RefreshPacingPolicy();
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static Task AdvanceRegistered(Fixture f, int milliseconds) => TransitionClock.AdvanceRegistered(f, milliseconds);

    private static int PendingCount(Fixture f)
    {
        var field = f.Pipeline.GetType().GetField("_pendingReplies", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var queues = (System.Collections.Concurrent.ConcurrentDictionary<string, BotAgent.Services.Reply.TenantReplyQueue>)field.GetValue(f.Pipeline)!;
        return queues.Values.Sum(queue => queue.Count);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task OrdinaryDraft()
    {
        await using var f = Legacy();
        await f.Inbound("legacy-ordinary");
        var call = await f.NextModel();
        Toggle(f, true);
        call.Reply("retired-draft");
        await f.Idle();
        Check(f.Source.Sends.IsEmpty, "pre-enable ordinary draft must not send");
        Check(call.Cancellation.IsCancellationRequested, "pre-enable ordinary generation must be cancelled");
        Check(f.Model.History.Count == 1, "ordinary work must not be replayed");
    }

    private static async Task RapidToggle()
    {
        await using var f = Legacy();
        await f.Inbound("legacy-fast-toggle");
        var call = await f.NextModel();
        Toggle(f, true); Toggle(f, false); Toggle(f, true); Toggle(f, false);
        call.Reply("retired-across-toggles");
        await f.Idle();
        Check(f.Source.Sends.IsEmpty && f.Model.History.Count == 1,
            "retirement must survive rapid enable-disable cycles");
        await f.Inbound("fresh-disabled-request", direct: true, target: 10003);
        (await f.NextModel()).Reply("fresh-disabled-answer");
        await f.Source.NextSend(); await f.Idle();
        Check(f.Source.Sends.Count == 1, "fresh legacy admission must still work after disable");
    }

    private static async Task ExplicitDraft()
    {
        await using var f = Legacy();
        await f.Inbound("legacy-explicit", direct: true);
        var old = await f.NextModel();
        Toggle(f, true);
        old.Reply("retired-explicit-draft");
        Check(old.Cancellation.IsCancellationRequested, "explicit draft must retire before transfer");
        await f.Until(() => f.PacedCount == 1 && f.ActiveCount == 0, "explicit handoff registered");
        await AdvanceRegistered(f, 2999);
        Check(f.Model.History.Count == 1 && f.Source.Sends.IsEmpty, "transfer must honor quiet");
        await AdvanceRegistered(f, 1);
        (await f.Model.Next()).Reply("transferred-answer");
        await f.Source.NextSend(); await f.Idle();
        Check(f.Model.History.Count == 2 && f.Source.Sends.Count == 1, "explicit transfer must occur once");
    }

    private static async Task CommittedSend()
    {
        await using var f = Legacy();
        var entered = Gate(); var release = Gate();
        f.Source.OnSend = async _ => { entered.TrySetResult(); await release.Task; return new BotAgent.Services.Qq.SendResult(true); };
        await f.Inbound("legacy-send", direct: true);
        var call = await f.NextModel(); call.Reply("started-send");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Toggle(f, true); Toggle(f, false); Toggle(f, true);
            Check(!call.Cancellation.IsCancellationRequested, "started send must retain ownership");
        }
        finally { release.TrySetResult(); }
        await f.Idle();
        Check(f.Source.Sends.Count == 1 && f.Model.History.Count == 1, "started send must not replay");
    }

    private static async Task CommittedEffect()
    {
        await using var f = new Fixture(settings => { settings.ReplyPacingEnabled = false; settings.EnableWebSearch = true; settings.MaxAgentSteps = 2; });
        var release = new TaskCompletionSource<BotAgent.Services.Net.WebSearchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Research.OnSearch = _ => release.Task;
        await f.Inbound("legacy-tool", direct: true);
        var first = await f.NextModel(); first.Reply(null, "synthetic-query");
        await f.Research.NextSearch();
        Toggle(f, true); Toggle(f, false); Toggle(f, true);
        release.TrySetResult(ControlledResearch.Result());
        var followup = await f.NextModel(); followup.Reply("tool-result");
        await f.Source.NextSend(); await f.Idle();
        Check(!first.Cancellation.IsCancellationRequested && f.ResearchCalls == 1,
            "started effect must finish exactly once");
        Check(f.Model.History.Count == 2 && f.Source.Sends.Count == 1, "effect followup must not regenerate");
    }

    private static async Task SlotWaiter() => await Pending(anotherConversation: true, explicitRequest: false);
    private static async Task PendingOrdinary() => await Pending(anotherConversation: false, explicitRequest: false);
    private static async Task PendingExplicit() => await Pending(anotherConversation: false, explicitRequest: true);

    private static async Task Pending(bool anotherConversation, bool explicitRequest)
    {
        await using var f = Legacy();
        var entered = Gate(); var release = Gate();
        f.Source.OnSend = async _ => { entered.TrySetResult(); await release.Task; return new BotAgent.Services.Qq.SendResult(true); };
        await f.Inbound("legacy-occupied", direct: true);
        (await f.NextModel()).Reply("first-committed-answer");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await f.Inbound("legacy-pending", direct: explicitRequest, target: anotherConversation ? 10003 : 10001);
            if (anotherConversation)
                await f.Until(() => f.Pipeline.InFlightReplies == 2, "legacy waiter claimed");
            else
                Check(PendingCount(f) == 1, "same-conversation request must be queued");
            Toggle(f, true);
        }
        finally { release.TrySetResult(); }
        await f.Until(() => f.Model.History.Count > 1 || f.Pipeline.InFlightReplies == 0, "legacy retirement drains");
        if (f.Model.History.Count > 1)
        {
            (await f.NextModel()).Reply("unexpected-legacy-followup");
            await f.Idle();
            throw new InvalidOperationException("pre-enable queue/waiter bypassed paced admission");
        }
        Check(f.Source.Sends.Count == 1, "only already-committed send may finish before quiet");
        if (explicitRequest)
        {
            await AdvanceRegistered(f, 2999);
            Check(f.Model.History.Count == 1, "queued explicit transfer must honor full quiet");
            await AdvanceRegistered(f, 1);
            (await f.Model.Next()).Reply("pending-explicit-answer");
            await f.Source.NextSend(); await f.Source.NextSend(); await f.Idle();
            Check(f.Model.History.Count == 2 && f.Source.Sends.Count == 2, "queued explicit request transfers once");
        }
        else
        {
            await f.Advance(3000); await f.Idle();
            Check(f.Model.History.Count == 1 && f.Source.Sends.Count == 1, "ordinary queue/waiter must be discarded");
        }
    }
}
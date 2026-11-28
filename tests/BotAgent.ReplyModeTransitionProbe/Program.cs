using BotAgent.ReplyPipelinePacingProbe;
using BotAgent.Services.Qq;

namespace BotAgent.ReplyModeTransitionProbe;

internal static class Program
{
    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] scenarios =
        [
            ("parameter changes retain an ordinary generation", OrdinaryDraft),
            ("rapid AI toggles cannot resurrect a retired draft", RapidToggle),
            ("explicit work retains the admitted quiet snapshot", ExplicitDraft),
            ("parameter changes preserve an already-started send", CommittedSend),
            ("parameter changes preserve an already-started effect", CommittedEffect),
            ("stop retires an ordinary global-slot waiter", () => Pending(true, false, true)),
            ("stop removes queued ordinary requests", () => Pending(false, false, true)),
            ("queued explicit work retains quiet after parameter updates", () => Pending(false, true, false)),
            ("maintenance cannot replay a completed opportunity", IntentScenarios.DuplicateOpportunity),
            ("null ordinary request is not an accepted result", () => IntentScenarios.NullOrdinary(false)),
            ("null proactive request is not an accepted result", () => IntentScenarios.NullOrdinary(true)),
            ("accepted background result survives parameter changes", IntentScenarios.AcceptedResult),
            ("null explicit request still observes human quiet", IntentScenarios.NullExplicit),
            ("private input retains explicit intent", IntentScenarios.PrivateExplicit),
            ("stopped deferred request cannot recapture a fresh epoch", EpochScenarios.StoppedDeferred),
            ("stop fences concurrent admission", StopTransferScenarios.ConcurrentAdmission),
            ("stop retires a detached paced dispatch", StopTransferScenarios.DetachedDispatch),
        ];
        var passed = 0;
        foreach (var (name, run) in scenarios)
        {
            try { await run(); passed++; Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
        }
        Console.WriteLine($"Single-policy lifecycle probe: {passed}/{scenarios.Length} passed.");
        return passed == scenarios.Length ? 0 : 1;
    }

    private static void RefreshParameters(Fixture f)
    {
        f.Settings.GroupInterjectionProbability = 0;
        f.Settings.GroupContinuationWaitSeconds = 10;
        f.Pipeline.RefreshPacingPolicy();
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static Task AdvanceRegistered(Fixture f, int milliseconds) => TransitionClock.AdvanceRegistered(f, milliseconds);
    private static Task AdvanceUntilIdle(Fixture f, int milliseconds) => TransitionClock.AdvanceUntilIdle(f, milliseconds);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task OrdinaryDraft()
    {
        await using var f = new Fixture();
        await f.Inbound("synthetic ordinary input");
        await AdvanceRegistered(f, 3000);
        var call = await f.NextModel();
        RefreshParameters(f);
        Check(!call.Cancellation.IsCancellationRequested, "parameter edits cannot cancel an admitted generation");
        call.Reply("synthetic admitted answer");
        await f.Source.NextSend(); await f.Idle();
        Check(f.Model.History.Count == 1 && f.Source.Sends.Count == 1, "admitted generation sends exactly once");
        await f.Inbound("synthetic fresh ordinary input", target: 10003);
        await AdvanceUntilIdle(f, 12000);
        Check(f.Model.History.Count == 1, "new opportunities use the updated zero probability");
    }

    private static async Task RapidToggle()
    {
        await using var f = new Fixture();
        await f.Inbound("synthetic old explicit input", direct: true);
        await AdvanceRegistered(f, 3000);
        var call = await f.NextModel();
        foreach (var enabled in new[] { false, true, false, true })
        {
            f.Settings.AiModeEnabled = enabled;
            f.Pipeline.RefreshPacingPolicy();
        }
        call.Reply("synthetic retired answer"); await f.Idle();
        Check(call.Cancellation.IsCancellationRequested && f.Source.Sends.IsEmpty && f.Model.History.Count == 1,
            "AI toggles must not replay a retired explicit generation");
        await f.Inbound("synthetic fresh explicit input", direct: true);
        await AdvanceRegistered(f, 3000);
        (await f.NextModel()).Reply("synthetic fresh answer");
        await f.Source.NextSend(); await f.Idle();
        Check(f.Source.Sends.Count == 1 && f.Model.History.Count == 2, "only fresh input is admitted after reenable");
    }

    private static async Task ExplicitDraft()
    {
        await using var f = new Fixture();
        await f.Inbound("synthetic queued explicit", direct: true);
        RefreshParameters(f);
        await AdvanceRegistered(f, 2999);
        Check(f.Model.History.Count == 0, "new settings cannot shorten an existing quiet window");
        await AdvanceRegistered(f, 1);
        var call = await f.Model.Next();
        RefreshParameters(f);
        call.Reply("synthetic explicit answer");
        await f.Source.NextSend(); await f.Idle();
        Check(f.Model.History.Count == 1 && f.Source.Sends.Count == 1, "explicit generation is never transferred or replayed");
    }

    private static async Task CommittedSend()
    {
        await using var f = new Fixture();
        var entered = Gate(); var release = Gate();
        f.Source.OnSend = async _ => { entered.TrySetResult(); await release.Task; return new SendResult(true); };
        await f.Inbound("synthetic send input", direct: true);
        await AdvanceRegistered(f, 3000);
        var call = await f.NextModel(); call.Reply("synthetic started send");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            RefreshParameters(f); f.Pipeline.ResizeGate(2);
            Check(!call.Cancellation.IsCancellationRequested, "started send retains its lease across configuration updates");
        }
        finally { release.TrySetResult(); }
        await f.Idle();
        Check(f.Source.Sends.Count == 1 && f.Model.History.Count == 1, "started send completes only once");
    }

    private static async Task CommittedEffect()
    {
        await using var f = new Fixture(s => { s.EnableWebSearch = true; s.MaxAgentSteps = 2; });
        var release = new TaskCompletionSource<BotAgent.Services.Net.WebSearchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Research.OnSearch = _ => release.Task;
        await f.Inbound("synthetic tool input", direct: true);
        await AdvanceRegistered(f, 3000);
        var first = await f.NextModel(); first.Reply(null, "synthetic query");
        await f.Research.NextSearch();
        RefreshParameters(f); release.TrySetResult(ControlledResearch.Result());
        (await f.NextModel()).Reply("synthetic tool result");
        await f.Source.NextSend(); await f.Idle();
        Check(!first.Cancellation.IsCancellationRequested && f.ResearchCalls == 1,
            "started effect must finish exactly once");
        Check(f.Model.History.Count == 2 && f.Source.Sends.Count == 1, "effect followup is not regenerated");
    }

    private static async Task Pending(bool anotherConversation, bool explicitRequest, bool stop)
    {
        await using var f = new Fixture();
        var entered = Gate(); var release = Gate();
        f.Source.OnSend = async count =>
        {
            if (count == 1) { entered.TrySetResult(); await release.Task; }
            return new SendResult(true);
        };
        await f.Inbound("synthetic occupied input", direct: true);
        await AdvanceRegistered(f, 3000);
        (await f.NextModel()).Reply("synthetic committed answer");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var target = anotherConversation ? 10003 : 10001;
            var waiting = await f.Inbound("synthetic pending input", direct: explicitRequest, target: target);
            if (anotherConversation)
            {
                await AdvanceRegistered(f, 3000);
                Check(f.Model.History.Count == 1 && f.PacedCount == 2, "global-slot waiter remains owned without starting generation");
            }
            if (stop) f.Pipeline.ForgetSession(waiting.SourceKey);
            else RefreshParameters(f);
        }
        finally { release.TrySetResult(); }
        await f.Until(() => f.ActiveCount == 0, "committed emission drains");
        if (stop)
        {
            await AdvanceUntilIdle(f, 10000);
            Check(f.Model.History.Count == 1 && f.Source.Sends.Count == 1, "stopped waiter produces no generation or send");
        }
        else
        {
            await AdvanceRegistered(f, 2999);
            Check(f.Model.History.Count == 1, "queued explicit request still honors its original quiet wait");
            await AdvanceRegistered(f, 1);
            (await f.NextModel()).Reply("synthetic pending explicit answer");
            await f.Idle();
            Check(f.Model.History.Count == 2 && f.Source.Sends.Count == 2, "queued explicit work completes once");
        }
    }
}

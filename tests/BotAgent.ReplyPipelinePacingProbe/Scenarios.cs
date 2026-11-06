using BotAgent.Services.Qq;

namespace BotAgent.ReplyPipelinePacingProbe;

internal static partial class Scenarios
{
    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task ContinuationContext(Action<bool, string> check)
    {
        await using var f = new Fixture();
        await f.Inbound("part-a");
        await f.Until(() => f.Clock.Pending > 0, "initial quiet wait");
        await f.Advance(1000);
        await f.Inbound("part-b");
        await f.Advance(1000);
        await f.Inbound("part-c");
        await f.Advance(2999);
        check(f.Model.History.Count == 0, "ordinary continuation has not called the model before quiet deadline");
        await f.Advance(1);
        var call = await f.NextModel();
        foreach (var text in new[] { "part-a", "part-b", "part-c" })
            check(call.Context.Any(message => message.Text == text), "final model context contains " + text);
        call.Reply("merged-answer");
        await f.Source.NextSend();
        await f.Idle();
        check(f.Source.Sends.Count == 1, "three continuations produce one complete reply");
    }

    public static async Task FinalBoundaryStale(Action<bool, string> check)
    {
        await using var f = new Fixture();
        var prepared = Barrier();
        var release = Barrier();
        f.Source.OnPrepare = async count => { if (count == 1) { prepared.TrySetResult(); await release.Task; } };
        await f.Inbound("before-model");
        await f.Until(() => f.Clock.Pending > 0, "quiet wait");
        await f.Advance(3000);
        var stale = await f.NextModel();
        stale.Reply("must-not-send");
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Inbound("arrived-before-first-send");
        release.TrySetResult();
        // A permitted immediate rebuild can reacquire the slot before we observe it idle.
        await f.Until(() => f.ActiveCount == 0 || f.Model.History.Count == 2, "stale draft discarded or replaced");
        check(f.Source.Sends.Count == 0, "actual final transport boundary rejects stale draft before outward send");
        await f.Advance(3000);
        var fresh = await f.NextModel();
        check(fresh.Context.Any(message => message.Text == "arrived-before-first-send"), "replacement model context includes pre-send continuation");
        fresh.Reply("fresh-answer");
        await f.Source.NextSend();
        await f.Idle();
        check(f.Source.Sends.All(send => send.Text != "must-not-send"), "stale output never reaches transport");
    }

    public static async Task ExplicitPreemption(Action<bool, string> check)
    {
        await using var f = new Fixture();
        await f.Inbound("ordinary-generating");
        await f.Until(() => f.Clock.Pending > 0, "quiet wait");
        await f.Advance(3000);
        var ordinary = await f.NextModel();
        await f.Inbound("explicit-successor", speaker: 20002, direct: true);
        check(ordinary.Cancellation.IsCancellationRequested, "explicit request cancels same-conversation ordinary generation");
        ordinary.Reply("cancelled-provider-late-result");
        await f.Until(() => f.ActiveCount == 0, "cancelled generation releases slot");
        await f.Advance(3000);
        var explicitCall = await f.NextModel();
        check(explicitCall.Context.Any(message => message.Text == "explicit-successor"), "explicit successor survives preemption");
        explicitCall.Reply("explicit-answer");
        await f.Source.NextSend();
        await f.Idle();
        check(f.Source.Sends.Count == 1, "provider ignoring cancellation cannot send the cancelled ordinary result");
    }

    public static async Task ExplicitDoesNotPreemptEffects(Action<bool, string> check)
    {
        await using var f = new Fixture();
        var acknowledged = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Source.OnSend = count => count == 1 ? acknowledged.Task : Task.FromResult(new SendResult(true));
        await f.Inbound("ordinary-emitting");
        await f.Until(() => f.Clock.Pending > 0, "quiet wait");
        await f.Advance(3000);
        var ordinary = await f.NextModel();
        ordinary.Reply("already-emitting");
        await f.Source.NextSend();
        await f.Inbound("explicit-after-emission", direct: true);
        check(!ordinary.Cancellation.IsCancellationRequested, "explicit request does not cancel an acquired emission lease");
        check(f.Model.History.Count == 1, "explicit successor waits while transport ACK is outstanding");
        acknowledged.TrySetResult(new SendResult(true));
        await f.Until(() => f.ActiveCount == 0, "emission completes");
        await f.Advance(3000);
        var next = await f.NextModel();
        check(next.Context.Any(message => message.Text == "explicit-after-emission"), "explicit successor is preserved across completed effects");
        next.Reply("successor-answer");
        await f.Source.NextSend();
        await f.Idle();
        check(f.Source.Sends.Count == 2, "both already-committed reply and explicit successor complete once");
    }

    public static async Task SoftDeadline(Action<bool, string> check)
    {
        await using var f = new Fixture(settings => settings.EnableWebSearch = true);
        await f.Inbound("soft-part-0");
        await f.Until(() => f.Clock.Pending > 0, "quiet wait");
        for (var i = 1; i <= 5; i++)
        {
            await f.Advance(2000);
            await f.Inbound("soft-part-" + i);
        }
        await f.Advance(1999);
        check(f.Model.History.Count == 0, "continuous input does not evaluate before the 12-second soft deadline");
        await f.Inbound("soft-latest");
        var quietDeadline = f.Clock.TickCount + 3000;
        await f.Advance(1);
        var soft = await f.NextModel();
        check(!soft.ToolsEnabled, "actual soft-cap model request disables search tools");
        check(soft.Context.Any(message => message.Text == "soft-latest"), "soft-cap evaluation sees the newest sequence");
        // Releasing the global slot precedes timer registration. A generic Pending check can
        // observe the unrelated 150ms worker timer and advance past a not-yet-registered poll.
        var quietPoll = f.Clock.WaitForDelayRegistration(TimeSpan.FromMilliseconds(50));
        soft.Reply(null, "synthetic-search");
        await quietPoll.WaitAsync(TimeSpan.FromSeconds(10));
        check(f.ResearchCalls == 0 && f.Source.Sends.IsEmpty, "soft draft stays internal until actual quiet");
        var finalQuietPoll = f.Clock.WaitForDelayRegistration(TimeSpan.FromMilliseconds(1));
        await f.Advance(checked((int)(quietDeadline - f.Clock.TickCount - 1)));
        var registration = await finalQuietPoll.WaitAsync(TimeSpan.FromSeconds(10));
        check(registration.RegisteredAt == quietDeadline - 1 && registration.DueAt == quietDeadline
            && f.Clock.TickCount == quietDeadline - 1 && f.PacedCount == 1 && f.Source.Sends.IsEmpty,
            "soft draft remains pending one millisecond before the original absolute quiet deadline");
        await f.Advance(1);
        await f.Idle();
        check(f.Clock.TickCount == quietDeadline, "soft draft completes at the original quiet deadline without advancing beyond it");
        check(f.ResearchCalls == 0, "soft cap does not execute tool output");
        check(f.Source.Sends.Count == 0, "soft cap does not force outward text when the model chooses silence");
    }

    public static async Task QuietWaitReleasesGlobalSlot(Action<bool, string> check)
    {
        await using var f = new Fixture();
        f.Settings.PrivateContinuationWaitSeconds = 0;
        await f.Inbound("waiting-group");
        await f.Until(() => f.Clock.Pending > 0, "group quiet wait");
        await f.Inbound("other-conversation-explicit", direct: true, group: false);
        var priority = await f.Model.Next();
        check(f.Clock.TickCount == 0, "other conversation starts while group quiet-window time has not advanced");
        check(priority.Context.Any(message => message.Text == "other-conversation-explicit"), "single global slot belongs to ready explicit conversation");
        check(!priority.Context.Any(message => message.Text == "waiting-group"), "waiting group neither consumes the only global slot nor leaks context");
        priority.Reply("priority-answer");
        await f.Source.NextSend();
        await f.Until(() => f.ActiveCount == 0, "priority reply finishes");
    }

    public static async Task ModeOff(Action<bool, string> check)
    {
        await using (var ordinary = new Fixture())
        {
            await ordinary.Inbound("queued-ordinary");
            await ordinary.Until(() => ordinary.Clock.Pending > 0, "quiet wait");
            ordinary.Settings.ReplyPacingEnabled = false;
            ordinary.Pipeline.RefreshPacingPolicy();
            await ordinary.Advance(5000);
            await ordinary.Idle();
            check(ordinary.Model.History.Count == 0 && ordinary.Source.Sends.Count == 0, "mode off discards ordinary queued work");
        }
        await using var explicitFixture = new Fixture();
        await explicitFixture.Inbound("queued-explicit", direct: true);
        await explicitFixture.Until(() => explicitFixture.Clock.Pending > 0, "explicit continuation wait");
        explicitFixture.Settings.ReplyPacingEnabled = false;
        explicitFixture.Pipeline.RefreshPacingPolicy();
        var legacy = await explicitFixture.Model.Next();
        legacy.Reply("legacy-once");
        await explicitFixture.Source.NextSend();
        await explicitFixture.Idle();
        await explicitFixture.Advance(30000);
        check(explicitFixture.Model.History.Count == 1 && explicitFixture.Source.Sends.Count == 1, "mode off returns queued explicit work to legacy exactly once");
    }

    public static async Task FullAckCooldown(Action<bool, string> check)
    {
        await using var f = new Fixture();
        var ack = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Source.OnSend = count => count == 1 ? ack.Task : Task.FromResult(new SendResult(true));
        await f.Inbound("direct-start", direct: true);
        await f.Until(() => f.Clock.Pending > 0, "quiet wait");
        await f.Advance(3000);
        (await f.NextModel()).Reply("acknowledged-reply");
        await f.Source.NextSend();
        await f.Advance(10000);
        ack.TrySetResult(new SendResult(true));
        await f.Idle();
        await f.Inbound("during-cooldown");
        await Fixture.Drain();
        check(f.Model.History.Count == 1, "successful direct reply also starts ordinary-interjection cooldown");
        await f.Advance(29999);
        await f.Inbound("before-ack-based-deadline");
        await Fixture.Drain();
        check(f.Model.History.Count == 1, "cooldown measures from final acknowledgement rather than model start");
        await f.Advance(1);
        await f.Inbound("continuation-after-cooldown-expiry");
        await f.Advance(3000);
        check(f.Model.History.Count == 1, "cooldown-rejected continuous turn is not resurrected after expiry");
        await f.Inbound("after-ack-based-deadline");
        await f.Until(() => f.Clock.Pending > 0, "post-cooldown quiet wait");
        await f.Advance(3000);
        var next = await f.NextModel();
        check(next.Context.Any(message => message.Text == "after-ack-based-deadline"), "ordinary interjection resumes after ACK-based cooldown");
        next.Reply("after-cooldown-answer");
        await f.Source.NextSend();
        await f.Idle();
    }

    public static async Task FailedAckDoesNotCoolDown(Action<bool, string> check)
    {
        await using var f = new Fixture();
        f.Source.OnSend = _ => Task.FromResult(new SendResult(false));
        await f.Inbound("failed-first");
        await f.Until(() => f.Clock.Pending > 0, "quiet wait");
        await f.Advance(3000);
        (await f.NextModel()).Reply("failed-outward-reply");
        await f.Source.NextSend();
        await f.Idle();
        await f.Inbound("retry-ordinary");
        await f.Until(() => f.Clock.Pending > 0, "retry quiet wait");
        await f.Advance(3000);
        var retry = await f.NextModel();
        check(f.Model.History.Count == 2, "failed transport ACK does not consume ordinary cooldown");
        retry.Reply(null);
        await f.Idle();
    }
}

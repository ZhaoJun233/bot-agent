using BotAgent.Services.Qq;

namespace BotAgent.ReplyPipelinePacingProbe;

internal static partial class Scenarios
{
    public static async Task SameSpeakerPromotion(Action<bool, string> check)
    {
        await using var f = new Fixture();
        await f.Inbound("promotion-ordinary");
        await f.Until(() => f.Clock.Pending > 0, "promotion quiet wait");
        await f.Advance(3000);
        var ordinary = await f.NextModel();
        await f.Inbound("promotion-explicit", direct: true);
        ordinary.Reply("promotion-stale");
        await f.Advance(3000);
        var promoted = await f.NextModel();
        check(f.Source.Sends.IsEmpty, "same-speaker explicit promotion discards ordinary draft");
        check(promoted.Context.Any(message => message.Text == "promotion-explicit"), "promoted evaluation includes explicit continuation");
        promoted.Reply("promotion-answer");
        await f.Source.NextSend();
        await f.Idle();
        check(f.Source.Sends.Count == 1, "same-turn promotion produces only the explicit answer");
    }

    public static async Task ModeOffGenerating(Action<bool, string> check)
    {
        await using (var f = new Fixture())
        {
            await f.Inbound("off-ordinary-generating");
            await f.Until(() => f.Clock.Pending > 0, "ordinary quiet wait");
            await f.Advance(3000);
            var ordinary = await f.NextModel();
            f.Settings.ReplyPacingEnabled = false;
            f.Pipeline.RefreshPacingPolicy();
            check(ordinary.Cancellation.IsCancellationRequested, "mode off cancels uncommitted ordinary generation");
            ordinary.Reply("off-ordinary-late");
            await f.Idle();
            check(f.Source.Sends.IsEmpty && f.Model.History.Count == 1, "late ordinary result is not sent or replayed after mode off");
        }
        await using (var f = new Fixture())
        {
            await f.Inbound("off-explicit-generating", direct: true);
            await f.Until(() => f.Clock.Pending > 0, "explicit quiet wait");
            await f.Advance(3000);
            var old = await f.NextModel();
            f.Settings.ReplyPacingEnabled = false;
            f.Pipeline.RefreshPacingPolicy();
            check(old.Cancellation.IsCancellationRequested, "mode off cancels uncommitted explicit generation before handoff");
            old.Reply("off-explicit-late");
            var handoff = await f.Model.Next();
            handoff.Reply("off-explicit-handoff");
            await f.Source.NextSend();
            await f.Idle();
            check(f.Model.History.Count == 2 && f.Source.Sends.Count == 1 && f.Source.Sends.Single().Text == "off-explicit-handoff", "explicit generation hands off once without sending provider late result");
        }
    }

    public static async Task ModeOffEmitting(Action<bool, string> check)
    {
        await using var f = new Fixture();
        var ack = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Source.OnSend = _ => ack.Task;
        await f.Inbound("off-emitting");
        await f.Until(() => f.Clock.Pending > 0, "emitting quiet wait");
        await f.Advance(3000);
        var call = await f.NextModel();
        call.Reply("off-committed");
        await f.Source.NextSend();
        f.Settings.ReplyPacingEnabled = false;
        f.Pipeline.RefreshPacingPolicy();
        check(!call.Cancellation.IsCancellationRequested, "mode off preserves a committed emission lease");
        ack.TrySetResult(new SendResult(true));
        await f.Idle();
        check(f.Source.Sends.Count == 1 && f.Model.History.Count == 1, "committed emission finishes once without legacy replay");
    }

    public static async Task ShutdownLateResult(Action<bool, string> check)
    {
        await using var f = new Fixture();
        await f.Inbound("shutdown-explicit", direct: true);
        await f.Until(() => f.Clock.Pending > 0, "shutdown quiet wait");
        await f.Advance(3000);
        var call = await f.NextModel();
        f.Pipeline.Shutdown();
        check(call.Cancellation.IsCancellationRequested, "shutdown cancels in-flight provider token");
        call.Reply("shutdown-late-result");
        await f.Idle();
        check(f.Source.Sends.IsEmpty && f.Model.History.Count == 1, "shutdown never sends or hands off a late explicit result");
    }
}

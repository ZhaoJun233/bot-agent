using BotAgent.Services.Qq;

namespace BotAgent.ReplyPipelinePacingProbe;

internal static partial class Scenarios
{
    public static async Task LastSegmentCooldown(Action<bool, string> check)
    {
        await using var f = new Fixture(settings => settings.GroupContinuationWaitSeconds = 0);
        var finalAck = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Source.OnSend = index => index == 2 ? finalAck.Task : Task.FromResult(new SendResult(true));
        await f.Inbound("segmented-explicit", direct: true);
        var call = await f.Model.Next();
        call.Reply("synthetic-segment-one!\nsynthetic-segment-two!");
        await f.Source.NextSend();
        await f.Until(() => f.Source.Sends.Count == 2 || (f.ActiveCount == 0 && f.PacedCount == 0), "second segment or completed reply");
        check(f.Source.Sends.Count == 2, $"two transport segments observed (actual={f.Source.Sends.Count}, preparations={f.Source.Preparations}, lengths={string.Join(",", f.Source.Sends.Select(send => send.Text.Length))})");
        await f.Source.NextSend();
        await f.Advance(5000);
        check(f.Source.Sends.Count == 2 && f.ActiveCount == 1, "split reply remains active until final segment ACK");
        finalAck.TrySetResult(new SendResult(true));
        await f.Idle();
        await f.Advance(29999);
        await f.Inbound("before-final-segment-cooldown");
        await f.Idle();
        check(f.Model.History.Count == 1, "full reply cooldown starts at final segment ACK not first segment send");
        await f.Advance(1);
        await f.Inbound("after-final-segment-cooldown");
        var next = await f.Model.Next();
        next.Reply("post-segments");
        await f.Source.NextSend();
        await f.Idle();
        check(f.Model.History.Count == 2, "new ordinary turn is admitted at exact final-ACK cooldown boundary");
    }

    public static async Task PartialSegmentNoCooldown(Action<bool, string> check)
    {
        await using var f = new Fixture(settings => settings.GroupContinuationWaitSeconds = 0);
        f.Source.OnSend = index => Task.FromResult(new SendResult(index != 2));
        await f.Inbound("partial-explicit", direct: true);
        var call = await f.Model.Next();
        call.Reply("synthetic-partial-one!\nsynthetic-partial-two!");
        await f.Source.NextSend();
        await f.Until(() => f.Source.Sends.Count == 2 || (f.ActiveCount == 0 && f.PacedCount == 0), "second segment or completed reply");
        check(f.Source.Sends.Count == 2, $"two transport segments observed (actual={f.Source.Sends.Count}, preparations={f.Source.Preparations}, lengths={string.Join(",", f.Source.Sends.Select(send => send.Text.Length))})");
        await f.Source.NextSend();
        await f.Idle();
        var failedAt = f.Clock.TickCount;
        await f.Inbound("ordinary-after-partial");
        var next = await f.Model.Next();
        check(f.Clock.TickCount == failedAt && f.Model.History.Count == 2, "partially acknowledged reply does not start complete-chat cooldown");
        next.Reply("partial-recovery");
        await f.Source.NextSend();
        await f.Idle();
        check(f.Source.Sends.Count == 3, "ordinary recovery sends once after partial transport failure");
    }
}

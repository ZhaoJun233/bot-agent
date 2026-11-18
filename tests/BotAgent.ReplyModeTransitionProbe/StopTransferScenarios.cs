using System.Reflection;
using BotAgent.Domain.Conversation;
using BotAgent.ReplyPipelinePacingProbe;
using BotAgent.Services;
using BotAgent.Services.OneBot;
using BotAgent.Services.Reply;

namespace BotAgent.ReplyModeTransitionProbe;

internal static class StopTransferScenarios
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task ConcurrentAdmission()
    {
        await using var f = new Fixture();
        f.Pipeline.RefreshPacingPolicy();
        var conversation = Seed(f, 10001);
        var epoch = CaptureEpoch(f, conversation);
        var other = Seed(f, 10003);
        var otherEpoch = CaptureEpoch(f, other);
        var pendingGate = Field(f, "_pendingGate");
        var pacingGate = Field(f, "_pacingSync");
        Exception? failure = null;
        using var stopEntered = new ManualResetEventSlim();
        using var ingressEntered = new ManualResetEventSlim();
        var stop = new Thread(() =>
        {
            stopEntered.Set();
            try { f.Pipeline.ForgetSession(conversation.SourceKey); }
            catch (Exception error) { failure = error; }
        });
        var ingress = new Thread(() =>
        {
            ingressEntered.Set();
            try { typeof(ReplyPipeline).GetMethod("RequestReplyInEpoch", Flags)!.Invoke(f.Pipeline, [conversation, (long?)1, false, true, epoch]); }
            catch (Exception error) { failure = error; }
        });
        Monitor.Enter(pendingGate);
        try
        {
            stop.Start();
            WaitBlocked(stop, stopEntered);
            if (Monitor.TryEnter(pacingGate, TimeSpan.FromMilliseconds(50)))
            {
                Monitor.Exit(pacingGate);
                throw new InvalidOperationException("Stop must retain pacing ownership while waiting for pending admission");
            }
            ingress.Start();
            WaitBlocked(ingress, ingressEntered);
        }
        finally { Monitor.Exit(pendingGate); }
        if (!stop.Join(TimeSpan.FromSeconds(5)) || !ingress.Join(TimeSpan.FromSeconds(5)) || failure is not null)
            throw new InvalidOperationException("concurrent Stop/admission did not complete", failure);
        if (ReferenceEquals(epoch, CaptureEpoch(f, conversation)))
            throw new InvalidOperationException("Stop did not retire the original admission epoch");
        await CheckFreshInput(f, other, otherEpoch);
    }

    internal static async Task DetachedDispatch()
    {
        await using var f = new Fixture();
        f.Pipeline.RefreshPacingPolicy();
        var conversation = Seed(f, 10001);
        var epoch = CaptureEpoch(f, conversation);
        var other = Seed(f, 10003);
        var otherEpoch = CaptureEpoch(f, other);
        var owner = typeof(ReplyPipeline).GetMethod("CapturePacedOwner", Flags)!.Invoke(f.Pipeline, [conversation, null, epoch]);
        object dispatch;
        lock (Field(f, "_pacingSync"))
        {
            var coordinator = (ReplyPacingCoordinator)Field(f, "_pacingCoordinator");
            var observation = coordinator.Observe(conversation.SourceKey, 10002, 1, 1, true, true,
                ReplyPacingOptions.Default, owner);
            dispatch = typeof(ReplyPipeline).GetMethod("TrackPacedObservation", Flags)!.Invoke(f.Pipeline, [observation, owner])!;
        }
        // Pause after real admission but before dispatch, then invalidate that captured owner.
        f.Pipeline.ForgetSession(conversation.SourceKey);
        typeof(ReplyPipeline).GetMethod("DispatchPacedWork", Flags)!.Invoke(f.Pipeline, [dispatch]);
        await f.Idle();
        if (ReferenceEquals(epoch, CaptureEpoch(f, conversation)))
            throw new InvalidOperationException("Stop did not retire a detached dispatch epoch");
        await CheckFreshInput(f, other, otherEpoch);
    }

    private static void WaitBlocked(Thread thread, ManualResetEventSlim entered)
    {
        if (!entered.Wait(TimeSpan.FromSeconds(2)) || !SpinWait.SpinUntil(
            () => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("fixture thread did not reach the held admission boundary");
    }

    private static async Task CheckFreshInput(Fixture f, BotConversation other, object otherEpoch)
    {
        await f.Idle();
        if (!ReferenceEquals(otherEpoch, CaptureEpoch(f, other)))
            throw new InvalidOperationException("Stop must not retire another source key");
        if (f.Model.History.Count != 0 || f.Source.Sends.Count != 0 || f.Pipeline.QueuedReplies != 0)
            throw new InvalidOperationException("stopped work must produce no model call or send");
        await f.Inbound("synthetic new request after stop", direct: true);
        await TransitionClock.AdvanceRegistered(f, 3000);
        (await f.Model.Next()).Reply("synthetic new reply after stop");
        await f.Idle();
        if (f.Model.History.Count != 1 || f.Source.Sends.Count != 1)
            throw new InvalidOperationException("new input after Stop must remain eligible");
    }

    private static BotConversation Seed(Fixture f, long target)
    {
        var input = new QqChatMessage(1, true, 10002, target, "synthetic", "synthetic explicit request", f.Clock.Now, true);
        var conversation = f.Registry.GetOrCreate(input) ?? throw new InvalidOperationException("missing conversation");
        conversation.Append(new ChatMessage { Role = MessageRole.Peer, SenderId = 10002, QqMessageId = 1,
            DirectToBot = true, MentionedBot = true, Text = "synthetic explicit request", Timestamp = f.Clock.Now });
        _ = CaptureEpoch(f, conversation);
        return conversation;
    }

    private static object CaptureEpoch(Fixture f, BotConversation conversation)
        => typeof(ReplyPipeline).GetMethod("CaptureReplyEpoch", Flags)!.Invoke(f.Pipeline, [conversation])!;
    private static object Field(Fixture f, string name) => typeof(ReplyPipeline).GetField(name, Flags)!.GetValue(f.Pipeline)!;
}

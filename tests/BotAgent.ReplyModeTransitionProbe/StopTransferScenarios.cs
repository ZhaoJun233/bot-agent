using System.Collections.Concurrent;
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

    internal static async Task QueueMigration()
    {
        await using var f = new Fixture();
        f.Settings.ReplyPacingEnabled = false;
        f.Pipeline.RefreshPacingPolicy();
        var input = new QqChatMessage(1, true, 10002, 10001, "synthetic", "synthetic explicit request", f.Clock.Now, true);
        var conversation = f.Registry.GetOrCreate(input) ?? throw new InvalidOperationException("missing conversation");
        conversation.Append(new ChatMessage { Role = MessageRole.Peer, SenderId = 10002, QqMessageId = 1, DirectToBot = true, MentionedBot = true, Text = "synthetic explicit request", Timestamp = f.Clock.Now });
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Field(string name) => typeof(ReplyPipeline).GetField(name, flags)!.GetValue(f.Pipeline)!;
        var captureEpoch = typeof(ReplyPipeline).GetMethod("CaptureReplyEpoch", flags)!;
        var epoch = captureEpoch.Invoke(f.Pipeline, [conversation]);
        var other = Seed(f, 10003);
        var otherEpoch = CaptureEpoch(f, other);
        var queue = new TenantReplyQueue();
        queue.Enqueue(new PendingReply(1, false, true, null, PendingReplyIntent.Explicit), 5);
        ((ConcurrentDictionary<string, TenantReplyQueue>)Field("_pendingReplies"))[conversation.SourceKey] = queue;
        ((ConcurrentDictionary<string, BotConversation>)Field("_pendingConversations"))[conversation.SourceKey] = conversation;
        var deferredGate = Field("_deferredGate");
        var pacingGate = Field("_pacingSync");
        Exception? stopFailure = null;
        using var entered = new ManualResetEventSlim();
        var stop = new Thread(() => { entered.Set(); try { f.Pipeline.ForgetSession(conversation.SourceKey); } catch (Exception ex) { stopFailure = ex; } });
        Monitor.Enter(deferredGate);
        var pacingHeld = false;
        var deferredHeld = true;
        bool completed;
        bool epochRetired;
        int admitted;
        try
        {
            stop.Start();
            if (!entered.Wait(TimeSpan.FromSeconds(2)) || !SpinWait.SpinUntil(() => (stop.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("setup: stop did not block at deferred gate");
            // Stop has completed DropPacedPending and now waits for _deferredGate. Hold pacing
            // while enabling, so the new asynchronous worker cannot claim _activeConversations.
            if (!Monitor.TryEnter(pacingGate, TimeSpan.FromSeconds(2)))
            {
                // An atomic Stop may retain pacing ownership while waiting for deferred.
                // Unblock Stop instead of deadlocking the corrected implementation.
                Monitor.Exit(deferredGate);
                deferredHeld = false;
                if (!stop.Join(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("setup: atomic stop did not finish");
                Monitor.Enter(pacingGate);
            }
            pacingHeld = true;
            f.Settings.ReplyPacingEnabled = true;
            f.Pipeline.RefreshPacingPolicy();
            if (deferredHeld) { Monitor.Exit(deferredGate); deferredHeld = false; }
            completed = stop.Join(TimeSpan.FromSeconds(2));
            if (!completed || stopFailure is not null) throw new InvalidOperationException("setup: stop failed to finish", stopFailure);
            epochRetired = !ReferenceEquals(epoch, captureEpoch.Invoke(f.Pipeline, [conversation]));
            var works = Field("_pacedWorks");
            admitted = (int)works.GetType().GetProperty("Count")!.GetValue(works)!;
        }
        finally
        {
            if (deferredHeld) Monitor.Exit(deferredGate);
            if (pacingHeld) Monitor.Exit(pacingGate);
            stop.Join(TimeSpan.FromSeconds(2));
        }
        if (!epochRetired || admitted != 0) throw new InvalidOperationException("Stop lost queued explicit ownership during OFF-to-ON transfer");
        await CheckFreshInput(f, other, otherEpoch);
    }

    internal static async Task DetachedHandoff()
    {
        await using var f = new Fixture();
        f.Settings.ReplyPacingEnabled = false;
        f.Pipeline.RefreshPacingPolicy();
        var conversation = Seed(f, 10001);
        var epoch = CaptureEpoch(f, conversation);
        var other = Seed(f, 10003);
        var otherEpoch = CaptureEpoch(f, other);
        var queue = new TenantReplyQueue();
        queue.Enqueue(new PendingReply(1, false, true, null, PendingReplyIntent.Explicit), 5);
        ((ConcurrentDictionary<string, TenantReplyQueue>)Field(f, "_pendingReplies"))[conversation.SourceKey] = queue;
        ((ConcurrentDictionary<string, BotConversation>)Field(f, "_pendingConversations"))[conversation.SourceKey] = conversation;
        object transition;
        lock (Field(f, "_pacingSync"))
            transition = typeof(ReplyPipeline).GetMethod("RetireLegacyForPacing", Flags)!.Invoke(f.Pipeline, null)!;
        if (f.Pipeline.QueuedReplies != 0) throw new InvalidOperationException("setup: transition did not detach queued ownership");
        // Hold the real handoff object between retirement and dispatch, with no queue registration.
        f.Pipeline.ForgetSession(conversation.SourceKey);
        f.Settings.ReplyPacingEnabled = true;
        f.Pipeline.RefreshPacingPolicy();
        typeof(ReplyPipeline).GetMethod("DispatchLegacyTransition", Flags)!.Invoke(f.Pipeline, [transition]);
        if (ReferenceEquals(epoch, CaptureEpoch(f, conversation)) || f.PacedCount != 0)
            throw new InvalidOperationException("Stop lost detached explicit handoff ownership");
        await CheckFreshInput(f, other, otherEpoch);
    }

    private static async Task CheckFreshInput(Fixture f, BotConversation other, object otherEpoch)
    {
        if (!ReferenceEquals(otherEpoch, CaptureEpoch(f, other)))
            throw new InvalidOperationException("Stop must not retire another source key");
        if (f.Model.History.Count != 0 || f.Source.Sends.Count != 0 || f.Pipeline.QueuedReplies != 0)
            throw new InvalidOperationException("stopped work must produce no model call or send");
        await f.Inbound("synthetic new request after stop", direct: true);
        await TransitionClock.AdvanceRegistered(f, 3000);
        var fresh = await f.Model.Next();
        fresh.Reply("synthetic new reply after stop");
        await f.Idle();
        if (f.Model.History.Count != 1 || f.Source.Sends.Count != 1)
            throw new InvalidOperationException("a new request after Stop must remain eligible");
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

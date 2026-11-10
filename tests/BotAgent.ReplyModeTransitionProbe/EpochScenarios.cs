using System.Reflection;
using BotAgent.Domain.Conversation;
using BotAgent.ReplyPipelinePacingProbe;
using BotAgent.Services;
using BotAgent.Services.Qq;
using BotAgent.Services.OneBot;
using BotAgent.Services.Conversations;
using BotAgent.Services.Reply;

namespace BotAgent.ReplyModeTransitionProbe;

internal static class EpochScenarios
{
    internal static async Task StoppedDeferred()
    {
        await using var f = new Fixture();
        var incoming = new QqChatMessage(1, true, 10002, 10001, "synthetic", "synthetic deferred request", f.Clock.Now, true);
        var conversation = f.Registry.GetOrCreate(incoming) ?? throw new InvalidOperationException("fixture conversation missing");
        conversation.Append(new ChatMessage { Role = MessageRole.Peer, SenderId = 10002, QqMessageId = 1,
            DirectToBot = true, MentionedBot = true, Text = "synthetic deferred request", Timestamp = f.Clock.Now });
        f.Pipeline.RefreshPacingPolicy();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var captureEpoch = typeof(ReplyPipeline).GetMethod("CaptureReplyEpoch", flags)!;
        var epoch = captureEpoch.Invoke(f.Pipeline, [conversation])!;
        var flushing = (Dictionary<object, BotConversation>)typeof(ReplyPipeline).GetField("_flushingDeferred", flags)!.GetValue(f.Pipeline)!;
        var flushOwner = new object();
        // Publish the same ownership marker as FlushDeferred, without scheduling nondeterministic threads.
        flushing.Add(flushOwner, conversation);
        var hookField = typeof(ReplyPipeline).GetField("_hooks", flags)!;
        var hooks = (ReplyHooks)hookField.GetValue(f.Pipeline)!;
        var stopped = false;
        hookField.SetValue(f.Pipeline, hooks with { IsDisposed = () =>
        {
            if (!stopped) { stopped = true; f.Pipeline.ForgetSession(conversation.SourceKey); }
            return false;
        } }); // RefreshPacingPolicy calls this after RequestReplyInEpoch's initial epoch check.
        try
        {
            typeof(ReplyPipeline).GetMethod("RequestReplyInEpoch", flags)!.Invoke(f.Pipeline,
                [conversation, (long?)1, false, true, true, epoch]);
        }
        finally
        {
            hookField.SetValue(f.Pipeline, hooks);
            flushing.Remove(flushOwner);
        }
        var epochRetired = !ReferenceEquals(epoch, captureEpoch.Invoke(f.Pipeline, [conversation]));
        var readmitted = f.PacedCount;
        if (readmitted > 0) await TransitionClock.AdvanceRegistered(f, 3000);
        if (!stopped || !epochRetired)
            throw new InvalidOperationException("invalid interleaving: stop must retire original epoch");
        if (readmitted != 0 || f.Model.History.Count != 0 || f.Source.Sends.Count != 0)
            throw new InvalidOperationException("Stopped deferred request was readmitted under a fresh epoch");

        // A genuinely new input after Stop must still use the new epoch and produce one reply.
        await f.Inbound("synthetic fresh input", direct: true);
        await TransitionClock.AdvanceRegistered(f, 3000);
        var fresh = await f.Model.Next();
        fresh.Reply("synthetic fresh reply");
        await f.Idle();
        if (f.Source.Sends.Count != 1 || f.Model.History.Count != 1)
            throw new InvalidOperationException("a new request after Stop must remain eligible");
    }
}

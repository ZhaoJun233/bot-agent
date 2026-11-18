using System.Reflection;
using BotAgent.Services.Net;
using BotAgent.Services.Reply;

namespace BotAgent.ReplyPipelinePacingProbe;

internal static partial class Scenarios
{
    public static async Task OtherSpeakerContext(Action<bool, string> check)
    {
        await using var f = new Fixture(s => s.GroupInterjectionProbability = 0);
        await f.Inbound("synthetic explicit speaker", direct: true);
        await f.Advance(1000);
        await f.Inbound("synthetic other speaker first", speaker: 20002);
        await f.Advance(1000);
        await f.Inbound("synthetic other speaker continuation", speaker: 20002);
        await f.Advance(1000);
        var call = await f.NextModel();
        check(f.Clock.TickCount <= 3050, "other-speaker continuation cannot postpone the explicit speaker's quiet deadline");
        check(call.Context.Any(message => message.Text == "synthetic other speaker first")
            && call.Context.Any(message => message.Text == "synthetic other speaker continuation"),
            "a ready speaker sees other speakers already present when generation begins");
        call.Reply("synthetic explicit contextual answer");
        await f.Source.NextSend();
        await f.Until(() => f.ActiveCount == 0 && f.PacedCount <= 1, "explicit turn retires");
        await f.Advance(5000);
        for (var poll = 0; poll < 20 && f.PacedCount > 0; poll++) await f.Advance(50);
        await f.Idle();
        check(f.Model.History.Count == 1 && f.Source.Sends.Count == 1,
            "context contribution does not admit a zero-probability ordinary turn");
    }

    public static async Task PriorReplyContext(Action<bool, string> check)
    {
        await using var f = new Fixture(s => s.GroupContinuationWaitSeconds = 0);
        await f.Inbound("synthetic first request", direct: true);
        var first = await f.NextModel();
        await f.Inbound("synthetic queued request", direct: true, speaker: 20002);
        check(f.Model.History.Count == 1, "a queued explicit request never overlaps the first model call");
        first.Reply("synthetic prior completed reply");
        await f.Source.NextSend();
        await f.Until(() => f.ActiveCount == 0 || f.Model.History.Count == 2, "first generation finishes");
        var second = await f.NextModel();
        check(second.Context.Any(message => message.Text == "synthetic prior completed reply"),
            "a queued generation sees the preceding completed bot reply");
        second.Reply("synthetic second contextual answer");
        await f.Source.NextSend(); await f.Idle();
        check(f.Model.History.Count == 2 && f.Source.Sends.Count == 2,
            "both explicit requests complete once without catch-up evaluations");
    }

    public static async Task AcceptedSnapshotContext(Action<bool, string> check)
    {
        await using var f = new Fixture(s =>
        {
            s.GroupContinuationWaitSeconds = 0;
            s.EnableWebSearch = true;
            s.MaxAgentSteps = 2;
        });
        var lookup = new TaskCompletionSource<WebSearchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Research.OnSearch = _ => lookup.Task;
        await f.Inbound("synthetic original accepted input", direct: true);
        var initial = await f.NextModel();
        initial.Reply(null, search: "synthetic inline lookup");
        await f.Until(() => f.ResearchCalls == 1, "inline effect accepted");
        await f.Inbound("synthetic same-speaker followup", direct: true);
        lookup.TrySetResult(ControlledResearch.Result());
        var continuation = await f.NextModel();
        check(continuation.Context.Any(message => message.Text == "synthetic original accepted input") &&
            continuation.Context.All(message => message.Text != "synthetic same-speaker followup"),
            "an accepted inline effect retains its original same-speaker context");
        continuation.Reply("synthetic accepted snapshot answer");
        await f.Source.NextSend();
        await f.Until(() => f.ActiveCount == 0 || f.Model.History.Count == 3, "accepted snapshot finishes");
        var successor = await f.NextModel();
        check(successor.Context.Any(message => message.Text == "synthetic same-speaker followup") &&
            successor.Context.Any(message => message.Text == "synthetic accepted snapshot answer"),
            "the successor receives both its followup and the completed accepted answer");
        successor.Reply("synthetic successor answer");
        await f.Source.NextSend(); await f.Idle();
        check(f.ResearchCalls == 1 && f.Model.History.Count == 3 && f.Source.Sends.Count == 2,
            "context isolation does not replay an accepted tool effect");
    }

    public static async Task IndependentResultContext(Action<bool, string> check)
    {
        await using var f = new Fixture(s => s.GroupContinuationWaitSeconds = 0);
        var conversation = await f.Inbound("synthetic independent original input", direct: true);
        var first = await f.NextModel();
        typeof(ReplyPipeline).GetMethod("RequestAcceptedTaskReply", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(f.Pipeline, [conversation, f.Settings.Snapshot()]);
        f.Settings.GroupContinuationWaitSeconds = 1;
        await f.Inbound("synthetic later independent peer", direct: true, speaker: 20002);
        first.Reply("synthetic completed before independent result");
        await f.Source.NextSend();
        await f.Until(() => f.ActiveCount == 0 || f.Model.History.Count == 2, "independent predecessor finishes");
        var result = await f.NextModel();
        check(result.Context.Any(message => message.Text == "synthetic independent original input") &&
            result.Context.Any(message => message.Text == "synthetic completed before independent result") &&
            result.Context.All(message => message.Text != "synthetic later independent peer"),
            "an independent result keeps its human sequence boundary and preceding self reply");
        result.Reply("synthetic independent result answer");
        await f.Source.NextSend();
        await f.Until(() => f.ActiveCount == 0 || f.Model.History.Count == 3, "independent result finishes");
        await f.Advance(1000);
        var successor = await f.NextModel();
        check(successor.Context.Any(message => message.Text == "synthetic later independent peer") &&
            successor.Context.Any(message => message.Text == "synthetic independent result answer"),
            "later human work remains separate from the accepted callback");
        successor.Reply("synthetic later independent answer");
        await f.Source.NextSend(); await f.Idle();
        check(f.Model.History.Count == 3 && f.Source.Sends.Count == 3,
            "independent result and later human work each complete once");
    }
}

using BotAgent.Domain.Ops;

namespace BotAgent.ReplyPipelinePacingProbe;

internal static partial class Scenarios
{
    public static async Task CompletedTrace(Action<bool, string> check)
    {
        foreach (var reply in new string?[] { "synthetic completed trace", null })
        {
            await using var f = new Fixture(s => s.GroupContinuationWaitSeconds = 0);
            await f.Inbound("synthetic trace input", direct: true);
            var call = await f.NextModel();
            check(f.Traces.ActiveCount == 1 && f.Traces.StartedTotal == 1,
                "actual model generation opens exactly one shape-only trace");
            call.Reply(reply);
            if (reply is not null) await f.Source.NextSend();
            await f.Idle();
            var traces = f.Traces.Recent();
            check(f.Traces.ActiveCount == 0 && f.Traces.CompletedTotal == 1 && traces.Count == 1,
                "completed reply or silence closes its trace once");
            var trace = traces.Single();
            check(trace.StartedAt >= f.Clock.Now.AddSeconds(-1) && trace.Outcome == "done"
                && trace.Nodes.Any(node => node.Kind == TurnNodeKind.Model),
                "completed trace retains clock, outcome and model node for the public projection");
            check(trace.Nodes.All(node => node.ReasonCode?.Contains("synthetic", StringComparison.Ordinal) != true),
                "trace reason codes never contain synthetic input or output text");
        }
    }

    public static async Task CancelledTrace(Action<bool, string> check)
    {
        await using var f = new Fixture(s => s.GroupContinuationWaitSeconds = 0);
        await f.Inbound("synthetic cancelled trace", direct: true);
        var call = await f.NextModel();
        check(f.Traces.ActiveCount == 1, "cancelled generation owns an active trace");
        f.Pipeline.Shutdown();
        call.Reply("synthetic late trace result");
        await f.Idle();
        check(f.Source.Sends.IsEmpty && f.Traces.ActiveCount == 0 && f.Traces.CompletedTotal == 1,
            "late provider completion clears the cancelled trace without sending");
    }
}

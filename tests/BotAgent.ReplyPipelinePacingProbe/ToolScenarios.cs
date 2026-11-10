using BotAgent.Services.Net;

namespace BotAgent.ReplyPipelinePacingProbe;

internal static partial class Scenarios
{
    public static async Task StaleToolDraft(Action<bool, string> check)
    {
        await using var f = new Fixture(settings => settings.EnableWebSearch = true);
        var result = new TaskCompletionSource<WebSearchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Research.OnSearch = _ => result.Task;
        await f.Inbound("tool-initial");
        await f.Until(() => f.Clock.Pending > 0, "tool quiet wait");
        await f.Advance(3000);
        var stale = await f.NextModel();
        await f.Inbound("tool-latest");
        stale.Reply("stale-tool-answer", "stale-query");
        await f.Advance(3000);
        var replacement = await f.NextModel();
        check(f.ResearchCalls == 0 && f.Source.Sends.IsEmpty, "stale model output starts neither tool nor transport effect");
        check(replacement.Context.Any(message => message.Text == "tool-latest"), "tool replacement consumes latest context");
        check(replacement.ToolsEnabled, "fresh evaluation positively enables search tools");
        replacement.Reply("fresh-tool-answer", "fresh-query");
        await f.Research.NextSearch();
        check(f.ResearchCalls == 1, "fresh replacement positively reaches actual research port once");
        await f.Source.NextSend();
        await f.Idle();
        result.TrySetResult(null);
        var followup = await f.Model.Next();
        followup.Reply("fresh-tool-complete");
        await f.Source.NextSend();
        await f.Idle();
        check(f.Source.Sends.All(send => send.Text != "stale-tool-answer"), "stale tool draft never escapes through a later callback");
    }

    public static async Task AcceptedResultBypass(Action<bool, string> check)
    {
        await using var f = new Fixture(settings => settings.EnableWebSearch = true);
        f.Settings.GroupInterjectionProbability = 0;
        var result = new TaskCompletionSource<WebSearchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Research.OnSearch = _ => result.Task;
        await f.Inbound("accepted-request", direct: true);
        await f.Until(() => f.Clock.Pending > 0, "accepted quiet wait");
        await f.Advance(3000);
        var request = await f.NextModel();
        request.Reply("accepted-start", "accepted-query");
        await f.Research.NextSearch();
        await f.Source.NextSend();
        await f.Idle();
        var completedAt = f.Clock.TickCount;
        result.TrySetResult(ControlledResearch.Result());
        var completion = await f.Model.Next();
        check(f.Clock.TickCount == completedAt, "accepted result bypasses human quiet wait during active reply cooldown");
        check(completion.SearchText == "synthetic-research-result", "actual research callback feeds completion context");
        completion.Reply("accepted-complete");
        await f.Source.NextSend();
        await f.Idle();
        check(f.ResearchCalls == 1 && f.Model.History.Count == 2 && f.Source.Sends.Count == 2, "accepted result completes once despite zero ordinary probability and active cooldown");
    }

    public static async Task InlineEffectsProtected(Action<bool, string> check)
    {
        await using var f = new Fixture(settings => settings.EnableWebSearch = true);
        f.Settings.MaxAgentSteps = 2;
        var result = new TaskCompletionSource<WebSearchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Research.OnSearch = _ => result.Task;
        await f.Inbound("inline-ordinary");
        await f.Until(() => f.Clock.Pending > 0, "inline quiet wait");
        await f.Advance(3000);
        var ordinary = await f.NextModel();
        ordinary.Reply(null, "inline-query");
        var effectToken = await f.Research.NextSearch();
        await f.Inbound("inline-explicit", direct: true, speaker: 20002);
        await f.Advance(3000);
        check(!ordinary.Cancellation.IsCancellationRequested && !effectToken.IsCancellationRequested, "explicit request cannot cancel already-started inline tool effect");
        check(f.Model.History.Count == 1, "explicit successor waits behind in-flight inline effect");
        result.TrySetResult(ControlledResearch.Result());
        var toolAnswer = await f.Model.Next();
        toolAnswer.Reply("inline-complete");
        await f.Source.NextSend();
        await f.Until(() => f.ActiveCount == 0 && f.PacedCount == 1, "inline predecessor retires");
        var explicitCall = await f.NextModel();
        explicitCall.Reply("inline-explicit-complete");
        await f.Source.NextSend();
        await f.Idle();
        check(f.ResearchCalls == 1 && f.Source.Sends.Count == 2, "inline effect and explicit successor each finish once");
    }
}

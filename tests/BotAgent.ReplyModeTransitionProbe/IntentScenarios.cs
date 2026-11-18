using BotAgent.ReplyPipelinePacingProbe;

namespace BotAgent.ReplyModeTransitionProbe;

internal static class IntentScenarios
{
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    public static async Task DuplicateOpportunity()
    {
        await using var f = new Fixture();
        var conversation = await f.Inbound("synthetic ordinary input");
        await TransitionClock.AdvanceRegistered(f, 3000);
        (await f.Model.Next()).Reply(null); await f.Idle();
        f.Settings.GroupInterjectionProbability = 0;
        for (var i = 0; i < 5; i++)
        {
            f.Pipeline.RequestReply(conversation, null);
            f.Pipeline.IdleFallbackTick();
        }
        await f.Advance(60000); await f.Idle();
        Check(f.Source.Sends.IsEmpty && f.Model.History.Count == 1,
            "maintenance and repeated requests cannot replay a silent ordinary opportunity");
    }

    public static async Task NullOrdinary(bool proactive)
    {
        await using var f = new Fixture(s => s.GroupInterjectionProbability = 0);
        var conversation = await f.Inbound("synthetic rejected ordinary input");
        for (var i = 0; i < 5; i++) f.Pipeline.RequestReply(conversation, null, proactive: proactive);
        await f.Advance(60000); await f.Idle();
        Check(f.Source.Sends.IsEmpty && f.Model.History.IsEmpty && f.PacedCount == 0,
            "null ordinary/proactive requests cannot manufacture accepted-result intent");
    }

    public static async Task NullExplicit()
    {
        await using var f = new Fixture(s => s.GroupInterjectionProbability = 0);
        var conversation = await f.Inbound("synthetic initially ordinary input");
        f.Pipeline.RequestReply(conversation, null, directInWindow: true);
        await TransitionClock.AdvanceRegistered(f, 2999);
        Check(f.Model.History.IsEmpty, "null direct request is human intent, not an immediately ready result");
        await TransitionClock.AdvanceRegistered(f, 1);
        (await f.Model.Next()).Reply("synthetic direct answer"); await f.Idle();
        Check(f.Source.Sends.Count == 1 && f.Model.History.Count == 1, "null explicit promotion sends exactly once");
    }

    public static async Task PrivateExplicit()
    {
        await using var f = new Fixture(s => s.GroupInterjectionProbability = 0);
        await f.Inbound("synthetic private input", group: false);
        f.Settings.PrivateContinuationWaitSeconds = 10;
        f.Pipeline.RefreshPacingPolicy();
        await TransitionClock.AdvanceRegistered(f, 1499);
        Check(f.Model.History.IsEmpty, "private explicit request honors its admitted 1.5-second quiet");
        await TransitionClock.AdvanceRegistered(f, 1);
        (await f.Model.Next()).Reply("synthetic private answer"); await f.Idle();
        Check(f.Source.Sends.Count == 1 && f.Model.History.Count == 1, "private request is never replayed");
    }

    public static async Task AcceptedResult()
    {
        await using var f = new Fixture(s => { s.MaxAgentSteps = 1; s.EnableWebSearch = true; });
        var release = new TaskCompletionSource<BotAgent.Services.Net.WebSearchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Research.OnSearch = _ => release.Task;
        await f.Inbound("synthetic research request", direct: true);
        await TransitionClock.AdvanceRegistered(f, 3000);
        (await f.Model.Next()).Reply(null, "synthetic detached query"); await f.Research.NextSearch();
        await f.Until(() => f.ActiveCount == 0, "detached search released generation");
        f.Settings.GroupInterjectionProbability = 0;
        f.Settings.GroupContinuationWaitSeconds = 30;
        f.Pipeline.RefreshPacingPolicy();
        var completedAt = f.Clock.TickCount;
        release.SetResult(ControlledResearch.Result());
        var result = await f.Model.Next();
        f.Pipeline.RefreshPacingPolicy();
        result.Reply("synthetic accepted result"); await f.Idle();
        Check(f.Model.History.Count == 2 && f.ResearchCalls == 1 && f.Source.Sends.Count == 1,
            "accepted result follows up exactly once without human lottery");
        Check(f.Clock.TickCount == completedAt, "accepted result does not acquire a new human quiet window");
    }
}

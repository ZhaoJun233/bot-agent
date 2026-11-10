using BotAgent.ReplyPipelinePacingProbe;

namespace BotAgent.ReplyModeTransitionProbe;

internal static class IntentScenarios
{
    private static void Toggle(Fixture f, bool enabled)
    {
        f.Settings.ReplyPacingEnabled = enabled;
        f.Pipeline.RefreshPacingPolicy();
    }
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    public static async Task OrdinaryCatchUp()
    {
        await using var f = new Fixture(s => s.ReplyPacingEnabled = false);
        var conversation = await f.Inbound("first ordinary input"); var first = await f.Model.Next();
        f.Settings.GroupCooldownSeconds = 30;
        await f.Inbound("ordinary deferred input");
        var last = conversation.Messages.Last(message => message.Role == BotAgent.Domain.Conversation.MessageRole.Peer);
        f.Pipeline.RequestReply(conversation, last.QqMessageId);
        first.Reply(null);
        await f.Until(() => f.ActiveCount == 0 && f.Clock.Pending > 0, "deferred scheduler registered");
        await TransitionClock.AdvanceRegistered(f, 150);
        ModelCall catchup;
        try { catchup = await f.Model.Next(); }
        catch (TimeoutException) { throw new InvalidOperationException($"catch-up model missing: active={f.ActiveCount} paced={f.PacedCount} models={f.Model.History.Count}"); }
        f.Settings.GroupInterjectionProbability = 0; Toggle(f, true); catchup.Reply("synthetic stale catch-up");
        await f.Until(() => f.ActiveCount == 0, "ordinary catch-up retired"); await f.Advance(4000);
        Check(f.Source.Sends.Count == 0 && f.Model.History.Count == 2 && f.PacedCount == 0,
            "queue durability must not transfer ordinary catch-up as explicit");
    }

    public static async Task NullCatchUp(bool proactive)
    {
        await using var f = new Fixture(s => s.ReplyPacingEnabled = false);
        var conversation = await f.Inbound("synthetic ordinary input"); var first = await f.Model.Next();
        f.Pipeline.RequestReply(conversation, null, proactive: proactive, catchUp: true);
        f.Settings.GroupInterjectionProbability = 0; Toggle(f, true); first.Reply("synthetic retired draft");
        await f.Until(() => f.ActiveCount == 0, "null-trigger draft retired"); await f.Advance(4000);
        Check(f.Source.Sends.Count == 0 && f.Model.History.Count == 1 && f.PacedCount == 0,
            "ordinary/proactive null trigger must not manufacture accepted-result intent");
    }

    public static async Task NullExplicit()
    {
        await using var f = new Fixture(s => s.ReplyPacingEnabled = false);
        var conversation = await f.Inbound("synthetic ordinary input"); var old = await f.Model.Next();
        f.Pipeline.RequestReply(conversation, null, catchUp: true, directInWindow: true);
        Toggle(f, true); old.Reply("synthetic retired draft");
        await f.Until(() => f.PacedCount == 1 && f.ActiveCount == 0, "null explicit transferred");
        await TransitionClock.AdvanceRegistered(f, 2999);
        Check(f.Model.History.Count == 1, "null direct request is human intent, not an immediately ready result");
        await TransitionClock.AdvanceRegistered(f, 1); (await f.Model.Next()).Reply("synthetic direct answer");
        await f.Source.NextSend(); await f.Idle();
        Check(f.Source.Sends.Count == 1 && f.Model.History.Count == 2, "null explicit transfer sends exactly once");
    }

    public static async Task PrivateExplicit()
    {
        await using var f = new Fixture(s => s.ReplyPacingEnabled = false);
        await f.Inbound("synthetic private input", group: false); var old = await f.Model.Next();
        Toggle(f, true); old.Reply("synthetic retired private draft");
        await f.Until(() => f.PacedCount == 1 && f.ActiveCount == 0, "private explicit transferred");
        await TransitionClock.AdvanceRegistered(f, 1499);
        Check(f.Model.History.Count == 1, "private explicit handoff must honor its 1.5-second quiet");
        await TransitionClock.AdvanceRegistered(f, 1); (await f.Model.Next()).Reply("synthetic private answer");
        await f.Source.NextSend(); await f.Idle();
        Check(f.Source.Sends.Count == 1 && f.Model.History.Count == 2, "private input transfers exactly once");
    }

    public static async Task AcceptedResultRoundTrip()
    {
        await using var f = new Fixture(s => { s.ReplyPacingEnabled = false; s.MaxAgentSteps = 1; s.EnableWebSearch = true; });
        var release = new TaskCompletionSource<BotAgent.Services.Net.WebSearchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Research.OnSearch = _ => release.Task;
        await f.Inbound("synthetic research request", direct: true); var first = await f.Model.Next();
        first.Reply(null, "synthetic detached query"); await f.Research.NextSearch();
        await f.Until(() => f.ActiveCount == 0, "detached search released generation");
        release.SetResult(ControlledResearch.Result());
        var legacyResult = await f.Model.Next();
        f.Settings.GroupInterjectionProbability = 0; Toggle(f, true); legacyResult.Reply("synthetic stale result");
        var pacedResult = await f.Model.Next();
        Toggle(f, false); pacedResult.Reply("synthetic stale paced result");
        var legacyAgain = await f.Model.Next(); Toggle(f, true); legacyAgain.Reply("synthetic stale legacy result");
        var final = await f.Model.Next(); final.Reply("synthetic accepted result");
        await f.Source.NextSend(); await f.Idle();
        Check(f.Model.History.Count == 5 && f.Source.Sends.Count == 1, "accepted result transfers exactly once each direction");
        Check(f.Clock.TickCount == 0, "accepted result identity must not acquire a human quiet window");
    }
}

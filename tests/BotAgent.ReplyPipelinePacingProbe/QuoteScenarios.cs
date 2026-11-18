namespace BotAgent.ReplyPipelinePacingProbe;

internal static partial class Scenarios
{
    private static TaskCompletionSource<(string?, long)> QuoteLookup(Fixture f)
    {
        f.Settings.QuickLoginUin = "10001";
        var result = new TaskCompletionSource<(string?, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Source.OnMessageInfo = (_, _) => result.Task;
        return result;
    }

    public static async Task LateQuotePromotion(Action<bool, string> check)
    {
        await using var f = new Fixture(s => s.GroupInterjectionProbability = 0);
        var lookup = QuoteLookup(f);
        await f.Inbound("synthetic quoted request", replyTo: 30001);
        await f.Until(() => f.Source.MessageInfoCalls == 1, "quote lookup started");
        await f.Advance(1000);
        check(f.Model.History.IsEmpty, "unresolved ordinary quote obeys zero interjection probability");
        lookup.SetResult(("synthetic earlier bot reply", 10001));
        await f.BackgroundIdle();
        await f.Advance(1999);
        check(f.Model.History.IsEmpty, "late quote promotion retains the original human quiet deadline");
        await f.Advance(1);
        var call = await f.NextModel();
        call.Reply("synthetic quoted answer"); await f.Source.NextSend(); await f.Idle();
        check(f.Model.History.Count == 1 && f.Source.Sends.Count == 1,
            "resolved self quote promotes the original refused opportunity exactly once");
    }

    public static async Task SentQuoteDoesNotReplay(Action<bool, string> check)
    {
        await using var f = new Fixture(s => s.GroupContinuationWaitSeconds = 0);
        var lookup = QuoteLookup(f);
        await f.Inbound("synthetic quote already answered", replyTo: 30001);
        (await f.NextModel()).Reply("synthetic ordinary answer");
        await f.Source.NextSend(); await f.Idle();
        lookup.SetResult(("synthetic earlier bot reply", 10001));
        await f.BackgroundIdle(); await f.Advance(10000); await f.Idle();
        check(f.Model.History.Count == 1 && f.Source.Sends.Count == 1,
            "late self quote cannot replay a completed ordinary answer");
    }

    public static async Task StoppedQuoteDoesNotReplay(Action<bool, string> check)
    {
        await using var f = new Fixture(s => s.GroupInterjectionProbability = 0);
        var lookup = QuoteLookup(f);
        var conversation = await f.Inbound("synthetic stopped quote", replyTo: 30001);
        await f.Until(() => f.Source.MessageInfoCalls == 1, "stopped quote lookup started");
        f.Pipeline.ForgetSession(conversation.SourceKey);
        lookup.SetResult(("synthetic earlier bot reply", 10001));
        await f.BackgroundIdle(); await f.Advance(4000); await f.Idle();
        check(f.Model.History.IsEmpty && f.Source.Sends.IsEmpty,
            "late quote callback cannot recapture a new epoch after Stop");
        await f.Inbound("synthetic fresh explicit request", direct: true);
        await f.Advance(3000);
        (await f.NextModel()).Reply("synthetic fresh explicit answer");
        await f.Source.NextSend(); await f.Idle();
        check(f.Model.History.Count == 1 && f.Source.Sends.Count == 1,
            "fresh explicit input remains eligible after a stopped quote");
    }
}

using BotAgent.Services.OneBot;

namespace BotAgent.ReplyPipelinePacingProbe;

internal static partial class Scenarios
{
    public static async Task RecallAfterCompletion(Action<bool, string> check)
    {
        await using var f = new Fixture(s =>
        {
            s.GroupContinuationWaitSeconds = 0;
            s.GroupInterjectionCooldownSeconds = 0;
        });
        var conversation = await f.Inbound("synthetic recalled input", direct: true);
        var original = conversation.Messages.Last();
        (await f.NextModel()).Reply("synthetic initial answer");
        await f.Source.NextSend(); await f.Idle();
        var recall = new QqRecallEvent(true, 10001, original.SenderId!.Value,
            0, original.QqMessageId!.Value, f.Clock.Now);
        f.Pipeline.OnMessageRecalled(recall);
        var call = await f.NextModel();
        check(call.Context.Any(message => message.Recalled), "recall after a completed answer starts an independent notice turn");
        call.Reply("synthetic recall comment");
        await f.Source.NextSend(); await f.Idle();
        f.Pipeline.OnMessageRecalled(recall);
        f.Pipeline.IdleFallbackTick();
        await f.Advance(4000); await f.Idle();
        check(f.Model.History.Count == 2 && f.Source.Sends.Count == 2,
            "duplicate recall and maintenance cannot replay either completed turn");
    }

    public static async Task RecallAdmission(Action<bool, string> check)
    {
        foreach (var probability in new[] { 0, 100 })
        {
            await using var f = new Fixture(s =>
            {
                s.GroupContinuationWaitSeconds = 0;
                s.GroupInterjectionProbability = probability;
                s.GroupInterjectionCooldownSeconds = 30;
            });
            var conversation = await f.Inbound("synthetic notice admission", direct: true);
            var original = conversation.Messages.Last();
            (await f.NextModel()).Reply("synthetic explicit answer");
            await f.Source.NextSend(); await f.Idle();
            if (probability == 0) await f.Advance(30000);
            f.Pipeline.OnMessageRecalled(new QqRecallEvent(true, 10001, original.SenderId!.Value,
                0, original.QqMessageId!.Value, f.Clock.Now));
            await f.Advance(4000); await f.Idle();
            check(f.Model.History.Count == 1 && f.Source.Sends.Count == 1,
                probability == 0 ? "group recall obeys zero interjection probability" : "group recall obeys completion cooldown");
        }
    }

    public static async Task PrivateRecall(Action<bool, string> check)
    {
        await using var f = new Fixture(s =>
        {
            s.PrivateContinuationWaitSeconds = 0;
            s.GroupInterjectionProbability = 0;
        });
        var conversation = await f.Inbound("synthetic private recall", group: false);
        var original = conversation.Messages.Last();
        (await f.NextModel()).Reply("synthetic private answer");
        await f.Source.NextSend(); await f.Idle();
        f.Pipeline.OnMessageRecalled(new QqRecallEvent(false, 0, original.SenderId!.Value,
            0, original.QqMessageId!.Value, f.Clock.Now));
        (await f.NextModel()).Reply("synthetic private recall comment");
        await f.Source.NextSend(); await f.Idle();
        check(f.Model.History.Count == 2 && f.Source.Sends.All(send => send.Target == 10002),
            "private recall remains independent of group probability and stays in its own conversation");
    }

    public static async Task RecallNoteExpiry(Action<bool, string> check)
    {
        // Case 1: Rejected recall followed by > 2 min gap does NOT leak RecallText to next message
        await using (var f = new Fixture(s =>
        {
            s.GroupContinuationWaitSeconds = 0;
            s.GroupInterjectionProbability = 0;
            s.GroupInterjectionCooldownSeconds = 0;
        }))
        {
            var conversation = await f.Inbound("synthetic initial", direct: true);
            var original = conversation.Messages.Last();
            (await f.NextModel()).Reply("synthetic initial answer");
            await f.Source.NextSend(); await f.Idle();

            f.Pipeline.OnMessageRecalled(new QqRecallEvent(true, 10001, original.SenderId!.Value,
                0, original.QqMessageId!.Value, f.Clock.Now));
            await f.Advance(120_001); await f.Idle();

            await f.Inbound("synthetic later question", direct: true);
            var laterCall = await f.NextModel();
            check(laterCall.RecallText is null, "expired recall note (>2m) does not leak into later turn");
            laterCall.Reply("synthetic later answer");
            await f.Source.NextSend(); await f.Idle();
        }

        // Case 2: Rejected recall within 2m boundary is still valid and consumed once
        await using (var f = new Fixture(s =>
        {
            s.GroupContinuationWaitSeconds = 0;
            s.GroupInterjectionProbability = 0;
            s.GroupInterjectionCooldownSeconds = 0;
        }))
        {
            var conversation = await f.Inbound("synthetic initial", direct: true);
            var original = conversation.Messages.Last();
            (await f.NextModel()).Reply("synthetic initial answer");
            await f.Source.NextSend(); await f.Idle();

            f.Pipeline.OnMessageRecalled(new QqRecallEvent(true, 10001, original.SenderId!.Value,
                0, original.QqMessageId!.Value, f.Clock.Now));
            await f.Advance(119_500); await f.Idle();

            await f.Inbound("synthetic prompt followup", direct: true);
            var promptCall = await f.NextModel();
            check(promptCall.RecallText is not null && promptCall.RecallText.Contains("撤回"),
                "recall note within 2m boundary is still injected into next turn");
            promptCall.Reply("synthetic followup answer");
            await f.Source.NextSend(); await f.Idle();

            await f.Inbound("synthetic second followup", direct: true);
            var secondCall = await f.NextModel();
            check(secondCall.RecallText is null, "consumed recall note does not replay on subsequent turn");
            secondCall.Reply("synthetic second answer");
            await f.Source.NextSend(); await f.Idle();
        }
    }
}

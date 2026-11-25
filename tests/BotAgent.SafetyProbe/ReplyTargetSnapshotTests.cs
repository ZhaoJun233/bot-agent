using BotAgent.Domain.Conversation;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void ReplyTargetSnapshotTests()
    {
        var stale = new ReplyTargetFacts(MessageCount: 3, TriggerMessageId: 10012, TriggerIndex: 1,
            TriggerUsable: true, LastSelfIndex: -1, TriggerWasCurrentAtGeneration: false);
        void Expect(string name, ReplyTargetFacts facts, long? target, string reason)
        {
            var result = ReplyTargetRules.Resolve(facts);
            Check(name, result.Target == target && result.ReasonCode == reason,
                $"target={result.Target?.ToString() ?? "none"}, reason={result.ReasonCode}");
        }

        Expect("quote: silence cannot keep an older queued trigger current", stale, null, "trigger_stale");
        Expect("quote: older queued trigger stays stale after an earlier self message",
            stale with { MessageCount = 5, TriggerIndex = 3, LastSelfIndex = 0 }, null, "trigger_stale");
        Expect("quote: a current snapshot survives messages arriving during generation",
            stale with { TriggerWasCurrentAtGeneration = true }, 10012, "trigger_current");
        Expect("quote: invalid model id cannot revive a stale queued trigger",
            stale with { Chosen = 19999, ChosenUsable = false, ChosenIndex = -1 }, null, "trigger_stale");
        Expect("quote: a valid model-selected image remains the target",
            stale with { Chosen = 10011, ChosenUsable = true, ChosenIndex = 0 }, 10011, "model_chosen");
        Expect("quote: a true direct address retains the trigger fallback",
            stale with { DirectAddress = true }, 10012, "trigger_current");
        Expect("quote: a true direct address retains priority over another target",
            stale with { DirectAddress = true, Chosen = 10011, ChosenUsable = true, ChosenIndex = 0 },
            10012, "direct_address_priority");
        Expect("quote: an already answered trigger is stale even with a current snapshot",
            stale with { TriggerWasCurrentAtGeneration = true, LastSelfIndex = 2 }, null, "trigger_stale");
        Expect("quote: a recalled current trigger is never quoted",
            stale with { TriggerWasCurrentAtGeneration = true, TriggerUsable = false }, null, "trigger_recalled");
        Expect("quote: the latest message still needs no quote",
            stale with { TriggerWasCurrentAtGeneration = true, TriggerIndex = 2 }, null, "target_is_latest");
        Expect("quote: echo guard still rejects a source mistaken for the recipient",
            stale with { Chosen = 10011, ChosenUsable = true, ChosenIndex = 0, TriggerWasEcho = true },
            null, "echo_ignored");
        Expect("quote: an evicted trigger is never quoted",
            stale with { TriggerWasCurrentAtGeneration = true, TriggerIndex = -1 }, null, "trigger_stale");
    }
}

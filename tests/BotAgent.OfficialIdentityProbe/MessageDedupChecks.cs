using System.Text.Json.Nodes;
using BotAgent.Domain.Platforms;
using BotAgent.Services.Qq;

internal static partial class RuntimeDeliveryChecks
{
    private static void ReceiveIndexed(Fixture f, FrozenClock clock, string id, string? index, bool group = true, bool full = false)
        => f.Dispatch(group ? full ? "GROUP_MESSAGE_CREATE" : "GROUP_AT_MESSAGE_CREATE" : "C2C_MESSAGE_CREATE",
            ReferenceEvent(clock, id, index, group: group));

    private static IDisposable IndexedSource(Fixture f, bool group, int position)
        => PlatformSourceExecution.Enter(OfficialProbeContext.Source(f.Gateway, group, f.Alias, f.Inbound[position].MessageId));

    public static async Task RunDedupAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("dedup distinct indexes preserve both inbound identities and the raw passive credential", async () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group);
                ReceiveIndexed(f, clock, "shared", "IDX_one", group);
                ReceiveIndexed(f, clock, "shared", "IDX_two", group);
                Require(f.Inbound.Count == 2 && f.Inbound[0].MessageId != f.Inbound[1].MessageId, "different indexes collapsed into one message");
                for (var i = 0; i < 2; i++)
                {
                    using var source = IndexedSource(f, group, i);
                    Require((await f.Send()).Ok, "accepted indexed message could not reply");
                }
                Require(f.Http.Messages.All(m => m["msg_id"]!.GetValue<string>() == "shared")
                    && f.Http.Messages.Select(m => m["msg_seq"]!.GetValue<int>()).SequenceEqual([1, 2]), "indexed identity changed the credential or reset its sequence");
            }
        });
        await test("dedup group full and at events share one normalized identity", () =>
        {
            using var f = new Fixture(clock);
            ReceiveIndexed(f, clock, "shared", "IDX_one");
            ReceiveIndexed(f, clock, "shared", "IDX_one", full: true);
            ReceiveIndexed(f, clock, "shared", "IDX_two", full: true);
            Require(f.Inbound.Count == 2, "group event families duplicate or conflate messages");
            return Task.CompletedTask;
        });
        await test("dedup restart retains both event identities without replay", () =>
        {
            using var f = new Fixture(clock);
            ReceiveIndexed(f, clock, "shared", "IDX_one"); ReceiveIndexed(f, clock, "shared", "IDX_two");
            Require(f.Inbound.Count == 2, "restart precondition lost an indexed event");
            var aliases = f.Inbound.Select(m => m.MessageId).ToArray(); var count = f.Ids.Count;
            f.Restart(); ReceiveIndexed(f, clock, "shared", "IDX_two"); ReceiveIndexed(f, clock, "shared", "IDX_one");
            Require(f.Inbound.Count == 2 && f.Ids.Count == count && f.Inbound.Select(m => m.MessageId).SequenceEqual(aliases), "restart replayed or remapped an indexed event");
            ReceiveIndexed(f, clock, "shared", "IDX_three");
            Require(f.Inbound.Count == 3, "restart rejected a distinct index");
            return Task.CompletedTask;
        });
        await test("dedup legacy unindexed trigger cannot be upgraded by guessing its index", () =>
        {
            using var f = new Fixture(clock); f.Receive("legacy"); var alias = f.Inbound.Single().MessageId;
            ReceiveIndexed(f, clock, "legacy", "IDX_one");
            Require(f.Inbound.Count == 1 && f.Inbound[0].MessageId == alias, "legacy trigger acquired a guessed identity");
            ReceiveIndexed(f, clock, "fresh", "IDX_one");
            Require(f.Inbound.Count == 2, "legacy compatibility blocked a fresh credential");
            return Task.CompletedTask;
        });
        await test("dedup malformed present index is refused before host admission", () =>
        {
            using var f = new Fixture(clock);
            var malformed = new JsonArray[] { new("msg_idx="), new("msg_idx=IDX_one", "msg_idx=IDX_two"),
                new("msg_idx=IDX_one", "msg_idx=IDX_one"), new("msg_idx=bad index"), new("msg_idx=" + new string('a', 513)) };
            for (var i = 0; i < malformed.Length; i++)
            {
                var data = ReferenceEvent(clock, "malformed-" + i, null);
                data["message_scene"]!["ext"] = malformed[i]; f.Dispatch("GROUP_AT_MESSAGE_CREATE", data);
            }
            Require(f.Inbound.Count == 0 && f.Ids.Count == 1, "malformed index was treated as an unindexed message");
            ReceiveIndexed(f, clock, "valid", "IDX_one"); Require(f.Inbound.Count == 1, "valid index control failed");
            return Task.CompletedTask;
        });
        await test("dedup missing index never replays an already indexed credential", () =>
        {
            using var f = new Fixture(clock); ReceiveIndexed(f, clock, "shared", "IDX_one");
            f.Receive("shared"); f.Restart(); f.Receive("shared");
            Require(f.Inbound.Count == 1, "missing index replayed a known indexed credential");
            return Task.CompletedTask;
        });
        await test("dedup distinct indexes share the bounded passive budget", async () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group); ReceiveIndexed(f, clock, "shared", "IDX_one", group);
                ReceiveIndexed(f, clock, "shared", "IDX_two", group); Require(f.Inbound.Count == 2, "budget precondition lost an indexed event");
                var limit = group ? 5 : 4;
                for (var i = 0; i <= limit; i++)
                {
                    using var source = IndexedSource(f, group, i % 2); var sent = await f.Send();
                    Require(i < limit ? sent.Ok : !sent.Ok && sent.Delivery?.ReasonCode == "trigger_budget_exhausted", "indexes multiplied the passive budget");
                }
                Require(f.Http.Messages.Count == limit, "excess indexed send reached HTTP");
            }
        });
        await test("dedup later indexes never extend the original passive window", async () =>
        {
            using var f = new Fixture(clock); ReceiveIndexed(f, clock, "shared", "IDX_one");
            clock.Now += TimeSpan.FromMinutes(4); ReceiveIndexed(f, clock, "shared", "IDX_two");
            Require(f.Inbound.Count == 2, "window precondition lost an indexed event");
            clock.Now += TimeSpan.FromMinutes(1);
            using var source = IndexedSource(f, true, 1);
            Require(!(await f.Send()).Ok && f.Http.Messages.Count == 0, "later index extended the reply window");
            ReceiveIndexed(f, clock, "shared", "IDX_three"); Require(f.Inbound.Count == 2, "expired credential admitted a fresh index");
        });
        await test("dedup relationship restoration never revives an indexed credential", () =>
        {
            using var f = new Fixture(clock, false); ReceiveIndexed(f, clock, "shared", "IDX_one", false);
            f.Dispatch("FRIEND_DEL", FriendEvent(clock)); f.Dispatch("FRIEND_ADD", FriendEvent(clock));
            clock.Now += TimeSpan.FromSeconds(1); ReceiveIndexed(f, clock, "shared", "IDX_two", false);
            Require(f.Inbound.Count == 1, "new index bypassed relationship revocation");
            ReceiveIndexed(f, clock, "fresh", "IDX_one", false); Require(f.Inbound.Count == 2, "fresh relationship control failed");
            return Task.CompletedTask;
        });
    }
}

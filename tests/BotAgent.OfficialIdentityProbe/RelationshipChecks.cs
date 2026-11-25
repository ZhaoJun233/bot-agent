using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Domain.Platforms;
using BotAgent.Services.Qq;

internal static partial class RuntimeDeliveryChecks
{
    private static JsonObject FriendEvent(FrozenClock clock, string user = "user-A")
        => new() { ["openid"] = user, ["timestamp"] = clock.Now.ToUnixTimeSeconds() };

    private static void ReceiveRelationship(Fixture fixture, string id)
    {
        var count = fixture.Inbound.Count;
        fixture.Receive(id);
        Require(fixture.Inbound.Count == count + 1, "relationship fixture ingress was not accepted");
    }

    public static async Task RunRelationshipsAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("relationship friend deletion revokes the existing passive trigger", async () =>
        {
            using var f = new Fixture(clock, false); ReceiveRelationship(f, "first");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            var count = f.Ids.Count;
            f.Dispatch("FRIEND_DEL", FriendEvent(clock));
            var refused = await f.Send();
            Require(!refused.Ok && refused.Delivery?.ReasonCode == "trigger_revoked" && f.Http.Messages.Count == 0,
                "deleted friend retained a passive send grant");
            Require(f.Ids.Count == count, "friend deletion allocated an identity");
        });
        await test("relationship friend deletion during upload prevents the final message", async () =>
        {
            using var f = new Fixture(clock, false); ReceiveRelationship(f, "first");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.BeforeUpload = () => { f.Dispatch("FRIEND_DEL", FriendEvent(clock)); return Task.CompletedTask; };
            var sent = await f.Gateway.SendImageWithReceiptAsync(false, f.Alias, [1, 2, 3]);
            Require(!sent.Ok && sent.Delivery?.Status == DeliveryStatus.Rejected && f.Http.Uploads == 1 && f.Http.Messages.Count == 0,
                "friend removal after upload still sent a message");
            Require(f.Rows().Single()["State"]!.GetValue<int>() == (int)OfficialDeliveryState.Abandoned,
                "removed media operation was not terminal");
        });
        await test("relationship friend deletion persists across restart without replay", async () =>
        {
            using var f = new Fixture(clock, false); ReceiveRelationship(f, "first");
            f.Dispatch("FRIEND_DEL", FriendEvent(clock)); f.Restart();
            f.Receive("first"); Require(f.Inbound.Count == 1, "deleted trigger reached host again");
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            Require(!(await f.Send()).Ok && f.Http.Messages.Count == 0, "restart restored a deleted relationship trigger");
        });
        await test("relationship restoration never resurrects the previous operation", async () =>
        {
            using var f = new Fixture(clock, false); ReceiveRelationship(f, "first");
            f.Dispatch("FRIEND_DEL", FriendEvent(clock));
            f.Dispatch("FRIEND_ADD", FriendEvent(clock));
            f.Dispatch("C2C_MSG_RECEIVE", FriendEvent(clock));
            using (PlatformSourceExecution.Enter(f.Lease("first")))
                Require(!(await f.Send()).Ok, "restoration resurrected an old trigger");
            clock.Now += TimeSpan.FromSeconds(1); ReceiveRelationship(f, "fresh");
            using var execution = PlatformSourceExecution.Enter(f.Lease("fresh"));
            Require((await f.Send()).Ok && f.Http.Messages.Count == 1, "a fresh authorized trigger could not reply");
        });
        await test("relationship removal isolates conversation kind and target", async () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group); ReceiveRelationship(f, "first");
                using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                f.Dispatch("FRIEND_DEL", FriendEvent(clock, group ? "group-A" : "user-B"));
                f.Dispatch("GROUP_DEL_ROBOT", new() { ["group_openid"] = group ? "group-B" : "user-A" });
                Require((await f.Send()).Ok && f.Http.Messages.Count == 1, "unrelated removal revoked this scope");
            }
        });
        await test("relationship notification switches preserve passive delivery but never grant active sends", async () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group); ReceiveRelationship(f, "first");
                foreach (var suffix in new[] { "REJECT", "RECEIVE" })
                {
                    f.Dispatch((group ? "GROUP_MSG_" : "C2C_MSG_") + suffix,
                        group ? new() { ["group_openid"] = "group-A", ["timestamp"] = clock.Now.ToUnixTimeSeconds() } : FriendEvent(clock));
                    Require(!(await f.Send()).Ok && f.Http.Messages.Count == (suffix == "REJECT" ? 0 : 1),
                        "notification switch granted a source-free send");
                    using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                    Require((await f.Send()).Ok, "notification switch revoked a valid passive trigger");
                }
                Require(f.Http.Messages.Count == 2, "notification switch produced extra transport");
            }
        });
        await test("relationship lifecycle events never allocate unknown group or friend aliases", () =>
        {
            using var f = new Fixture(clock); var count = f.Ids.Count;
            f.Dispatch("GROUP_ADD_ROBOT", new() { ["group_openid"] = "group-unknown" });
            f.Dispatch("GROUP_DEL_ROBOT", new() { ["group_openid"] = "group-unknown" });
            f.Dispatch("FRIEND_ADD", FriendEvent(clock, "user-unknown"));
            f.Dispatch("FRIEND_DEL", FriendEvent(clock, "user-unknown"));
            Require(f.Ids.Count == count && f.Inbound.Count == 0, "lifecycle events minted message identities");
            return Task.CompletedTask;
        });
        await test("relationship malformed and alternate identity fields never choose a target", async () =>
        {
            using var f = new Fixture(clock, false); ReceiveRelationship(f, "first"); var count = f.Ids.Count;
            foreach (var value in new JsonNode?[] { null, JsonValue.Create(42), JsonValue.Create(" "), new JsonObject() })
                f.Dispatch("FRIEND_DEL", new() { ["openid"] = value, ["user_openid"] = "user-A",
                    ["author"] = new JsonObject { ["union_openid"] = "user-A" } });
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            Require((await f.Send()).Ok && f.Ids.Count == count + 1, "malformed friend event revoked or remapped an identity");
        });
        await test("relationship friend removal clears only its live quote indexes", async () =>
        {
            using var f = new Fixture(clock, false);
            ReceiveReference(f, clock, "old", "REFIDX_old", group: false);
            var old = long.Parse(f.Ref("old").NativeMessageId);
            f.Dispatch("FRIEND_DEL", FriendEvent(clock));
            clock.Now += TimeSpan.FromSeconds(1); ReceiveRelationship(f, "fresh");
            using var execution = PlatformSourceExecution.Enter(f.Lease("fresh"));
            var sent = await f.Gateway.SendTextAsync(false, f.Alias, "synthetic", replyToMessageId: old);
            Require(sent.Ok && sent.Delivery?.Status == DeliveryStatus.Degraded
                && f.Http.Messages.Single()["message_reference"] is null, "friend deletion kept an old quote credential");
        });
        await test("relationship member removal leaves ordinary group replies available", async () =>
        {
            using var f = new Fixture(clock); ReceiveRelationship(f, "first");
            f.Dispatch("GROUP_MEMBER_REMOVE", new() { ["group_openid"] = "group-A", ["member_openid"] = "member-A",
                ["timestamp"] = clock.Now.ToUnixTimeSeconds() });
            using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            Require((await f.Send()).Ok && f.Http.Messages.Count == 1, "one member leaving stopped ordinary group delivery");
        });
    }
}

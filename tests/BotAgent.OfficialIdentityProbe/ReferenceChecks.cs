using System.Reflection;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Services.OneBot;
using BotAgent.Services.Official;

internal static partial class RuntimeDeliveryChecks
{
    private static JsonObject ReferenceEvent(FrozenClock clock, string id, string? index, string? quoted = null, bool group = true)
    {
        var ext = new JsonArray();
        if (index is not null) ext.Add("msg_idx=" + index);
        if (quoted is not null) ext.Add("ref_msg_idx=" + quoted);
        return new() { ["id"] = id, ["group_openid"] = group ? "group-A" : null,
            ["author"] = new JsonObject { [group ? "member_openid" : "user_openid"] = group ? "member-A" : "user-A" },
            ["timestamp"] = clock.Now.ToString("O"), ["content"] = "synthetic-reference-input",
            ["message_type"] = quoted is null ? 0 : 103, ["message_scene"] = new JsonObject { ["ext"] = ext } };
    }
    private static void ReceiveReference(Fixture f, FrozenClock clock, string id, string? index, string? quoted = null, bool group = true)
    {
        var before = f.Inbound.Count;
        f.Dispatch(group ? "GROUP_AT_MESSAGE_CREATE" : "C2C_MESSAGE_CREATE", ReferenceEvent(clock, id, index, quoted, group));
        Require(f.Inbound.Count == before + 1, "reference fixture ingress was not accepted");
    }

    public static async Task RunReferencesAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("reference capacity preserves conflict tombstones and isolates group invalidation", async () =>
        {
            using var f = new Fixture(clock); ReceiveReference(f, clock, "one", "REFIDX_shared"); ReceiveReference(f, clock, "two", "REFIDX_shared");
            var remember = typeof(OfficialBotGateway).GetMethod("RememberReference", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var conversation = f.MessageKey("one").Scope.Conversation!;
            for (var i = 1; i <= 4096; i++) remember.Invoke(f.Gateway, [conversation, 10001L + i, "REFIDX_fill_" + i]);
            var cache = (System.Collections.IDictionary)typeof(OfficialBotGateway).GetField("_references", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Gateway)!;
            Require(cache.Count == 4096, "reference cache exceeded its bound");
            ReceiveReference(f, clock, "three", "REFIDX_shared"); f.Receive("trigger");
            using var execution = PlatformSourceExecution.Enter(f.Lease("trigger"));
            Require((await f.Gateway.SendTextAsync(true, f.Alias, "synthetic", replyToMessageId: long.Parse(f.Ref("three").NativeMessageId))).Delivery?.Status == DeliveryStatus.Degraded, "capacity forgot an index conflict");
            f.Dispatch("GROUP_DEL_ROBOT", new() { ["group_openid"] = "group-B" });
            Require(cache.Count == 4096, "unrelated group removal cleared this scope");
            f.Dispatch("GROUP_DEL_ROBOT", new() { ["group_openid"] = "group-A" });
            Require(cache.Count == 0, "removed group retained reference cache");
        });
        await test("reference invalid own index and oversized fields never become a credential", async () =>
        {
            using var f = new Fixture(clock);
            var data = ReferenceEvent(clock, "bad-own", "REFIDX_first");
            data["message_scene"]!["ext"]!.AsArray().Add("msg_idx=REFIDX_second"); f.Dispatch("GROUP_AT_MESSAGE_CREATE", data);
            var large = ReferenceEvent(clock, "large", new string('x', 513)); f.Dispatch("GROUP_AT_MESSAGE_CREATE", large);
            f.Receive("trigger"); using var execution = PlatformSourceExecution.Enter(f.Lease("trigger"));
            foreach (var id in new[] { "bad-own", "large" })
                Require((await f.Gateway.SendTextAsync(true, f.Alias, "synthetic", replyToMessageId: long.Parse(f.Ref(id).NativeMessageId))).Delivery?.Status == DeliveryStatus.Degraded, "malformed own index reached wire");
            var oversized = ReferenceEvent(clock, "oversized", "REFIDX_size", "REFIDX_absent");
            oversized["message_scene"]!["ext"]!.AsArray().Add("auth_token=test-token-synthetic-sensitive");
            for (var i = 0; i < 33; i++) oversized["message_scene"]!["ext"]!.AsArray().Add("ignored=x");
            var before = f.Inbound.Count;
            f.Dispatch("GROUP_AT_MESSAGE_CREATE", oversized);
            Require(f.Inbound.Count == before, "oversized field list was interpreted");
        });
        await test("reference text and image use the index while keeping a different passive trigger", async () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group);
                ReceiveReference(f, clock, "quoted", "REFIDX_quote==", group: group); f.Receive("trigger");
                var quote = long.Parse(f.Ref("quoted").NativeMessageId);
                using var execution = PlatformSourceExecution.Enter(f.Lease("trigger"));
                var text = await f.Gateway.SendTextAsync(group, f.Alias, "synthetic-output", replyToMessageId: quote);
                f.Http.BeforeUpload = () => { ReceiveReference(f, clock, "later", "REFIDX_later==", group: group); return Task.CompletedTask; };
                var image = await f.Gateway.SendImageWithReceiptAsync(group, f.Alias, [1, 2, 3], replyToMessageId: quote);
                Require(text.Ok && image.Ok && f.Http.Messages.Count == 2 && f.Http.Messages.All(m =>
                    m["message_reference"]?["message_id"]?.GetValue<string>() == "REFIDX_quote=="
                    && m["msg_id"]?.GetValue<string>() == "trigger"), "reference index missing or borrowed as trigger");
                Require(f.Http.Messages.Select(m => m["msg_seq"]!.GetValue<int>()).SequenceEqual(new[] { 1, 2 }), "quote consumed an extra reservation");
            }
        });
        await test("reference own messages use ext_info ref_idx and bind it into the operation digest", async () =>
        {
            using var f = new Fixture(clock); f.Receive("trigger"); using var execution = PlatformSourceExecution.Enter(f.Lease("trigger"));
            f.Http.MessageResponse = n => Reply(200, new JsonObject { ["id"] = "out-" + n,
                ["ext_info"] = new JsonObject { ["ref_idx"] = "REFIDX_out-" + n } }.ToJsonString());
            var first = await f.Send(); var second = await f.Send();
            Require((await f.Gateway.SendTextAsync(true, f.Alias, "same", replyToMessageId: first.MessageId)).Ok, "valid own quote failed");
            Require((await f.Gateway.SendTextAsync(true, f.Alias, "same", replyToMessageId: second.MessageId)).Ok, "second valid own quote failed");
            Require(f.Http.Messages[2]["message_reference"]?["message_id"]?.GetValue<string>() == "REFIDX_out-1"
                && f.Http.Messages[3]["message_reference"]?["message_id"]?.GetValue<string>() == "REFIDX_out-2", "raw outgoing id substituted for ref_idx");
            Require(f.Rows()[2]["ContentDigest"]!.GetValue<string>() != f.Rows()[3]["ContentDigest"]!.GetValue<string>(), "quote omitted from durable content binding");
            Require(!System.IO.File.ReadAllText(f.LedgerPath).Contains("REFIDX_"), "reference credential persisted in ledger");
        });
        await test("reference unknown foreign and nonmessage aliases fail before quota and upload", async () =>
        {
            using var f = new Fixture(clock); f.Receive("trigger"); using var execution = PlatformSourceExecution.Enter(f.Lease("trigger"));
            var foreign = f.Ids.AliasFor(new(new("app-B", OfficialIdentityKind.Message,
                new(PlatformId.QqOfficial, "app-B", ConversationKind.GroupChat, "group-A"), OfficialMessageDirection.Inbound), "foreign"));
            var otherGroup = f.Ids.AliasFor(new(new("app-A", OfficialIdentityKind.Message,
                new(PlatformId.QqOfficial, "app-A", ConversationKind.GroupChat, "group-B"), OfficialMessageDirection.Inbound), "other"));
            foreach (var alias in new[] { foreign, otherGroup, f.Alias, 10001L })
            {
                var text = await f.Gateway.SendTextAsync(true, f.Alias, "synthetic", replyToMessageId: alias);
                var image = await f.Gateway.SendImageWithReceiptAsync(true, f.Alias, [1], replyToMessageId: alias);
                Require(text.Delivery?.Status == DeliveryStatus.Rejected && image.Delivery?.Status == DeliveryStatus.Rejected, "invalid quote was sent");
            }
            Require(f.Http.Tokens == 0 && f.Http.Uploads == 0 && f.Http.Messages.Count == 0 && f.Rows().Count == 0, "invalid quote consumed resources");
        });
        await test("reference missing index degrades only confirmed delivery and cannot authorize a send", async () =>
        {
            using var f = new Fixture(clock); f.Receive("quoted"); f.Receive("trigger");
            var quote = long.Parse(f.Ref("quoted").NativeMessageId);
            Require(!(await f.Gateway.SendTextAsync(true, f.Alias, "synthetic", replyToMessageId: quote)).Ok && f.Http.Tokens == 0, "quote became send authorization");
            using var execution = PlatformSourceExecution.Enter(f.Lease("trigger"));
            var result = await f.Gateway.SendTextAsync(true, f.Alias, "synthetic", replyToMessageId: quote);
            Require(result.Ok && result.Delivery?.Status == DeliveryStatus.Degraded && result.Delivery.ReasonCode == "reference_unavailable"
                && f.Http.Messages.Single()["message_reference"] is null
                && f.Rows().Single()["Status"]!.GetValue<int>() == (int)DeliveryStatus.Degraded, "missing reference silently reported success");
            f.Http.MessageResponse = _ => Reply(503, "{}");
            var unknown = await f.Gateway.SendTextAsync(true, f.Alias, "synthetic", replyToMessageId: quote);
            Require(unknown.Delivery?.Status == DeliveryStatus.Unknown, "quote degradation hid uncertain delivery");
        });
        await test("reference inbound resolves an existing scoped alias without inventing one", () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group); ReceiveReference(f, clock, "old", "REFIDX_old", group: group);
                var old = f.Inbound.Single().MessageId;
                ReceiveReference(f, clock, "reply", "REFIDX_new", "REFIDX_old", group);
                Require(f.Inbound.Last().ReplyToMessageId == old, "known scoped reference was lost");
                var data = ReferenceEvent(clock, "unknown", "REFIDX_unknown", "REFIDX_absent", group);
                data["msg_elements"] = new JsonArray(new JsonObject { ["msg_idx"] = "REFIDX_absent", ["message_type"] = 103, ["content"] = "synthetic supplied preview" });
                f.Dispatch(group ? "GROUP_AT_MESSAGE_CREATE" : "C2C_MESSAGE_CREATE", data);
                Require(f.Inbound.Last().ReplyToMessageId is null && f.Inbound.Last().ReplyToPreviewText == "synthetic supplied preview", "preview was lost or fabricated an identity");
            }
            return Task.CompletedTask;
        });
        await test("reference unknown and cross conversation indexes explicitly report unavailable", () =>
        {
            using var f = new Fixture(clock); ReceiveReference(f, clock, "old", "REFIDX_old");
            var data = ReferenceEvent(clock, "cross", "REFIDX_cross", "REFIDX_old"); data["group_openid"] = "group-B";
            f.Dispatch("GROUP_AT_MESSAGE_CREATE", data);
            Require(f.Inbound.Last().ReplyToMessageId is null && f.Inbound.Last().ReplyToPreviewText == "（引用原文不可用）", "cross-scope quote borrowed another message");
            ReceiveReference(f, clock, "missing", "REFIDX_missing", "REFIDX_never");
            Require(f.Inbound.Last().ReplyToMessageId is null && f.Inbound.Last().ReplyToPreviewText == "（引用原文不可用）", "missing quote disappeared");
            return Task.CompletedTask;
        });
        await test("reference duplicate fields elements and malformed indexes never choose the first", () =>
        {
            using var f = new Fixture(clock); ReceiveReference(f, clock, "old", "REFIDX_old");
            for (var i = 0; i < 5; i++)
            {
                var data = ReferenceEvent(clock, "bad-" + i, "REFIDX_" + i, "REFIDX_absent");
                if (i == 0) data["message_scene"]!["ext"]!.AsArray().Add("ref_msg_idx=REFIDX_old");
                if (i == 1) data["message_scene"]!["ext"] = new JsonArray("ref_msg_idx=bad\nindex");
                if (i == 2) data["msg_elements"] = new JsonArray(
                    new JsonObject { ["msg_idx"] = "REFIDX_absent", ["content"] = "first" },
                    new JsonObject { ["msg_idx"] = "REFIDX_absent", ["content"] = "second" });
                if (i == 3) data["msg_elements"] = new JsonArray(new JsonObject { ["msg_idx"] = "REFIDX_other", ["content"] = "unrelated" });
                if (i == 4) data["msg_elements"] = new JsonArray(new JsonObject { ["msg_idx"] = "REFIDX_absent", ["content"] = new string('x', 2049) });
                f.Dispatch("GROUP_AT_MESSAGE_CREATE", data);
                Require(f.Inbound.Last().ReplyToMessageId is null && f.Inbound.Last().ReplyToPreviewText == "（引用原文不可用）", "malformed reference became trusted preview");
            }
            return Task.CompletedTask;
        });
        await test("reference conflicting indexes remain unavailable for earlier and later aliases", async () =>
        {
            using var f = new Fixture(clock); ReceiveReference(f, clock, "one", "REFIDX_shared"); ReceiveReference(f, clock, "two", "REFIDX_shared");
            ReceiveReference(f, clock, "three", "REFIDX_shared"); f.Receive("trigger");
            using var execution = PlatformSourceExecution.Enter(f.Lease("trigger"));
            foreach (var id in new[] { "one", "two", "three" })
                Require((await f.Gateway.SendTextAsync(true, f.Alias, "synthetic", replyToMessageId: long.Parse(f.Ref(id).NativeMessageId))).Delivery?.Status == DeliveryStatus.Degraded, "ambiguous index picked an alias");
            ReceiveReference(f, clock, "reply", "REFIDX_reply", "REFIDX_shared");
            Require(f.Inbound.Last().ReplyToMessageId is null, "ambiguous reference resolved");
        });
        await test("reference lifetime and disconnect do not reconstruct indexes from durable message ids", async () =>
        {
            foreach (var transition in new[] { "READY", "disconnect", "restart", "expiry" })
            {
                using var f = new Fixture(clock, false); ReceiveReference(f, clock, "old", "REFIDX_old", group: false);
                if (transition == "READY") f.Dispatch("READY", new());
                if (transition == "disconnect") typeof(OfficialBotGateway).GetMethod("SetConnected", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Gateway, [false]);
                if (transition == "restart") f.Restart();
                if (transition == "expiry") clock.Now += TimeSpan.FromHours(1);
                f.Receive("trigger"); using var execution = PlatformSourceExecution.Enter(f.Lease("trigger"));
                var result = await f.Gateway.SendTextAsync(false, f.Alias, "synthetic", replyToMessageId: long.Parse(f.Ref("old").NativeMessageId));
                Require(result.Delivery?.Status == DeliveryStatus.Degraded && f.Http.Messages.Last()["message_reference"] is null, "stale reference survived lifecycle");
            }
        });
        await test("reference malformed success metadata preserves delivery without minting a quote index", async () =>
        {
            foreach (var extra in new[] { "null", "[]", "{\"ref_idx\":42}", "{\"ref_idx\":\"a\",\"ref_idx\":\"b\"}", "{\"ref_idx\":\"bad index\"}" })
            {
                using var f = new Fixture(clock); f.Receive("trigger"); using var execution = PlatformSourceExecution.Enter(f.Lease("trigger"));
                f.Http.MessageResponse = _ => Reply(200, "{\"id\":\"out\",\"ext_info\":" + extra + "}");
                var first = await f.Send(); Require(first.Ok && first.MessageId > 0, "optional quote metadata invalidated success");
                var second = await f.Gateway.SendTextAsync(true, f.Alias, "synthetic", replyToMessageId: first.MessageId);
                Require(second.Delivery?.Status == DeliveryStatus.Degraded && f.Http.Messages.Last()["message_reference"] is null, "malformed result index was used");
            }
        });
    }
}

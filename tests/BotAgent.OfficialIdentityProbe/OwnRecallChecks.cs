using System.Text.Json.Nodes;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services;
using BotAgent.Services.Qq;

internal static partial class RuntimeDeliveryChecks
{
    public static async Task RunOwnRecallAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        foreach (var group in new[] { true, false })
        foreach (var body in new[] { "", "{}", " { } \n" })
            await test($"own recall {(group ? "group" : "C2C")} body length {body.Length} is durable and executes DELETE once across restart", async () =>
            {
                using var f = new Fixture(clock, group); f.Receive("first");
                f.Http.RecallResponse = _ => Reply(200, body);
                long message;
                using (PlatformSourceExecution.Enter(f.Lease("first")))
                {
                    message = (await f.Send()).MessageId;
                    f.Http.BeforeRecall = _ =>
                    {
                        var row = f.Rows().Single();
                        Require(row["MessageAttemptedAt"]!.GetValue<long>() > 0
                            && row["Recall"]!["Attempts"]!.GetValue<int>() == 1
                            && row["Recall"]!["Status"] is null, "DELETE preceded its durable attempt");
                        return Task.CompletedTask;
                    };
                    Require((await f.Recall(message)).Status == DeliveryStatus.Success, "documented empty recall acknowledgement not confirmed");
                    Require((await f.Recall(message)).Status == DeliveryStatus.Success, "confirmed duplicate lost receipt");
                }
                f.Restart();
                using (PlatformSourceExecution.Enter(f.Lease("first")))
                    Require((await f.Recall(message)).Status == DeliveryStatus.Success, "restart lost recall receipt");
                Require(f.Http.Recalls.SequenceEqual(new[] { group ? "/v2/groups/group-A/messages/sent" : "/v2/users/user-A/messages/sent" })
                    && f.Http.Messages.Count == 1, "recall replayed, routed incorrectly, or sent a message");
            });
        foreach (var seconds in new[] { 119, 120 })
            await test($"own recall enforces attempt deadline at {seconds} seconds", async () =>
            {
                using var f = new Fixture(clock); f.Receive("first"); using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
                var message = (await f.Send()).MessageId; clock.Now += TimeSpan.FromSeconds(seconds);
                var result = await f.Recall(message);
                Require(seconds < 120 ? result.Status == DeliveryStatus.Success && f.Http.Recalls.Count == 1
                    : result.ReasonCode == "recall_expired" && f.Http.Recalls.Count == 0, "two-minute boundary incorrect");
            });
        await test("own recall delayed send receipt cannot extend the window", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.BeforeMessage = _ => { clock.Now += TimeSpan.FromSeconds(121); return Task.CompletedTask; };
            var message = (await f.Send()).MessageId;
            Require((await f.Recall(message)).ReasonCode == "recall_expired" && f.Http.Recalls.Count == 0, "receipt extended deadline");
        });
        await test("own recall rejects unproven incoming and foreign identities before HTTP", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
            var own = (await f.Send()).MessageId;
            var unproven = f.Ids.AliasFor(f.MessageKey("not-sent", OfficialMessageDirection.Outbound));
            var foreign = f.Ids.AliasFor(new(new("app-B", OfficialIdentityKind.Message,
                new(PlatformId.QqOfficial, "app-B", BotAgent.Domain.Conversation.ConversationKind.GroupChat, "group-A"),
                OfficialMessageDirection.Outbound), "sent"));
            foreach (var message in new[] { long.Parse(f.Ref("first").NativeMessageId), foreign, 10001L })
                Require((await f.Recall(message)).ReasonCode == "recall_message_scope_mismatch", "invalid message identity accepted");
            Require((await f.Recall(unproven)).ReasonCode == "own_message_unverified", "alias alone became ownership proof");
            var crossTarget = await ((IQqChatSource)f.Gateway).RecallOwnMessageAsync(false, f.Alias, own);
            Require(crossTarget.ReasonCode == "recall_source_invalid" && f.Http.Recalls.Count == 0, "foreign conversation reached DELETE");
        });
        await test("own recall rejects legacy receipt without attempt-time proof", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); long message;
            using (PlatformSourceExecution.Enter(f.Lease("first"))) message = (await f.Send()).MessageId;
            f.Gateway.Dispose();
            var legacy = JsonNode.Parse(File.ReadAllText(f.LedgerPath))!.AsObject(); legacy["Version"] = 3;
            foreach (var row in legacy["Operations"]!.AsObject())
            { row.Value!.AsObject().Remove("MessageAttemptedAt"); row.Value!.AsObject().Remove("Recall"); }
            File.WriteAllText(f.LedgerPath, legacy.ToJsonString()); f.Restart();
            using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
            Require((await f.Recall(message)).ReasonCode == "own_message_time_unverified" && f.Http.Recalls.Count == 0, "legacy time invented");
        });
        foreach (var (status, body, expected, reason) in new[]
        {
            (503, "", DeliveryStatus.Unknown, "delivery_outcome_unknown"),
            (204, "", DeliveryStatus.Unknown, "invalid_response"),
            (201, "{}", DeliveryStatus.Unknown, "invalid_response"),
            (204, "{}", DeliveryStatus.Unknown, "invalid_response"),
            (200, "[]", DeliveryStatus.Unknown, "invalid_response"),
            (200, "null", DeliveryStatus.Unknown, "invalid_response"),
            (200, " ", DeliveryStatus.Unknown, "invalid_response"),
            (200, "{\"unrecognized\":true}", DeliveryStatus.Unknown, "invalid_response"),
            (200, "{\"code\":0}", DeliveryStatus.Unknown, "invalid_response"),
            (200, "{\"code\":40062003}", DeliveryStatus.Rejected, "permission_denied"),
            (400, "{\"code\":40064004}", DeliveryStatus.Rejected, "recall_expired"),
            (400, "{\"code\":40061002}", DeliveryStatus.Rejected, "invalid_parameters"),
            (403, "untrusted body", DeliveryStatus.Rejected, "permission_denied"),
            (429, "", DeliveryStatus.Throttled, "rate_limited")
        })
            await test($"own recall keeps {status}/{reason} terminal across duplicate and restart", async () =>
            {
                using var f = new Fixture(clock); f.Receive("first"); long message;
                f.Http.RecallResponse = _ => Reply(status, body);
                using (PlatformSourceExecution.Enter(f.Lease("first")))
                {
                    message = (await f.Send()).MessageId; var result = await f.Recall(message);
                    Require(result.Status == expected && result.ReasonCode == reason, "incorrect structured receipt: " + result.ReasonCode);
                    Require((await f.Recall(message)).Status == expected, "duplicate lost terminal result");
                }
                f.Restart(); using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
                Require((await f.Recall(message)).Status == expected && f.Http.Recalls.Count == 1, "restart replayed recall");
            });
        await RecallFailureChecks(test, clock);
        await RecallConcurrencyChecks(test, clock);
    }

    private static async Task RecallFailureChecks(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("own recall retries only one confirmed credential rejection", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
            var message = (await f.Send()).MessageId;
            f.Http.RecallResponse = count => count == 1 ? Reply(401, "{\"code\":11244}") : Reply(200, "");
            Require((await f.Recall(message)).Status == DeliveryStatus.Success && f.Http.Recalls.Count == 2 && f.Http.Tokens == 2,
                "confirmed credential rejection not retried exactly once");
            Require((await f.Recall(message)).Status == DeliveryStatus.Success && f.Http.Recalls.Count == 2, "duplicate repeated credential retry");
        });
        foreach (var mode in new[] { "retired", "expired", "rejected" })
            await test($"own recall credential retry rechecks {mode}", async () =>
            {
                using var f = new Fixture(clock); f.Receive("first"); using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
                var message = (await f.Send()).MessageId;
                f.Http.RecallResponse = _ =>
                {
                    if (mode == "retired") f.Current = false;
                    if (mode == "expired") clock.Now += TimeSpan.FromSeconds(121);
                    return Reply(401, "{\"code\":11244}");
                };
                var result = await f.Recall(message);
                Require(!result.IsSuccess && f.Http.Recalls.Count == (mode == "rejected" ? 2 : 1), "unsafe credential retry");
            });
        foreach (var failure in new[] { "transport", "attempt-write", "receipt-write", "retired-at-write" })
            await test($"own recall handles {failure} without replay", async () =>
            {
                using var f = new Fixture(clock); f.Receive("first"); long message;
                using (PlatformSourceExecution.Enter(f.Lease("first")))
                {
                    message = (await f.Send()).MessageId;
                    if (failure == "transport") f.Http.BeforeRecall = _ => throw new HttpRequestException("synthetic-failure");
                    else f.AfterCommit = snapshot =>
                    {
                        var recall = JsonNode.Parse(snapshot)!["Operations"]!.AsObject().Single().Value!["Recall"];
                        if (recall is null) return;
                        if (failure == "retired-at-write") f.Current = false;
                        else if (failure == "attempt-write" && recall["Status"] is null
                            || failure == "receipt-write" && recall["Status"] is not null) throw new IOException("synthetic-write-failure");
                    };
                    var result = await f.Recall(message);
                    Require(!result.IsSuccess, "failure reported success");
                    if (failure is "transport" or "receipt-write") Require(result.Status == DeliveryStatus.Unknown, "possible effect lost Unknown");
                }
                f.AfterCommit = null; f.Current = true; f.Restart();
                using var restored = PlatformSourceExecution.Enter(f.Lease("first"));
                var recovered = await f.Recall(message);
                Require(f.Http.Recalls.Count == (failure is "attempt-write" or "retired-at-write" ? 0 : 1), "recovery replayed DELETE");
                if (failure is "transport" or "attempt-write") Require(recovered.Status == DeliveryStatus.Unknown, "recovery lost uncertain outcome");
            });
    }

    private static async Task RecallConcurrencyChecks(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("own recall concurrent callers cannot share one durable grant", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
            var message = (await f.Send()).MessageId;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Http.BeforeRecall = async _ => { entered.SetResult(); await release.Task; };
            var first = f.Recall(message);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second = await f.Recall(message);
                Require(second.Status == DeliveryStatus.Unknown && f.Http.Recalls.Count == 1, "second caller repeated pending recall");
            }
            finally { release.TrySetResult(); }
            Require((await first).Status == DeliveryStatus.Success && f.Http.Recalls.Count == 1, "first recall did not retain ownership");
        });
        foreach (var mode in new[] { "gate-retirement", "attempt-expiry", "transport-cancel", "transport-disposed" })
            await test("own recall final boundary: " + mode, async () =>
            {
                using var f = new Fixture(clock); f.Receive("first"); using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
                var message = (await f.Send()).MessageId;
                DeliveryResult result;
                if (mode == "gate-retirement")
                {
                    var gate = (SemaphoreSlim)typeof(BotAgent.Services.Official.OfficialBotGateway)
                        .GetField("_sendGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Gateway)!;
                    await gate.WaitAsync(); Task<DeliveryResult> pending;
                    try { pending = f.Recall(message); Require(!pending.IsCompleted, "recall bypassed shared send gate"); f.Current = false; }
                    finally { gate.Release(); }
                    result = await pending;
                }
                else
                {
                    if (mode == "transport-cancel") f.Http.BeforeRecall = _ => throw new OperationCanceledException();
                    else if (mode == "transport-disposed") f.Http.BeforeRecall = _ => throw new ObjectDisposedException("synthetic-http");
                    else f.AfterCommit = snapshot =>
                    {
                        var recall = JsonNode.Parse(snapshot)!["Operations"]!.AsObject().Single().Value!["Recall"];
                        if (recall is not null && recall["Status"] is null) clock.Now += TimeSpan.FromSeconds(121);
                    };
                    result = await f.Recall(message);
                }
                Require(mode.StartsWith("transport-") ? result.Status == DeliveryStatus.Unknown && f.Http.Recalls.Count == 1
                    : result.Status == DeliveryStatus.Rejected && f.Http.Recalls.Count == 0, "final boundary allowed unsafe effect or lost uncertainty");
                if (mode.StartsWith("transport-"))
                {
                    f.Restart(); using var restarted = PlatformSourceExecution.Enter(f.Lease("first"));
                    Require((await f.Recall(message)).Status == DeliveryStatus.Unknown && f.Http.Recalls.Count == 1, "restart replayed uncertain transport");
                }
            });
        foreach (var edit in new Action<JsonObject>[]
        {
            row => row.Remove("MessageAttemptedAt"), row => row.Remove("Recall"),
            row => row["MessageAttemptedAt"] = long.MaxValue,
            row => row["Recall"] = new JsonObject { ["Attempts"] = 3, ["UpdatedAt"] = row["UpdatedAt"]!.DeepClone(),
                ["Status"] = null, ["Reason"] = null, ["RetryAfter"] = 0, ["CredentialRejected"] = false }
        })
            await test("own recall malformed v4 proof fails closed without rewriting", async () =>
            {
                using var f = new Fixture(clock); f.Receive("first"); long message;
                using (PlatformSourceExecution.Enter(f.Lease("first"))) message = (await f.Send()).MessageId;
                f.Gateway.Dispose();
                var root = JsonNode.Parse(File.ReadAllText(f.LedgerPath))!; edit(root["Operations"]!.AsObject().Single().Value!.AsObject());
                var malformed = root.ToJsonString(); File.WriteAllText(f.LedgerPath, malformed); f.Restart();
                using var scope = PlatformSourceExecution.Enter(f.Lease("first"));
                Require((await f.Recall(message)).ReasonCode == "delivery_store_unavailable" && f.Http.Recalls.Count == 0
                    && File.ReadAllText(f.LedgerPath) == malformed, "invalid snapshot rewritten or used for recall");
            });
    }
}

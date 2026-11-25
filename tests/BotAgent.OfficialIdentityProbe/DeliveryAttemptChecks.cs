using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Domain.Platforms;

internal static partial class DeliveryLedgerChecks
{
    private static async Task AttemptChecksAsync(Func<string, Func<Task>, Task> test)
    {
        async Task Check(string name, Action body) => await test("delivery-attempt " + name, () => { body(); return Task.CompletedTask; });
        await Check("upload and final send share one reservation but commit separate attempts", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Guid.NewGuid();
            Require(ledger.Prepare(Trigger(), id, Content, true).Allowed, "media preparation failed");
            Require(!ledger.Prepare(Trigger(), id, Content, false).Allowed && !ledger.BeginAttempt(id).Allowed, "media kind was changed or upload bypassed");
            Require(ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload).Allowed, "upload not granted");
            Require(!ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload).Allowed && Refused(() => ledger.Complete(id, DeliveryResult.Ok())), "upload was replayed or treated as a message");
            Require(ledger.CompleteUpload(id, Content).State == OfficialDeliveryState.Uploaded, "upload receipt missing");
            Require(ledger.Read(id)?.Result is null && !ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload).Allowed, "upload produced delivery success");
            Require(ledger.BeginAttempt(id).Allowed, "final message not granted");
            var final = ledger.Complete(id, DeliveryResult.Ok());
            Require(final.MessageAttempts == 1 && final.UploadAttempts == 1 && final.Sequence == 1, "phase counters or shared sequence incorrect");
            Require(ledger.Read(Prepare(ledger, Trigger()))?.Sequence == 2, "media used more than one budget slot");
        });
        await Check("each phase permits exactly one confirmed credential retry", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Guid.NewGuid(); ledger.Prepare(Trigger(), id, Content, true);
            ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload); ledger.RecordCredentialRejection(id);
            Require(!ledger.BeginAttempt(id).Allowed && ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload).Allowed, "upload retry escaped its phase");
            Require(Refused(() => ledger.RecordCredentialRejection(id)), "third upload could be scheduled");
            ledger.CompleteUpload(id, Content); ledger.BeginAttempt(id); ledger.RecordCredentialRejection(id);
            Require(!ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload).Allowed && ledger.BeginAttempt(id).Allowed, "message retry escaped its phase");
            Require(Refused(() => ledger.RecordCredentialRejection(id)), "third message could be scheduled");
            var result = ledger.Complete(id, DeliveryResult.Rejected("authentication_invalid"));
            Require(result.UploadAttempts == 2 && result.MessageAttempts == 2 && result.Sequence == 1 && !ledger.BeginAttempt(id).Allowed, "retry refunded quota or replayed");
        });
        await Check("retry expiry and scope revocation are checked before another attempt", () =>
        {
            foreach (var revoke in new[] { false, true })
            {
                using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
                ledger.AcceptTrigger(Trigger(), clock.Now); var id = Prepare(ledger, Trigger()); ledger.BeginAttempt(id); ledger.RecordCredentialRejection(id);
                if (revoke) ledger.RevokeScope(Trigger().Scope.Conversation!); else clock.Now += TimeSpan.FromMinutes(5);
                Require(!ledger.BeginAttempt(id).Allowed && ledger.Read(id)?.State == OfficialDeliveryState.Abandoned, "invalid trigger allowed retry");
            }
        });
        await Check("media receipt expiry denies final send without refund", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Guid.NewGuid(); ledger.Prepare(Trigger(), id, Content, true); ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload);
            ledger.CompleteUpload(id, Content, clock.Now.UtcTicks + TimeSpan.TicksPerSecond); clock.Now += TimeSpan.FromSeconds(1);
            Require(ledger.BeginAttempt(id).ReasonCode == "media_reference_expired", "expired receipt reached final send");
            Require(ledger.Read(Prepare(ledger, Trigger()))?.Sequence == 2, "expired media refunded sequence");
        });
        await Check("revocation during upload prevents continuation", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Guid.NewGuid(); ledger.Prepare(Trigger(), id, Content, true); ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload);
            ledger.RevokeScope(Trigger().Scope.Conversation!);
            Require(ledger.CompleteUpload(id, Content).State == OfficialDeliveryState.Abandoned && !ledger.BeginAttempt(id).Allowed, "revoked upload continued");
        });
        await Check("partial upload cannot become a partial message receipt", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Guid.NewGuid(); ledger.Prepare(Trigger(), id, Content, true); ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload);
            Require(Refused(() => ledger.Complete(id, new(DeliveryStatus.PartialSuccess, ReasonCode: "partial"))), "partial upload became a message receipt");
        });
        await Check("unknown upload is terminal and never implies a sent message", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Guid.NewGuid(); ledger.Prepare(Trigger(), id, Content, true); ledger.BeginAttempt(id, OfficialDeliveryPhase.Upload);
            ledger.Complete(id, DeliveryResult.Unknown("delivery_outcome_unknown"));
            Require(Refused(() => ledger.RecordCredentialRejection(id)) && !ledger.BeginAttempt(id).Allowed, "uncertain upload was retried");
        });
        await Check("invalid persisted phase counters and receipts fail closed", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var ledger = Open(dir, clock)) { ledger.AcceptTrigger(Trigger(), clock.Now); Prepare(ledger, Trigger()); }
            var original = File.ReadAllText(dir.Map);
            foreach (var edit in new Action<JsonObject>[]
            {
                row => row["MessageAttempts"] = 3, row => row["MessageAttempts"] = 1, row => row.Remove("MessageAttempts"),
                row => row["Upload"] = new JsonObject { ["Attempts"] = 0, ["ReceiptDigest"] = Content, ["ExpiresAt"] = 0 },
                row => row["State"] = (int)OfficialDeliveryState.Uploaded
            })
            {
                var root = JsonNode.Parse(original)!.AsObject(); edit(root["Operations"]!.AsObject().First().Value!.AsObject());
                var invalid = root.ToJsonString(); File.WriteAllText(dir.Map, invalid);
                Require(Refused(() => Open(dir, clock).Dispose()) && File.ReadAllText(dir.Map) == invalid, "invalid attempt snapshot was accepted or rebuilt");
            }
        });
    }
}

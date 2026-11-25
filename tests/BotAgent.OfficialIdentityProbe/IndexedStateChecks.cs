using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;

internal static partial class DeliveryLedgerChecks
{
    private static OfficialIdentityKey Indexed(string index, string id = "shared")
    {
        var raw = Trigger(id); return new(raw.Scope, raw.NativeId, index);
    }

    public static async Task RunIndexedAsync(Func<string, Func<Task>, Task> test)
    {
        async Task Check(string name, Action body) => await test("indexed-state " + name, () => { body(); return Task.CompletedTask; });
        await Check("aliases preserve old keys and indexed scope across reload", () =>
        {
            using var dir = new SyntheticDirectory(); var ids = new OfficialIdentityMap(dir.Map);
            var legacy = Trigger("shared"); var old = ids.AliasFor(legacy);
            var first = ids.AliasFor(Indexed("IDX_one")); var second = ids.AliasFor(Indexed("IDX_two"));
            Require(new[] { old, first, second }.Distinct().Count() == 3, "indexed aliases merged");
            var reopened = new OfficialIdentityMap(dir.Map);
            Require(reopened.AliasFor(legacy) == old && reopened.AliasFor(Indexed("IDX_one")) == first
                && reopened.IdentityOf(second, legacy.Scope) == Indexed("IDX_two")
                && reopened.OriginalOf(first, legacy.Scope) == "shared", "alias reload changed identity");
            Require(reopened.IdentityOf(first, Trigger(target: "different").Scope) is null, "complete identity escaped expected scope");
            Require(Refused(() => reopened.ExportLegacySnapshot()), "downgrade discarded an index");
        });
        await Check("index validation never decorates unrelated identity kinds or outgoing receipts", () =>
        {
            Require(Refused(() => new OfficialIdentityKey(new(App, OfficialIdentityKind.Group), "target", "IDX_one")), "group acquired message index");
            var inbound = Trigger(); var outbound = new OfficialIdentityScope(App, OfficialIdentityKind.Message, inbound.Scope.Conversation, OfficialMessageDirection.Outbound);
            Require(Refused(() => new OfficialIdentityKey(outbound, "receipt", "IDX_one")), "outbound receipt acquired inbound index");
            foreach (var index in new[] { "", "bad index", new string('x', 513), "bad\ud800" })
                Require(Refused(() => Indexed(index)), "invalid index accepted");
        });
        await Check("v2 migration preserves attempts receipts sequence and revocation", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); Guid completed, attempting, prepared;
            var legacy = Trigger("legacy"); var removed = Trigger("removed", target: "removed-target");
            using (var ledger = Open(dir, clock))
            {
                ledger.AcceptTrigger(legacy, clock.Now); completed = Prepare(ledger, legacy);
                ledger.BeginAttempt(completed); ledger.Complete(completed, DeliveryResult.Ok());
                attempting = Prepare(ledger, legacy); ledger.BeginAttempt(attempting); prepared = Prepare(ledger, legacy);
                ledger.AcceptTrigger(removed, clock.Now); ledger.RevokeScope(removed.Scope.Conversation!);
            }
            var v2 = JsonNode.Parse(File.ReadAllText(dir.Map))!.AsObject(); v2["Version"] = 2;
            foreach (var row in v2["Triggers"]!.AsObject()) row.Value!.AsObject().Remove("EventKeys");
            foreach (var row in v2["Operations"]!.AsObject())
            { row.Value!.AsObject().Remove("MessageAttemptedAt"); row.Value!.AsObject().Remove("Recall"); }
            File.WriteAllText(dir.Map, v2.ToJsonString());
            using var reopened = Open(dir, clock);
            Require(reopened.Read(completed)?.Result?.Status == DeliveryStatus.Success
                && reopened.Read(attempting)?.Result?.Status == DeliveryStatus.Unknown
                && reopened.Read(prepared)?.State == OfficialDeliveryState.Abandoned, "v2 migration lost operation state");
            Require(!reopened.BeginAttempt(attempting).Allowed && !reopened.BeginAttempt(prepared).Allowed, "migration replayed old operation");
            Require(reopened.AcceptTrigger(legacy, clock.Now).ReasonCode == "trigger_duplicate"
                && reopened.AcceptTrigger(Indexed("IDX_guess", "legacy"), clock.Now).ReasonCode == "trigger_identity_ambiguous", "legacy event was reattributed");
            Require(reopened.Prepare(legacy, Guid.NewGuid(), Content).Operation?.Sequence == 4
                && !reopened.Prepare(removed, Guid.NewGuid(), Content).Allowed, "migration reset sequence or revocation");
            Require(JsonNode.Parse(File.ReadAllText(dir.Map))!["Version"]!.GetValue<int>() == 4, "migration did not persist v4");
        });
        await Check("v3 and v2 reject missing extra duplicate or invalid event metadata without rewriting bytes", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var ledger = Open(dir, clock)) ledger.AcceptTrigger(Indexed("IDX_one"), clock.Now);
            var original = File.ReadAllText(dir.Map);
            foreach (var change in new Action<JsonObject>[] {
                root => root["Triggers"]!.AsObject().First().Value!.AsObject().Remove("EventKeys"),
                root => root["Version"] = 2,
                root => root["Triggers"]!.AsObject().First().Value!["EventKeys"] = new JsonArray(),
                root => root["Triggers"]!.AsObject().First().Value!["EventKeys"] = new JsonArray("invalid"),
                root => { var row = root["Triggers"]!.AsObject().First().Value!; row["EventKeys"]!.AsArray().Add(row["EventKeys"]![0]!.DeepClone()); } })
            {
                var root = JsonNode.Parse(original)!.AsObject(); change(root); var corrupted = root.ToJsonString();
                File.WriteAllText(dir.Map, corrupted);
                Require(Refused(() => { using var rejected = Open(dir, clock); }), "corrupt event state was admitted");
                Require(File.ReadAllText(dir.Map) == corrupted, "corrupt state was rewritten");
            }
        });
        await Check("unobserved or missing index cannot borrow a known credential", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Indexed("IDX_one"), clock.Now);
            Require(!ledger.Prepare(Indexed("IDX_unknown"), Guid.NewGuid(), Content).Allowed
                && !ledger.Prepare(Trigger("shared"), Guid.NewGuid(), Content).Allowed, "unobserved identity borrowed permission");
            Require(ledger.Prepare(Indexed("IDX_one"), Guid.NewGuid(), Content).Operation?.Sequence == 1, "rejected index consumed budget");
        });
        await Check("reordered index shortens expiry and cannot extend an earlier event", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Indexed("IDX_later"), clock.Now);
            Require(ledger.AcceptTrigger(Indexed("IDX_earlier"), clock.Now.AddMinutes(-4)).Allowed, "valid earlier index refused");
            var id = Prepare(ledger, Indexed("IDX_later")); clock.Now += TimeSpan.FromMinutes(1);
            Require(!ledger.BeginAttempt(id).Allowed, "reordered timestamp extended credential");
        });
        await Check("ambiguous indexed commit cannot deliver or replay after restart", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var storage = new FaultSnapshot(new OfficialDeliverySnapshotFile(dir.Map)))
            using (var ledger = new OfficialDeliveryLedger(storage, App, () => clock.Now))
            {
                ledger.AcceptTrigger(Indexed("IDX_one"), clock.Now); storage.FailAfterCommit = true;
                Require(Refused(() => ledger.AcceptTrigger(Indexed("IDX_two"), clock.Now)), "ambiguous commit admitted host work");
                storage.FailAfterCommit = false;
                Require(Refused(() => ledger.Prepare(Indexed("IDX_two"), Guid.NewGuid(), Content)), "retired writer granted send");
            }
            using var reopened = Open(dir, clock);
            Require(reopened.AcceptTrigger(Indexed("IDX_two"), clock.Now).ReasonCode == "trigger_duplicate", "ambiguous committed event replayed");
            Require(reopened.AcceptTrigger(Indexed("IDX_three"), clock.Now).Allowed, "fresh event remained blocked");
        });
        await test("indexed-state concurrent events reserve one shared sequence budget", async () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            var keys = Enumerable.Range(0, 8).Select(i => Indexed("IDX_" + i)).ToArray();
            foreach (var key in keys) Require(ledger.AcceptTrigger(key, clock.Now).Allowed, "concurrency precondition rejected index");
            var results = await Task.WhenAll(keys.Select(key => Task.Run(() => ledger.Prepare(key, Guid.NewGuid(), Content))));
            Require(results.Count(r => r.Allowed) == 5 && results.Where(r => r.Allowed).Select(r => r.Operation!.Sequence).Order().SequenceEqual([1, 2, 3, 4, 5]), "concurrent indexes multiplied sequences");
        });
        await Check("event capacity counts indexes even when they share one credential", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var ledger = Open(dir, clock))
            {
                ledger.AcceptTrigger(Indexed("IDX_one"), clock.Now);
                InjectSnapshot(ledger, root =>
                {
                    var events = root["Triggers"]!.AsObject().First().Value!["EventKeys"]!.AsArray();
                    for (var i = 1; i < 10000; i++) events.Add(i.ToString("x64"));
                });
                Require(ledger.AcceptTrigger(Indexed("IDX_overflow"), clock.Now).ReasonCode == "ledger_capacity"
                    && ledger.AcceptTrigger(Trigger("new-credential"), clock.Now).ReasonCode == "ledger_capacity", "shared credential bypassed event capacity");
            }
            using var reopened = Open(dir, clock);
            Require(reopened.AcceptTrigger(Indexed("IDX_one"), clock.Now).ReasonCode == "trigger_duplicate", "capacity recovery lost dedup evidence");
        });
    }
}

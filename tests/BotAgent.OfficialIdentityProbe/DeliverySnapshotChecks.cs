using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Platforms;
using BotAgent.Services;

internal static partial class DeliveryLedgerChecks
{
    private sealed class FaultSnapshot(IOfficialDeliverySnapshot inner) : IOfficialDeliverySnapshot
    {
        public bool FailBeforeCommit { get; set; }
        public bool FailAfterCommit { get; set; }
        public string? Load() => inner.Load();
        public void Commit(string snapshot)
        {
            if (FailBeforeCommit) throw new IOException("synthetic_write_denied");
            inner.Commit(snapshot);
            if (FailAfterCommit) throw new IOException("synthetic_ambiguous_publication");
        }
        public void Dispose() => inner.Dispose();
    }

    // Capacity setup replaces SQL fixture insertion; the behavior assertions remain unchanged.
    private static void InjectSnapshot(OfficialDeliveryLedger ledger, Action<JsonObject> edit)
    {
        var field = typeof(OfficialDeliveryLedger).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var current = field.GetValue(ledger)!;
        var state = JsonSerializer.SerializeToNode(current, current.GetType())!.AsObject();
        edit(state);
        field.SetValue(ledger, JsonSerializer.Deserialize(state.ToJsonString(), current.GetType()));
    }

    private static async Task SnapshotChecksAsync(Func<string, Func<Task>, Task> test)
    {
        async Task Check(string name, Action body) => await test("delivery-snapshot " + name, () => { body(); return Task.CompletedTask; });
        await Check("ambiguous committed attempt becomes unknown on reopen", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); Guid id;
            using (var storage = new FaultSnapshot(new OfficialDeliverySnapshotFile(dir.Map)))
            using (var ledger = new OfficialDeliveryLedger(storage, App, () => clock.Now))
            {
                ledger.AcceptTrigger(Trigger(), clock.Now); id = Prepare(ledger, Trigger());
                storage.FailAfterCommit = true;
                Require(Refused(() => ledger.BeginAttempt(id)), "ambiguous commit granted HTTP");
                storage.FailAfterCommit = false;
                Require(Refused(() => ledger.BeginAttempt(id)), "ambiguous writer resumed");
            }
            using var reopened = Open(dir, clock);
            Require(reopened.Read(id)?.Result?.Status == DeliveryStatus.Unknown && !reopened.BeginAttempt(id).Allowed, "ambiguous attempt replayed");
        });
        await Check("actual atomic replacement failure grants no send", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Prepare(ledger, Trigger());
            var original = File.ReadAllBytes(dir.Map);
            File.Delete(dir.Map); Directory.CreateDirectory(dir.Map);
            try { Require(Refused(() => ledger.BeginAttempt(id)), "failed replacement granted HTTP"); }
            finally { Directory.Delete(dir.Map); File.WriteAllBytes(dir.Map, original); }
            Require(Refused(() => ledger.BeginAttempt(id)), "replacement failure did not retire writer");
        });
        await Check("schema and reference corruption never replaces original bytes", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var ledger = Open(dir, clock)) { ledger.AcceptTrigger(Trigger(), clock.Now); Prepare(ledger, Trigger()); }
            var original = File.ReadAllText(dir.Map);
            var changes = new Action<JsonObject>[]
            {
                root => root["Version"] = 99,
                root => root["Version"] = 1,
                root => root["ApplicationHash"] = new string('0', 64),
                root => root["Unexpected"] = true,
                root => root.Remove("ClockFloor"),
                root => root["ClockFloor"] = -1,
                root => root["Triggers"]!.AsObject().First().Value!["NextSequence"] = 0,
                root => root["Triggers"]!.AsObject().First().Value!["Budget"] = 99,
                root => root["Triggers"]!.AsObject().First().Value!["ExpiresAt"] = 0,
                root => root["Operations"]!.AsObject().First().Value!["TriggerKey"] = new string('1', 64),
                root => root["Operations"]!.AsObject().First().Value!["ContentDigest"] = "invalid",
                root => root["Operations"]!.AsObject().First().Value!["State"] = 99,
                root => root["Operations"]!.AsObject().First().Value!["UpdatedAt"] = long.MaxValue,
                root => root["Operations"]!.AsObject().First().Value!["Status"] = 0,
                root => root["Operations"]!.AsObject().First().Value!["MessageAlias"] = long.MaxValue,
                root => root["Operations"]!.AsObject().First().Value!["Reason"] = "synthetic-private-canary",
                root => root["Operations"]!.AsObject().First().Value!["RetryAfter"] = -1,
                root => root["Scopes"]!["invalid"] = clock.Now.UtcTicks,
                root => root["Triggers"] = null,
                root => root["Operations"]!.AsObject()[Guid.NewGuid().ToString()] = null
            };
            foreach (var change in changes)
            {
                var root = JsonNode.Parse(original)!.AsObject(); change(root);
                File.WriteAllText(dir.Map, root.ToJsonString()); var before = SHA256.HashData(File.ReadAllBytes(dir.Map));
                Require(Refused(() => { using var ignored = Open(dir, clock); }), "invalid snapshot admitted");
                Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(dir.Map))), "invalid snapshot replaced");
            }
            File.WriteAllText(dir.Map, original.Insert(1, "\"Version\":1,"));
            Require(Refused(() => { using var ignored = Open(dir, clock); }), "duplicate JSON field admitted");
        });
        await Check("different JSON keys cannot overwrite the same operation identity", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var ledger = Open(dir, clock)) { ledger.AcceptTrigger(Trigger(), clock.Now); Prepare(ledger, Trigger()); }
            var root = JsonNode.Parse(File.ReadAllText(dir.Map))!.AsObject();
            var rows = root["Operations"]!.AsObject(); var first = rows.First();
            rows.Add(Guid.Parse(first.Key).ToString("N"), first.Value!.DeepClone());
            File.WriteAllText(dir.Map, root.ToJsonString());
            Require(Refused(() => { using var ignored = Open(dir, clock); }), "duplicate decoded operation accepted");
        });
        await Check("full capacity snapshot fits size bound and remains recoverable", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var ledger = Open(dir, clock))
            {
                ledger.AcceptTrigger(Trigger(), clock.Now); var id = Prepare(ledger, Trigger());
                ledger.BeginAttempt(id); ledger.Complete(id, DeliveryResult.Rejected("synthetic_terminal"));
            }
            var bytes = WriteCapacityFixture(dir.Map, clock.Now.UtcTicks);
            Require(bytes < IOfficialDeliverySnapshot.MaxBytes, "declared capacity exceeds snapshot bound");
            Console.WriteLine($"SNAPSHOT_CAPACITY_INPUT bytes={bytes} heap_bytes={GC.GetTotalMemory(false)} heap_limit_bytes={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}");
            var timer = Stopwatch.StartNew();
            using var reopened = Open(dir, clock);
            var recovery = timer.ElapsedMilliseconds; timer.Restart();
            Require(reopened.AcceptTrigger(Trigger("overflow-full-snapshot"), clock.Now).ReasonCode == "ledger_capacity", "full snapshot evicted old entries");
            Console.WriteLine($"SNAPSHOT_CAPACITY triggers=10000 operations=50000 scopes=10000 bytes={bytes} recovery_ms={recovery} commit_ms={timer.ElapsedMilliseconds} peak_working_set_bytes={Process.GetCurrentProcess().PeakWorkingSet64}");
        });
        await Check("snapshot byte cap refuses writes before replacing state", () =>
        {
            using var dir = new SyntheticDirectory(); using var storage = new OfficialDeliverySnapshotFile(dir.Map);
            storage.Commit("synthetic-original");
            try { storage.Commit(new string('界', IOfficialDeliverySnapshot.MaxBytes / 3 + 1)); throw new InvalidOperationException("oversize commit accepted"); }
            catch (InvalidDataException) { }
            Require(storage.Load() == "synthetic-original", "oversize snapshot replaced original");
        });
    }

    private static long WriteCapacityFixture(string path, long now)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var trigger = root["Triggers"]!.AsObject().First().Value!.DeepClone();
        var operation = root["Operations"]!.AsObject().First().Value!.DeepClone();
        using var output = File.Create(path);
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        foreach (var field in root.Where(item => item.Key is not ("Triggers" or "Operations" or "Scopes")))
        {
            writer.WritePropertyName(field.Key); field.Value!.WriteTo(writer);
        }
        writer.WriteStartObject("Triggers");
        for (var n = 1; n <= 10000; n++)
        {
            var key = n.ToString("x64"); var row = trigger.DeepClone();
            row["NextSequence"] = 5; row["ScopeKey"] = key; row["Revoked"] = true;
            writer.WritePropertyName(key); row.WriteTo(writer);
            if (n % 100 == 0) writer.Flush();
        }
        writer.WriteEndObject(); writer.WriteStartObject("Operations");
        for (var n = 1; n <= 10000; n++)
        {
            for (var seq = 1; seq <= 5; seq++)
            {
                var row = operation.DeepClone(); row["TriggerKey"] = n.ToString("x64"); row["Sequence"] = seq;
                writer.WritePropertyName(Guid.NewGuid().ToString()); row.WriteTo(writer);
            }
            if (n % 100 == 0) writer.Flush();
        }
        writer.WriteEndObject(); writer.WriteStartObject("Scopes");
        for (var n = 1; n <= 10000; n++)
        {
            writer.WriteNumber(n.ToString("x64"), now);
            if (n % 100 == 0) writer.Flush();
        }
        writer.WriteEndObject(); writer.WriteEndObject(); writer.Flush(); output.Flush(flushToDisk: true);
        return output.Length;
    }
}

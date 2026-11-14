using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using Microsoft.Data.Sqlite;
using BotAgent.Services;

internal static partial class DeliveryLedgerChecks
{
    private const string App = "synthetic-ledger-app";
    private static readonly string Content = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-content-canary")));
    private static readonly Guid CrashOperation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static OfficialIdentityKey Trigger(string id = "synthetic-trigger-canary", bool group = true, string target = "synthetic-target-canary", string app = App)
        => new(new(app, OfficialIdentityKind.Message, new(PlatformId.QqOfficial, app,
            group ? ConversationKind.GroupChat : ConversationKind.PrivateChat, target), OfficialMessageDirection.Inbound), id);
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
    private static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return true; }
    }
    private static OfficialDeliveryLedger Open(SyntheticDirectory directory, FrozenClock clock) => new(new OfficialDeliverySnapshotFile(directory.Map), App, () => clock.Now);
    private static Guid Prepare(OfficialDeliveryLedger ledger, OfficialIdentityKey trigger)
    {
        var id = Guid.NewGuid();
        Require(ledger.Prepare(trigger, id, Content).Allowed, "prepare refused");
        return id;
    }

    public static async Task ChildAsync(string path, string phase)
    {
        var clock = new FrozenClock();
        using var ledger = new OfficialDeliveryLedger(new OfficialDeliverySnapshotFile(path), App, () => clock.Now);
        Require(ledger.AcceptTrigger(Trigger(), clock.Now).Allowed, "child accept refused");
        var media = phase is "uploading" or "uploaded" or "upload-rejected" or "upload-retrying";
        Require(ledger.Prepare(Trigger(), CrashOperation, Content, media).Allowed, "child prepare refused");
        if (phase != "prepared") Require(ledger.BeginAttempt(CrashOperation, media ? OfficialDeliveryPhase.Upload : OfficialDeliveryPhase.Message).Allowed, "child begin refused");
        if (phase == "uploaded") ledger.CompleteUpload(CrashOperation, Content);
        if (phase is "upload-rejected" or "message-rejected" or "upload-retrying" or "message-retrying") ledger.RecordCredentialRejection(CrashOperation);
        if (phase is "upload-retrying" or "message-retrying")
            Require(ledger.BeginAttempt(CrashOperation, media ? OfficialDeliveryPhase.Upload : OfficialDeliveryPhase.Message).Allowed, "child retry refused");
        if (phase == "completed") ledger.Complete(CrashOperation, DeliveryResult.Ok(), Channels.AliasBase + 123);
        Console.WriteLine("LEDGER_COMMITTED");
        await Task.Delay(Timeout.Infinite);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        async Task Check(string name, Action body) => await test("delivery-ledger " + name, () => { body(); return Task.CompletedTask; });
        await Check("group budget reserves exactly five unique sequences", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            Require(ledger.AcceptTrigger(Trigger(), clock.Now).Allowed, "accept failed");
            for (var n = 1; n <= 5; n++) Require(ledger.Read(Prepare(ledger, Trigger()))?.Sequence == n, "sequence was reused");
            Require(ledger.Prepare(Trigger(), Guid.NewGuid(), Content).ReasonCode == "trigger_budget_exhausted", "sixth reservation escaped budget");
        });
        await Check("C2C gets sixty minutes and four reservations", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            var trigger = Trigger(group: false); Require(ledger.AcceptTrigger(trigger, clock.Now).Allowed, "C2C accept failed");
            clock.Now += TimeSpan.FromMinutes(59);
            for (var n = 1; n <= 4; n++) Require(ledger.BeginAttempt(Prepare(ledger, trigger)).Allowed, "C2C window shortened");
            Require(!ledger.Prepare(trigger, Guid.NewGuid(), Content).Allowed, "fifth C2C reservation allowed");
        });
        await Check("event time fixes expiry and duplicate cannot renew", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            Require(ledger.AcceptTrigger(Trigger(), clock.Now - TimeSpan.FromMinutes(4)).Allowed, "old but valid event refused");
            var id = Prepare(ledger, Trigger()); clock.Now += TimeSpan.FromMinutes(1);
            Require(ledger.AcceptTrigger(Trigger(), clock.Now).ReasonCode == "trigger_duplicate", "duplicate renewed window");
            Require(!ledger.BeginAttempt(id).Allowed && ledger.Read(id)?.State == OfficialDeliveryState.Abandoned, "exact deadline allowed send");
            Require(!ledger.AcceptTrigger(Trigger("future"), clock.Now.AddTicks(1)).Allowed, "future event accepted");
        });
        await Check("application conversation and direction cannot cross bind", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            var first = Trigger(); var other = Trigger(target: "synthetic-other");
            Require(ledger.AcceptTrigger(first, clock.Now).Allowed && ledger.AcceptTrigger(other, clock.Now).Allowed, "scope collision");
            Require(ledger.AcceptTrigger(Trigger(group: false), clock.Now).Allowed, "direction scope collision");
            Require(Refused(() => ledger.AcceptTrigger(Trigger(app: "other-app"), clock.Now)), "foreign app accepted");
            var outbound = new OfficialIdentityKey(new(App, OfficialIdentityKind.Message, first.Scope.Conversation, OfficialMessageDirection.Outbound), "synthetic-outbound");
            Require(Refused(() => ledger.AcceptTrigger(outbound, clock.Now)), "outbound accepted as trigger");
            var id = Prepare(ledger, first);
            Require(ledger.Prepare(other, id, Content).ReasonCode == "operation_conflict", "operation changed trigger");
            Require(ledger.Prepare(first, id, new string('a', 64)).ReasonCode == "operation_conflict", "operation changed content");
        });
        await Check("one prepared operation grants only one attempt", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Prepare(ledger, Trigger());
            Require(!ledger.Prepare(Trigger(), id, Content).Allowed, "repeated prepare returned a new grant");
            Require(ledger.BeginAttempt(id).Allowed && !ledger.BeginAttempt(id).Allowed, "attempt can replay");
            ledger.Complete(id, DeliveryResult.Throttled(7));
            Require(!ledger.BeginAttempt(id).Allowed && ledger.Read(id)?.Result?.RetryAfterSeconds == 7, "terminal operation replayed or receipt lost");
            Require(Refused(() => ledger.Complete(id, DeliveryResult.Ok())), "terminal receipt overwritten");
        });
        await test("delivery-ledger concurrent prepare and begin preserve budget", async () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now);
            var decisions = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => ledger.Prepare(Trigger(), Guid.NewGuid(), Content))));
            var accepted = decisions.Where(d => d.Allowed).Select(d => d.Operation!).ToArray();
            Require(accepted.Length == 5 && accepted.Select(o => o.Sequence).Distinct().Count() == 5, "concurrency exceeded budget");
            var starts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ledger.BeginAttempt(accepted[0].OperationId).Allowed)));
            Require(starts.Count(x => x) == 1, "concurrent duplicate HTTP grants");
        });
        await Check("restart retains dedup sequences and never resumes preparations", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); Guid id;
            using (var ledger = Open(dir, clock)) { ledger.AcceptTrigger(Trigger(), clock.Now); id = Prepare(ledger, Trigger()); }
            using var reopened = Open(dir, clock);
            Require(reopened.Read(id)?.Result?.ReasonCode == "abandoned_before_attempt" && !reopened.BeginAttempt(id).Allowed, "prepared operation resumed");
            Require(reopened.AcceptTrigger(Trigger(), clock.Now).ReasonCode == "trigger_duplicate", "restart lost dedup");
            Require(reopened.Read(Prepare(reopened, Trigger()))?.Sequence == 2, "restart reused sequence");
        });
        await Check("scope removal fences old triggers and prepared operations", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Prepare(ledger, Trigger());
            ledger.RevokeScope(Trigger().Scope.Conversation!);
            Require(!ledger.BeginAttempt(id).Allowed && !ledger.AcceptTrigger(Trigger("new-id-old-time"), clock.Now).Allowed, "revoked scope resumed");
            Require(ledger.AcceptTrigger(Trigger("other", target: "unrelated-target"), clock.Now).Allowed, "unrelated scope revoked");
            clock.Now += TimeSpan.FromSeconds(1);
            Require(ledger.AcceptTrigger(Trigger("fresh"), clock.Now).Allowed, "new post-removal event refused");
        });
        await Check("clock rollback cannot prolong an existing trigger", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var ledger = Open(dir, clock)) ledger.AcceptTrigger(Trigger(), clock.Now);
            clock.Now -= TimeSpan.FromMinutes(1); using var reopened = Open(dir, clock);
            Require(Refused(() => reopened.Prepare(Trigger(), Guid.NewGuid(), Content)), "clock rollback extended window");
            clock.Now += TimeSpan.FromMinutes(1); Require(reopened.Prepare(Trigger(), Guid.NewGuid(), Content).Allowed, "clock catch-up did not recover");
        });
        await Check("single writer and wrong application refuse without replacement", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var ledger = Open(dir, clock))
            {
                ledger.AcceptTrigger(Trigger(), clock.Now);
                Require(Refused(() => { using var second = Open(dir, clock); }), "second writer admitted");
            }
            var before = SHA256.HashData(File.ReadAllBytes(dir.Map));
            Require(Refused(() => { using var wrong = new OfficialDeliveryLedger(new OfficialDeliverySnapshotFile(dir.Map), "other-app", () => clock.Now); }), "wrong application admitted");
            Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(dir.Map))), "wrong application changed journal");
        });
        await Check("corrupt and unrelated databases are not rebuilt", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            File.WriteAllText(dir.Map, "synthetic-corrupt-journal"); var before = File.ReadAllBytes(dir.Map);
            Require(Refused(() => { using var ignored = Open(dir, clock); }) && before.SequenceEqual(File.ReadAllBytes(dir.Map)), "corrupt journal replaced");
            File.Delete(dir.Map);
            using (var db = new SqliteConnection($"Data Source={dir.Map};Pooling=False"))
            { db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE unrelated(value TEXT)"; command.ExecuteNonQuery(); }
            before = File.ReadAllBytes(dir.Map);
            Require(Refused(() => { using var ignored = Open(dir, clock); }) && before.SequenceEqual(File.ReadAllBytes(dir.Map)), "unrelated database modified");
        });
        await Check("storage denial grants no attempt and retires writer", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using var storage = new FaultSnapshot(new OfficialDeliverySnapshotFile(dir.Map));
            using var ledger = new OfficialDeliveryLedger(storage, App, () => clock.Now);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Prepare(ledger, Trigger());
            storage.FailBeforeCommit = true;
            Require(Refused(() => ledger.BeginAttempt(id)), "failed commit granted attempt");
            storage.FailBeforeCommit = false;
            Require(Refused(() => ledger.BeginAttempt(id)), "retired writer resumed");
        });
        await Check("journal stores no native IDs body or result wording", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
            using (var ledger = Open(dir, clock))
            {
                ledger.AcceptTrigger(Trigger(), clock.Now); var id = Prepare(ledger, Trigger()); ledger.BeginAttempt(id);
                ledger.Complete(id, DeliveryResult.Unknown("delivery_outcome_unknown", "synthetic-response-canary"));
            }
            var bytes = Encoding.UTF8.GetString(File.ReadAllBytes(dir.Map));
            foreach (var forbidden in new[] { App, "synthetic-trigger-canary", "synthetic-target-canary", "synthetic-content-canary", "synthetic-response-canary" })
                Require(!bytes.Contains(forbidden, StringComparison.Ordinal), "private material persisted");
        });
        await Check("full revocation cache still invalidates prepared operations durably", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); Guid id;
            using (var ledger = Open(dir, clock))
            {
                ledger.AcceptTrigger(Trigger(), clock.Now); id = Prepare(ledger, Trigger());
                InjectSnapshot(ledger, state =>
                {
                    var scopes = state["Scopes"]!.AsObject();
                    for (var n = 1; n <= 10000; n++) scopes.Add(n.ToString("x64"), clock.Now.UtcTicks);
                });
                try { ledger.RevokeScope(Trigger().Scope.Conversation!); } catch (InvalidOperationException) { }
                Require(!ledger.BeginAttempt(id).Allowed, "full revocation table left old preparation enabled");
            }
            using var reopened = Open(dir, clock);
            Require(!reopened.AcceptTrigger(Trigger("old-after-restart"), clock.Now).Allowed, "full-table revocation barrier was not durable");
        });
        await Check("full trigger table rejects new entries without forgetting old work", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Prepare(ledger, Trigger());
            InjectSnapshot(ledger, state =>
            {
                var triggers = state["Triggers"]!.AsObject();
                var sample = triggers.First().Value!.DeepClone();
                sample["NextSequence"] = 0;
                for (var n = 1; n < 10000; n++) triggers.Add(n.ToString("x64"), sample.DeepClone());
            });
            Require(ledger.AcceptTrigger(Trigger("overflow"), clock.Now).ReasonCode == "ledger_capacity", "capacity allowed eviction");
            Require(ledger.Read(id)?.State == OfficialDeliveryState.Prepared && ledger.BeginAttempt(id).Allowed, "capacity forgot existing preparation");
        });
        await Check("retention separates confirmed and unknown outcomes and rejects pruned replay", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            var eventTime = clock.Now; ledger.AcceptTrigger(Trigger(), eventTime);
            var known = Prepare(ledger, Trigger()); var unknown = Prepare(ledger, Trigger());
            ledger.BeginAttempt(known); ledger.Complete(known, DeliveryResult.Ok());
            ledger.BeginAttempt(unknown); ledger.Complete(unknown, DeliveryResult.Unknown("delivery_outcome_unknown"));
            clock.Now += TimeSpan.FromDays(2); ledger.AcceptTrigger(Trigger("two-days"), clock.Now);
            Require(ledger.Read(known) is null && ledger.Read(unknown)?.Result?.Status == DeliveryStatus.Unknown, "unknown receipt pruned too soon");
            clock.Now += TimeSpan.FromDays(6); ledger.AcceptTrigger(Trigger("eight-days"), clock.Now);
            Require(ledger.Read(unknown) is null && !ledger.AcceptTrigger(Trigger(), eventTime).Allowed, "pruned old event was resurrected");
        });
        await Check("receipt refuses an alias outside the existing safe range", () =>
        {
            using var dir = new SyntheticDirectory(); var clock = new FrozenClock(); using var ledger = Open(dir, clock);
            ledger.AcceptTrigger(Trigger(), clock.Now); var id = Prepare(ledger, Trigger()); ledger.BeginAttempt(id);
            Require(Refused(() => ledger.Complete(id, DeliveryResult.Ok(), long.MaxValue)), "unsafe alias entered durable receipt");
        });
        await SnapshotChecksAsync(test);
        await AttemptChecksAsync(test);
        await RunCrashRecoveryAsync(test);
    }

    public static async Task RunCrashRecoveryAsync(Func<string, Func<Task>, Task> test)
    {
        foreach (var phase in new[] { "prepared", "attempting", "completed", "uploading", "uploaded", "upload-rejected", "message-rejected", "upload-retrying", "message-retrying" })
            await test("delivery-ledger process kill after " + phase, async () =>
            {
                using var dir = new SyntheticDirectory(); var clock = new FrozenClock();
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--ledger-child", dir.Map, phase }) start.ArgumentList.Add(arg);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("child_not_started");
                try
                {
                    Require(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) == "LEDGER_COMMITTED", "child did not commit");
                    child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    using var reopened = Open(dir, clock); var operation = reopened.Read(CrashOperation)!;
                    Require(!reopened.BeginAttempt(CrashOperation).Allowed, "crashed operation replayed");
                    Require(operation.Result?.Status == (phase is "attempting" or "uploading" or "upload-retrying" or "message-retrying"
                        ? DeliveryStatus.Unknown : phase == "completed" ? DeliveryStatus.Success : DeliveryStatus.Rejected), "crash outcome was invented");
                    Require(!reopened.BeginAttempt(CrashOperation, OfficialDeliveryPhase.Upload).Allowed, "crashed upload replayed");
                    if (phase == "upload-retrying") Require(operation.UploadAttempts == 2, "upload retry was not durable");
                    if (phase == "message-retrying") Require(operation.MessageAttempts == 2, "message retry was not durable");
                    if (phase == "completed") Require(operation.Result?.MessageId == (Channels.AliasBase + 123).ToString(), "success receipt lost");
                    Console.WriteLine($"CRASH_CHECK phase={phase} recovery_assertions=passed child_exited={child.HasExited}");
                }
                catch (Exception error)
                {
                    Console.WriteLine($"CRASH_CHECK phase={phase} recovery_assertions=failed type={error.GetType().Name} hresult={error.HResult}");
                    var watch = Stopwatch.StartNew();
                    for (var attempt = 0; attempt < 6; attempt++)
                    {
                        try
                        {
                            using var probeLock = new FileStream(dir.Map + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            Console.WriteLine($"CRASH_LOCK phase={phase} elapsed_ms={watch.ElapsedMilliseconds} available=true");
                            break;
                        }
                        catch (IOException locked)
                        {
                            Console.WriteLine($"CRASH_LOCK phase={phase} elapsed_ms={watch.ElapsedMilliseconds} available=false hresult={locked.HResult}");
                        }
                        await Task.Delay(10);
                    }
                    throw;
                }
                finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            });
    }
}

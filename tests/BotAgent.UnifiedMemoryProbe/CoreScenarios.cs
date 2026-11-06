using BotAgent.Domain.Configuration;
using BotAgent.Domain.Memory;
using BotAgent.Engine.Memory;
using static BotAgent.UnifiedMemoryProbe.Probe;

namespace BotAgent.UnifiedMemoryProbe;

internal static class CoreScenarios
{
    public static void Run()
    {
        Isolation();
        Lifecycle();
        FailureAndExpiry();
        AtomicUse();
        ConflictsAndDenial();
    }

    private static void Isolation()
    {
        var store = new VolatileStore();
        var memory = new UnifiedMemory(store);
        Check(!memory.Enabled && memory.RequestRemember(Source, Request()).Status == UnifiedMemoryStatus.Disabled, "default disabled");
        memory.SetEnabled(true);
        Check(memory.RequestRemember(Source with { IsReliableIdentity = false }, Request()).Status == UnifiedMemoryStatus.UnreliableIdentity, "unreliable denied");
        Check(memory.RequestRemember(Source with { Actor = null }, Request()).Status == UnifiedMemoryStatus.UnreliableIdentity, "missing identity denied");
        Check(memory.RequestRemember(Source with { Driver = default }, Request()).Status == UnifiedMemoryStatus.UnreliableIdentity, "missing driver denied");
        Check(memory.RequestRemember(Source, Request() with { ExplicitSelfExpression = false }).Status == UnifiedMemoryStatus.ExplicitRequestRequired, "inference denied");
        Check(memory.RequestRemember(Source, Request("brief; secret health detail")).Status == UnifiedMemoryStatus.InvalidFact, "free text disguised as low sensitivity denied");
        Check(memory.RequestRemember(Source, Request() with { Fact = new((UnifiedMemoryFactCategory)99, "brief") }).Status == UnifiedMemoryStatus.InvalidFact, "unknown category denied");
        var foreign = Scope(Target) with { Driver = new DriverKey("test", "account-a", "driver-b") };
        Check(memory.RequestRemember(Source, Request("brief", foreign)).Status == UnifiedMemoryStatus.InvalidTarget, "cross driver target denied");
        foreign = Scope(Target) with { Conversation = Target.Conversation with { AccountScope = "account-b" } };
        Check(memory.RequestRemember(Source, Request("brief", foreign)).Status == UnifiedMemoryStatus.InvalidTarget, "cross account target denied");
        foreign = Scope(Target) with { Conversation = Target.Conversation with { PlatformId = "other" } };
        Check(memory.RequestRemember(Source, Request("brief", foreign)).Status == UnifiedMemoryStatus.InvalidTarget, "cross platform target denied");
        var candidate = memory.RequestRemember(Source, Request("brief", Scope(Target))).Entry!;
        Check(memory.Read(Source, "help").Entries.IsEmpty && memory.Read(Target, "help").Entries.IsEmpty, "candidate not usable even at source");
        Check(memory.Confirm(Source, Consent(candidate)).Status == UnifiedMemoryStatus.NotOwnerPrivateChat, "group confirmation denied");
        Check(memory.Confirm(Private with { Actor = Subject with { NativeUserId = "10002" } }, Consent(candidate)).Status == UnifiedMemoryStatus.NotOwnerPrivateChat, "other subject cannot confirm");
        Check(memory.Confirm(Private with { Driver = new("test", "account-a", "driver-b") }, Consent(candidate)).Status == UnifiedMemoryStatus.NotOwnerPrivateChat, "other driver cannot confirm");
        Check(memory.Confirm(Private, Consent(candidate) with { Token = Guid.NewGuid() }).Status == UnifiedMemoryStatus.InvalidConfirmation, "forged confirmation denied");
        Check(memory.Confirm(Private, Consent(candidate) with { AllowSourceLocal = false }).Status == UnifiedMemoryStatus.InvalidConfirmation, "source local requires consent");
        Check(memory.Confirm(Private, Consent(candidate) with { AcknowledgeGroupAudience = false }).Status == UnifiedMemoryStatus.InvalidConfirmation, "group audience warning acknowledged");
        Check(memory.Confirm(Private, Consent(candidate)).Succeeded, "owner private confirmation accepted");
        Check(memory.Read(Source, "help").Entries.Length == 1 && memory.Read(Target, "help").Entries.Length == 1, "source and exact target readable");
        Check(memory.Read(Private, "help").Entries.IsEmpty, "private confirmation does not migrate source");
        Check(memory.Read(Target with { Conversation = Target.Conversation with { NativeTargetId = "20003" } }, "help").Entries.IsEmpty, "future group excluded");
        Check(memory.Read(Target with { Conversation = Target.Conversation with { ThreadId = "thread-a" } }, "help").Entries.IsEmpty, "different thread excluded");
        Check(memory.Read(Target with { Actor = Subject with { NativeUserId = "10002" } }, "help").Entries.IsEmpty, "other subject cannot read");
        Check(memory.List(Source).IsEmpty, "no group management enumeration");
        Check(memory.Read(Target, "").Entries.IsEmpty && memory.Read(Target, "help", 0).Entries.IsEmpty, "empty query and zero budget excluded");
    }

    private static void Lifecycle()
    {
        var store = new VolatileStore();
        var memory = new UnifiedMemory(store);
        var invalidations = new List<string>();
        memory.Invalidated += value => invalidations.Add(value.Reason);
        var entry = Remember(memory);
        var receipt = memory.Read(Target, "help").Receipt!;
        Check(memory.ValidateReceipt(receipt, Target), "issued receipt valid");
        Check(!memory.ValidateReceipt(new(Guid.NewGuid()), Target), "forged receipt denied");
        Check(!memory.ValidateReceipt(receipt, Source), "receipt destination bound");
        Check(memory.Revoke(Private, entry.FactId, Scope(Target)).Succeeded, "revoke accepted");
        Check(!memory.ValidateReceipt(receipt, Target) && memory.Read(Target, "help").Entries.IsEmpty, "revoke invalidates pending target use");
        Check(memory.Read(Source, "help").Entries.Length == 1, "revoke preserves source");
        receipt = memory.Read(Source, "help").Receipt!;
        var edited = memory.Edit(Private, entry.FactId, 1, Request("detailed", Scope(Target)));
        Check(edited.Succeeded && edited.Entry!.Version == 2, "edit creates next candidate");
        Check(!memory.ValidateReceipt(receipt, Source) && memory.Read(Source, "help").Entries.IsEmpty, "edit retires old version immediately");
        Check(store.State.Entries.Single().Fact.Value == "detailed" && !store.State.Entries.Single().Confirmed, "old body and grants removed atomically");
        Check(memory.Confirm(Private, Consent(entry)).Status == UnifiedMemoryStatus.StaleVersion, "old version token not inherited");
        Check(memory.Confirm(Private, Consent(edited.Entry!)).Succeeded, "new version requires new consent");
        receipt = memory.Read(Target, "help").Receipt!;
        memory.SetEnabled(false);
        Check(!memory.ValidateReceipt(receipt, Target) && memory.Read(Source, "help").Entries.IsEmpty, "disable invalidates all model use");
        Check(memory.RequestRemember(Source, Request()).Status == UnifiedMemoryStatus.Disabled, "disable stops collection");
        Check(memory.List(Private).Length == 1, "disable retains manageable data");
        Check(memory.Forget(Private, entry.FactId).Succeeded, "forget works while disabled");
        Check(store.State.Entries.IsEmpty && store.State.Suppressions.Length == 1, "forget removes body retaining minimal marker");
        Check(memory.IsLegacyProfileSuppressed(Subject, Scope(Source)), "forget permanently suppresses matching legacy source");
        Check(!memory.IsLegacyProfileSuppressed(Subject, Scope(Target)), "legacy suppression does not merge history scopes");
        memory.SetEnabled(true);
        Check(memory.RequestRemember(Source, Request()).Status == UnifiedMemoryStatus.Suppressed, "forgotten source cannot regenerate");
        Check(new UnifiedMemory(store).IsLegacyProfileSuppressed(Subject, Scope(Source)), "suppression survives reopen");
        Check(invalidations.Contains("edited") && invalidations.Contains("revoked") && invalidations.Contains("disabled") && invalidations.Contains("forgotten"), "lifecycle invalidation notifications emitted");
    }

    private static void FailureAndExpiry()
    {
        var broken = new UnifiedMemory(new VolatileStore { FailLoad = true });
        Check(!broken.Enabled && broken.SetEnabled(true).Status == UnifiedMemoryStatus.StorageFailure, "load failure not converted to writable empty state");
        Check(broken.IsLegacyProfileSuppressed(Subject, Scope(Source)), "storage fault conservatively suppresses legacy");
        var store = new VolatileStore();
        var memory = new UnifiedMemory(store);
        var entry = Remember(memory);
        var receipt = memory.Read(Target, "help").Receipt!;
        var revision = store.State.Revision;
        store.FailSave = true;
        Check(memory.Revoke(Private, entry.FactId, Scope(Target)).Status == UnifiedMemoryStatus.StorageFailure, "save failure reported");
        Check(store.State.Revision == revision && !memory.ValidateReceipt(receipt, Target) && !memory.Enabled, "failed write never publishes permissions and fails closed");
        var clock = new ProbeClock();
        memory = new(new VolatileStore(), clock);
        memory.SetEnabled(true);
        entry = memory.RequestRemember(Source, Request()).Entry!;
        clock.Now = clock.Now.AddMinutes(16);
        Check(memory.Confirm(Private, Consent(entry)).Status == UnifiedMemoryStatus.InvalidConfirmation, "stale confirmation expires");
        entry = Remember(memory);
        receipt = memory.Read(Target, "help").Receipt!;
        clock.Now = clock.Now.AddMinutes(11);
        Check(!memory.ValidateReceipt(receipt, Target), "reading receipt expires");
        Check(!new UnifiedMemory(new VolatileStore { State = store.State }).ValidateReceipt(receipt, Target), "receipt not portable across instances");
    }

    private static void ConflictsAndDenial()
    {
        var memory = new UnifiedMemory(new VolatileStore());
        var original = Remember(memory);
        var receipt = memory.Read(Target, "help").Receipt!;
        var next = memory.RequestRemember(Source, Request("detailed", Scope(Target))).Entry!;
        Check(next.FactId == original.FactId && next.Version == 2 && !memory.ValidateReceipt(receipt, Target), "new expression retires same source category");
        memory.Confirm(Private, Consent(next));
        var otherSource = Source with { Conversation = Source.Conversation with { NativeTargetId = "20004" } };
        var conflict = memory.RequestRemember(otherSource, Request("brief", Scope(Target))).Entry!;
        memory.Confirm(Private, Consent(conflict));
        Check(memory.Read(Target, "help").Entries.IsEmpty, "conflicting authorized sources omitted");
        Check(memory.Reject(Private, conflict.FactId).Succeeded, "subject denial accepted");
        Check(memory.Read(otherSource, "help").Entries.IsEmpty && memory.List(Private).Length == 2, "denial stops use without forgetting body");
        Check(memory.Read(Target, "help").Entries.Single().Fact.Value == "detailed", "denied source no longer resolves as conflicting truth");
    }

    private static void AtomicUse()
    {
        var memory = new UnifiedMemory(new VolatileStore());
        var entry = Remember(memory);
        var receipt = memory.Read(Target, "help").Receipt!;
        var invoked = 0;
        Check(memory.TryUseReceipt(receipt, Target, () => invoked++), "atomic receipt starts allowed external call");
        memory.Revoke(Private, entry.FactId, Scope(Target));
        Check(!memory.TryUseReceipt(receipt, Target, () => invoked++) && invoked == 1, "revoked receipt never starts external call");
    }
}

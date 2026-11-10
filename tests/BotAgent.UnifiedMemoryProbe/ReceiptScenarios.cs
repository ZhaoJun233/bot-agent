using BotAgent.Domain.Memory;
using BotAgent.Engine.Memory;
using static BotAgent.UnifiedMemoryProbe.Probe;

namespace BotAgent.UnifiedMemoryProbe;

internal static class ReceiptScenarios
{
    public static void Run()
    {
        Legacy();
        SubmissionRace();
    }

    private static void Legacy()
    {
        var store = new VolatileStore();
        var memory = new UnifiedMemory(store);
        var receipt = memory.AuthorizeLegacyProfile(Source, Subject)!;
        Check(!memory.Enabled && memory.ValidateReceipt(receipt, Source), "legacy reading permission independent of unified switch");
        Check(memory.AuthorizeLegacyProfile(Source with { IsReliableIdentity = false }, Subject) is null, "unreliable context never permits legacy");
        Check(memory.AuthorizeLegacyProfile(Source, Subject with { AccountScope = "other-account" }) is null, "legacy subject account cannot be mixed");
        Check(memory.AuthorizeLegacyProfile(Source, Subject with { PlatformId = "other-platform" }) is null, "legacy subject platform cannot be mixed");
        Check(!memory.ValidateReceipt(receipt, Target), "legacy receipt bound to exact scope");
        var entry = Remember(memory);
        Check(!memory.ValidateReceipt(receipt, Source), "legacy receipt participates in global revision invalidation");
        receipt = memory.AuthorizeLegacyProfile(Source, Subject)!;
        var aggregate = memory.AuthorizeLegacyProfile(Source)!;
        memory.Forget(Private, entry.FactId);
        Check(!memory.ValidateReceipt(receipt, Source) && !memory.ValidateReceipt(aggregate, Source), "forget invalidates member and aggregate legacy receipts");
        Check(memory.AuthorizeLegacyProfile(Source, Subject) is null && memory.AuthorizeLegacyProfile(Source) is null, "forgotten source refuses future member and aggregate legacy reads");
        Check(memory.AuthorizeLegacyProfile(Target, Subject) is not null, "legacy scope suppression does not collapse source into history key");
        Check(memory.AuthorizeLegacyProfile(Source, Subject with { NativeUserId = "10002" }) is not null, "other explicit subject remains eligible");
        Check(new UnifiedMemory(store).AuthorizeLegacyProfile(Source, Subject) is null, "legacy authorization suppression survives new authority");
        var sourceReceipt = memory.AuthorizeLegacyProfile(Target, Subject)!;
        store.FailSave = true;
        memory.SetEnabled(false);
        Check(!memory.ValidateReceipt(sourceReceipt, Target) && memory.AuthorizeLegacyProfile(Target, Subject) is null, "storage failure closes outstanding legacy receipts");
    }

    private static void SubmissionRace()
    {
        var memory = new UnifiedMemory(new VolatileStore());
        var entry = Remember(memory);
        var receipt = memory.Read(Target, "help").Receipt!;
        using var insideSubmission = new ManualResetEventSlim();
        using var finishSubmission = new ManualResetEventSlim();
        using var attemptedRevocation = new ManualResetEventSlim();
        var order = new List<string>();
        var submission = Task.Run(() => memory.TryUseReceipt(receipt, Target, () =>
        {
            insideSubmission.Set();
            if (!finishSubmission.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("synthetic submission barrier");
            order.Add("submitted");
        }));
        Check(insideSubmission.Wait(TimeSpan.FromSeconds(5)), "synthetic submission entered authorization lock");
        var revocation = Task.Run(() =>
        {
            attemptedRevocation.Set();
            var result = memory.Revoke(Private, entry.FactId, Scope(Target));
            order.Add("revoked");
            return result;
        });
        Check(attemptedRevocation.Wait(TimeSpan.FromSeconds(5)), "synthetic concurrent revocation started");
        finishSubmission.Set();
        Task.WaitAll(submission, revocation);
        Check(submission.Result && revocation.Result.Succeeded && order.SequenceEqual(new[] { "submitted", "revoked" }), "in-flight synchronous submission linearizes before concurrent revoke");
        Check(!memory.TryUseReceipt(receipt, Target, () => throw new InvalidOperationException("must not submit")), "no later external submission after revoke completes");
    }
}

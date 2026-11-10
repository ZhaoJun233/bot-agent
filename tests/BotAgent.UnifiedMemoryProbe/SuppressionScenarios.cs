using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Memory;
using static BotAgent.UnifiedMemoryProbe.Probe;

namespace BotAgent.UnifiedMemoryProbe;

internal static class SuppressionScenarios
{
    public static void Run()
    {
        EditCannotResurrectForgottenSource();
        PendingCandidateCannotConfirmAfterForget();
        SuppressionIsIsolatedAndLegitimateEditsRequireConsent();
    }

    private static void EditCannotResurrectForgottenSource()
    {
        IUnifiedMemory memory = new UnifiedMemory(new VolatileStore());
        Check(memory.SetEnabled(true).Succeeded, "enable suppression fixture");
        var forgottenRequest = new UnifiedMemoryRequest(new(UnifiedMemoryFactCategory.ReplyLanguage, "zh"), [Scope(Target)], true);
        var forgotten = Confirmed(memory, Source, Private, forgottenRequest);
        var retained = Confirmed(memory, Source, Private, Request("brief", Scope(Target)));
        Check(memory.Forget(Private, forgotten.FactId).Succeeded, "forget first source fact");
        Check(memory.RequestRemember(Source, forgottenRequest).Status == UnifiedMemoryStatus.Suppressed, "direct recreation suppressed");
        var edit = memory.Edit(Private, retained.FactId, retained.Version, forgottenRequest);
        Check(edit.Status == UnifiedMemoryStatus.Suppressed, "edit cannot recreate suppressed subject/source");
        Check(memory.List(Private).Single() == retained, "denied edit preserves identity version and confirmation");
        Check(memory.Edit(Private, retained.FactId, retained.Version, Request("detailed")).Status == UnifiedMemoryStatus.Suppressed,
            "suppression covers any new fact from the same source");
        Check(memory.Edit(Source, retained.FactId, retained.Version, forgottenRequest).Status == UnifiedMemoryStatus.NotOwnerPrivateChat,
            "suppression does not bypass private owner authorization");
        Check(memory.Edit(Private, retained.FactId, retained.Version + 1, forgottenRequest).Status == UnifiedMemoryStatus.StaleVersion,
            "suppression preserves stale version rejection");
        Check(memory.Edit(Private, retained.FactId, retained.Version, forgottenRequest with { ExplicitSelfExpression = false }).Status
            == UnifiedMemoryStatus.ExplicitRequestRequired, "suppression preserves explicit expression validation");
        var read = memory.Read(Target, "synthetic question");
        Check(read.Entries.All(entry => entry.Fact != forgottenRequest.Fact), "forgotten fact stays out of reads");
        Check(memory.Revoke(Private, retained.FactId, Scope(Target)).Succeeded, "suppression still permits revoking retained grants");
        Check(memory.Forget(Private, retained.FactId).Succeeded && memory.List(Private).IsEmpty, "suppression still permits forgetting retained facts");
    }

    private static void PendingCandidateCannotConfirmAfterForget()
    {
        var store = new VolatileStore();
        IUnifiedMemory memory = new UnifiedMemory(store);
        Check(memory.SetEnabled(true).Succeeded, "enable pending fixture");
        var forgotten = Confirmed(memory, Source, Private, Request());
        var pending = memory.RequestRemember(Source, new(new(UnifiedMemoryFactCategory.ReplyLanguage, "en"), [Scope(Target)], true)).Entry!;
        Check(pending is { Confirmed: false }, "second source candidate awaiting consent");
        Check(memory.Forget(Private, forgotten.FactId).Succeeded, "forget while another source candidate pending");
        IUnifiedMemory reopened = new UnifiedMemory(store);
        Check(reopened.Confirm(Private, Consent(pending)).Status == UnifiedMemoryStatus.Suppressed, "pending confirmation cannot bypass persisted suppression");
        Check(reopened.List(Private).Single() == pending, "denied confirmation preserves pending candidate");
        Check(reopened.Read(Source, "synthetic question").Entries.IsEmpty
            && reopened.Read(Target, "synthetic question").Entries.IsEmpty, "suppressed candidate remains unavailable at source and target");
    }

    private static void SuppressionIsIsolatedAndLegitimateEditsRequireConsent()
    {
        IUnifiedMemory memory = new UnifiedMemory(new VolatileStore());
        Check(memory.SetEnabled(true).Succeeded, "enable isolation fixture");
        var forgotten = Confirmed(memory, Source, Private, Request());
        Check(memory.Forget(Private, forgotten.FactId).Succeeded, "isolation fixture suppresses original source");
        (string Label, UnifiedMemoryContext Source)[] isolated =
        [
            ("subject", Source with { Actor = new("test", "account-a", "10002") }),
            ("conversation", Target),
            ("thread", Source with { Conversation = Source.Conversation with { ThreadId = "thread-a" } }),
            ("conversation kind", Source with { Conversation = Source.Conversation with { Kind = ConversationKind.ChannelChat } }),
            ("driver", Source with { Driver = new("test", "account-a", "driver-b") }),
            ("account", new(new("test", "account-b", "10001"), new("test", "account-b", "driver-a"),
                new("test", "account-b", ConversationKind.GroupChat, "20001"), true)),
            ("platform", new(new("other", "account-a", "10001"), new("other", "account-a", "driver-a"),
                new("other", "account-a", ConversationKind.GroupChat, "20001"), true))
        ];
        foreach (var (label, source) in isolated)
        {
            var owner = new UnifiedMemoryContext(source.Actor, source.Driver,
                new(source.Driver.PlatformId, source.Driver.AccountScope, ConversationKind.PrivateChat, source.Actor!.NativeUserId), true);
            var original = Confirmed(memory, source, owner, Request());
            var receipt = memory.Read(source, "synthetic question").Receipt!;
            var edited = memory.Edit(owner, original.FactId, original.Version, Request("detailed"));
            Check(edited.Succeeded, label + " isolation allows legitimate edit");
            var candidate = edited.Entry!;
            Check(candidate.FactId == original.FactId && candidate.Version == original.Version + 1
                && candidate.Subject == original.Subject && candidate.Source == original.Source, label + " edit preserves identity and original source");
            Check(!candidate.Confirmed && !candidate.AllowSourceLocal && candidate.SharedTargets.IsEmpty
                && !memory.ValidateReceipt(receipt, source) && memory.Read(source, "synthetic question").Entries.IsEmpty,
                label + " edit invalidates previous authority until fresh consent");
            Check(memory.Confirm(owner, Consent(original)).Status == UnifiedMemoryStatus.StaleVersion, label + " old consent cannot authorize new version");
            Check(memory.Confirm(owner, Consent(candidate) with { AllowSourceLocal = false }).Status == UnifiedMemoryStatus.InvalidConfirmation,
                label + " confirmation still requires source permission");
            Check(memory.Confirm(owner, Consent(candidate) with { AcknowledgeGroupAudience = false }).Status == UnifiedMemoryStatus.InvalidConfirmation,
                label + " confirmation still requires audience acknowledgment");
            Check(memory.Confirm(owner, Consent(candidate)).Succeeded, label + " fresh confirmation succeeds");
            Check(memory.Read(source, "synthetic question").Entries.Single().Fact == Request("detailed").Fact, label + " edited fact readable after consent");
        }
        Check(memory.RequestRemember(Source, Request()).Status == UnifiedMemoryStatus.Suppressed, "other identities and sources never clear original suppression");
    }

    private static UnifiedMemoryEntry Confirmed(IUnifiedMemory memory, UnifiedMemoryContext source,
        UnifiedMemoryContext owner, UnifiedMemoryRequest request)
    {
        var candidate = memory.RequestRemember(source, request);
        Check(candidate.Succeeded, "create synthetic candidate");
        var confirmed = memory.Confirm(owner, Consent(candidate.Entry!));
        Check(confirmed.Succeeded, "confirm synthetic candidate");
        return confirmed.Entry!;
    }
}

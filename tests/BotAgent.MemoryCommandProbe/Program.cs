using BotAgent.Domain.Configuration;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Services.Memory;
using BotAgent.Services.Qq;
using static BotAgent.MemoryCommandProbe.CommandFixture;

namespace BotAgent.MemoryCommandProbe;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            await IdentityAndConsumption();
            await PrivateReviewAndBoundaries();
            await CommandLifecycle.Run();
            await DefaultInstanceChecks.Run();
            Summary();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task IdentityAndConsumption()
    {
        var f = new CommandFixture();
        var message = f.Message("//memory remember length brief");
        var unreliable = message with { Envelope = new PlatformInboundEnvelope(message.Envelope!.Origin, message.Envelope.Message, false) };
        using (PlatformSourceExecution.Enter(f.Lease(unreliable)))
        {
            Check(UnifiedMemoryCommandService.TryContext(unreliable) is null, "source trust does not promote unreliable identity");
            Check(await f.Commands.HandleAsync(unreliable), "unreliable memory command consumed");
            Check(f.Source.Sends.Count == 0 && f.Store.State.Entries.IsEmpty, "unreliable identity has no memory or reply effect");
        }
        Check(!await f.Commands.HandleAsync(f.Message("ordinary synthetic message")), "ordinary message not consumed");
        Check(await f.Commands.HandleAsync(message), "unleased command consumed");
        Check(f.Source.Sends.Count == 0 && f.Store.State.Entries.IsEmpty, "unleased command produces no read/write/reply");
        using (PlatformSourceExecution.Enter(f.Lease(message)))
        {
            Check(UnifiedMemoryCommandService.TryContext(message) == Private, "native attested identity ignores scalar QQ IDs and flags");
            Check(await f.Commands.HandleAsync(message with { Envelope = null }), "missing envelope consumed");
            Check(f.Source.Sends.Count == 0, "missing envelope cannot select reply recipient");
        }
        using (PlatformSourceExecution.Enter(f.Lease(message, current: false)))
        {
            Check(UnifiedMemoryCommandService.TryContext(message) is null, "stale driver lease rejected");
            Check(f.Commands.TryHandle(message), "stale command synchronously consumed");
        }
        var wrongOrigin = new PlatformMessageOrigin(Driver with { InstanceId = "driver-B" }, message.Envelope!.Message.Ref);
        using (PlatformSourceExecution.Enter(new(f.Source, f.Source, wrongOrigin, CancellationToken.None, () => true)))
            Check(UnifiedMemoryCommandService.TryContext(message) is null, "different driver lease rejected");
        var wrongTrigger = new PlatformMessageOrigin(Driver, message.Envelope!.Message.Ref with { NativeMessageId = "other-message" });
        using (PlatformSourceExecution.Enter(new(f.Source, f.Source, wrongTrigger, CancellationToken.None, () => true)))
            Check(UnifiedMemoryCommandService.TryContext(message) is null, "different source trigger rejected");
        var unknownOwner = new FakeSource();
        using (PlatformSourceExecution.Enter(new(unknownOwner, unknownOwner, message.Envelope!.Origin, CancellationToken.None, () => true)))
            Check(await f.Commands.HandleAsync(message) && f.Store.State.Entries.IsEmpty, "unrelated source owner cannot invoke core");
        Check(await f.Run("//memoryXYZ remember length brief") == MemoryCommandView.Unavailable, "malformed reserved prefix never falls through");
        var reply = await f.Run("//memory remember length brief");
        Check(reply.Contains("allow-source") && reply.Contains("private-only"), "private source also requires explicit local authorization");
        Check(f.Source.LastTarget == Private.Conversation, "reply uses native DM identity rather than scalar group flag");
        Check(f.Store.State.Entries.Single().Subject == Subject, "persisted actor is native opaque subject");
        Check(f.Memory.Read(Private, "synthetic request").Entries.IsEmpty, "unconfirmed draft unavailable to retrieval");
        f.Source.ThrowOnSend = true;
        using (PlatformSourceExecution.Enter(f.Lease(message)))
            Check(f.Commands.TryHandle(message with { Text = "//memory list" }), "transport failure stays consumed without exception");
    }

    private static async Task PrivateReviewAndBoundaries()
    {
        var f = new CommandFixture();
        var groupResponse = await f.Run($"//memory remember language en to {Token(Target)} {Token(Private)}", Group);
        var draft = f.Store.State.Entries.Single();
        Check(groupResponse == MemoryCommandView.GroupOnly, "group request emits only generic private handoff");
        Check(!groupResponse.Contains(draft.FactId.ToString("N")) && !groupResponse.Contains(draft.ConfirmationToken.ToString("N"))
            && !groupResponse.Contains("scope:") && !groupResponse.Contains("language"), "group contains no fact ID token candidate or scope list");
        Check(draft.Source == Scope(Group) && draft.ProposedTargets.SequenceEqual([Scope(Target), Scope(Private)]), "group source and exact finite targets retained");
        var shown = await f.Run($"//memory show {draft.FactId:N}");
        Check(shown.Contains(Token(Group)) && shown.Contains(Token(Target)) && shown.Contains(Token(Private)), "private review shows exact source and every destination");
        Check(shown.Contains("未来加入成员") && shown.Contains("allow-source") && shown.Contains("accept-group-audience"), "group future audience and source use explicitly acknowledged");
        Check((await f.Run("//memory list")).Contains(draft.FactId.ToString("N")), "private list discovers group pending fact");
        foreach (var text in new[] { "//memory list", $"//memory show {draft.FactId:N}", Confirm(draft),
            $"//memory forget {draft.FactId:N}", "//memory scope", "//memory help", "//memory stop" })
            Check(await f.Run(text, Group) == groupResponse, "all group management commands return identical nondisclosure");
        Check(!f.Store.State.Entries.Single().Confirmed && f.Memory.Enabled, "group commands cannot grant forget or stop");
        var stranger = Private with { Actor = Subject with { NativeUserId = "opaque-user-a" } };
        var missing = await f.Run($"//memory show {Guid.NewGuid():N}");
        Check(await f.Run($"//memory show {draft.FactId:N}", stranger) == missing, "case-distinct actor cannot enumerate fact presence");
        Check(await f.Run($"//memory show {draft.FactId:N}", Admin) == missing, "administrator cannot inspect another user's memory");
        Check(await f.Run(Confirm(draft), Admin) == MemoryCommandView.Unavailable, "administrator cannot grant another user's draft");
        Check(await f.Run(Confirm(draft, "private-only")) == MemoryCommandView.Unavailable, "group source requires group-audience acknowledgement");
        Check(await f.Run($"//memory confirm {draft.FactId:N} {draft.Version} {draft.ConfirmationToken:N}") == MemoryCommandView.Unavailable,
            "missing explicit source consent cannot confirm");
        Check(await f.Run(Confirm(draft)) == MemoryCommandView.Done, "private exact confirmation grants reviewed version");
        Check(f.Memory.Read(Group, "synthetic request").Entries.Length == 1 && f.Memory.Read(Target, "synthetic request").Entries.Length == 1
            && f.Memory.Read(Private, "synthetic request").Entries.Length == 1, "only reviewed source and target scopes active");
        Check(f.Memory.Read(Target with { Actor = stranger.Actor }, "synthetic request").Entries.IsEmpty, "other member cannot retrieve subject's preference");
        Check(await f.Run($"//memory remember length brief to {Token(Target).Replace("driver-A", "driver-B")}") == MemoryCommandView.Unavailable,
            "cross-driver destination rejected");
        Check(await f.Run("//memory remember medical-health secret") == MemoryCommandView.Unavailable, "sensitive arbitrary fact category rejected");
        Check(await f.Run("//memory remember length arbitrary-user-text") == MemoryCommandView.Unavailable, "arbitrary free-text preference rejected");
        Check(await f.Run("//memory remember length brief to all") == MemoryCommandView.Unavailable, "unbounded wildcard alias rejected");
        Check(await f.Run($"//memory remember length brief to {Token(Target)} {Token(Target)}") == MemoryCommandView.Unavailable,
            "duplicate targets rejected without broadening");
        var mixed = Context(BotAgent.Domain.Conversation.ConversationKind.GroupChat, "Case/Percent%|Target", "Thread/Case");
        Check(MemoryCommandSyntax.TryScope(Token(mixed), Private, out var decoded) && decoded == Scope(mixed), "opaque case delimiters and thread round trip without scalar IDs");
        Check(!MemoryCommandSyntax.TryScope(Token(mixed).Replace("Case", "case"), Private, out var changed) || changed != Scope(mixed), "case changes never normalize to original destination");
        Check(await f.Run("//memory list 2147483647") == MemoryCommandView.Unavailable, "unbounded list page rejected without overflow");
    }
}

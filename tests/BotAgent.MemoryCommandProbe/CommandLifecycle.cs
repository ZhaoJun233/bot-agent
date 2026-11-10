using BotAgent.Domain.Memory;
using BotAgent.Services.Memory;
using static BotAgent.MemoryCommandProbe.CommandFixture;

namespace BotAgent.MemoryCommandProbe;

internal static class CommandLifecycle
{
    public static async Task Run()
    {
        var disabled = new CommandFixture(enabled: false);
        Check(!disabled.Memory.Enabled, "builtin memory defaults disabled");
        Check(await disabled.Run("//memory start") == MemoryCommandView.Unavailable && !disabled.Memory.Enabled, "ordinary subject cannot enable global memory");
        Check(await disabled.Run("//memory start", Group with { Actor = Admin.Actor }) == MemoryCommandView.GroupOnly
            && !disabled.Memory.Enabled, "administrator group command cannot enable");
        var wrongDriverAdmin = Admin with { Driver = Driver with { InstanceId = "driver-B" } };
        Check(await disabled.Run("//memory start", wrongDriverAdmin) == MemoryCommandView.Unavailable && !disabled.Memory.Enabled,
            "same administrator name in another driver has no global authority");
        Check(await disabled.Run("//memory remember length brief") == MemoryCommandView.Unavailable && disabled.Store.State.Entries.IsEmpty,
            "disabled request cannot create draft");
        Check(await disabled.Run("//memory start", Admin) == MemoryCommandView.Done && disabled.Memory.Enabled, "explicit private administrator can enable");

        var f = new CommandFixture();
        await f.Run($"//memory remember length brief to {Token(Target)} {Token(Private)}", Group);
        var original = f.Store.State.Entries.Single();
        await f.Run(Confirm(original));
        var originalReceipt = f.Memory.Read(Target, "synthetic request").Receipt!;
        Check(f.Memory.ValidateReceipt(originalReceipt, Target), "confirmed command produces valid target receipt");
        var editedText = await f.Run($"//memory edit {original.FactId:N} {original.Version} length detailed to {Token(Target)} {Token(Private)}");
        var edited = f.Store.State.Entries.Single();
        Check(editedText.Contains("allow-source") && editedText.Contains("accept-group-audience"), "edit produces complete fresh private confirmation");
        Check(edited.Version == original.Version + 1 && edited.ConfirmationToken != original.ConfirmationToken
            && !edited.Confirmed && edited.SharedTargets.IsEmpty, "edit increments version and clears all prior grants");
        Check(!f.Memory.ValidateReceipt(originalReceipt, Target) && f.Memory.Read(Group, "synthetic request").Entries.IsEmpty, "edit immediately invalidates old receipts and source grant");
        Check(await f.Run(Confirm(original)) == MemoryCommandView.Unavailable, "old version or token cannot reconfirm");
        Check(await f.Run(Confirm(edited)) == MemoryCommandView.Done, "new explicit confirmation reauthorizes edited preference");
        var beforeRevoke = f.Memory.Read(Target, "synthetic request").Receipt!;
        Check(await f.Run($"//memory revoke {edited.FactId:N} {Token(Target)}") == MemoryCommandView.Done, "exact target revoke succeeds");
        Check(f.Memory.Read(Target, "synthetic request").Entries.IsEmpty && f.Memory.Read(Group, "synthetic request").Entries.Length == 1
            && f.Memory.Read(Private, "synthetic request").Entries.Length == 1, "revoke leaves source and unrelated confirmed target intact");
        Check(!f.Memory.ValidateReceipt(beforeRevoke, Target), "revoke invalidates prior receipt");
        var shown = await f.Run($"//memory show {edited.FactId:N}");
        Check(!shown.Contains(Token(Target)) && shown.Contains(Token(Private)), "management display shows live grants not revoked proposed targets");
        Check(await f.Run(Confirm(edited)) == MemoryCommandView.Unavailable, "old token cannot resurrect revoked target");
        Check(await f.Run($"//memory forget {Guid.NewGuid():N}") == MemoryCommandView.Unavailable,
            "unknown lifecycle target produces generic failure");

        Check(await f.Run("//memory stop") == MemoryCommandView.Unavailable && f.Memory.Enabled, "nonadmin cannot disable");
        Check(await f.Run("//memory stop", Admin) == MemoryCommandView.Done && !f.Memory.Enabled, "private administrator can stop global future use");
        Check(f.Memory.Read(Group, "synthetic request").Entries.IsEmpty && (await f.Run("//memory list")).Contains(edited.FactId.ToString("N")),
            "disabled state blocks retrieval but preserves owner management");
        Check(await f.Run($"//memory reject {edited.FactId:N}") == MemoryCommandView.Done, "owner can deny whole fact while disabled");
        Check(!f.Store.State.Entries.Single().Confirmed && f.Store.State.Entries.Single().SharedTargets.IsEmpty,
            "denial removes all grants");
        Check(await f.Run($"//memory forget {edited.FactId:N}") == MemoryCommandView.Done && f.Store.State.Entries.IsEmpty,
            "forget deletes whole fact and grants while disabled");
        Check(f.Memory.IsLegacyProfileSuppressed(Subject, Scope(Group)), "forget leaves anti-resurrection suppression for original source");

        var expiring = new CommandFixture();
        await expiring.Run("//memory remember explanation examples");
        var draft = expiring.Store.State.Entries.Single();
        expiring.Clock.Now += TimeSpan.FromMinutes(16);
        Check(await expiring.Run(Confirm(draft, "private-only")) == MemoryCommandView.Unavailable && expiring.Memory.Read(Private, "synthetic request").Entries.IsEmpty,
            "expired confirmation cannot grant memory");
        await expiring.Run($"//memory edit {draft.FactId:N} {draft.Version} explanation step-by-step");
        var replacement = expiring.Store.State.Entries.Single();
        Check(await expiring.Run(Confirm(replacement, "private-only")) == MemoryCommandView.Done, "explicit edit refreshes expired draft and requires new token");
    }
}

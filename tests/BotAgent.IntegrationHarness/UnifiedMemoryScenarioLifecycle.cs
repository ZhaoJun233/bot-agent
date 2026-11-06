namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task CheckUnifiedMemoryLifecycleAsync(UnifiedMemoryFixture f, string id)
    {
        await f.RestartAsync();
        Check("M153.24 persisted target grant survives real process restart", HasPreference(await f.ChatAsync("M153_RESTART_GRANTED", UnifiedMemoryFixture.Target)));
        Check("M153.25 persisted source grant survives real process restart", HasPreference(await f.ChatAsync("M153_RESTART_SOURCE", UnifiedMemoryFixture.Source)));
        UnifiedMemoryFixture.Require("M153.26 owner revokes exact target", (await f.CommandAsync("//memory revoke " + id + " " + UnifiedMemoryFixture.TargetScope)).Contains("已完成"));
        Check("M153.27 revoked target stops chat injection", !HasMemory(await f.ChatAsync("M153_REVOKED_TARGET", UnifiedMemoryFixture.Target)));
        Check("M153.28 revocation retains independent source grant", HasPreference(await f.ChatAsync("M153_REVOKED_SOURCE", UnifiedMemoryFixture.Source)));
        await CheckUnifiedMemoryAgentAfterRevokeAsync(f);
        UnifiedMemoryFixture.Require("M153.29 governor can stop", (await f.CommandAsync("//memory stop")).Contains("已完成"));
        Check("M153.30 stopped feature injects nothing", !HasMemory(await f.ChatAsync("M153_STOPPED", UnifiedMemoryFixture.Source)));
        Check("M153.31 stop retains owner management", (await f.CommandAsync("//memory list")).Contains(id));
        UnifiedMemoryFixture.Require("M153.32 governor can restart", (await f.CommandAsync("//memory start")).Contains("已完成"));
        Check("M153.33 start restores existing source grant", HasPreference(await f.ChatAsync("M153_RESUMED", UnifiedMemoryFixture.Source)));
        UnifiedMemoryFixture.Require("M153.34 owner forgets fact", (await f.CommandAsync("//memory forget " + id)).Contains("已完成"));
        Check("M153.35 forget removes private management entry", !(await f.CommandAsync("//memory list")).Contains(id));
        var forgotten = await f.ChatAsync("M153_FORGOTTEN_SOURCE", UnifiedMemoryFixture.Source);
        Check("M153.36 forget removes source injection", !HasMemory(forgotten));
        Check("M153.36b forget suppresses synthetic legacy profile", !RequestText(forgotten).Contains(UnifiedMemoryFixture.LegacySentinel));
        Check("M153.36c forget cannot rehydrate untrusted legacy profiles", !HasUntrustedLegacy(forgotten));
        await f.RestartAsync();
        var restarted = await f.ChatAsync("M153_RESTART_FORGOTTEN", UnifiedMemoryFixture.Source);
        Check("M153.37 restart cannot revive forgotten source", !HasMemory(restarted));
        Check("M153.37b restart cannot revive suppressed legacy profile", !RequestText(restarted).Contains(UnifiedMemoryFixture.LegacySentinel));
        Check("M153.37c restart cannot rehydrate untrusted legacy profiles", !HasUntrustedLegacy(restarted));
        Check("M153.38 restart cannot revive revoked target", !HasMemory(await f.ChatAsync("M153_RESTART_REVOKED", UnifiedMemoryFixture.Target)));
        Check("M153.39 forgotten entry stays absent after restart", !(await f.CommandAsync("//memory list")).Contains(id));
    }
}

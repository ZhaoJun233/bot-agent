using BotAgent.Domain.Memory;
using BotAgent.Services.Memory;
using BotAgent.Services.Qq;
using static BotAgent.MemoryCommandProbe.CommandFixture;

namespace BotAgent.MemoryCommandProbe;

internal static class DefaultInstanceChecks
{
    public static async Task Run()
    {
        var f = new CommandFixture();
        var privateDefault = Private with { Driver = Driver with { InstanceId = "" } };
        var targetDefault = Target with { Driver = privateDefault.Driver };
        var message = f.Message("//memory scope", privateDefault);
        using (PlatformSourceExecution.Enter(f.Lease(message)))
            Check(UnifiedMemoryCommandService.TryContext(message) == privateDefault, "exact default empty instance is attested");
        var token = Token(targetDefault);
        Check(token.Contains("//group/"), "default instance token retains empty field");
        Check(MemoryCommandSyntax.TryScope(token, privateDefault, out var parsed) && parsed == Scope(targetDefault), "empty instance scope round trips exactly");
        Check(!MemoryCommandSyntax.TryScope(Token(Target), privateDefault, out _), "default instance cannot target named driver");
        Check(!MemoryCommandSyntax.TryScope(token, Private, out _), "named instance cannot target default driver");
        Check(await f.Run("//memory scope", privateDefault) == Token(privateDefault), "command exposes exact default scope");
        var review = await f.Run("//memory remember length brief to " + token, privateDefault);
        Check(review.Contains(token) && review.Contains("//memory confirm"), "default instance can request private review");
        var entry = f.Memory.List(privateDefault).Single();
        Check(await f.Run(Confirm(entry), privateDefault) == MemoryCommandView.Done, "default instance can confirm reviewed grant");
        Check(f.Memory.Read(targetDefault, "synthetic request").Entries.Length == 1, "default target grant becomes readable");
        Check(f.Memory.Read(Target, "synthetic request").Entries.IsEmpty, "default grant cannot authorize named instance");
        foreach (var invalid in new string?[] { null, " ", "\t", "\n", new('x', 257) })
        {
            var bad = privateDefault with { Driver = privateDefault.Driver with { InstanceId = invalid! } };
            Check(!MemoryCommandSyntax.Instance(invalid), "null whitespace control and oversized instances are not command identities");
            try
            {
                var invalidMessage = f.Message("//memory scope", bad);
                using (PlatformSourceExecution.Enter(f.Lease(invalidMessage)))
                    Check(UnifiedMemoryCommandService.TryContext(invalidMessage) is null, "unattestable instance is rejected by command adapter");
            }
            catch (ArgumentException error) when (error.Message == "invalid_platform_origin")
            {
                Check(true, "invalid instance is rejected before envelope can be constructed");
            }
        }
    }
}

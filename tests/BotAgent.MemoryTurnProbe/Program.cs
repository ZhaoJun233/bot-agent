namespace BotAgent.MemoryTurnProbe;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            AttestationChecks.Run();
            TurnChecks.Run();
            await TurnChecks.DeferredWork();
            CapacityChecks.Run();
            DefaultInstanceChecks.Run();
            LegacyProfileKeyChecks.Run();
        }
        catch (Exception exception)
        {
            Probe.Check(false, "unexpected synthetic probe failure: " + exception.GetType().Name);
        }
        Console.WriteLine($"MemoryTurnProbe: {Probe.Passed} passed, {Probe.Failed} failed");
        return Probe.Failed == 0 ? 0 : 1;
    }
}

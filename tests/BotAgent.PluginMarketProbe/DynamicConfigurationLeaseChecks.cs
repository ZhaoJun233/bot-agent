using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services;
using BotAgent.Services.Plugins;

internal partial class Program
{
    private static int RunWaitingConfigurationReadCheck(SettingsBox box, IPluginConfiguration reader,
        PluginManager.InvocationGate gate, Action revoke, bool expectSnapshot)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var configurationGate = typeof(SettingsBox).GetField("_gate", hidden)!.GetValue(box)!;
        var owner = typeof(PluginManager.InvocationGate).GetField("_owner", hidden)!.GetValue(gate)!;
        var entry = typeof(PluginManager.InvocationGate).GetField("_entry", hidden)!.GetValue(gate)!;
        var registryGate = typeof(PluginManager).GetField("_sync", hidden)!.GetValue(owner)!;
        var inFlight = entry.GetType().GetProperty("InFlight")!;
        int CountLeases()
        {
            lock (registryGate) return (int)inFlight.GetValue(entry)!;
        }
        var before = CountLeases();
        PluginConfigurationSnapshot? snapshot = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { snapshot = reader.Read(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        Monitor.Enter(configurationGate);
        try
        {
            worker.Start();
            if (!SpinWait.SpinUntil(() => CountLeases() == before + 1
                && (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("configuration_reader_did_not_wait_with_generation_lease");
            revoke();
        }
        finally { Monitor.Exit(configurationGate); }
        if (!worker.Join(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("configuration_reader_did_not_drain");
        if (expectSnapshot)
        {
            if (failure is not null || snapshot?.Values.Count != 10)
                throw new InvalidOperationException("approved_control_reader_lost_disabled_business_configuration");
        }
        else if (snapshot is not null || failure is not InvalidOperationException
            { Message: "plugin_configuration_not_ready" })
            throw new InvalidOperationException("revoked_waiting_configuration_reader_returned_values");
        if (CountLeases() != before)
            throw new InvalidOperationException("configuration_reader_leaked_generation_lease");
        return 4;
    }
}

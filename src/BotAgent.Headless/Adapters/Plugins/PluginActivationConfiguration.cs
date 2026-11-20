using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>One approved owner's values, protected by its business or control generation lease.</summary>
internal sealed class PluginActivationConfiguration(PluginPackageManifest manifest, PluginSettingsStore store)
    : IPluginConfiguration
{
    private PluginManager.InvocationGate? _gate;

    internal PluginActivationConfiguration ControlView() => new(manifest, store);
    internal void Publish(PluginManager.InvocationGate gate) => Volatile.Write(ref _gate, gate);

    public PluginConfigurationSnapshot Read()
    {
        var gate = Volatile.Read(ref _gate);
        using var lease = gate?.TryAcquire() ?? throw new InvalidOperationException("plugin_configuration_not_ready");
        var snapshot = store.ReadRuntimeConfiguration(manifest);
        if (!gate!.IsReady) throw new InvalidOperationException("plugin_configuration_not_ready");
        return snapshot;
    }
}

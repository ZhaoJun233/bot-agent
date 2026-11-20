using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

namespace BotAgent.Adapters.Plugins;

internal sealed class PluginActivationDependencies : IPluginDependencies
{
    private PluginManager.InvocationGate? _gate;
    internal void Publish(PluginManager.InvocationGate gate) => Volatile.Write(ref _gate, gate);
    public T Get<T>(string providerId) where T : class, IPluginContribution
        => (Volatile.Read(ref _gate) ?? throw new InvalidOperationException("plugin_dependency_unavailable"))
            .GetDependency<T>(providerId);
}

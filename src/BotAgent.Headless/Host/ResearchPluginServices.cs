using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Plugins.Execution;
using BotAgent.Plugins.Net;
using BotAgent.Services.Net;

namespace BotAgent.Host;

/// <summary>Explicit host adapters and owner-scoped host ports, disposed after package drain.</summary>
internal sealed class ResearchPluginServices(IGroundedTextModel model, IWebPageTransport pages,
    IToolExecution execution, Func<ResearchOptions>? options, params IDisposable[] clients)
    : IServiceProvider, IOwnedHostServices, IDisposable
{
    private readonly Dictionary<(string PluginId, Type ServiceType), object> _owned = new();

    internal ResearchPluginServices(IGroundedTextModel model, IWebPageTransport pages,
        IToolExecution execution, params IDisposable[] clients) : this(model, pages, execution, null, clients) { }

    internal void RegisterForPlugin<T>(string ownerPluginId, T service) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPluginId);
        ArgumentNullException.ThrowIfNull(service);
        _owned[(ownerPluginId, typeof(T))] = service;
    }

    public object? GetService(Type type) => type == typeof(IGroundedTextModel) ? model
        : type == typeof(IWebPageTransport) ? pages
        : type == typeof(IToolExecution) ? execution
        : type == typeof(Func<ResearchOptions>) ? options
        : null;

    public object? GetServiceForPlugin(string pluginId, bool approvedHostDistribution, Type serviceType)
    {
        if (!approvedHostDistribution) return null;
        if (serviceType == typeof(Func<ResearchOptions>))
            return string.Equals(pluginId, "preset.feature.websearch", StringComparison.Ordinal) ? options : null;
        return _owned.TryGetValue((pluginId, serviceType), out var service) ? service : null;
    }

    public void Dispose() { foreach (var client in clients) client.Dispose(); }
}

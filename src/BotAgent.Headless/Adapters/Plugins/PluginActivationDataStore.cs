extern alias StorageModule;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Plugins.Net;
using BotAgent.Plugins.Execution;
using BotAgent.Services.Plugins;
using PackageDataStore = StorageModule::BotAgent.Adapters.Persistence.PackageDataStore;

namespace BotAgent.Adapters.Plugins;

internal interface IOwnedHostServices
{
    object? GetServiceForPlugin(string pluginId, bool approvedHostDistribution, Type serviceType);
}

/// <summary>One unforgeable resource capability per actual runtime generation.</summary>
internal sealed class PluginActivationDataStore : IPackageDataStore
{
    private readonly IPackageDataStore _store;
    private PluginManager.InvocationGate? _gate;

    internal PluginActivationDataStore(PluginPackageManifest manifest)
    {
        var data = manifest.Data!;
        var receipt = data.Receipt is { } r ? new OperationReceiptBinding(manifest.Id, r.Contract, r.Version,
            PluginLifecycleManifest.Fingerprint(manifest).ToLowerInvariant() + "." + Guid.NewGuid().ToString("N")) : null;
        _store = new PackageDataStore(data.Tables, data.StatePrefixes, data.Usage, data.Audit, receipt);
    }

    private PluginActivationDataStore(IPackageDataStore store) => _store = store;
    internal PluginActivationDataStore ControlView() => new(_store);

    internal void Publish(PluginManager.InvocationGate gate) => Volatile.Write(ref _gate, gate);

    public T Read<T>(Func<IPackageDataSession, T> operation) => Invoke(operation, write: false);
    public T Write<T>(Func<IPackageDataSession, T> operation) => Invoke(operation, write: true);

    private T Invoke<T>(Func<IPackageDataSession, T> operation, bool write)
    {
        var gate = Volatile.Read(ref _gate);
        using var lease = gate?.TryAcquire() ?? throw new InvalidOperationException("plugin_data_not_ready");
        T Run(IPackageDataSession session)
        {
            // A caller can wait for the database writer after acquiring its generation lease.
            if (!gate!.IsReady) throw new InvalidOperationException("plugin_data_not_ready");
            return operation(session);
        }
        return write ? _store.Write(Run) : _store.Read(Run);
    }

    internal static PluginContext Scope(PluginContext context, PluginActivationDataStore? data,
        PluginActivationWeb? web, PluginActivationModel? model, PluginActivationExecution? execution, IPluginDependencies dependencies,
        PluginActivationConfiguration? configuration = null, string? pluginId = null, bool approvedHostDistribution = false)
        => new(new Services(context.Services, data, web, model, execution, dependencies, configuration, pluginId, approvedHostDistribution),
            context.EnvironmentInfo);

    private sealed class Services(IServiceProvider parent, PluginActivationDataStore? data,
        PluginActivationWeb? web, PluginActivationModel? model, PluginActivationExecution? execution, IPluginDependencies dependencies,
        PluginActivationConfiguration? configuration, string? pluginId, bool approvedHostDistribution) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IPackageDataStore) ? data
            : serviceType == typeof(IWebPageTransport) ? web
            : serviceType == typeof(IGroundedTextModel) ? model
            : serviceType == typeof(IToolExecution) ? execution
            : serviceType == typeof(IPluginConfiguration) ? configuration
            : serviceType == typeof(IPluginDependencies) ? dependencies
            : parent is IOwnedHostServices owned && pluginId is not null
                ? owned.GetServiceForPlugin(pluginId, approvedHostDistribution, serviceType)
                : serviceType == typeof(Func<BotAgent.Services.Net.ResearchOptions>)
                    && (!approvedHostDistribution || !string.Equals(pluginId, "preset.feature.websearch", StringComparison.Ordinal))
                    ? null
                    : parent.GetService(serviceType);
    }
}

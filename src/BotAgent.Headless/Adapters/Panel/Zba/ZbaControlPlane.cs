extern alias StorageModule;

using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Management;
using BotAgent.Panel;
using BotAgent.Services;
using BotAgent.Services.Settings;
using ConfigurationStore = StorageModule::BotAgent.Storage.ConfigurationStore;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed class ZbaControlPlane
{
    internal ZbaControlPlane(ConfigurationStore configuration,
        SettingsHotReload.ConfigurationRequestAuthorization authorization, IManagedAuthorization managedAuthorization,
        PluginMarketService market, Func<DateTimeOffset> now, bool hadStoredConfigurationBeforeStartup)
    {
        Context = ZbaRuntimeContext.Create(now, hadStoredConfigurationBeforeStartup);
        Backend = new(configuration, authorization, managedAuthorization, market, Context, now);
        Endpoint = new(Backend, Context.Options, now, Context.Version, Context.InstanceId, message => FileLog.Write("Zba", message));
    }

    internal ZbaRuntimeContext Context { get; }
    internal ZbaControlBackend Backend { get; }
    internal ZbaEndpoint Endpoint { get; }
}

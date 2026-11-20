using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>Plugin management needs authenticated configuration and package services, without a business runtime.</summary>
internal sealed partial class ZbaPluginManagement
{
    private readonly PluginMarketService? _market;

    private readonly Func<ConfigurationCaller, ConfigurationAccess, IDisposable> _authorize;
    private readonly Func<ConfigurationCaller, ConfigurationReadKind, CancellationToken, ValueTask<ConfigurationReadResult>> _readConfiguration;
    private readonly Func<ConfigurationCaller, ZbaConfigurationSaveRequest, CancellationToken, ValueTask<ConfigurationSaveResult>> _saveConfiguration;

    internal ZbaPluginManagement(PluginMarketService? market,
        Func<ConfigurationCaller, ConfigurationAccess, IDisposable> authorize,
        Func<ConfigurationCaller, ConfigurationReadKind, CancellationToken, ValueTask<ConfigurationReadResult>> readConfiguration,
        Func<ConfigurationCaller, ZbaConfigurationSaveRequest, CancellationToken, ValueTask<ConfigurationSaveResult>> saveConfiguration)
    {
        _market = market;
        _authorize = authorize;
        _readConfiguration = readConfiguration;
        _saveConfiguration = saveConfiguration;
    }

    private IDisposable Authorize(ConfigurationCaller caller, ConfigurationAccess required) => _authorize(caller, required);

    private PluginMarketService Market => _market
        ?? throw new ZbaBackendException(503, "plugin_marketplace_unavailable", "插件市场组件不可用。");

    public async ValueTask<ZbaPluginCatalog> ListPluginCatalogAsync(
        ConfigurationCaller caller, string? query, string? category, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        return await Market.ReadCatalogAsync(query, category, ct).ConfigureAwait(false);
    }

    public async ValueTask<ZbaInstalledPlugins> ListInstalledPluginsAsync(
        ConfigurationCaller caller, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        return await Market.ReadInstalledAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<ZbaPluginSourceResolution> ResolvePluginSourceAsync(
        ConfigurationCaller caller, string sourceUrl, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.ResolveSourceAsync(sourceUrl, ct).ConfigureAwait(false);
    }

    public async ValueTask<ZbaPluginOperationResult> InstallPluginAsync(
        ConfigurationCaller caller, ZbaPluginMutationRequest request, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.InstallAsync(request, CallerScope(caller), ct,
            token => PersistInstallDisabledAsync(caller, request.Manifest.Id, token)).ConfigureAwait(false);
    }

    public async ValueTask<ZbaPluginOperationResult> UpdatePluginAsync(
        ConfigurationCaller caller, ZbaPluginMutationRequest request, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.UpdateAsync(request, CallerScope(caller), ct).ConfigureAwait(false);
    }

    public async ValueTask<ZbaPluginOperationResult> UninstallPluginAsync(
        ConfigurationCaller caller, string pluginId, ZbaPluginUninstallRequest request, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.UninstallAsync(pluginId, request, CallerScope(caller), ct).ConfigureAwait(false);
    }

    public ValueTask<ZbaPluginOperationResult> FindPluginOperationAsync(
        ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        return ValueTask.FromResult(Market.FindOperation(idempotencyKey));
    }

    public async ValueTask<ZbaPluginSettings> ReadPluginSettingsAsync(
        ConfigurationCaller caller, string pluginId, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        return await Market.ReadSettingsAsync(pluginId, ct).ConfigureAwait(false);
    }

    public async ValueTask<ZbaPluginSettings> SavePluginSettingsAsync(
        ConfigurationCaller caller, string pluginId, ZbaPluginSettingsSaveRequest request,
        CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.SaveSettingsAsync(pluginId, request, CallerScope(caller), ct).ConfigureAwait(false);
    }

    /// <summary>回执里的调用方标识只用认证主体，绝不用 token 或插件提供的身份。</summary>
    private static string CallerScope(ConfigurationCaller caller) => ZbaCallerAuthorization.Scope(caller);
}

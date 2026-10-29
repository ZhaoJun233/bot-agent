using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>
/// 插件市场与提供商模型发现的宿主实现：只做鉴权与投影，下载/校验/落盘在
/// <see cref="PluginMarketService" /> 与 <see cref="ProviderModelDiscovery" /> 里。
/// </summary>
internal sealed partial class ZbaHostBackend
{
    private readonly PluginMarketService? _market;
    private readonly Func<string, ModelProviderRoute?>? _providerRoute;
    private readonly ProviderModelDiscovery? _discovery;

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
        return await Market.InstallAsync(request, CallerScope(caller), ct).ConfigureAwait(false);
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

    /// <summary>模型发现失败也只回 <c>errorCode</c>（HTTP 200），前端始终保留手填。</summary>
    public async ValueTask<ZbaProviderModels> ListProviderModelsAsync(
        ConfigurationCaller caller, string providerKey, CancellationToken ct = default)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        if (_discovery is null || _providerRoute is null)
            return new ZbaProviderModels(Array.Empty<string>(), "model_route_unavailable");
        // 只接受已登记（含新存停用）的 provider；路由缺失一律明确失败，不猜地址或密钥。
        var route = _providerRoute(providerKey);
        if (route is null) return new ZbaProviderModels(Array.Empty<string>(), "model_route_unavailable");
        var result = await _discovery.ListAsync(route, ct).ConfigureAwait(false);
        return new ZbaProviderModels(result.Models, result.ErrorCode);
    }

    /// <summary>回执里的调用方标识只用认证主体，绝不用 token 或插件提供的身份。</summary>
    private static string CallerScope(ConfigurationCaller caller) => caller.AuthorityId + ":" + caller.SubjectId;
}

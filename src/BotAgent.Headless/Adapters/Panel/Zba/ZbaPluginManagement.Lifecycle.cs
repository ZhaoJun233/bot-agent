using BotAgent.Domain.Configuration;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaPluginManagement
{
    public async ValueTask<ZbaPluginLifecyclePreview> PreviewPluginLifecycleAsync(ConfigurationCaller caller,
        ZbaPluginLifecyclePreviewRequest request, CancellationToken cancellationToken)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.PreviewLifecycleAsync(request, CallerScope(caller), cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<ZbaPluginOperationResult> RollbackPluginAsync(ConfigurationCaller caller,
        ZbaPluginMutationRequest request, CancellationToken cancellationToken)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.RollbackAsync(request, CallerScope(caller), cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<ZbaPluginUiSession> OpenPluginUiSessionAsync(ConfigurationCaller caller,
        string pluginId, CancellationToken cancellationToken)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        var assets = Market.UiAssets ?? throw new ZbaBackendException(503, "plugin_ui_unavailable", "插件界面服务不可用。");
        return await assets.OpenSessionAsync(pluginId, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<ZbaPluginUiSessionClosed> ClosePluginUiSessionAsync(ConfigurationCaller caller,
        string pluginId, string sessionId, CancellationToken cancellationToken)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        var assets = Market.UiAssets ?? throw new ZbaBackendException(503, "plugin_ui_unavailable", "插件界面服务不可用。");
        return await assets.CloseSessionAsync(pluginId, sessionId, cancellationToken).ConfigureAwait(false);
    }
    public ValueTask<ZbaPluginUiAssetResult> ReadPluginUiAssetAsync(string pluginId, string releaseSha,
        string ticket, string assetPath, bool authenticatedCaller, CancellationToken cancellationToken)
    {
        var assets = _market?.UiAssets;
        if (assets is null)
            return ValueTask.FromResult(new ZbaPluginUiAssetResult(404, "application/json; charset=utf-8",
                Array.Empty<byte>(), "plugin_ui_asset_not_found", null, "no-store"));
        return assets.ReadAssetAsync(pluginId, releaseSha, ticket, assetPath, authenticatedCaller, cancellationToken);
    }
    public async ValueTask<ZbaPluginBundlePreview> PreviewPluginBundleAsync(ConfigurationCaller caller,
        ZbaPluginBundlePreviewRequest request, CancellationToken cancellationToken)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.PreviewBundleAsync(request, CallerScope(caller), cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<ZbaPluginOperationResult> ApplyPluginBundleAsync(ConfigurationCaller caller,
        ZbaPluginBundleApplyRequest request, CancellationToken cancellationToken)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        return await Market.ApplyBundleAsync(request, CallerScope(caller), cancellationToken).ConfigureAwait(false);
    }
}
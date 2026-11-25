using BotAgent.Domain.Configuration;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaHostBackend
{
    private readonly ZbaPluginManagement _pluginManagement;

    public ValueTask<ZbaPluginCatalog> ListPluginCatalogAsync(ConfigurationCaller caller, string? query, string? category, CancellationToken ct = default)
        => _pluginManagement.ListPluginCatalogAsync(caller, query, category, ct);

    public ValueTask<ZbaInstalledPlugins> ListInstalledPluginsAsync(ConfigurationCaller caller, CancellationToken ct = default)
        => _pluginManagement.ListInstalledPluginsAsync(caller, ct);

    public ValueTask<ZbaPluginDiscovery> DiscoverPluginSourceAsync(ConfigurationCaller caller, string sourceUrl, CancellationToken ct = default)
        => _pluginManagement.DiscoverPluginSourceAsync(caller, sourceUrl, ct);

    public ValueTask<ZbaPluginPreview> PreviewPluginAsync(ConfigurationCaller caller, ZbaPluginPreviewRequest request, CancellationToken ct = default)
        => _pluginManagement.PreviewPluginAsync(caller, request, ct);

    public ValueTask<ZbaPluginSourceResolution> ResolvePluginSourceAsync(ConfigurationCaller caller, string sourceUrl, CancellationToken ct = default)
        => _pluginManagement.ResolvePluginSourceAsync(caller, sourceUrl, ct);

    public ValueTask<ZbaPluginOperationResult> InstallPluginAsync(ConfigurationCaller caller, ZbaPluginMutationRequest request, CancellationToken ct = default)
        => _pluginManagement.InstallPluginAsync(caller, request, ct);

    public ValueTask<ZbaPluginLifecyclePreview> PreviewPluginLifecycleAsync(ConfigurationCaller caller, ZbaPluginLifecyclePreviewRequest request, CancellationToken cancellationToken)
        => _pluginManagement.PreviewPluginLifecycleAsync(caller, request, cancellationToken);

    public ValueTask<ZbaPluginOperationResult> RollbackPluginAsync(ConfigurationCaller caller, ZbaPluginMutationRequest request, CancellationToken cancellationToken)
        => _pluginManagement.RollbackPluginAsync(caller, request, cancellationToken);

    public ValueTask<ZbaPluginOperationResult> UpdatePluginAsync(ConfigurationCaller caller, ZbaPluginMutationRequest request, CancellationToken ct = default)
        => _pluginManagement.UpdatePluginAsync(caller, request, ct);

    public ValueTask<ZbaPluginOperationResult> UninstallPluginAsync(ConfigurationCaller caller, string pluginId, ZbaPluginUninstallRequest request,
        CancellationToken ct = default)
        => _pluginManagement.UninstallPluginAsync(caller, pluginId, request, ct);

    public ValueTask<ZbaPluginOperationResult> FindPluginOperationAsync(ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default)
        => _pluginManagement.FindPluginOperationAsync(caller, idempotencyKey, ct);

    public ValueTask<ZbaPluginSettings> ReadPluginSettingsAsync(ConfigurationCaller caller, string pluginId, CancellationToken ct = default)
        => _pluginManagement.ReadPluginSettingsAsync(caller, pluginId, ct);

    public ValueTask<ZbaPluginSettings> SavePluginSettingsAsync(ConfigurationCaller caller, string pluginId, ZbaPluginSettingsSaveRequest request,
        CancellationToken ct = default)
        => _pluginManagement.SavePluginSettingsAsync(caller, pluginId, request, ct);

    public ValueTask<ZbaPluginUiSession> OpenPluginUiSessionAsync(ConfigurationCaller caller, string pluginId, CancellationToken ct = default)
        => _pluginManagement.OpenPluginUiSessionAsync(caller, pluginId, ct);

    public ValueTask<ZbaPluginUiSessionClosed> ClosePluginUiSessionAsync(ConfigurationCaller caller, string pluginId, string sessionId, CancellationToken ct = default)
        => _pluginManagement.ClosePluginUiSessionAsync(caller, pluginId, sessionId, ct);

    public ValueTask<ZbaPluginUiAssetResult> ReadPluginUiAssetAsync(string pluginId, string releaseSha, string ticket, string assetPath, bool authenticatedCaller, CancellationToken ct = default)
        => _pluginManagement.ReadPluginUiAssetAsync(pluginId, releaseSha, ticket, assetPath, authenticatedCaller, ct);

    public ValueTask<ZbaPluginBundlePreview> PreviewPluginBundleAsync(ConfigurationCaller caller, ZbaPluginBundlePreviewRequest request, CancellationToken ct = default)
        => _pluginManagement.PreviewPluginBundleAsync(caller, request, ct);

    public ValueTask<ZbaPluginOperationResult> ApplyPluginBundleAsync(ConfigurationCaller caller, ZbaPluginBundleApplyRequest request, CancellationToken ct = default)
        => _pluginManagement.ApplyPluginBundleAsync(caller, request, ct);
}

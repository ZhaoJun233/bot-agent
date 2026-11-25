using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaControlBackend
{
    public ValueTask<ZbaQuotaPage> ListQuotasAsync(ConfigurationCaller caller, CancellationToken ct = default)
        => RequireBusiness(ct).ListQuotasAsync(caller, ct);

    public ValueTask<ZbaQuotaSaveResult> SaveQuotaAsync(ConfigurationCaller caller, ZbaQuotaSaveRequest request, CancellationToken ct = default)
        => RequireBusiness(ct).SaveQuotaAsync(caller, request, ct);

    public ValueTask<ZbaQuotaFindResult> FindQuotaAsync(ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default)
        => RequireBusiness(ct).FindQuotaAsync(caller, idempotencyKey, ct);

    public ValueTask<ZbaSessionPage> ListSessionsAsync(ConfigurationCaller caller, string sourceKey, int limit, string? cursor, CancellationToken ct = default)
        => RequireBusiness(ct).ListSessionsAsync(caller, sourceKey, limit, cursor, ct);

    public ValueTask<ZbaSessionEnsureResult> EnsureSessionAsync(ConfigurationCaller caller, string sourceKey, CancellationToken ct = default)
        => RequireBusiness(ct).EnsureSessionAsync(caller, sourceKey, ct);

    public ValueTask<ManagedPage> ListCapabilitiesAsync(ConfigurationCaller caller, ManagedPageRequest request, CancellationToken ct = default)
        => RequireBusiness(ct).ListCapabilitiesAsync(caller, request, ct);

    public ValueTask<ManagedInspectResult> InspectCapabilityAsync(ConfigurationCaller caller, ManagedObjectKey key, CancellationToken ct = default)
        => RequireBusiness(ct).InspectCapabilityAsync(caller, key, ct);


    public ValueTask<ManagedOperationResult> ExecuteCapabilityAsync(ConfigurationCaller caller, ManagedExecuteRequest request, CancellationToken ct = default)
        => RequireBusiness(ct).ExecuteCapabilityAsync(caller, request, ct);

    public ValueTask<ManagedCancellationResult> CancelCapabilityAsync(ConfigurationCaller caller, ManagedOperationKey key, CancellationToken ct = default)
        => RequireBusiness(ct).CancelCapabilityAsync(caller, key, ct);

    public ValueTask<ZbaTaskAccepted> SubmitTaskAsync(ConfigurationCaller caller, ZbaTaskSubmission submission, string idempotencyKey, CancellationToken ct = default)
        => RequireBusiness(ct).SubmitTaskAsync(caller, submission, idempotencyKey, ct);

    public ValueTask<ZbaTaskPage> ListTasksAsync(ConfigurationCaller caller, int limit, string? cursor, CancellationToken ct = default)
        => RequireBusiness(ct).ListTasksAsync(caller, limit, cursor, ct);

    public ValueTask<ZbaTaskSummary?> GetTaskAsync(ConfigurationCaller caller, string id, CancellationToken ct = default)
        => RequireBusiness(ct).GetTaskAsync(caller, id, ct);

    public ValueTask<ZbaTaskResult?> GetTaskResultAsync(ConfigurationCaller caller, string id, CancellationToken ct = default)
        => RequireBusiness(ct).GetTaskResultAsync(caller, id, ct);

    public ValueTask<ZbaTaskCancelResult> CancelTaskAsync(ConfigurationCaller caller, string id, CancellationToken ct = default)
        => RequireBusiness(ct).CancelTaskAsync(caller, id, ct);

    public ValueTask<IReadOnlyList<ZbaApprovalSummary>> ListApprovalsAsync(ConfigurationCaller caller, CancellationToken ct = default)
        => RequireBusiness(ct).ListApprovalsAsync(caller, ct);

    public ValueTask<ZbaApprovalDecisionResult> DecideApprovalAsync(ConfigurationCaller caller, string requestId, bool approve, CancellationToken ct = default)
        => RequireBusiness(ct).DecideApprovalAsync(caller, requestId, approve, ct);

    public ValueTask<ZbaTracePage> ListTracesAsync(ConfigurationCaller caller, int limit, CancellationToken ct = default)
        => RequireBusiness(ct).ListTracesAsync(caller, limit, ct);

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

    public ValueTask<ZbaProviderModels> ListProviderModelsAsync(ConfigurationCaller caller, string providerKey, CancellationToken ct = default)
        => RequireBusiness(ct).ListProviderModelsAsync(caller, providerKey, ct);
}

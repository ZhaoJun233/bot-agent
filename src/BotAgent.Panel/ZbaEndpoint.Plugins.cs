namespace BotAgent.Panel;

/// <summary>
/// 插件市场与插件设置的 HTTP 适配层。这里只做形状校验与状态码映射，
/// 下载、校验、暂存与设置持久化全部在宿主侧完成（Panel 不碰文件、网络与数据库）。
/// </summary>
public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandlePluginCatalogAsync(
        ConfigurationCaller caller, ZbaQuery query, string requestId, CancellationToken ct)
    {
        var result = await _backend
            .ListPluginCatalogAsync(caller, query.Get("q"), query.Get("category"), ct)
            .ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginInstalledAsync(
        ConfigurationCaller caller, string requestId, CancellationToken ct)
    {
        var result = await _backend.ListInstalledPluginsAsync(caller, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginSourceResolveAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaPluginSourceResolveRequest>(request);
        ZbaRules.ValidatePluginSource(body.SourceUrl);
        var result = await _backend.ResolvePluginSourceAsync(caller, body.SourceUrl, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginMutationAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct, bool update)
    {
        var body = Body<ZbaPluginMutationRequest>(request);
        ZbaRules.ValidatePluginManifest(body.Manifest);
        if (!body.TrustAcknowledged) throw new ZbaBadRequestException("trust_not_acknowledged");
        ZbaRules.ValidatePluginIdempotencyKey(body.IdempotencyKey);
        ZbaRules.ValidatePluginRevision(body.ExpectedRevision);
        var result = update
            ? await _backend.UpdatePluginAsync(caller, body, ct).ConfigureAwait(false)
            : await _backend.InstallPluginAsync(caller, body, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginUninstallAsync(
        ConfigurationCaller caller, string pluginId, ZbaRequest request, string requestId, CancellationToken ct)
    {
        ZbaRules.ValidatePluginId(pluginId);
        var body = Body<ZbaPluginUninstallRequest>(request);
        ZbaRules.ValidatePluginIdempotencyKey(body.IdempotencyKey);
        ZbaRules.ValidatePluginRevision(body.ExpectedRevision);
        var result = await _backend.UninstallPluginAsync(caller, pluginId, body, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginOperationAsync(
        ConfigurationCaller caller, string idempotencyKey, string requestId, CancellationToken ct)
    {
        ZbaRules.ValidatePluginIdempotencyKey(idempotencyKey);
        var result = await _backend.FindPluginOperationAsync(caller, idempotencyKey, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginSettingsReadAsync(
        ConfigurationCaller caller, string pluginId, string requestId, CancellationToken ct)
    {
        ZbaRules.ValidatePluginId(pluginId);
        var result = await _backend.ReadPluginSettingsAsync(caller, pluginId, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginSettingsSaveAsync(
        ConfigurationCaller caller, string pluginId, ZbaRequest request, string requestId, CancellationToken ct)
    {
        ZbaRules.ValidatePluginId(pluginId);
        var body = Body<ZbaPluginSettingsSaveRequest>(request);
        ZbaRules.ValidatePluginIdempotencyKey(body.IdempotencyKey);
        ZbaRules.ValidatePluginRevision(body.ExpectedRevision);
        ZbaRules.ValidatePluginSettingEdits(body.Edits);
        var result = await _backend.SavePluginSettingsAsync(caller, pluginId, body, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }
}

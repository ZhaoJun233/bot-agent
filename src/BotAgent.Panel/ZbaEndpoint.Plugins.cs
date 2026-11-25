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
        if (!update && !body.TrustAcknowledged) throw new ZbaBadRequestException("trust_not_acknowledged");
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

    private async ValueTask<ZbaResponse> HandlePluginUiSessionOpenAsync(
        ConfigurationCaller caller, string pluginId, string requestId, CancellationToken ct)
    {
        ZbaRules.ValidatePluginId(pluginId);
        var result = await _backend.OpenPluginUiSessionAsync(caller, pluginId, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginUiSessionCloseAsync(
        ConfigurationCaller caller, string pluginId, string sessionId, string requestId, CancellationToken ct)
    {
        ZbaRules.ValidatePluginId(pluginId);
        var result = await _backend.ClosePluginUiSessionAsync(caller, pluginId, sessionId, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginUiAssetAsync(
        ZbaRequest request, string pluginId, string releaseSha, string ticket, string assetPath,
        string requestId, CancellationToken ct)
    {
        var hasAuthHeader = request.Headers.ContainsKey("Authorization");
        var authenticatedCaller = (hasAuthHeader || string.Equals(ticket, "auth", StringComparison.Ordinal))
            && _security.Authorize(request, ZbaScope.Read).Ok;
        var result = await _backend.ReadPluginUiAssetAsync(pluginId, releaseSha, ticket, assetPath, authenticatedCaller, ct).ConfigureAwait(false);
        if (result.Status != 200)
            return Error(result.Status, result.ErrorCode ?? "plugin_ui_asset_not_found", "无法加载插件界面资源。",
                requestId: requestId, authenticate: result.Status == 401);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache-Control"] = result.CacheControl,
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
        };
        if (result.ContentSecurityPolicy is not null) headers["Content-Security-Policy"] = result.ContentSecurityPolicy;
        return new ZbaResponse(200, result.ContentType, result.Bytes, headers);
    }

    private async ValueTask<ZbaResponse> HandlePluginBundlePreviewAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaPluginBundlePreviewRequest>(request);
        if (body.Manifests is null || body.Manifests.Count is 0 or > 16)
            throw new ZbaBadRequestException("plugin_bundle_invalid");
        foreach (var manifest in body.Manifests) ZbaRules.ValidatePluginManifest(manifest);
        var result = await _backend.PreviewPluginBundleAsync(caller, body, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginBundleApplyAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaPluginBundleApplyRequest>(request);
        if (body.Manifests is null || body.Manifests.Count is 0 or > 16)
            throw new ZbaBadRequestException("plugin_bundle_invalid");
        foreach (var manifest in body.Manifests) ZbaRules.ValidatePluginManifest(manifest);
        if (!body.TrustAcknowledged) throw new ZbaBadRequestException("trust_not_acknowledged");
        ZbaRules.ValidatePluginIdempotencyKey(body.IdempotencyKey);
        ZbaRules.ValidatePluginRevision(body.ExpectedRevision);
        var result = await _backend.ApplyPluginBundleAsync(caller, body, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }
}
namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandleSessionsListAsync(
        ConfigurationCaller caller, ZbaQuery query, string requestId, CancellationToken ct)
    {
        var sourceKey = query.Get("sourceKey");
        if (string.IsNullOrWhiteSpace(sourceKey)) throw new ZbaBadRequestException("missing_source_key");
        ZbaRules.ValidatePanelSourceKey(sourceKey);
        var limit = query.GetInt("limit", ZbaContract.DefaultPageSize, 1, ZbaContract.MaxPageSize);
        var page = await _backend.ListSessionsAsync(caller, sourceKey, limit, query.GetCursor(), ct).ConfigureAwait(false);
        return Json(page, requestId);
    }

    private async ValueTask<ZbaResponse> HandleSessionsEnsureAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaSessionEnsureRequest>(request);
        ZbaRules.ValidatePanelSourceKey(body.SourceKey);
        var result = await _backend.EnsureSessionAsync(caller, body.SourceKey, ct).ConfigureAwait(false);
        return Json(result, requestId);
    }
}

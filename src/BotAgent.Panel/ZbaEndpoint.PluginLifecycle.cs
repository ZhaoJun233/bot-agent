namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandlePluginLifecyclePreviewAsync(ConfigurationCaller caller,
        ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaPluginLifecyclePreviewRequest>(request);
        ZbaRules.ValidatePluginId(body.PluginId);
        ZbaRules.ValidatePluginRevision(body.ExpectedRevision);
        return Json(await _backend.PreviewPluginLifecycleAsync(caller, body, ct).ConfigureAwait(false), requestId);
    }
    private async ValueTask<ZbaResponse> HandlePluginRollbackAsync(ConfigurationCaller caller,
        ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaPluginMutationRequest>(request);
        ZbaRules.ValidatePluginManifest(body.Manifest);
        ZbaRules.ValidatePluginRevision(body.ExpectedRevision);
        ZbaRules.ValidatePluginIdempotencyKey(body.IdempotencyKey);
        return Json(await _backend.RollbackPluginAsync(caller, body, ct).ConfigureAwait(false), requestId);
    }
}
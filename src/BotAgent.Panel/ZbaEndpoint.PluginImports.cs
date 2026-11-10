namespace BotAgent.Panel;

public sealed partial class ZbaEndpoint
{
    private async ValueTask<ZbaResponse> HandlePluginSourceDiscoverAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaPluginDiscoveryRequest>(request);
        ZbaRules.ValidatePluginSource(body.SourceUrl);
        return Json(await _backend.DiscoverPluginSourceAsync(caller, body.SourceUrl, ct).ConfigureAwait(false), requestId);
    }

    private async ValueTask<ZbaResponse> HandlePluginPreviewAsync(
        ConfigurationCaller caller, ZbaRequest request, string requestId, CancellationToken ct)
    {
        var body = Body<ZbaPluginPreviewRequest>(request);
        ZbaRules.ValidatePluginRevision(body.ExpectedRevision);
        return Json(await _backend.PreviewPluginAsync(caller, body, ct).ConfigureAwait(false), requestId);
    }
}
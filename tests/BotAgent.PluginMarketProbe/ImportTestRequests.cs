using BotAgent.Adapters.Plugins;
using BotAgent.Panel;

// Legacy lifecycle regressions now enter through real JSON preview admission. This is test-only:
// production must use the authorized coordinator callback; focused regressions exercise that below.
internal static class ImportTestRequests
{
    private static readonly Dictionary<string, string> Previews = new(StringComparer.Ordinal);
    internal static async Task<ZbaPluginOperationResult> InstallWithJsonPreviewAsync(this PluginMarketService market,
        ZbaPluginMutationRequest request, string caller, CancellationToken ct)
    {
        if (!Previews.TryGetValue(request.IdempotencyKey, out var previewId))
        {
            var preview = await market.PreviewAsync(new("json", request.ExpectedRevision, Manifest: request.Manifest), caller, ct);
            previewId = preview.PreviewId;
            Previews[request.IdempotencyKey] = previewId;
        }
        return await market.InstallAsync(request with { PreviewId = previewId }, caller, ct, _ => Task.CompletedTask);
    }
}
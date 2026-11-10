using System.Reflection;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

// Synthetic orchestration fixtures only: real previews, no production admission bypass.
internal static class LifecycleTestRequests
{
    private static readonly Dictionary<string, string> Previews = new(StringComparer.Ordinal);
    internal static async Task<ZbaPluginOperationResult> UpdateWithLifecyclePreviewAsync(this PluginMarketService market,
        ZbaPluginMutationRequest request, string caller, CancellationToken ct)
    {
        if (!Previews.TryGetValue(request.IdempotencyKey, out var preview))
        {
            var result = await market.PreviewLifecycleAsync(new("update", request.Manifest.Id, request.ExpectedRevision,
                new("json", Manifest: request.Manifest)), caller, ct);
            preview = result.PreviewId; Previews[request.IdempotencyKey] = preview;
        }
        return await market.UpdateAsync(request with { PreviewId = preview }, caller, ct);
    }
    internal static async Task<ZbaPluginOperationResult> UninstallWithLifecyclePreviewAsync(this PluginMarketService market,
        string id, ZbaPluginUninstallRequest request, string caller, CancellationToken ct)
    {
        if (!Previews.TryGetValue(request.IdempotencyKey, out var preview))
        {
            var result = await market.PreviewLifecycleAsync(new("uninstall", id, request.ExpectedRevision), caller, ct);
            preview = result.PreviewId; Previews[request.IdempotencyKey] = preview;
        }
        return await market.UninstallAsync(id, request with { PreviewId = preview, TrustAcknowledged = true }, caller, ct);
    }
    internal static async Task SaveState(PluginPackageService packages, InstalledPluginPackage state)
    {
        var store = typeof(PluginPackageService).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(packages)!;
        await (Task)store.GetType().GetMethod("SaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(store, [state, CancellationToken.None])!;
        await packages.GetInstalledAsync();
    }
    internal static async Task Settle(PluginPackageService packages, string id)
    {
        var state = (await packages.GetInstalledAsync()).Single(p => p.Id == id);
        if (state.Pending is null && !state.PendingUninstall) return;
        await SaveState(packages, state.PendingUninstall ? new(id) : state with
            { Active = state.Pending ?? state.Active, Previous = System.Text.Json.JsonSerializer.Serialize(state.Active) == System.Text.Json.JsonSerializer.Serialize(state.Pending) ? state.Previous : state.Active ?? state.Previous, Pending = null });
    }
}
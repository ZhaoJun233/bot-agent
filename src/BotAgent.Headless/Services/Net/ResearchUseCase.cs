using BotAgent.Services.Links;
using BotAgent.Services.Plugins;

namespace BotAgent.Services.Net;

/// <summary>Host entry points resolve the current package for each request; no research state lives here.</summary>
public sealed class ResearchUseCase(PluginManager registry) : IResearchUseCase, IResearchDiagnostics, ILinkPreviewer
{
    private const string ProviderId = "preset.feature.websearch";
    private static bool IsUnavailable(InvalidOperationException ex)
        => ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing";

    public bool IsReady => registry.IsReady(ProviderId);

    public bool TryBeginSearch(string sourceKey, int cooldownSeconds, out string reason)
    {
        if (registry.TryGetContribution<IResearchUseCase>(ProviderId, out var current) && current is not null)
        {
            try { return current.TryBeginSearch(sourceKey, cooldownSeconds, out reason); }
            catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
        }
        reason = "plugin_contribution_unavailable";
        return false;
    }

    public async Task<WebSearchResult?> SearchAsync(string sourceKey, string query, CancellationToken ct = default)
    {
        if (!registry.TryGetContribution<IResearchUseCase>(ProviderId, out var current) || current is null) return null;
        try { return await current.SearchAsync(sourceKey, query, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
    }

    public async Task<string?> ReadPageAsync(string sourceKey, string url, CancellationToken ct = default)
    {
        if (!registry.TryGetContribution<IResearchUseCase>(ProviderId, out var current) || current is null) return null;
        try { return await current.ReadPageAsync(sourceKey, url, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
    }

    public void AddNote(string sourceKey, string note)
    {
        if (!registry.TryGetContribution<IResearchUseCase>(ProviderId, out var current) || current is null) return;
        try { current.AddNote(sourceKey, note); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public void KeepNote(string sourceKey, string? note, string why)
    {
        if (!registry.TryGetContribution<IResearchUseCase>(ProviderId, out var current) || current is null) return;
        try { current.KeepNote(sourceKey, note, why); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public string? TakeNote(string sourceKey)
    {
        if (!registry.TryGetContribution<IResearchUseCase>(ProviderId, out var current) || current is null) return null;
        try { return current.TakeNote(sourceKey); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
    }

    public async Task<string?> DescribeAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        if (!registry.TryGetContribution<ILinkPreviewer>(ProviderId, out var previewer) || previewer is null) return null;
        try { return await previewer.DescribeAsync(urls, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
    }

    public async Task<WebSearchResult> TestSearchAsync(string query, CancellationToken ct)
    {
        if (registry.TryGetControlContribution<IResearchDiagnostics>(ProviderId, out var diagnostics) && diagnostics is not null)
        {
            try { return await diagnostics.TestSearchAsync(query, ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
        }
        return new WebSearchResult(query, null, [], "无", "搜索服务还没初始化");
    }

    public async Task<(string? Text, string? Error)> TestReadPageAsync(string url, CancellationToken ct)
    {
        if (registry.TryGetControlContribution<IResearchDiagnostics>(ProviderId, out var diagnostics) && diagnostics is not null)
        {
            try { return await diagnostics.TestReadPageAsync(url, ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
        }
        return (null, "搜索服务还没初始化");
    }
}

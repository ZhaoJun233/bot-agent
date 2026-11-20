namespace BotAgent.Services.Net;

/// <summary>Search and page-reading provider contract.</summary>
public interface IWebSearch : BotAgent.Domain.Plugins.IPluginContribution
{
    Task<WebSearchResult?> SearchAsync(string query, CancellationToken ct);
    Task<(string? Text, string? Error)> ReadPageAsync(string url, CancellationToken ct);
}

/// <summary>Source-scoped cooldown and one-shot research notes.</summary>
public interface IResearchUseCase : BotAgent.Domain.Plugins.IPluginContribution
{
    bool IsReady { get; }
    bool TryBeginSearch(string sourceKey, int cooldownSeconds, out string reason);
    Task<WebSearchResult?> SearchAsync(string sourceKey, string query, CancellationToken ct = default);
    Task<string?> ReadPageAsync(string sourceKey, string url, CancellationToken ct = default);
    void AddNote(string sourceKey, string note);
    void KeepNote(string sourceKey, string? note, string why);
    string? TakeNote(string sourceKey);
}

public interface IResearchDiagnostics : BotAgent.Domain.Plugins.IPluginControlContribution
{
    Task<WebSearchResult> TestSearchAsync(string query, CancellationToken ct);
    Task<(string? Text, string? Error)> TestReadPageAsync(string url, CancellationToken ct);
}

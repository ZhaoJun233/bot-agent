namespace BotAgent.Services.Net;

/// <summary>Research-owned configuration; no host credentials, routing or unrelated settings.</summary>
public sealed record ResearchOptions(
    bool EnableWebSearch,
    bool WebSearchUseModelSearch,
    string WebSearchSources,
    int WebSearchMaxResults,
    int WebSearchCooldownSeconds,
    int WebSearchTimeoutSeconds,
    int WebSearchReadMaxChars,
    bool EnableLinkPreview,
    int LinkPreviewTimeoutSeconds,
    int LinkPreviewMax);

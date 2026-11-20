namespace BotAgent.Services.Net;

internal static class HostResearchOptions
{
    internal static Func<ResearchOptions> CreateAccessor(Func<AppSettings> settings) => () => Project(settings());

    internal static void SeedEnvironment(AppSettings settings)
    {
        var seeded = ResearchSettings.SeedEnvironment(Project(settings), Environment.GetEnvironmentVariable,
            Console.Error.WriteLine);
        settings.EnableWebSearch = seeded.EnableWebSearch;
        settings.WebSearchUseModelSearch = seeded.WebSearchUseModelSearch;
        settings.WebSearchSources = seeded.WebSearchSources;
        settings.WebSearchMaxResults = seeded.WebSearchMaxResults;
        settings.WebSearchCooldownSeconds = seeded.WebSearchCooldownSeconds;
        settings.WebSearchTimeoutSeconds = seeded.WebSearchTimeoutSeconds;
        settings.WebSearchReadMaxChars = seeded.WebSearchReadMaxChars;
        settings.EnableLinkPreview = seeded.EnableLinkPreview;
        settings.LinkPreviewTimeoutSeconds = seeded.LinkPreviewTimeoutSeconds;
        settings.LinkPreviewMax = seeded.LinkPreviewMax;
    }

    private static ResearchOptions Project(AppSettings settings) => new(
        settings.EnableWebSearch, settings.WebSearchUseModelSearch, settings.WebSearchSources,
        settings.WebSearchMaxResults, settings.WebSearchCooldownSeconds, settings.WebSearchTimeoutSeconds,
        settings.WebSearchReadMaxChars, settings.EnableLinkPreview, settings.LinkPreviewTimeoutSeconds,
        settings.LinkPreviewMax);
}

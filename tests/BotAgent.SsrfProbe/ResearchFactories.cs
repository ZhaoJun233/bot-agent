using BotAgent.Adapters.Net;
using BotAgent.Services;
using BotAgent.Services.Links;
using BotAgent.Services.Net;

internal static class ResearchFactories
{
    internal static WebSearchService Search(IHttpFetcher http, Func<AppSettings> settings, Action<string> log,
        IHttpFetcher? pageHttp = null) => new(ResearchFixture.Model(http, settings),
            new WebPageTransport(pageHttp ?? http, () => false), ResearchFixture.Options(settings), log);

    internal static LinkPreviewer Links(IHttpFetcher pages, Func<AppSettings> settings, Action<string> log)
        => new(new WebPageTransport(pages, () => false), ResearchFixture.Options(settings), log);
}

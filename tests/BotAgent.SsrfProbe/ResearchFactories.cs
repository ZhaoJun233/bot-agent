using System.Reflection;
using System.Runtime.Loader;
using BotAgent.Adapters.Net;
using BotAgent.Domain.Ports;
using BotAgent.Plugins.Execution;
using BotAgent.Plugins.Net;
using BotAgent.Services;
using BotAgent.Services.Net;
using BotAgent.TestFixtures;

internal static class ResearchFactories
{
    private static readonly (AssemblyLoadContext Context, Assembly Assembly) Plugin = LoadWebSearch();
    private static readonly Type SearchType = Plugin.Assembly.GetType("BotAgent.Services.Net.WebSearchService", throwOnError: true)!;
    private static readonly Type LinkPreviewerType = Plugin.Assembly.GetType("BotAgent.Services.Links.LinkPreviewer", throwOnError: true)!;
    private static readonly MethodInfo PreviewMethod = LinkPreviewerType.GetMethod("PreviewAsync", BindingFlags.Public | BindingFlags.Instance)!;

    private static (AssemblyLoadContext Context, Assembly Assembly) LoadWebSearch()
    {
        var root = OfficialPluginsTestLocator.ResolveMarketplaceRoot(OfficialPluginsTestLocator.FindRepoRoot());
        return OfficialPluginsTestLocator.LoadIsolatedPlugin(root, "websearch", "BotAgent.Package.WebSearch");
    }

    internal static IWebSearch Search(IGroundedTextModel model, IWebPageTransport pages, Func<ResearchOptions> options,
        Action<string> log, IToolExecution? circuit = null)
        => (IWebSearch)Activator.CreateInstance(SearchType, model, pages, options, log, circuit)!;

    internal static IWebSearch Search(IHttpFetcher http, Func<AppSettings> settings, Action<string> log,
        IHttpFetcher? pageHttp = null)
        => Search(ResearchFixture.Model(http, settings), new WebPageTransport(pageHttp ?? http, () => false),
            ResearchFixture.Options(settings), log);

    internal static LinkPreviewHandle Links(IWebPageTransport pages, Func<ResearchOptions> options, Action<string> log)
        => new(Activator.CreateInstance(LinkPreviewerType, pages, options, log)!);

    internal static LinkPreviewHandle Links(IHttpFetcher pages, Func<AppSettings> settings, Action<string> log)
        => Links(new WebPageTransport(pages, () => false), ResearchFixture.Options(settings), log);

    internal sealed record PreviewSnapshot(string Url, string? Title);

    internal sealed class LinkPreviewHandle(object instance)
    {
        internal async Task<PreviewSnapshot?> PreviewAsync(string url, CancellationToken ct)
        {
            var task = (Task)PreviewMethod.Invoke(instance, [url, ct])!;
            await task.ConfigureAwait(false);
            var result = task.GetType().GetProperty("Result")!.GetValue(task);
            if (result is null) return null;
            var type = result.GetType();
            return new PreviewSnapshot(
                (string)type.GetProperty("Url")!.GetValue(result)!,
                (string?)type.GetProperty("Title")!.GetValue(result));
        }
    }
}

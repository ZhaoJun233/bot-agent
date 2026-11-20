using System.Net;
using BotAgent.Adapters.Net;
using BotAgent.Domain.Ports;
using BotAgent.Plugins.Execution;
using BotAgent.Services;
using BotAgent.Services.Links;
using BotAgent.Services.Net;
using BotAgent.Services.Resilience;

internal static class ResearchPortChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var options = new ResearchOptions(true, false, "Synthetic|https://example.com/first?q={q}",
            1, 30, 20, 300, false, 5, 3);
        var html = "<html><title>Synthetic</title><body><a href=\"/one\">First result</a>"
            + "<a href=\"/two\">Second result</a><p>" + new string('x', 800) + "</p></body></html>";
        var http = new SyntheticHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent(html) });
        var pages = new WebPageTransport(http, () => false);
        var model = new UnusedModel();
        var search = new WebSearchService(model, pages, () => options, _ => { });
        var first = await search.SearchAsync("synthetic", default);
        options = options with { WebSearchMaxResults = 2, WebSearchSources = "Synthetic|https://example.com/second?q={q}" };
        var second = await search.SearchAsync("synthetic", default);
        check(first?.Hits.Count == 1 && second?.Hits.Count == 2 && http.Urls.Count == 2
            && http.Urls[0].AbsolutePath == "/first" && http.Urls[1].AbsolutePath == "/second",
            "narrow research options refresh source and result limit between calls");
        check(model.Calls == 0, "source-only options cause zero model calls");

        var shortPage = await search.ReadPageAsync("https://example.com/page", default);
        options = options with { WebSearchReadMaxChars = 500 };
        var longPage = await search.ReadPageAsync("https://example.com/page", default);
        check(shortPage.Text is { Length: >= 300 and <= 301 } && longPage.Text is { Length: >= 500 and <= 501 },
            "narrow research options refresh page clipping without changing its existing bound");

        var links = new LinkPreviewer(pages, () => options, _ => { });
        var calls = http.Urls.Count;
        check(await links.PreviewAsync("https://example.com/link", default) is null && http.Urls.Count == calls,
            "disabled narrow link options perform no fetch");
        options = options with { EnableLinkPreview = true };
        check((await links.PreviewAsync("https://example.com/link", default))?.Title == "Synthetic"
            && http.Urls.Count == calls + 1, "link options refresh enablement for an uncached URL");
        options = options with { EnableLinkPreview = false };
        check(await links.PreviewAsync("https://example.com/other", default) is null && http.Urls.Count == calls + 1,
            "later link disablement prevents another uncached fetch");

        var settings = new AppSettings { WebSearchReadMaxChars = 300 };
        var legacy = ResearchFactories.Search(http, () => settings, _ => { }, pageHttp: http);
        var legacyShort = await legacy.ReadPageAsync("https://example.com/page", default);
        settings = new AppSettings { WebSearchReadMaxChars = 500 };
        var legacyLong = await legacy.ReadPageAsync("https://example.com/page", default);
        check(legacyShort.Text?.Length == shortPage.Text?.Length && legacyLong.Text?.Length == longPage.Text?.Length,
            "actual host projection refreshes each current settings snapshot");

        var circuit = new DenyingExecution();
        var denied = new WebSearchService(model, pages, () => options, _ => { }, circuit);
        calls = http.Urls.Count;
        var deniedSearch = await denied.SearchAsync("synthetic", default);
        var deniedPage = await denied.ReadPageAsync("https://example.com/page", default);
        check(deniedSearch?.Error == "tool_circuit_open" && deniedPage.Error == "tool_circuit_open"
            && circuit.Calls == 2 && model.Calls == 0 && http.Urls.Count == calls,
            "public circuit port denies search and read with the original reason and zero work");

        var oldIdentity = typeof(ToolRunResult<>).FullName + ", " + typeof(ToolCircuitBreaker).Assembly.FullName;
        check(Type.GetType(oldIdentity, throwOnError: true) == typeof(ToolRunResult<>)
            && typeof(ToolRunResult<>).Assembly == typeof(IToolExecution).Assembly,
            "legacy host result identity forwards to the public execution contract");
    }

    private sealed class UnusedModel : IGroundedTextModel
    {
        internal int Calls;
        public Task<GroundedTextResult> GenerateAsync(GroundedTextRequest request, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("unexpected_synthetic_model_call");
        }
    }

    private sealed class DenyingExecution : IToolExecution
    {
        internal int Calls;
        public Task<ToolRunResult<T>> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ToolRunResult<T>(false, default, "tool_circuit_open", false));
        }
    }
}

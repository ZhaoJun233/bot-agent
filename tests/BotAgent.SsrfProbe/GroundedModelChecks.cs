using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Domain.Ports;
using BotAgent.Model;
using BotAgent.Services;
using BotAgent.Services.Net;

internal static class GroundedModelChecks
{
    private const string Answer = """
        {"candidates":[{"content":{"parts":[{"text":" First "},{"thoughtSignature":"opaque"},{"text":" "},{"text":"Second"}]},
          "groundingMetadata":{"groundingChunks":[{"web":{"title":"Example","uri":"https://example.com/a"}},
            {"web":{"title":"EXAMPLE","uri":"https://example.com/b"}},{"web":{"title":" ","uri":"https://example.com/c"}},
            {"web":{"title":"Other"}}],"webSearchQueries":["synthetic query",null," "]}}]}
        """;
    private static readonly GroundedTextRequest Request = new("synthetic prompt", 0.2, 900);

    internal static async Task Run(Action<bool, string> check)
    {
        var options = new ModelOptions { BaseUrl = "https://model.example.com/v1/", Model = "synthetic-grounded-test", ApiKey = " synthetic-first " };
        var accesses = 0;
        var payloads = new List<JsonNode>();
        var http = new SyntheticHttp(request =>
        {
            payloads.Add(JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!);
            return new(HttpStatusCode.OK) { Content = new StringContent(Answer) };
        });
        var model = new GeminiGroundedTextModel(http, () => { accesses++; return options; });
        var result = await model.GenerateAsync(Request, default);
        check(http.Urls.Single().ToString() == "https://model.example.com/v1beta/models/synthetic-grounded-test:generateContent" &&
              http.Authorization.Single() == "Bearer synthetic-first" && accesses == 1,
            "grounding derives route and credential from one current model snapshot");
        var payload = payloads.Single();
        check(payload["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>() == Request.Prompt &&
              payload["contents"]![0]!["role"]!.GetValue<string>() == "user" &&
              payload["tools"]![0]!["google_search"] is JsonObject &&
              payload["generationConfig"]!["temperature"]!.GetValue<double>() == 0.2 &&
              payload["generationConfig"]!["maxOutputTokens"]!.GetValue<int>() == 900,
            "grounding protocol preserves caller prompt, sampling and native search tool without injecting research policy");
        check(result.Text == "First\nSecond" && result.ErrorCode is null && result.Sources.Length == 4 &&
              result.Sources[1].Url == "https://example.com/b" && result.SearchQueries.SequenceEqual(new[] { "synthetic query" }),
            "model materializes text and source metadata while leaving title filtering to research");
        options = new() { BaseUrl = "https://next.example.com/gateway/v1", Model = "synthetic-grounded-next", ApiKey = "synthetic-next" };
        await model.GenerateAsync(Request, default);
        check(http.Urls.Count == 2 && accesses == 2 && http.Urls[1].Host == "next.example.com" &&
              http.Urls[1].AbsolutePath == "/gateway/v1beta/models/synthetic-grounded-next:generateContent" &&
              http.Authorization[1] == "Bearer synthetic-next", "next model call adopts current route and credential with exactly one send");

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway })
        {
            var content = new PendingContent();
            var failureHttp = new SyntheticHttp(_ => new(status) { Content = content });
            var failure = await new GeminiGroundedTextModel(failureHttp, () => options).GenerateAsync(Request, default);
            var expected = status switch { HttpStatusCode.Unauthorized => "model_auth_failed", HttpStatusCode.TooManyRequests => "model_rate_limited", _ => "model_upstream_error" };
            check(failure.ErrorCode == expected && failure.StatusCode == (int)status && failure.Text is null &&
                  failure.Sources.Length == 0 && content.Disposed && !content.Entered.Task.IsCompleted && failureHttp.Urls.Count == 1,
                "grounding failure has safe code, no retry and releases unread provider body: " + (int)status);
        }
        foreach (var malformed in new[] { "synthetic-private-response", "{\"candidates\":\"synthetic-private-response\"}" })
        {
            var failure = await new GeminiGroundedTextModel(new SyntheticHttp(_ => new(HttpStatusCode.OK)
                { Content = new StringContent(malformed) }), () => options).GenerateAsync(Request, default);
            check(failure.ErrorCode is "model_malformed_response" or "model_operation_failed" &&
                  !failure.ToString().Contains("synthetic-private-response"), "malformed grounding output never exposes provider body");
        }
        var networkFailure = await new GeminiGroundedTextModel(new SyntheticHttp(_ =>
            throw new HttpRequestException("synthetic-private-exception")), () => options).GenerateAsync(Request, default);
        check(networkFailure.ErrorCode == "model_network_error" && !networkFailure.ToString().Contains("synthetic-private-exception"),
            "grounding transport failure never exposes exception text");
        var missingHttp = new SyntheticHttp(_ => throw new InvalidOperationException("must not send"));
        var missing = await new GeminiGroundedTextModel(missingHttp, () => new() { ApiKey = "" }).GenerateAsync(Request, default);
        check(missing.ErrorCode == "model_route_unavailable" && missingHttp.Urls.Count == 0, "missing model configuration fails before network");

        var pendingContent = new PendingContent();
        using (var cts = new CancellationTokenSource())
        {
            var pending = new GeminiGroundedTextModel(new SyntheticHttp(_ => new(HttpStatusCode.OK) { Content = pendingContent }),
                () => options).GenerateAsync(Request, cts.Token);
            await pendingContent.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!pending.IsCompleted && !pendingContent.Disposed, "model call retains response ownership throughout body read");
            cts.Cancel();
            var canceled = false;
            try { await pending; } catch (OperationCanceledException) { canceled = true; }
            check(canceled && pendingContent.Disposed, "model body cancellation propagates and releases response");
        }
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            var canceled = false;
            try { await new GeminiGroundedTextModel(missingHttp, () => options).GenerateAsync(Request, cts.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && missingHttp.Urls.Count == 0, "already canceled model call never reaches transport");
        }

        var pageHttp = new SyntheticHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent(
            """{"results":[{"title":"Synthetic fallback","url":"https://example.com/result","content":"Synthetic snippet"}]}""") });
        var settings = new AppSettings { WebSearchUseModelSearch = true, WebSearchSources = "searx|https://example.com/search?q={q}" };
        var search = ResearchFactories.Search(model, new WebPageTransport(pageHttp, () => false), ResearchFixture.Options(() => settings), _ => { });
        var answer = await search.SearchAsync("synthetic question", default);
        check(answer?.Answer == "First\nSecond" && answer.Hits.Select(hit => hit.Title).SequenceEqual(new[] { "Example", "Other" }) &&
              answer.Hits.All(hit => hit.Url.Length == 0 && hit.Snippet.Length == 0) && pageHttp.Urls.Count == 0,
            "research retains case-insensitive title filtering and returns grounding without source fallback");
        check(payloads.Last()["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>().EndsWith("问题：synthetic question"),
            "research owns the original search instruction");
        var failedModelHttp = new SyntheticHttp(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("synthetic-secret-body") });
        var logs = new List<string>();
        var fallback = await ResearchFactories.Search(new GeminiGroundedTextModel(failedModelHttp, () => options),
            new WebPageTransport(pageHttp, () => false), ResearchFixture.Options(() => settings), logs.Add).SearchAsync("synthetic question", default);
        check(fallback?.Hits.Single().Title == "Synthetic fallback" && failedModelHttp.Urls.Count == 1 && pageHttp.Urls.Count == 1 &&
              pageHttp.Authorization.All(value => value is null) && logs.All(line => !line.Contains("synthetic-secret-body")),
            "failed model falls back once to public source without credential or failure-body leakage");
    }

    private sealed class PendingContent : HttpContent
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, default);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

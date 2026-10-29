using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;
using BotAgent.Model;

internal static class DiscoveryChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var protocol in Enum.GetValues<ModelProtocol>())
        {
            var reply = protocol switch
            {
                ModelProtocol.GeminiGenerateContent => """{"models":[{"name":"models/example-z","supportedGenerationMethods":["generateContent"]},{"name":"models/embedding-only","supportedGenerationMethods":["embedContent"]},{"name":"models/example-a"}]}""",
                ModelProtocol.Ollama => """{"models":[{"name":"example-z:latest"},{"model":"example-a"}]}""",
                _ => """{"data":[{"id":"example-z"},{"id":"example-a"},{"id":"example-z"}]}"""
            };
            var route = Route(protocol);
            Uri? observed = null;
            var authenticated = false;
            var http = new FakeHttp((request, _) =>
            {
                observed = request.RequestUri;
                authenticated = protocol switch
                {
                    ModelProtocol.AnthropicMessages => Header(request, "x-api-key") == "synthetic-key" && Header(request, "anthropic-version") == "2023-06-01" && request.Headers.Authorization is null,
                    ModelProtocol.GeminiGenerateContent => Header(request, "x-goog-api-key") == "synthetic-key" && request.Headers.Authorization is null,
                    ModelProtocol.Ollama => request.Headers.Authorization is null,
                    _ => request.Headers.Authorization?.ToString() == "Bearer synthetic-key"
                };
                check(request.Method == HttpMethod.Get && request.Content is null, protocol + " discovery GET sends no prompt or model payload");
                return Task.FromResult(Response(reply));
            });
            var result = await new ProviderModelDiscovery(http).ListAsync(route);
            if (protocol == ModelProtocol.AzureOpenAi)
            {
                check(result.ErrorCode == "model_discovery_unsupported" && result.Models.Count == 0 && http.Calls == 0, "Azure catalog explicit manual fallback without network");
                continue;
            }
            check(result.ErrorCode is null && result.Models.Count == 2 && result.Models[0] == "example-a", protocol + " model ids normalized sorted deduplicated");
            check(authenticated && http.Calls == 1, protocol + " discovery uses native auth and single bounded request");
            check(observed?.Host == "example.com" && observed.Query.Contains("tenant=synthetic") && observed.AbsolutePath == (protocol == ModelProtocol.Ollama ? "/api/tags" : "/v1/models"), protocol + " native list path keeps authority and query");
        }
        foreach (var (protocol, endpoint, expected) in new[]
        {
            (ModelProtocol.OpenAiChat, "/v1/chat/completions/", "/v1/models"),
            (ModelProtocol.OpenAiResponses, "/v1/responses", "/v1/models"),
            (ModelProtocol.AnthropicMessages, "/v1/messages", "/v1/models"),
            (ModelProtocol.GeminiGenerateContent, "/v1/models/example-model:streamGenerateContent", "/v1/models"),
            (ModelProtocol.Ollama, "/api/chat", "/api/tags"),
            (ModelProtocol.Ollama, "/api/tags", "/api/tags"),
            (ModelProtocol.Ollama, "", "/api/tags")
        })
        {
            var http = new FakeHttp((request, _) =>
            {
                check(request.RequestUri!.AbsolutePath == expected, protocol + " full endpoint discovery normalization");
                return Task.FromResult(Response(protocol is ModelProtocol.Ollama or ModelProtocol.GeminiGenerateContent ? """{"models":[]}""" : """{"data":[]}"""));
            });
            check((await new ProviderModelDiscovery(http).ListAsync(Route(protocol) with { BaseUrl = "https://example.com" + endpoint })).ErrorCode is null, protocol + " empty catalog succeeds");
        }
        foreach (var protocol in new[] { ModelProtocol.AnthropicMessages, ModelProtocol.GeminiGenerateContent })
        {
            var calls = 0;
            var http = new FakeHttp((request, _) =>
            {
                calls++;
                if (calls == 2) check(request.RequestUri!.Query.EndsWith(Uri.EscapeDataString(protocol == ModelProtocol.AnthropicMessages ? "example-a" : "cursor/+=")), protocol + " native cursor is encoded not followed as a URL");
                return Task.FromResult(Response(protocol == ModelProtocol.AnthropicMessages
                    ? calls == 1 ? """{"data":[{"id":"example-a"}],"has_more":true,"last_id":"example-a"}""" : """{"data":[{"id":"example-b"}],"has_more":false}"""
                    : calls == 1 ? """{"models":[{"name":"models/example-a"}],"nextPageToken":"cursor/+="}""" : """{"models":[{"name":"models/example-b"}]}"""));
            });
            var listed = await new ProviderModelDiscovery(http).ListAsync(Route(protocol));
            check(listed.ErrorCode is null && listed.Models.Count == 2 && calls == 2, protocol + " bounded native pagination");
        }
        var badIds = new JsonArray(new JsonObject { ["id"] = "example/valid:latest" }, new JsonObject { ["id"] = "<script>synthetic</script>" }, new JsonObject { ["id"] = "synthetic private text" }, new JsonObject { ["id"] = "synthetic\nvalue" }, new JsonObject { ["id"] = new string('x', 257) });
        var filtered = new FakeHttp((_, _) => Task.FromResult(Response(new JsonObject { ["data"] = badIds }.ToJsonString())));
        var safe = await new ProviderModelDiscovery(filtered).ListAsync(Route(ModelProtocol.OpenAiChat));
        check(safe.ErrorCode is null && safe.Models.SequenceEqual(new[] { "example/valid:latest" }), "catalog reflects only bounded safe identifiers, never descriptions");
        foreach (var (body, error) in new[]
        {
            ("[]", "model_malformed_response"), ("{", "model_malformed_response"), ("""{"data":{}}""", "model_malformed_response"),
            ("""{"data":[42]}""", "model_malformed_response"), ("""{"error":{"message":"synthetic private detail"}}""", "model_upstream_error")
        })
        {
            var http = new FakeHttp((_, _) => Task.FromResult(Response(body)));
            var result = await new ProviderModelDiscovery(http).ListAsync(Route(ModelProtocol.OpenAiChat));
            check(result.ErrorCode == error && result.Models.Count == 0 && http.Calls == 1, "malformed discovery safe error without retry");
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            var http = new FakeHttp((_, _) => Task.FromResult(Response("synthetic private detail", status)));
            var result = await new ProviderModelDiscovery(http).ListAsync(Route(ModelProtocol.OpenAiResponses));
            check(result.ErrorCode == (status == HttpStatusCode.Unauthorized ? "model_auth_failed" : status == HttpStatusCode.TooManyRequests ? "model_rate_limited" : "model_upstream_error") && result.Models.Count == 0 && http.Calls == 1, "discovery status sanitized without retry");
        }
        foreach (var unknownLength in new[] { false, true })
        {
            var disposed = false;
            var http = new FakeHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new TrackedContent(new string(' ', 256 * 1024 + 1), unknownLength, () => disposed = true) }));
            var result = await new ProviderModelDiscovery(http).ListAsync(Route(ModelProtocol.OpenAiChat));
            check(result.ErrorCode == "model_response_too_large" && disposed, "discovery caps bytes and disposes " + (unknownLength ? "chunked" : "declared") + " response");
        }
        var ids = new JsonArray(Enumerable.Range(0, 501).Select(i => (JsonNode)new JsonObject { ["id"] = "example-" + i }).ToArray());
        var tooMany = new FakeHttp((_, _) => Task.FromResult(Response(new JsonObject { ["data"] = ids }.ToJsonString())));
        check((await new ProviderModelDiscovery(tooMany).ListAsync(Route(ModelProtocol.OpenAiChat))).ErrorCode == "model_discovery_limit", "model count cap rejects incomplete discovery");
        var repeating = new FakeHttp((_, _) => Task.FromResult(Response("""{"models":[],"nextPageToken":"same"}""")));
        check((await new ProviderModelDiscovery(repeating).ListAsync(Route(ModelProtocol.GeminiGenerateContent))).ErrorCode == "model_discovery_limit" && repeating.Calls == 2, "repeated cursor rejected before unbounded requests");
        var pages = 0;
        var paged = new FakeHttp((_, _) => Task.FromResult(Response(new JsonObject { ["models"] = new JsonArray(), ["nextPageToken"] = "cursor-" + ++pages }.ToJsonString())));
        check((await new ProviderModelDiscovery(paged).ListAsync(Route(ModelProtocol.GeminiGenerateContent))).ErrorCode == "model_discovery_limit" && paged.Calls == 5, "pagination capped at five requests");
        foreach (var invalid in new[] { "https://synthetic@example.com/v1", "https://example.com/v1#synthetic", "file:///synthetic" })
        {
            var http = new FakeHttp((_, _) => Task.FromResult(Response("{}")));
            check((await new ProviderModelDiscovery(http).ListAsync(Route(ModelProtocol.OpenAiChat) with { BaseUrl = invalid })).ErrorCode == "model_route_unavailable" && http.Calls == 0, "discovery invalid route fails without network");
        }
        var missingKey = new FakeHttp((_, _) => Task.FromResult(Response("{}")));
        check((await new ProviderModelDiscovery(missingKey).ListAsync(Route(ModelProtocol.OpenAiChat) with { ApiKey = "" })).ErrorCode == "model_credential_missing" && missingKey.Calls == 0, "discovery required credential absent fails closed");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await new ProviderModelDiscovery(missingKey).ListAsync(Route(ModelProtocol.OpenAiChat), cancelled.Token); check(false, "discovery cancelled"); }
        catch (OperationCanceledException) { check(missingKey.Calls == 0, "discovery precancelled caller skips network"); }
    }

    private static ModelProviderRoute Route(ModelProtocol protocol) => new("synthetic", "https://example.com" + (protocol == ModelProtocol.Ollama ? "/api" : "/v1") + "?tenant=synthetic", "", protocol == ModelProtocol.Ollama ? "" : "synthetic-key", protocol);
    private static string? Header(HttpRequestMessage request, string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    private sealed class TrackedContent(string value, bool unknownLength, Action disposed) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();
        protected override bool TryComputeLength(out long length) { length = Encoding.UTF8.GetByteCount(value); return !unknownLength; }
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }
}

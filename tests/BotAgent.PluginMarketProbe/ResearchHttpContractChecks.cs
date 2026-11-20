using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Net;

internal partial class Program
{
    internal static async Task<int> RunResearchHttpContractChecks()
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        var description = ResearchHttpContract.Describe();
        var route = description.Routes.Single();
        Check(typeof(ResearchHttpContract).Assembly == typeof(IResearchDiagnostics).Assembly
            && description.Owner == "preset.feature.websearch" && route.Method == "POST"
            && route.Path == "/api/search/test" && route.Admin && route.OperationId == ResearchHttpContract.Operation,
            "research independently owns the authenticated legacy diagnostic declaration");
        var schemas = JsonNode.Parse(description.SchemasJson)!;
        Check(schemas[route.RequestSchema!] is JsonObject && schemas[route.ResponseSchema] is JsonObject
            && schemas["ResearchTestResult"]!["oneOf"]!.AsArray().Select(n => n!["$ref"]!.GetValue<string>())
                .SequenceEqual(["#/components/schemas/ResearchSearchResult", "#/components/schemas/ResearchReadResult"]),
            "both diagnostic modes are described by resolvable response schemas");
        Check(schemas["ResearchTestRequest"]!["anyOf"]!.AsArray().All(n =>
            n!["properties"]!.AsObject().Single().Value!["pattern"]!.GetValue<string>() == @"\S"),
            "request schema requires a query or URL containing a non-whitespace character");
        foreach (var field in new[] { "query", "url" })
            Check(schemas["ResearchTestRequest"]!["properties"]![field]!["type"]!.AsArray()
                .Select(n => n!.GetValue<string>()).SequenceEqual(["string", "null"]),
                field + " retains nullable string input semantics");
        schemas["ResearchTestRequest"]!["type"] = "synthetic-mutated";
        Check(JsonNode.Parse(ResearchHttpContract.Describe().SchemasJson)!["ResearchTestRequest"]!["type"]!.GetValue<string>() == "object",
            "a consumer cannot mutate a subsequent contract description");

        var diagnostic = new ResearchHttpDiagnostic();
        var resolutions = 0;
        var binding = ResearchHttpContract.Bind<string>(caller =>
        {
            Check(caller == "synthetic:caller", "the binding retains the caller supplied by authenticated transport");
            resolutions++;
            return diagnostic;
        });
        var unread = new ResearchHttpJsonBody("{}");
        try
        {
            await binding.Invoke("synthetic.unknown", "synthetic:caller", unread, default);
            throw new InvalidOperationException("unknown_research_operation_accepted");
        }
        catch (PluginHttpException error)
        {
            Check(error.Status == 404 && error.Code == "not_found" && unread.Reads == 0 && resolutions == 0,
                "unknown operation is rejected before body access or diagnostic resolution");
        }
        foreach (var input in new[] { "{}", "null", "{\"query\":null,\"url\":null}", "{\"query\":\"  \",\"url\":\" \\t \"}" })
            await Reject(input, 400, "请填 query（搜索词）或 url（要读的页面）");
        Check(resolutions == 0 && diagnostic.SearchCalls == 0 && diagnostic.ReadCalls == 0,
            "missing arguments cannot reach the diagnostic provider");
        foreach (var input in new[] { "{\"query\":123}", "[]", "\"synthetic\"",
            "{\"query\":123,\"url\":\"https://example.com/\"}" })
        {
            try
            {
                await Invoke(input);
                throw new Exception("invalid_legacy_shape_accepted");
            }
            catch (InvalidOperationException)
            {
                Check(resolutions == 0, "legacy type errors remain outside diagnostic error translation");
            }
        }

        diagnostic.Page = ("synthetic page", null);
        var read = await Invoke("{\"query\":\"unused\",\"url\":\"  https://example.com/page  \",\"unknown\":true}");
        Check(read["ok"]!.GetValue<bool>() && read["mode"]!.GetValue<string>() == "read"
            && read["text"]!.GetValue<string>() == "synthetic page" && read.ContainsKey("error") && read["error"] is null
            && diagnostic.ReadCalls == 1 && diagnostic.SearchCalls == 0 && diagnostic.LastInput == "https://example.com/page",
            "URL priority trims input, ignores extra fields and performs exactly one page call");
        Check(RequiredFields("ResearchReadResult").SetEquals(read.Select(p => p.Key)),
            "page response fields agree with the published schema including explicit null error");
        diagnostic.Page = (null, "synthetic_read_error");
        read = await Invoke("{\"url\":\"https://example.com/\"}");
        Check(!read["ok"]!.GetValue<bool>() && read.ContainsKey("text") && read["text"] is null
            && read["error"]!.GetValue<string>() == "synthetic_read_error",
            "provider read failure remains a normal result with explicit null text");

        diagnostic.Result = new WebSearchResult("synthetic query", null,
            [new WebSearchHit("synthetic title", "https://example.com/hit", "synthetic snippet")], "synthetic-provider");
        var search = await Invoke("{\"query\":\"  synthetic query  \",\"url\":null}");
        Check(search["ok"]!.GetValue<bool>() && search["mode"]!.GetValue<string>() == "search"
            && search["provider"]!.GetValue<string>() == diagnostic.Result.Provider
            && search.ContainsKey("answer") && search["answer"] is null && search.ContainsKey("error") && search["error"] is null
            && search["note"]!.GetValue<string>() == diagnostic.Result.Describe() && diagnostic.SearchCalls == 1
            && diagnostic.LastInput == "synthetic query", "search keeps trimmed input, provider, nullable fields and the original description");
        var hit = search["hits"]![0]!.AsObject();
        Check(hit["title"]!.GetValue<string>() == "synthetic title" && hit["url"]!.GetValue<string>() == "https://example.com/hit"
            && hit["snippet"]!.GetValue<string>() == "synthetic snippet"
            && RequiredFields("ResearchHit").SetEquals(hit.Select(p => p.Key))
            && RequiredFields("ResearchSearchResult").SetEquals(search.Select(p => p.Key)),
            "search and hit wire fields agree with their independent schemas");
        diagnostic.Result = new WebSearchResult("synthetic empty", null, [], "synthetic-provider", "synthetic_search_error");
        search = await Invoke("{\"query\":\"synthetic empty\"}");
        Check(!search["ok"]!.GetValue<bool>() && search["hits"]!.AsArray().Count == 0
            && search["error"]!.GetValue<string>() == "synthetic_search_error"
            && search["note"]!.GetValue<string>() == diagnostic.Result.Describe(),
            "empty provider results retain failure information and original no-content description");

        diagnostic.Failure = new InvalidOperationException("synthetic_failure");
        await Reject("{\"query\":\"synthetic exception\"}", 500, "synthetic_failure");
        diagnostic.Failure = null;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await Invoke("{\"query\":\"synthetic cancel\"}", canceled.Token);
            throw new InvalidOperationException("canceled_diagnostic_accepted");
        }
        catch (PluginHttpException error)
        {
            Check(error.Status == 500 && error.Code == new OperationCanceledException(canceled.Token).Message
                && diagnostic.LastToken == canceled.Token, "cancellation reaches the provider and retains legacy diagnostic error translation");
        }
        var unavailable = ResearchHttpContract.Bind<string>(_ => throw new InvalidOperationException("synthetic_unavailable"));
        try
        {
            await unavailable.Invoke(ResearchHttpContract.Operation, "synthetic:caller", new ResearchHttpJsonBody("{\"query\":\"synthetic\"}"), default);
            throw new InvalidOperationException("unavailable_diagnostic_accepted");
        }
        catch (PluginHttpException error) { Check(error.Status == 500 && error.Code == "synthetic_unavailable", "resolver failures retain diagnostic error shape"); }
        return checks;

        HashSet<string> RequiredFields(string name) => JsonNode.Parse(description.SchemasJson)![name]!["required"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        async Task<JsonObject> Invoke(string json, CancellationToken ct = default)
            => (JsonObject)await binding.Invoke(ResearchHttpContract.Operation, "synthetic:caller", new ResearchHttpJsonBody(json), ct);
        async Task Reject(string json, int status, string code)
        {
            try { await Invoke(json); }
            catch (PluginHttpException error) { Check(error.Status == status && error.Code == code, "diagnostic rejection preserves status and message"); return; }
            throw new InvalidOperationException("expected_diagnostic_rejection");
        }
    }

    private sealed class ResearchHttpJsonBody(string json) : PluginHttpBody
    {
        internal int Reads { get; private set; }
        public override T Read<T>() { Reads++; return JsonSerializer.Deserialize<T>(json)!; }
    }

    private sealed class ResearchHttpDiagnostic : IResearchDiagnostics
    {
        internal int SearchCalls { get; private set; }
        internal int ReadCalls { get; private set; }
        internal string? LastInput { get; private set; }
        internal CancellationToken LastToken { get; private set; }
        internal Exception? Failure { get; set; }
        internal WebSearchResult Result { get; set; } = new("synthetic", null, [], "synthetic-provider");
        internal (string? Text, string? Error) Page { get; set; }
        public Task<WebSearchResult> TestSearchAsync(string query, CancellationToken ct)
        { SearchCalls++; Enter(query, ct); return Task.FromResult(Result); }
        public Task<(string? Text, string? Error)> TestReadPageAsync(string url, CancellationToken ct)
        { ReadCalls++; Enter(url, ct); return Task.FromResult(Page); }
        private void Enter(string value, CancellationToken ct)
        { LastInput = value; LastToken = ct; ct.ThrowIfCancellationRequested(); if (Failure is not null) throw Failure; }
    }
}

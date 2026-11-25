using System.Text.Json.Nodes;
using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Net;

/// <summary>Research-owned diagnostic wire contract. Authentication and JSON transport stay with the host.</summary>
public static class ResearchHttpContract
{
    public const string Operation = "research.test";
    public const string InvalidJson = "请求体不是合法 JSON";

    public static PluginHttpDescription Describe() => new("preset.feature.websearch",
        Array.AsReadOnly(new PluginHttpRoute[]
        {
            new("POST", "/api/search/test", true, Operation, "Test search or page reading without sending a message",
                "ResearchTestRequest", "ResearchTestResult")
        }), Schemas().ToJsonString());

    public static PluginHttpBinding<TCaller> Bind<TCaller>(Func<TCaller, IResearchDiagnostics> resolve)
        => new(Describe(), async (operation, caller, body, ct) =>
        {
            if (operation != Operation) throw new PluginHttpException(404, "not_found");
            var request = body.Read<JsonNode>();
            // Preserve the legacy JSON shape semantics, including its global error path for non-string values.
            var query = request?["query"]?.GetValue<string>()?.Trim();
            var url = request?["url"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(url))
                throw new PluginHttpException(400, "请填 query（搜索词）或 url（要读的页面）");
            try
            {
                var diagnostics = resolve(caller);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var (text, error) = await diagnostics.TestReadPageAsync(url, ct).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["ok"] = text is not null, ["mode"] = "read", ["text"] = text, ["error"] = error
                    };
                }
                var result = await diagnostics.TestSearchAsync(query!, ct).ConfigureAwait(false);
                return new JsonObject
                {
                    ["ok"] = result.HasContent,
                    ["mode"] = "search",
                    ["provider"] = result.Provider,
                    ["answer"] = result.Answer,
                    ["hits"] = new JsonArray(result.Hits.Select(hit => (JsonNode)new JsonObject
                    {
                        ["title"] = hit.Title, ["url"] = hit.Url, ["snippet"] = hit.Snippet
                    }).ToArray()),
                    ["note"] = result.Describe(),
                    ["error"] = result.Error
                };
            }
            catch (Exception error) { throw new PluginHttpException(500, error.Message); }
        });

    private static JsonObject Schemas() => new()
    {
        ["ResearchTestRequest"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Provide a non-whitespace query or URL. Values are trimmed; URL takes precedence. Unknown properties are ignored.",
            ["properties"] = new JsonObject { ["query"] = NullableString(), ["url"] = NullableString() },
            ["anyOf"] = new JsonArray(NonEmpty("query"), NonEmpty("url"))
        },
        ["ResearchTestResult"] = new JsonObject
        {
            ["oneOf"] = new JsonArray(Reference("ResearchSearchResult"), Reference("ResearchReadResult"))
        },
        ["ResearchSearchResult"] = RequiredObject(("ok", Boolean()), ("mode", Mode("search")),
            ("provider", String()), ("answer", NullableString()),
            ("hits", new JsonObject { ["type"] = "array", ["items"] = Reference("ResearchHit") }),
            ("note", String()), ("error", NullableString())),
        ["ResearchReadResult"] = RequiredObject(("ok", Boolean()), ("mode", Mode("read")),
            ("text", NullableString()), ("error", NullableString())),
        ["ResearchHit"] = RequiredObject(("title", String()), ("url", String()), ("snippet", String())),
        ["ResearchTestError"] = RequiredObject(("error", String()))
    };

    private static JsonObject NonEmpty(string name) => new()
    {
        ["required"] = new JsonArray(name),
        ["properties"] = new JsonObject { [name] = new JsonObject { ["type"] = "string", ["pattern"] = @"\S" } }
    };
    private static JsonObject RequiredObject(params (string Name, JsonObject Schema)[] properties)
    {
        var fields = new JsonObject();
        foreach (var (name, schema) in properties) fields[name] = schema;
        return new JsonObject { ["type"] = "object", ["properties"] = fields,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()) };
    }
    private static JsonObject Reference(string name) => new() { ["$ref"] = "#/components/schemas/" + name };
    private static JsonObject String() => new() { ["type"] = "string" };
    private static JsonObject NullableString() => new() { ["type"] = new JsonArray("string", "null") };
    private static JsonObject Boolean() => new() { ["type"] = "boolean" };
    private static JsonObject Mode(string value) => new() { ["type"] = "string", ["enum"] = new JsonArray(value) };
}

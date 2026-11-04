using System.Reflection;
using System.Text.Json;
using BotAgent.Panel;

namespace BotAgent.ZbaProbe;

internal static class RuntimeLogApiChecks
{
    private const string ReadToken = "synthetic-log-read";
    private const string AdminToken = "synthetic-log-admin";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var backend = DispatchProxy.Create<IZbaBackend, LogBackendProxy>();
        var probe = (LogBackendProxy)(object)backend;
        var endpoint = Endpoint(backend);
        foreach (var token in new string?[] { null, "synthetic-invalid" })
        {
            var response = await endpoint.ProcessAsync(Request(token));
            check(response.Status == 401, "logs requires a valid bearer token");
            NoStore(response, check);
        }
        check(probe.Calls == 0, "unauthenticated logs never calls backend");

        var page = await endpoint.ProcessAsync(Request(ReadToken));
        check(page.Status == 200 && probe.LastLimit == 200, "read token can read logs with default limit 200");
        NoStore(page, check);
        var json = Json(page);
        check(json.GetProperty("capacity").GetInt32() == 600, "logs reports fixed capacity 600");
        var items = json.GetProperty("items");
        check(items.GetArrayLength() == 2, "logs response contains items");
        var first = items[0];
        check(first.GetProperty("time").GetInt64() == 2000
            && first.GetProperty("level").GetString() == "warn"
            && first.GetProperty("source").GetString() == "model"
            && first.GetProperty("message").GetString() == "模型返回空回复"
            && !first.GetProperty("redacted").GetBoolean(), "log entry uses frozen JSON field names and types");
        check(items[1].GetProperty("redacted").GetBoolean(), "redaction metadata survives serialization");

        foreach (var (query, expected) in new[] { ("limit=1", 1), ("limit=600", 600) })
        {
            var response = await endpoint.ProcessAsync(Request(ReadToken, query));
            check(response.Status == 200 && probe.LastLimit == expected, "logs accepts boundary " + query);
            NoStore(response, check);
        }
        var admin = await endpoint.ProcessAsync(Request(AdminToken, "limit=7"));
        check(admin.Status == 200 && probe.LastLimit == 7, "admin scope includes log read access");

        var calls = probe.Calls;
        foreach (var query in new[] { "limit=", "limit=0", "limit=601", "limit=-1", "limit=abc", "limit=1.5",
            "limit=%20", "limit=%202", "limit=2%20", "limit=%2B2", "limit=2147483648" })
        {
            var response = await endpoint.ProcessAsync(Request(ReadToken, query));
            check(response.Status == 400 && Code(response) == "invalid_limit", "logs rejects malformed " + query);
            NoStore(response, check);
        }
        var duplicate = await endpoint.ProcessAsync(Request(ReadToken, "limit=1&limit=2"));
        check(duplicate.Status == 400, "logs rejects duplicate limit parameters");
        var unknown = await endpoint.ProcessAsync(Request(ReadToken, "offset=1"));
        check(unknown.Status == 400 && Code(unknown) == "unknown_query_parameter", "logs rejects unknown query parameters");
        check(probe.Calls == calls, "invalid log queries never reach backend");
        NoStore(unknown, check);

        // This existing backend deliberately does not implement the optional log API.
        var unavailable = await Endpoint(new SyntheticZbaBackend()).ProcessAsync(Request(ReadToken));
        check(unavailable.Status == 503 && Code(unavailable) == "logs_unavailable", "default backend fails explicitly when logs unavailable");
        NoStore(unavailable, check);

        var route = endpoint.Routes.SingleOrDefault(route => route.Template == "/zba/v1/logs" && route.Method == "GET");
        check(route is not null && route.Scope == ZbaScope.Read && !route.Public
            && route.OperationId == "logs.list" && route.ResponseSchema == "ZbaLogPage", "log route is authenticated read-only and discoverable");
        var openapi = Json(await endpoint.ProcessAsync(new ZbaRequest("GET", "/zba/v1/openapi.json", "",
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + ReadToken }, [], "203.0.113.10")));
        var operation = openapi.GetProperty("paths").GetProperty("/zba/v1/logs").GetProperty("get");
        check(operation.GetProperty("operationId").GetString() == "logs.list", "OpenAPI exposes log operation ID");
        check(operation.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            == "#/components/schemas/ZbaLogPage", "OpenAPI log success response references page schema");
        var schemas = openapi.GetProperty("components").GetProperty("schemas");
        var pageSchema = schemas.GetProperty("ZbaLogPage").GetProperty("properties");
        check(pageSchema.GetProperty("items").GetProperty("items").GetProperty("$ref").GetString()
            == "#/components/schemas/ZbaLogEntry" && pageSchema.GetProperty("capacity").GetProperty("type").GetString() == "integer",
            "OpenAPI page links log entry schema and integer capacity");
        var entrySchema = schemas.GetProperty("ZbaLogEntry").GetProperty("properties");
        foreach (var (name, type) in new[] { ("time", "integer"), ("level", "string"), ("source", "string"),
            ("message", "string"), ("redacted", "boolean") })
            check(entrySchema.GetProperty(name).GetProperty("type").GetString() == type, "OpenAPI log entry field " + name);
    }

    private static ZbaEndpoint Endpoint(IZbaBackend backend) => new(backend,
        new ZbaOptions { ReadToken = ReadToken, AdminToken = AdminToken },
        () => DateTimeOffset.UnixEpoch, "synthetic", "synthetic-logs");
    private static ZbaRequest Request(string? token, string query = "")
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token is not null) headers["Authorization"] = "Bearer " + token;
        return new("GET", "/zba/v1/logs", query, headers, [], "203.0.113.10");
    }
    private static JsonElement Json(ZbaResponse response) => JsonDocument.Parse(response.Body).RootElement.Clone();
    private static string? Code(ZbaResponse response) => Json(response).GetProperty("error").GetProperty("code").GetString();
    private static void NoStore(ZbaResponse response, Action<bool, string> check)
        => check(response.Headers.TryGetValue("Cache-Control", out var value) && value == "no-store", "log responses never cache");

    public class LogBackendProxy : DispatchProxy
    {
        public int Calls { get; private set; }
        public int LastLimit { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name != nameof(IZbaBackend.ListLogsAsync)) throw new InvalidOperationException("Unexpected synthetic backend operation");
            Calls++;
            LastLimit = (int)arguments![1]!;
            var entries = new[] {
                new ZbaLogEntry(2000, "warn", "model", "模型返回空回复", false),
                new ZbaLogEntry(1000, "info", "runtime", "已隐藏的日志内容（8 字符）", true)
            };
            return ValueTask.FromResult(new ZbaLogPage(entries.Take(LastLimit).ToArray(), 600));
        }
    }
}

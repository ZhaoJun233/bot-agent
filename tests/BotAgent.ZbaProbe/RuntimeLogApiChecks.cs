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
        await CheckFeedAsync(check);
    }

    private static async Task CheckFeedAsync(Action<bool, string> check)
    {
        var backend = DispatchProxy.Create<IZbaBackend, LogBackendProxy>();
        var probe = (LogBackendProxy)(object)backend;
        var endpoint = Endpoint(backend);
        foreach (var token in new string?[] { null, "synthetic-invalid" })
        {
            var unauthorized = await endpoint.ProcessAsync(Request(token, feed: true));
            check(unauthorized.Status == 401, "feed requires valid bearer authorization on each poll");
            NoStore(unauthorized, check);
        }
        check(probe.FeedCalls == 0, "unauthenticated feed never reaches backend");
        var meta = Json(await endpoint.ProcessAsync(Request(ReadToken) with { Path = "/zba/v1/meta" }));
        check(meta.GetProperty("features").EnumerateArray().Any(value => value.GetString() == "logs.feed"),
            "meta advertises the incremental log feed operation");
        var response = await endpoint.ProcessAsync(Request(ReadToken, feed: true));
        var page = Json(response);
        check(response.Status == 200 && probe.LastCursor is null, "feed initial request omits cursor");
        NoStore(response, check);
        var item = page.GetProperty("items")[0];
        check(item.GetProperty("id").GetString() == LogBackendProxy.FeedEpoch.ToString("N") + ":1"
            && item.GetProperty("sequence").GetString() == "1" && item.GetProperty("time").GetInt64() == 1000
            && item.GetProperty("level").GetString() == "info" && item.GetProperty("source").GetString() == "runtime"
            && item.GetProperty("message").GetString() == "已隐藏的日志内容（8 字符）" && item.GetProperty("redacted").GetBoolean(),
            "feed freezes safe JSON fields with string sequence and stable ID");
        check(page.GetProperty("capacity").GetInt32() == 600 && !page.GetProperty("hasMore").GetBoolean()
            && !page.TryGetProperty("resetReason", out _) && !page.TryGetProperty("omittedCount", out _),
            "normal feed omits reset metadata rather than guessing loss");
        var cursorText = page.GetProperty("cursor").GetString()!;
        var delta = await endpoint.ProcessAsync(Request(AdminToken, "cursor=" + Uri.EscapeDataString(cursorText), feed: true));
        check(delta.Status == 200 && probe.LastCursor?.Sequence == 1, "admin can poll feed using unchanged cursor");
        NoStore(delta, check);
        var revoked = await endpoint.ProcessAsync(Request(null, "cursor=" + cursorText, feed: true));
        check(revoked.Status == 401 && probe.FeedCalls == 2, "a previous authorized poll does not authorize later requests");
        NoStore(revoked, check);

        var invalidQueries = new[] { "cursor=", "cursor=invalid", "cursor=" + cursorText + "&cursor=" + cursorText,
            "cursor=" + cursorText[..36] + "01", "cursor=" + cursorText[..36] + "-1",
            "cursor=" + cursorText[..36] + "9223372036854775808", "cursor=" + new string('x', 56),
            "cursor=" + Uri.EscapeDataString(cursorText + " "), "limit=200", "offset=0" };
        var before = probe.FeedCalls;
        foreach (var query in invalidQueries)
        {
            var invalid = await endpoint.ProcessAsync(Request(ReadToken, query, feed: true));
            check(invalid.Status == 400, "feed rejects malformed, duplicate or unsupported query");
            NoStore(invalid, check);
        }
        check(probe.FeedCalls == before, "invalid cursor cannot call backend");
        var malformed = await endpoint.ProcessAsync(Request(ReadToken, "cursor=invalid", feed: true));
        check(Code(malformed) == "invalid_log_cursor", "malformed cursor has stable safe error code");
        var unavailable = await Endpoint(new SyntheticZbaBackend()).ProcessAsync(Request(ReadToken, feed: true));
        check(unavailable.Status == 503 && Code(unavailable) == "logs_unavailable", "optional feed defaults to explicit unavailable");
        NoStore(unavailable, check);

        probe.FeedResponse = new ZbaLogFeedPage([new(LogBackendProxy.FeedEpoch.ToString("N") + ":451", "451", 1000,
            "info", "runtime", "已隐藏的日志内容（8 字符）", true)],
            new ZbaLogFeedCursor(LogBackendProxy.FeedEpoch, 451).ToString(), false, 600, "expired", 449);
        var reset = Json(await endpoint.ProcessAsync(Request(ReadToken, "cursor=" + cursorText, feed: true)));
        check(reset.GetProperty("resetReason").GetString() == "expired" && reset.GetProperty("omittedCount").GetInt64() == 449
            && !reset.GetProperty("hasMore").GetBoolean(), "feed preserves explicit reset and exact omission metadata");
        var route = endpoint.Routes.SingleOrDefault(route => route.Template == "/zba/v1/logs/feed" && route.Method == "GET");
        check(route is not null && route.Scope == ZbaScope.Read && !route.Public && route.OperationId == "logs.feed"
            && route.ResponseSchema == "ZbaLogFeedPage", "feed route is additive authenticated read-only JSON");
        var openapi = Json(await endpoint.ProcessAsync(new ZbaRequest("GET", "/zba/v1/openapi.json", "",
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + ReadToken }, [], "synthetic-client")));
        var operation = openapi.GetProperty("paths").GetProperty("/zba/v1/logs/feed").GetProperty("get");
        check(operation.GetProperty("operationId").GetString() == "logs.feed", "OpenAPI exposes separate feed operation");
        var parameter = operation.GetProperty("parameters").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "cursor");
        check(!parameter.GetProperty("required").GetBoolean() && parameter.GetProperty("schema").GetProperty("maxLength").GetInt32() == 55,
            "OpenAPI declares optional bounded cursor and fixed server pagination");
        var schema = openapi.GetProperty("components").GetProperty("schemas").GetProperty("ZbaLogFeedPage");
        check(schema.GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32() == 200
            && schema.GetProperty("properties").GetProperty("capacity").GetProperty("const").GetInt32() == 600,
            "OpenAPI records page and ring bounds");
        var entry = openapi.GetProperty("components").GetProperty("schemas").GetProperty("ZbaLogFeedEntry");
        check(entry.GetProperty("properties").GetProperty("sequence").GetProperty("type").GetString() == "string"
            && entry.GetProperty("properties").GetProperty("message").GetProperty("maxLength").GetInt32() == 512,
            "OpenAPI preserves precise sequence strings and safe projection bounds");
    }

    private static ZbaEndpoint Endpoint(IZbaBackend backend) => new(backend,
        new ZbaOptions { ReadToken = ReadToken, AdminToken = AdminToken },
        () => DateTimeOffset.UnixEpoch, "synthetic", "synthetic-logs");
    private static ZbaRequest Request(string? token, string query = "", bool feed = false)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token is not null) headers["Authorization"] = "Bearer " + token;
        return new("GET", feed ? "/zba/v1/logs/feed" : "/zba/v1/logs", query, headers, [], "synthetic-client");
    }
    private static JsonElement Json(ZbaResponse response) => JsonDocument.Parse(response.Body).RootElement.Clone();
    private static string? Code(ZbaResponse response) => Json(response).GetProperty("error").GetProperty("code").GetString();
    private static void NoStore(ZbaResponse response, Action<bool, string> check)
        => check(response.Headers.TryGetValue("Cache-Control", out var value) && value == "no-store", "log responses never cache");

    public class LogBackendProxy : DispatchProxy
    {
        public int Calls { get; private set; }
        public int LastLimit { get; private set; }
        public static readonly Guid FeedEpoch = Guid.ParseExact("10001000100010001000100010001000", "N");
        public int FeedCalls { get; private set; }
        public ZbaLogFeedCursor? LastCursor { get; private set; }
        public ZbaLogFeedPage FeedResponse { get; set; } = new(
            [new(FeedEpoch.ToString("N") + ":1", "1", 1000, "info", "runtime", "已隐藏的日志内容（8 字符）", true)],
            new ZbaLogFeedCursor(FeedEpoch, 1).ToString(), false, 600);
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name == nameof(IZbaBackend.ReadLogFeedAsync))
            {
                FeedCalls++;
                LastCursor = (ZbaLogFeedCursor?)arguments![1];
                return ValueTask.FromResult(FeedResponse);
            }
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

using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Panel;

namespace BotAgent.ZbaProbe;

/// <summary>
/// ZBA v1 契约探针。全部输入都是合成的（test-admin-token / sk-test-zba-secret-value /
/// 203.0.113.x / synthetic-*）；不读日志、不碰 data/、不连服务器。
/// </summary>
internal static class Program
{
    private const string AdminToken = "test-admin-token";
    private const string ReadToken = "test-read-token";
    private const string WrongToken = "test-wrong-token";
    private const string SecretValue = "sk-test-zba-secret-value";

    private const string Remote = "203.0.113.10";
    private const string BruteRemote = "203.0.113.66";
    internal const long IssuedUnixSeconds = 1_700_000_000;

    internal static readonly DateTimeOffset FixedNow = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    internal static readonly Guid Generation = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid Nonce = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid ForbiddenNonce = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid UnsupportedNonce = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly string ConfigKey = $"cfg1:{Generation:N}:{IssuedUnixSeconds}:{Nonce:N}";
    private static readonly string ManagedKey = $"mg1:{Generation:N}:{IssuedUnixSeconds}:{Nonce:N}";

    private static int _passed;
    private static int _failed;
    private static int _skipped;
    private static readonly List<string> FailureList = new();

    public static async Task<int> Main()
    {
        Console.WriteLine("ZBA v1 contract probe (synthetic backend, no external IO)");

        var backend = new SyntheticZbaBackend();
        var endpoint = CreateEndpoint(backend, ConfiguredOptions());

        await RunAsync("A. routes / OpenAPI", () => RouteAndOpenApiChecksAsync(endpoint));
        await RunAsync("B. authentication / authorization", () => AuthChecksAsync(backend));
        await RunAsync("C. closed JSON", () => ClosedJsonChecksAsync(backend));
        await RunAsync("D. configuration", () => ConfigurationChecksAsync(backend, endpoint));
        await RunAsync("E. capabilities", () => CapabilityChecksAsync(backend, endpoint));
        await RunAsync("F. tasks", () => TaskChecksAsync(backend, endpoint));
        await RunAsync("G. approvals", () => ApprovalChecksAsync(endpoint));
        await RunAsync("H. events", () => EventChecksAsync(backend, endpoint));
        await RunAsync("I. HttpListener adapter smoke", () => HttpListenerSmokeAsync(backend));
        await RunAsync("J. sessions", () => SessionChecksAsync());
        await RunAsync("K. plugin marketplace / provider discovery", () => PluginChecksAsync());
        await RunAsync("L. quota management contract", () => QuotaChecksAsync());

        Console.WriteLine();
        Console.WriteLine($"passed={_passed} failed={_failed} skipped={_skipped}");
        if (FailureList.Count > 0)
        {
            Console.WriteLine("failures:");
            foreach (var failure in FailureList) Console.WriteLine("  " + failure);
        }

        return _failed == 0 ? 0 : 1;
    }

    // ───────────────────────── harness ─────────────────────────

    private static async Task RunAsync(string title, Func<Task> body)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Check(title + " completed without exception", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Check(string label, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("  PASS " + label);
            return;
        }

        _failed++;
        var line = "  FAIL " + label + (detail is null ? string.Empty : " :: " + detail);
        FailureList.Add(line);
        Console.WriteLine(line);
    }

    private static void CheckStatus(string label, ZbaResponse response, int status, string? errorCode = null)
    {
        var actual = ErrorCode(response);
        Check(label, response.Status == status && (errorCode is null || actual == errorCode),
            $"status={response.Status} code={actual ?? "<none>"}");
    }

    private static void CheckErrorReason(string label, ZbaResponse response, string expectedReason)
    {
        var reason = ErrorReason(response);
        Check(label, reason == expectedReason, $"reason={reason ?? "<none>"} status={response.Status}");
    }

    // ───────────────────────── request helpers ─────────────────────────

    private static ZbaEndpoint CreateEndpoint(IZbaBackend backend, ZbaOptions options, int maxBodyBytes = ZbaContract.MaxBodyBytes)
        => new(backend, options with { MaxBodyBytes = maxBodyBytes }, () => FixedNow, "1.0.0-probe", "zi-probe-0001");

    private static ZbaOptions ConfiguredOptions() => new()
    {
        AdminToken = AdminToken,
        ReadToken = ReadToken
    };

    private static Dictionary<string, string> Headers(string? token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token is not null) headers["Authorization"] = "Bearer " + token;
        return headers;
    }

    private static ZbaRequest Req(
        string method,
        string path,
        string? json = null,
        string? token = null,
        string? remote = Remote,
        string? idempotencyKey = null,
        string? query = null)
    {
        var headers = Headers(token);
        if (idempotencyKey is not null) headers["Idempotency-Key"] = idempotencyKey;
        return new ZbaRequest(
            method,
            path,
            query ?? string.Empty,
            headers,
            json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json),
            remote);
    }

    private static ZbaRequest ReqBytes(string method, string path, byte[] body, string? token, string? query = null)
        => new(method, path, query ?? string.Empty, Headers(token), body, Remote);

    private static JsonElement BodyJson(ZbaResponse response)
        => JsonDocument.Parse(response.Body).RootElement.Clone();

    private static string BodyText(ZbaResponse response) => Encoding.UTF8.GetString(response.Body);

    private static string? ErrorCode(ZbaResponse response)
    {
        try
        {
            var root = BodyJson(response);
            if (root.ValueKind != JsonValueKind.Object) return null;
            return root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ErrorReason(ZbaResponse response)
    {
        try
        {
            var root = BodyJson(response);
            if (root.ValueKind != JsonValueKind.Object) return null;
            return root.TryGetProperty("error", out var error) && error.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResponseHeader(ZbaResponse response, string name)
        => response.Headers.TryGetValue(name, out var value) ? value : string.Empty;

    // ───────────────────────── A. routes / OpenAPI ─────────────────────────

    private static async Task RouteAndOpenApiChecksAsync(ZbaEndpoint endpoint)
    {
        var openApiResponse = await endpoint.ProcessAsync(Req("GET", "/zba/v1/openapi.json", token: ReadToken));
        CheckStatus("openapi responds 200", openApiResponse, 200);
        Check("openapi content type", openApiResponse.ContentType.StartsWith("application/json", StringComparison.Ordinal),
            openApiResponse.ContentType);

        var openApi = BodyJson(openApiResponse);
        ReviewPanelContractRegression.CheckOpenApi(openApi, (value, label) => Check(label, value));
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in openApi.GetProperty("paths").EnumerateObject())
            foreach (var method in path.Value.EnumerateObject())
                declared.Add(method.Name + " " + path.Name);

        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in endpoint.Routes)
        {
            expected.Add(route.Method.ToLowerInvariant() + " " + route.Template);
            var declaredOperation = openApi.GetProperty("paths").TryGetProperty(route.Template, out var pathNode)
                && pathNode.TryGetProperty(route.Method.ToLowerInvariant(), out var operation)
                && operation.TryGetProperty("operationId", out var operationId)
                && operationId.GetString() == route.OperationId;
            Check("openapi declares " + route.OperationId, declaredOperation, route.Method + " " + route.Template);
        }

        Check("openapi has no extra paths", declared.SetEquals(expected),
            $"declared={declared.Count} expected={expected.Count}");

        foreach (var route in endpoint.Routes)
        {
            var response = await endpoint.ProcessAsync(RouteSmokeRequest(route));
            Check("route callable " + route.OperationId, response.Status is not (404 or 405 or 500),
                $"status={response.Status} code={ErrorCode(response) ?? "<none>"}");
        }

        var metaResponse = await endpoint.ProcessAsync(Req("GET", "/zba/v1/meta", token: ReadToken));
        CheckStatus("meta responds 200", metaResponse, 200);
        var meta = BodyJson(metaResponse);
        Check("meta.contractVersion is zba-1.0",
            meta.TryGetProperty("contractVersion", out var contractVersion) && contractVersion.GetString() == ZbaContract.Version);
        Check("meta.maxBodyBytes echoes option",
            meta.TryGetProperty("maxBodyBytes", out var maxBody) && maxBody.GetInt32() == ZbaContract.MaxBodyBytes);
        Check("meta.instanceId is present",
            meta.TryGetProperty("instanceId", out var instanceId)
            && instanceId.GetString() == "zi-probe-0001");
        var metaFeatures = meta.TryGetProperty("features", out var featureNode)
            ? featureNode.EnumerateArray().Select(item => item.GetString()).ToArray()
            : Array.Empty<string?>();
        Check("meta advertises session features",
            metaFeatures.Contains("sessions.list") && metaFeatures.Contains("sessions.ensure")
            && metaFeatures.Contains("tasks.result") && metaFeatures.Contains("tasks.find")
            && metaFeatures.Contains("tasks.receipts"),
            string.Join(",", metaFeatures));

        var schemas = openApi.GetProperty("components").GetProperty("schemas");
        Check("openapi operation keys are string codecs",
            schemas.GetProperty("ConfigurationOperationKey").GetProperty("type").GetString() == "string"
            && schemas.GetProperty("ManagedOperationKey").GetProperty("type").GetString() == "string");
        Check("openapi declares session schemas",
            schemas.TryGetProperty("ZbaSessionPage", out _)
            && schemas.TryGetProperty("ZbaSessionSummary", out _)
            && schemas.TryGetProperty("ZbaSessionEnsureRequest", out _)
            && schemas.TryGetProperty("ZbaSessionEnsureResult", out _));
        var saveResponses = openApi.GetProperty("paths").GetProperty("/zba/v1/configuration/save")
            .GetProperty("post").GetProperty("responses");
        Check("openapi advertises 405/410/422",
            saveResponses.TryGetProperty("405", out _)
            && saveResponses.TryGetProperty("410", out _)
            && saveResponses.TryGetProperty("422", out _));

        var health = await endpoint.ProcessAsync(Req("GET", "/zba/v1/health"));
        Check("health carries contract header", ResponseHeader(health, "X-Zba-Contract") == ZbaContract.Version);
        Check("health configured=true with tokens",
            BodyJson(health).TryGetProperty("configured", out var configured) && configured.GetBoolean());
    }

    private static ZbaRequest RouteSmokeRequest(ZbaRouteInfo route) => route.OperationId switch
    {
        "quotas.list" => Req("GET", route.Template, token: AdminToken),
        "quotas.save" => Req("POST", route.Template, "{\"sourceKey\":\"group:10001\",\"expectedRevision\":\"0\",\"idempotencyKey\":\"quota-route-smoke\",\"dailyTokenLimit\":70}", AdminToken),
        "quotas.find" => Req("POST", route.Template, "{\"idempotencyKey\":\"quota-route-smoke\"}", AdminToken),
        "health" => Req("GET", route.Template),
        "meta" or "openapi" or "status" or "capabilities.list" or "tasks.list" or "approvals.list"
            => Req("GET", route.Template, token: AdminToken),
        "events.read" => Req("GET", route.Template, token: AdminToken, query: "?after=0&limit=10"),
        "capabilities.inspect" => Req("POST", route.Template, InspectJson("synthetic-1"), AdminToken),
        "capabilities.validate" => Req("POST", route.Template, ValidationJson("zba-1.0"), AdminToken),
        "capabilities.execute" => Req("POST", route.Template, ExecuteJson("zba-1.0"), AdminToken),
        "capabilities.query" or "capabilities.cancel" => Req("POST", route.Template, ManagedKeyJson(ManagedKey), AdminToken),
        "configuration.read" => Req("POST", route.Template, "{\"kind\":0}", AdminToken),
        "configuration.save" => Req("POST", route.Template, SaveJson("zba-1.0", "[]"), AdminToken),
        "configuration.find" => Req("POST", route.Template, "{\"key\":\"" + ConfigKey + "\"}", AdminToken),
        "tasks.submit" => Req("POST", route.Template, TaskJson(), AdminToken, idempotencyKey: "route-smoke-01"),
        "tasks.find" => Req("POST", route.Template, "{\"key\":\"route-smoke-01\"}", AdminToken),
        "tasks.get" => Req("GET", route.Template.Replace("{id}", "task-1"), token: AdminToken),
        "tasks.result" => Req("GET", route.Template.Replace("{id}", "task-1"), token: AdminToken),
        "tasks.cancel" => Req("POST", route.Template.Replace("{id}", "task-1"), token: AdminToken),
        "sessions.list" => Req("GET", route.Template, token: AdminToken, query: "?sourceKey=panel%3Aworkspace"),
        "sessions.ensure" => Req("POST", route.Template, "{\"sourceKey\":\"panel:workspace\"}", AdminToken),
        "approvals.decide" => Req("POST", route.Template.Replace("{id}", "approval-ok"), "{\"approve\":true}", AdminToken),
        "plugins.catalog" => Req("GET", route.Template, token: AdminToken),
        "plugins.installed" => Req("GET", route.Template, token: AdminToken),
        "plugins.sources.resolve" => Req("POST", route.Template,
            "{\"sourceUrl\":\"https://github.com/example-org/example-plugins/releases/tag/" + "community.example.hello-1.0.0\"}", AdminToken),
        "plugins.install" or "plugins.update" => Req("POST", route.Template, PluginMutationJson(), AdminToken),
        "plugins.uninstall" => Req("POST", route.Template.Replace("{id}", "community.example.hello"),
            "{\"expectedRevision\":\"p0000000000000000\",\"idempotencyKey\":\"plugin:uninstall:route-smoke\"}", AdminToken),
        "plugins.operations.get" => Req("GET", route.Template.Replace("{key}", "plugin:install:route-smoke"), token: AdminToken),
        "plugins.settings.read" => Req("GET", route.Template.Replace("{id}", "community.example.hello"), token: AdminToken),
        "plugins.settings.save" => Req("PUT", route.Template.Replace("{id}", "community.example.hello"),
            "{\"expectedRevision\":\"r1\",\"idempotencyKey\":\"plugin:settings:route-smoke\",\"edits\":[]}", AdminToken),
        "providers.models" => Req("GET", route.Template.Replace("{key}", "synthetic-provider"), token: AdminToken),
        _ => throw new InvalidOperationException("unmapped synthetic route " + route.OperationId)
    };

    // ───────────────────────── J. sessions ─────────────────────────

    private static async Task SessionChecksAsync()
    {
        var backend = new SyntheticZbaBackend();
        var endpoint = CreateEndpoint(backend, ConfiguredOptions());

        CheckStatus("sessions list without sourceKey 400",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/sessions", token: AdminToken)), 400, "missing_source_key");
        CheckStatus("sessions list rejects non-panel source",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/sessions", token: AdminToken,
                query: "?sourceKey=group%3A10001")), 403, "source_key_not_allowed");
        CheckStatus("read token can list sessions",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/sessions", token: ReadToken,
                query: "?sourceKey=panel%3Aworkspace")), 200);
        CheckStatus("read token cannot ensure session",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/sessions/ensure",
                "{\"sourceKey\":\"panel:workspace\"}", ReadToken)), 403, "forbidden");

        var first = await endpoint.ProcessAsync(Req("POST", "/zba/v1/sessions/ensure",
            "{\"sourceKey\":\"panel:workspace\"}", AdminToken));
        CheckStatus("admin ensure 200", first, 200);
        var firstBody = BodyJson(first);
        Check("first ensure created=true", firstBody.GetProperty("created").GetBoolean());
        var session = firstBody.GetProperty("session");
        Check("ensure returns server session", session.GetProperty("backend").GetString() == "server");
        Check("ensure returns stable fields",
            session.TryGetProperty("id", out _) && session.TryGetProperty("name", out _)
            && session.TryGetProperty("current", out _) && session.TryGetProperty("historyChars", out _));

        var second = await endpoint.ProcessAsync(Req("POST", "/zba/v1/sessions/ensure",
            "{\"sourceKey\":\"panel:workspace\"}", AdminToken));
        Check("second ensure created=false", !BodyJson(second).GetProperty("created").GetBoolean());

        CheckStatus("ensure rejects unknown field",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/sessions/ensure",
                "{\"sourceKey\":\"panel:workspace\",\"name\":\"x\"}", AdminToken)), 400, "invalid_json");
        CheckStatus("ensure rejects missing sourceKey",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/sessions/ensure", "{}", AdminToken)), 400, "invalid_json");

        var list = await endpoint.ProcessAsync(Req("GET", "/zba/v1/sessions", token: AdminToken,
            query: "?sourceKey=panel%3Aworkspace"));
        CheckStatus("sessions list 200", list, 200);
        var listBody = BodyJson(list);
        Check("sessions list echoes sourceKey", listBody.GetProperty("sourceKey").GetString() == "panel:workspace");
        Check("sessions list has synthetic session", listBody.GetProperty("items").GetArrayLength() == 1);
        Check("sessions list passes default limit", backend.LastSessionLimit == ZbaContract.DefaultPageSize,
            backend.LastSessionLimit?.ToString());
        Check("sessions list carries no history text",
            !listBody.GetRawText().Contains("synthetic-history", StringComparison.Ordinal));
    }

    // ───────────────────────── B. auth ─────────────────────────

    private static async Task AuthChecksAsync(SyntheticZbaBackend backend)
    {
        var none = CreateEndpoint(backend, new ZbaOptions { AdminToken = null, ReadToken = null });
        var health = await none.ProcessAsync(Req("GET", "/zba/v1/health"));
        CheckStatus("no-token health 200", health, 200);
        Check("no-token health configured=false",
            BodyJson(health).TryGetProperty("configured", out var configured) && !configured.GetBoolean());
        CheckStatus("no-token status 503",
            await none.ProcessAsync(Req("GET", "/zba/v1/status")), 503, "zba_not_configured");

        var endpoint = CreateEndpoint(backend, ConfiguredOptions());
        var missing = await endpoint.ProcessAsync(Req("GET", "/zba/v1/status", remote: "203.0.113.21"));
        CheckStatus("missing Authorization 401", missing, 401, "unauthorized");
        Check("401 carries WWW-Authenticate", ResponseHeader(missing, "WWW-Authenticate") == "Bearer");
        CheckStatus("wrong token 401",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/status", token: WrongToken, remote: "203.0.113.22")),
            401, "unauthorized");
        CheckStatus("read token can read",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/status", token: ReadToken, remote: "203.0.113.23")), 200);
        CheckStatus("read token cannot admin",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/configuration/save", SaveJson("zba-1.0", "[]"), ReadToken,
                remote: "203.0.113.24")), 403, "forbidden");
        CheckStatus("admin token can read",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/status", token: AdminToken, remote: "203.0.113.25")), 200);
        CheckStatus("admin token can write",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/configuration/save", SaveJson("zba-1.0", "[]"), AdminToken,
                remote: "203.0.113.26")), 200);

        var loopback = CreateEndpoint(backend,
            new ZbaOptions { AdminToken = null, ReadToken = null, AllowLoopbackWithoutToken = true });
        CheckStatus("loopback without token can read",
            await loopback.ProcessAsync(Req("GET", "/zba/v1/status", remote: "127.0.0.1")), 200);
        CheckStatus("non-loopback without token 503",
            await loopback.ProcessAsync(Req("GET", "/zba/v1/status", remote: "203.0.113.70")), 503, "zba_not_configured");

        var brute = CreateEndpoint(backend, ConfiguredOptions());
        var statuses = new List<int>();
        for (var attempt = 0; attempt < 10; attempt++)
            statuses.Add((await brute.ProcessAsync(Req("GET", "/zba/v1/status", token: WrongToken, remote: BruteRemote))).Status);
        Check("first 10 bad tokens are 401", statuses.All(status => status == 401), string.Join(",", statuses));
        CheckStatus("11th bad token is 429",
            await brute.ProcessAsync(Req("GET", "/zba/v1/status", token: WrongToken, remote: BruteRemote)),
            429, "too_many_authentication_failures");
    }

    // ───────────────────────── C. closed JSON ─────────────────────────

    private static async Task ClosedJsonChecksAsync(SyntheticZbaBackend backend)
    {
        var endpoint = CreateEndpoint(backend, ConfiguredOptions());
        CheckStatus("unknown field 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/configuration/read",
                "{\"kind\":0,\"extra\":1}", AdminToken)), 400, "invalid_json");
        CheckStatus("duplicate field 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/configuration/read",
                "{\"kind\":0,\"kind\":1}", AdminToken)), 400, "duplicate_field");
        CheckStatus("case-alias duplicate field 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/configuration/read",
                "{\"kind\":0,\"Kind\":1}", AdminToken)), 400, "invalid_json");
        CheckStatus("nested case-alias duplicate field 400",
            await SaveAsync(endpoint, "zba-1.0",
                "[{\"slot\":0,\"Slot\":0,\"kind\":1,\"value\":\"synthetic-secret\"}]"), 400, "invalid_json");
        CheckStatus("nested duplicate field 400",
            await SaveAsync(endpoint, "zba-1.0",
                "[{\"slot\":0,\"slot\":1,\"kind\":1,\"value\":\"synthetic-secret\"}]"), 400, "duplicate_field");

        var invalidUtf8 = Encoding.UTF8.GetBytes("{\"kind\":0,\"x\":\"")
            .Concat(new byte[] { 0xFF })
            .Concat(Encoding.UTF8.GetBytes("\"}"))
            .ToArray();
        CheckStatus("invalid UTF-8 400",
            await endpoint.ProcessAsync(ReqBytes("POST", "/zba/v1/configuration/read", invalidUtf8, AdminToken)),
            400, "invalid_utf8");
        CheckStatus("GET with body 400",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/status", "{}", AdminToken)), 400, "body_not_allowed");

        var tiny = CreateEndpoint(backend, ConfiguredOptions(), maxBodyBytes: 64);
        var oversized = Encoding.UTF8.GetBytes("{\"kind\":0,\"padding\":\"" + new string('a', 64) + "\"}");
        CheckStatus("oversized body 413",
            await tiny.ProcessAsync(ReqBytes("POST", "/zba/v1/configuration/read", oversized, AdminToken)),
            413, "body_too_large");

        CheckStatus("unknown query 400",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/capabilities", token: AdminToken, query: "?foo=1")),
            400, "unknown_query_parameter");
        var statusUnknownQuery = await endpoint.ProcessAsync(
            Req("GET", "/zba/v1/status", token: AdminToken, query: "?foo=1"));
        CheckStatus("status unknown query 400", statusUnknownQuery, 400, "unknown_query_parameter");
    }

    // ───────────────────────── D. configuration ─────────────────────────

    private static async Task ConfigurationChecksAsync(SyntheticZbaBackend backend, ZbaEndpoint endpoint)
    {
        CheckStatus("save ok 200", await SaveAsync(endpoint, "zba-1.0", "[]"), 200);
        CheckStatus("save version conflict 409", await SaveAsync(endpoint, "zba-conflict", "[]"), 409);
        CheckStatus("save idempotency conflict 409",
            await SaveAsync(endpoint, "zba-idempotency-conflict", "[]"), 409);
        CheckStatus("save forbidden 403", await SaveAsync(endpoint, "zba-forbidden", "[]"), 403);
        CheckStatus("save storage unavailable 503", await SaveAsync(endpoint, "zba-storage-down", "[]"), 503);

        CheckStatus("Replace without value 400",
            await SaveAsync(endpoint, "zba-1.0", "[{\"slot\":0,\"kind\":1}]"), 400, "missing_secret_value");
        CheckStatus("Keep with value 400",
            await SaveAsync(endpoint, "zba-1.0", "[{\"slot\":0,\"kind\":0,\"value\":\"synthetic\"}]"),
            400, "secret_value_not_allowed");
        CheckStatus("duplicate secret slot 400",
            await SaveAsync(endpoint, "zba-1.0", "[{\"slot\":0,\"kind\":0},{\"slot\":0,\"kind\":0}]"),
            400, "duplicate_secret_slot");
        var lastSaveBeforeNull = backend.LastSave;
        CheckStatus("null secret element 400",
            await SaveAsync(endpoint, "zba-1.0", "[null]"), 400, "invalid_json");
        CheckStatus("mixed null secret element 400",
            await SaveAsync(endpoint, "zba-1.0", "[{\"slot\":0,\"kind\":0},null]"), 400, "invalid_json");
        Check("null secret element does not call backend", ReferenceEquals(backend.LastSave, lastSaveBeforeNull));
        CheckStatus("null secrets collection 400",
            await SaveAsync(endpoint, "zba-1.0", "null"), 400, "missing_secrets");
        CheckStatus("validate null secret element 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/validate",
                "{\"contractRevision\":\"zba-1.0\",\"expectedVersion\":\"7\",\"changes\":{},\"secrets\":[null]}",
                AdminToken)), 400, "invalid_json");
        CheckStatus("validate null secrets collection 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/validate",
                "{\"contractRevision\":\"zba-1.0\",\"expectedVersion\":\"7\",\"changes\":{},\"secrets\":null}",
                AdminToken)), 400, "missing_secrets");
        // 目标去重按“目标”计算：七个相同的固定槽位只算一个目标，因此先撞重复而不是超限。
        var sevenIdenticalSlots = "[" + string.Join(",", Enumerable.Repeat("{\"slot\":0,\"kind\":0}", 7)) + "]";
        CheckStatus("seven identical slots 400",
            await SaveAsync(endpoint, "zba-1.0", sevenIdenticalSlots), 400, "duplicate_secret_slot");
        var sixDistinctSlots = "[{\"slot\":0,\"kind\":0},{\"slot\":1,\"kind\":0},{\"slot\":2,\"kind\":0},"
            + "{\"slot\":3,\"kind\":0},{\"slot\":4,\"kind\":0},{\"slot\":5,\"kind\":0}]";
        CheckStatus("six distinct slots 200", await SaveAsync(endpoint, "zba-1.0", sixDistinctSlots), 200);
        var tooManyKeys = "[" + string.Join(",",
            Enumerable.Range(0, 65).Select(index => "{\"credentialKey\":\"synthetic-key-" + index + "\",\"kind\":0}")) + "]";
        CheckStatus("more than sixty-four credential targets 400",
            await SaveAsync(endpoint, "zba-1.0", tooManyKeys), 400, "too_many_secrets");
        var sixtyFourKeys = "[" + string.Join(",",
            Enumerable.Range(0, 64).Select(index => "{\"credentialKey\":\"synthetic-key-" + index + "\",\"kind\":0}")) + "]";
        CheckStatus("sixty-four credential targets 200", await SaveAsync(endpoint, "zba-1.0", sixtyFourKeys), 200);

        CheckStatus("both secret targets 400",
            await SaveAsync(endpoint, "zba-1.0",
                "[{\"slot\":0,\"credentialKey\":\"synthetic-key\",\"kind\":0}]"), 400, "invalid_secret_slot");
        CheckStatus("no secret target 400",
            await SaveAsync(endpoint, "zba-1.0", "[{\"kind\":0}]"), 400, "invalid_secret_slot");
        CheckStatus("duplicate credential key 400",
            await SaveAsync(endpoint, "zba-1.0",
                "[{\"credentialKey\":\"synthetic-key\",\"kind\":0},{\"credentialKey\":\"synthetic-key\",\"kind\":0}]"),
            400, "duplicate_secret_slot");
        CheckStatus("padded credential key 400",
            await SaveAsync(endpoint, "zba-1.0", "[{\"credentialKey\":\" synthetic-key \",\"kind\":0}]"),
            400, "invalid_secret_slot");

        var credentialReplace = await SaveAsync(endpoint, "zba-1.0",
            "[{\"credentialKey\":\"synthetic-key\",\"kind\":1,\"value\":\"" + SecretValue + "\"}]");
        CheckStatus("credential key replace 200", credentialReplace, 200);
        Check("credential key replace reaches backend",
            backend.LastSave is { Secrets.Count: 1 }
            && backend.LastSave.Secrets[0].CredentialKey == "synthetic-key"
            && backend.LastSave.Secrets[0].Value == SecretValue,
            "synthetic credential edit shape mismatch");
        Check("credential key replace hides secret value",
            !BodyText(credentialReplace).Contains(SecretValue, StringComparison.Ordinal));
        CheckStatus("credential key delete 200",
            await SaveAsync(endpoint, "zba-1.0", "[{\"credentialKey\":\"synthetic-key\",\"kind\":2}]"), 200);
        Check("credential key delete reaches backend",
            backend.LastSave is { Secrets.Count: 1 }
            && backend.LastSave.Secrets[0].CredentialKey == "synthetic-key"
            && backend.LastSave.Secrets[0].Kind == SecretEditKind.Delete,
            "synthetic credential delete shape mismatch");
        CheckStatus("credential key keep 200",
            await SaveAsync(endpoint, "zba-1.0", "[{\"credentialKey\":\"synthetic-key\",\"kind\":0}]"), 200);
        Check("credential key keep reaches backend",
            backend.LastSave is { Secrets.Count: 1 }
            && backend.LastSave.Secrets[0].CredentialKey == "synthetic-key"
            && backend.LastSave.Secrets[0].Kind == SecretEditKind.Keep,
            "synthetic credential keep shape mismatch");

        var secretJson = "[{\"slot\":0,\"kind\":1,\"value\":\"" + SecretValue + "\"}]";
        var saved = await SaveAsync(endpoint, "zba-1.0", secretJson);
        CheckStatus("save with replacement secret 200", saved, 200);
        Check("secret edit reaches backend",
            backend.LastSave is { Secrets.Count: 1 } && backend.LastSave.Secrets[0].Value == SecretValue,
            "synthetic secret shape mismatch");
        Check("save response hides secret value", !BodyText(saved).Contains(SecretValue, StringComparison.Ordinal));

        var read = await endpoint.ProcessAsync(Req("POST", "/zba/v1/configuration/read", "{\"kind\":0}", AdminToken));
        CheckStatus("configuration read 200", read, 200);
        Check("configuration read hides secret value", !BodyText(read).Contains(SecretValue, StringComparison.Ordinal));
        CheckStatus("configuration read missing kind 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/configuration/read", "{}", AdminToken)), 400);
    }

    private static ValueTask<ZbaResponse> SaveAsync(ZbaEndpoint endpoint, string revision, string secretsJson)
        => endpoint.ProcessAsync(Req("POST", "/zba/v1/configuration/save", SaveJson(revision, secretsJson), AdminToken));

    private static string SaveJson(string revision, string secretsJson)
        => "{\"contractRevision\":\"" + revision + "\",\"expectedVersion\":\"7\",\"key\":\"" + ConfigKey
           + "\",\"changes\":{},\"secrets\":" + secretsJson + "}";

    // ───────────────────────── E. capabilities ─────────────────────────

    private static async Task CapabilityChecksAsync(SyntheticZbaBackend backend, ZbaEndpoint endpoint)
    {
        var list = await endpoint.ProcessAsync(Req("GET", "/zba/v1/capabilities", token: AdminToken,
            query: "?limit=10&cursor=cursor1"));
        CheckStatus("capabilities list 200", list, 200);
        Check("capabilities list passes paging",
            backend.LastCapabilityPage is { Limit: 10, Cursor: "cursor1" },
            backend.LastCapabilityPage is null ? "no backend call" : "paging mismatch");

        CheckStatus("inspect ok 200",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/inspect", InspectJson("synthetic-1"), AdminToken)), 200);
        CheckStatus("inspect forbidden 403",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/inspect", InspectJson("forbidden"), AdminToken)),
            403, null);
        CheckStatus("inspect unsupported 422",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/inspect", InspectJson("unsupported"), AdminToken)),
            422, null);

        CheckStatus("validate ok 200",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/validate", ValidationJson("zba-1.0"), AdminToken)), 200);
        CheckStatus("execute ok 200",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/execute", ExecuteJson("zba-1.0"), AdminToken)), 200);
        CheckStatus("execute unsupported 422",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/execute", ExecuteJson("zba-unsupported"), AdminToken)),
            422, null);
        CheckStatus("query ok 200",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/query", ManagedKeyJson(ManagedKey), AdminToken)), 200);
        CheckStatus("query forbidden 403",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/query",
                ManagedKeyJson($"mg1:{Generation:N}:{IssuedUnixSeconds}:{ForbiddenNonce:N}"), AdminToken)), 403, null);
        CheckStatus("cancel ok 200",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/capabilities/cancel", ManagedKeyJson(ManagedKey), AdminToken)), 200);
    }

    private static string InspectJson(string instanceId)
        => "{\"key\":{\"kind\":\"driver\",\"key\":{\"platformId\":\"synthetic\",\"accountScope\":\"default\",\"instanceId\":\""
           + instanceId + "\"}}}";

    private static string ValidationJson(string revision)
        => "{\"contractRevision\":\"" + revision + "\",\"expectedVersion\":\"7\",\"changes\":{},\"secrets\":[]}";

    private static string ExecuteJson(string revision)
        => "{\"contractRevision\":\"" + revision + "\",\"expectedDescriptorRevision\":\"rev-1\",\"key\":\""
           + ManagedKey + "\",\"command\":{\"kind\":\"driver\",\"key\":{\"platformId\":\"synthetic\","
           + "\"accountScope\":\"default\",\"instanceId\":\"synthetic-1\"},\"action\":0}}";

    private static string ManagedKeyJson(string key) => "\"" + key + "\"";

    // ───────────────────────── F. tasks ─────────────────────────

    private static async Task TaskChecksAsync(SyntheticZbaBackend backend, ZbaEndpoint endpoint)
    {
        CheckStatus("submit without idempotency key 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks", TaskJson(), AdminToken)),
            400, "missing_idempotency_key");
        CheckStatus("submit invalid idempotency key 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks", TaskJson(), AdminToken, idempotencyKey: "bad key!")),
            400, "invalid_idempotency_key");
        CheckStatus("submit invalid target 400",
            await SubmitAsync(endpoint, TaskJson(target: "remote"), "idem-invalid-target"), 400, "invalid_target");
        CheckStatus("submit invalid timeout 400",
            await SubmitAsync(endpoint, TaskJson(timeoutSeconds: 5), "idem-invalid-timeout"), 400, "invalid_timeout");
        CheckStatus("submit invalid prompt 400",
            await SubmitAsync(endpoint, TaskJson(prompt: string.Empty), "idem-invalid-prompt"), 400, "invalid_prompt");

        var accepted = await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks", TaskJson(), AdminToken,
            idempotencyKey: "idem-key-0001"));
        CheckStatus("submit accepted 200", accepted, 200);
        var firstId = BodyJson(accepted).GetProperty("id").GetString();
        var replay = await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks", TaskJson(), AdminToken,
            idempotencyKey: "idem-key-0001"));
        var replayId = BodyJson(replay).GetProperty("id").GetString();
        Check("same idempotency key returns same task id",
            !string.IsNullOrEmpty(firstId) && firstId == replayId, "ids differ");
        var second = await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks", TaskJson(), AdminToken,
            idempotencyKey: "idem-key-0002"));
        Check("different idempotency key returns different task id",
            BodyJson(second).GetProperty("id").GetString() != firstId, "ids matched");

        var list = await endpoint.ProcessAsync(Req("GET", "/zba/v1/tasks", token: AdminToken, query: "?limit=20"));
        CheckStatus("tasks list 200", list, 200);
        Check("tasks list passes limit", backend.LastTaskLimit == 20, "limit mismatch");
        CheckStatus("task get 200",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/tasks/task-1", token: AdminToken)), 200);
        CheckStatus("unknown task 404",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/tasks/task-missing", token: AdminToken)),
            404, "task_not_found");
        CheckStatus("cancel unknown task 404",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks/task-missing/cancel", token: AdminToken)),
            404, "task_not_found");

        var cancel = await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks/task-1/cancel", token: AdminToken));
        CheckStatus("task cancel 200", cancel, 200);
        Check("task cancel reports requested outcome",
            BodyJson(cancel).GetProperty("outcome").GetString() == "requested", "outcome mismatch");

        var unknownOutcome = await endpoint.ProcessAsync(
            Req("POST", "/zba/v1/tasks/task-unknown-outcome/cancel", token: AdminToken));
        CheckStatus("cancel with unknown outcome 200", unknownOutcome, 200);
        var unknownBody = BodyJson(unknownOutcome);
        Check("unknown outcome stays unknown",
            unknownBody.GetProperty("outcome").GetString() == "unknown");
        Check("unknown outcome is not disguised as cancelled",
            unknownBody.GetProperty("state").GetInt32() == (int)ZbaTaskState.Unknown
            && unknownBody.GetProperty("state").GetInt32() != (int)ZbaTaskState.Cancelled
            && !unknownBody.GetProperty("cancellationRequested").GetBoolean());

        CheckStatus("unknown task result 404",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/tasks/task-missing/result", token: AdminToken)),
            404, "task_not_found");
        var runningResult = await endpoint.ProcessAsync(Req("GET", "/zba/v1/tasks/task-1/result", token: AdminToken));
        CheckStatus("running task result 200", runningResult, 200);
        var runningBody = BodyJson(runningResult);
        Check("running task result is unavailable",
            !runningBody.GetProperty("available").GetBoolean()
            && runningBody.GetProperty("reasonCode").GetString() == "not_terminal");

        var doneResult = await endpoint.ProcessAsync(Req("GET", "/zba/v1/tasks/task-done/result", token: AdminToken));
        CheckStatus("succeeded task result 200", doneResult, 200);
        var doneBody = BodyJson(doneResult);
        Check("succeeded task result is available",
            doneBody.GetProperty("available").GetBoolean()
            && doneBody.GetProperty("summary").GetString() == "合成任务完成。"
            && doneBody.GetProperty("summaryChars").GetInt32() == 7
            && !doneBody.GetProperty("summaryTruncated").GetBoolean());
        Check("task result omits prompt",
            !Encoding.UTF8.GetString(doneResult.Body).Contains("synthetic task prompt", StringComparison.Ordinal));
        CheckStatus("read token cannot read task result",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/tasks/task-done/result", token: ReadToken)),
            403, "forbidden");

        var findUnknown = await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks/find",
            "{\"key\":\"synthetic-missing-01\"}", AdminToken));
        CheckStatus("find unknown 200", findUnknown, 200);
        var findUnknownBody = BodyJson(findUnknown);
        Check("find unknown reports unknown",
            findUnknownBody.GetProperty("code").GetString() == "unknown"
            && findUnknownBody.GetProperty("instanceId").GetString() == "zi-probe-0001");

        CheckStatus("find invalid key 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks/find", "{\"key\":\"short\"}", AdminToken)),
            400, "invalid_idempotency_key");
        CheckStatus("find missing key 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks/find", "{}", AdminToken)),
            400, "invalid_json");

        var findFound = await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks/find",
            "{\"key\":\"synthetic-found-01\"}", AdminToken));
        CheckStatus("find found 200", findFound, 200);
        var findFoundBody = BodyJson(findFound);
        Check("find found returns task summary",
            findFoundBody.GetProperty("code").GetString() == "found"
            && findFoundBody.GetProperty("task").GetProperty("id").GetString() == "task-done");
        Check("find never returns prompt or result text",
            !Encoding.UTF8.GetString(findFound.Body).Contains("synthetic task prompt", StringComparison.Ordinal)
            && !Encoding.UTF8.GetString(findFound.Body).Contains("合成任务完成", StringComparison.Ordinal));
        CheckStatus("read token cannot find task",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks/find",
                "{\"key\":\"synthetic-found-01\"}", ReadToken)), 403, "forbidden");
    }

    private static ValueTask<ZbaResponse> SubmitAsync(ZbaEndpoint endpoint, string json, string idempotencyKey)
        => endpoint.ProcessAsync(Req("POST", "/zba/v1/tasks", json, AdminToken, idempotencyKey: idempotencyKey));

    private static string TaskJson(
        string target = "server",
        int timeoutSeconds = 60,
        string prompt = "synthetic task prompt")
        => "{\"sourceKey\":\"synthetic-source\",\"sessionId\":\"synthetic-session\",\"prompt\":\"" + prompt
           + "\",\"target\":\"" + target + "\",\"timeoutSeconds\":" + timeoutSeconds + "}";

    /// <summary>合成安装请求：清单是固定版本 + 全 a 的 SHA-256，绝不指向真实仓库。</summary>
    private static string PluginMutationJson(
        string idempotencyKey = "plugin:install:route-smoke",
        string expectedRevision = "p0000000000000000",
        bool trustAcknowledged = true,
        string manifestId = "community.example.hello")
        => "{\"manifest\":{\"id\":\"" + manifestId + "\",\"version\":\"1.0.0\","
           + "\"entryAssembly\":\"HelloPlugin.dll\",\"entryType\":\"Marketplace.Examples.HelloPlugin\","
           + "\"sha256\":\"" + new string('a', 64) + "\","
           + "\"downloadUrl\":\"https://github.com/example-org/example-plugins/releases/download/"
           + manifestId + "-1.0.0/" + manifestId + "-1.0.0.zip\","
           + "\"apiVersion\":1,\"targetFramework\":\"net8.0\",\"minimumHostVersion\":\"1.0.0\","
           + "\"permissions\":[],\"dependencies\":[],\"settings\":[]},"
           + "\"expectedRevision\":\"" + expectedRevision + "\","
           + "\"idempotencyKey\":\"" + idempotencyKey + "\","
           + "\"trustAcknowledged\":" + (trustAcknowledged ? "true" : "false") + "}";

    // ───────────────────────── K. plugin marketplace ─────────────────────────

    private static async Task QuotaChecksAsync()
    {
        var endpoint = CreateEndpoint(new SyntheticZbaBackend(), ConfiguredOptions());
        const string save = "{\"sourceKey\":\"group:10001\",\"expectedRevision\":\"0\",\"idempotencyKey\":\"quota-test-0001\",\"dailyTokenLimit\":70}";
        CheckStatus("quota anonymous list denied", await endpoint.ProcessAsync(Req("GET", "/zba/v1/quotas")), 401);
        CheckStatus("quota read bearer lists", await endpoint.ProcessAsync(Req("GET", "/zba/v1/quotas", token: ReadToken)), 200);
        CheckStatus("quota read bearer cannot save", await endpoint.ProcessAsync(Req("POST", "/zba/v1/quotas/save", save, ReadToken)), 403);
        CheckStatus("quota read bearer cannot find receipts", await endpoint.ProcessAsync(Req("POST", "/zba/v1/quotas/find", "{\"idempotencyKey\":\"quota-test-0001\"}", ReadToken)), 403);
        CheckStatus("quota cookie is not bearer", await endpoint.ProcessAsync(Req("POST", "/zba/v1/quotas/save", save)), 401);
        CheckStatus("quota administrator saves", await endpoint.ProcessAsync(Req("POST", "/zba/v1/quotas/save", save, AdminToken)), 200);
        CheckStatus("quota range checked", await endpoint.ProcessAsync(Req("POST", "/zba/v1/quotas/save", save.Replace(":70", ":0"), AdminToken)), 400, "invalid_daily_token_limit");
        CheckStatus("quota revision checked", await endpoint.ProcessAsync(Req("POST", "/zba/v1/quotas/save", save.Replace("\"0\"", "\"-1\""), AdminToken)), 400, "invalid_quota_revision");
        CheckStatus("quota unknown fields rejected", await endpoint.ProcessAsync(Req("POST", "/zba/v1/quotas/save", save.Replace(":70}", ":70,\"usedTokens\":0}"), AdminToken)), 400);
        CheckStatus("quota missing required fields rejected", await endpoint.ProcessAsync(Req("POST", "/zba/v1/quotas/save", "{}", AdminToken)), 400);
        CheckStatus("quota receipt lookup", await endpoint.ProcessAsync(Req("POST", "/zba/v1/quotas/find", "{\"idempotencyKey\":\"quota-test-0001\"}", AdminToken)), 200);
        var meta = BodyJson(await endpoint.ProcessAsync(Req("GET", "/zba/v1/meta", token: ReadToken)));
        Check("quota feature negotiation", meta.GetProperty("features").EnumerateArray().Any(x => x.GetString() == "quotas.find"));
    }

    private static async Task PluginChecksAsync()
    {
        var backend = new SyntheticZbaBackend();
        var endpoint = CreateEndpoint(backend, ConfiguredOptions());

        CheckStatus("B12 fixed release source reaches backend", await endpoint.ProcessAsync(Req("POST",
            "/zba/v1/plugins/sources/resolve", "{\"sourceUrl\":\"https://github.com/example-org/example-plugins/releases/tag/probe-1.0.0\"}", AdminToken)), 200);
        CheckStatus("B12 tag-less release path rejected", await endpoint.ProcessAsync(Req("POST",
            "/zba/v1/plugins/sources/resolve", "{\"sourceUrl\":\"https://github.com/example-org/example-plugins/releases/tag\"}", AdminToken)), 422);
        var catalog = await endpoint.ProcessAsync(Req("GET", "/zba/v1/plugins/catalog", token: ReadToken));
        CheckStatus("plugin catalog 200 for read token", catalog, 200);
        var catalogBody = BodyJson(catalog);
        Check("plugin catalog exposes revision/source/trustNotice/categories/plugins",
            catalogBody.TryGetProperty("revision", out _) && catalogBody.TryGetProperty("source", out _)
            && catalogBody.TryGetProperty("trustNotice", out _) && catalogBody.TryGetProperty("categories", out _)
            && catalogBody.GetProperty("plugins").GetArrayLength() == 1);
        var entry = catalogBody.GetProperty("plugins")[0];
        Check("catalog entry carries manifest and real state",
            entry.GetProperty("manifest").GetProperty("id").GetString() == "community.example.hello"
            && entry.GetProperty("enabled").GetBoolean() == false
            && entry.GetProperty("status").GetString() == "not_installed"
            && entry.GetProperty("restartRequired").GetBoolean() == false);
        Check("catalog search filters", BodyJson(await endpoint.ProcessAsync(Req("GET", "/zba/v1/plugins/catalog",
                token: ReadToken, query: "?q=nomatch"))).GetProperty("plugins").GetArrayLength() == 0);
        CheckStatus("catalog rejects unknown query name",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/plugins/catalog", token: ReadToken, query: "?source=evil")),
            400, "unknown_query_parameter");

        var installed = await endpoint.ProcessAsync(Req("GET", "/zba/v1/plugins/installed", token: ReadToken));
        CheckStatus("installed plugins 200", installed, 200);
        Check("installed plugins report installed version",
            BodyJson(installed).GetProperty("plugins")[0].GetProperty("installedVersion").GetString() == "1.0.0");

        CheckStatus("install without trust acknowledgement 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/plugins/install",
                PluginMutationJson(trustAcknowledged: false), AdminToken)), 400, "trust_not_acknowledged");
        CheckStatus("install with bad manifest id 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/plugins/install",
                PluginMutationJson(manifestId: "Community.Example"), AdminToken)), 400, "invalid_plugin_id");
        CheckStatus("install with non-fixed version 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/plugins/install",
                PluginMutationJson().Replace("\"version\":\"1.0.0\"", "\"version\":\"latest\"", StringComparison.Ordinal),
                AdminToken)), 400, "invalid_manifest");
        CheckStatus("install with short sha256 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/plugins/install",
                PluginMutationJson().Replace(new string('a', 64), "abc", StringComparison.Ordinal),
                AdminToken)), 400, "invalid_manifest");
        CheckStatus("install rejects short idempotency key 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/plugins/install",
                PluginMutationJson(idempotencyKey: "short"), AdminToken)), 400, "invalid_idempotency_key");
        CheckStatus("read token cannot install",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/plugins/install", PluginMutationJson(), ReadToken)),
            403, "forbidden");
        var install = await endpoint.ProcessAsync(Req("POST", "/zba/v1/plugins/install",
            PluginMutationJson(), AdminToken));
        CheckStatus("install 200", install, 200);
        Check("install reaches backend with the submitted manifest",
            backend.LastInstall is { Manifest.Id: "community.example.hello", TrustAcknowledged: true });
        var operation = BodyJson(install).GetProperty("operation");
        Check("install operation is restart-bound",
            operation.GetProperty("kind").GetString() == "install"
            && operation.GetProperty("status").GetString() == "restart_required"
            && operation.GetProperty("restartRequired").GetBoolean());

        CheckStatus("update requires admin", await endpoint.ProcessAsync(
            Req("POST", "/zba/v1/plugins/update", PluginMutationJson(), ReadToken)), 403, "forbidden");
        CheckStatus("update 200", await endpoint.ProcessAsync(
            Req("POST", "/zba/v1/plugins/update", PluginMutationJson(), AdminToken)), 200);

        CheckStatus("uninstall rejects bad plugin id 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/plugins/Bad.Id/uninstall",
                "{\"expectedRevision\":\"p0000000000000000\",\"idempotencyKey\":\"plugin:uninstall:bad\"}", AdminToken)),
            400, "invalid_plugin_id");
        CheckStatus("uninstall 200", await endpoint.ProcessAsync(Req("POST",
            "/zba/v1/plugins/community.example.hello/uninstall",
            "{\"expectedRevision\":\"p0000000000000000\",\"idempotencyKey\":\"plugin:uninstall:ok\"}", AdminToken)), 200);

        var found = await endpoint.ProcessAsync(Req("GET",
            "/zba/v1/plugins/operations/plugin%3Ainstall%3Aroute-smoke", token: ReadToken));
        CheckStatus("operation lookup by idempotency key 200", found, 200);
        Check("operation lookup returns the receipt", BodyJson(found).TryGetProperty("revision", out _));

        var settings = await endpoint.ProcessAsync(Req("GET",
            "/zba/v1/plugins/community.example.hello/settings", token: ReadToken));
        CheckStatus("plugin settings read 200", settings, 200);
        var settingsBody = BodyJson(settings);
        Check("settings never echo secret values",
            settingsBody.GetProperty("configuredSecrets").GetProperty("apiKey").GetBoolean()
            && !settingsBody.GetProperty("values").TryGetProperty("apiKey", out _));
        CheckStatus("settings save rejects unknown key 400",
            await endpoint.ProcessAsync(Req("PUT", "/zba/v1/plugins/community.example.hello/settings",
                "{\"expectedRevision\":\"r1\",\"idempotencyKey\":\"plugin:settings:bad\",\"edits\":[],"
                + "\"extra\":true}", AdminToken)), 400, "invalid_json");
        CheckStatus("settings save 200", await endpoint.ProcessAsync(Req("PUT",
            "/zba/v1/plugins/community.example.hello/settings",
            "{\"expectedRevision\":\"r1\",\"idempotencyKey\":\"plugin:settings:ok\","
            + "\"edits\":[{\"key\":\"greeting\",\"kind\":1,\"value\":\"hi\"},"
            + "{\"key\":\"verbose\",\"kind\":0}]}", AdminToken)), 200);
        Check("settings edits reach backend", backend.LastSettingsSave is { Edits.Count: 2 });

        var models = await endpoint.ProcessAsync(Req("GET", "/zba/v1/providers/synthetic-provider/models", token: ReadToken));
        CheckStatus("provider models 200", models, 200);
        Check("provider models lists identifiers", BodyJson(models).GetProperty("models").GetArrayLength() == 2
            && backend.LastProviderKey == "synthetic-provider");
        var missing = await endpoint.ProcessAsync(Req("GET", "/zba/v1/providers/synthetic-missing/models", token: ReadToken));
        Check("provider models failure stays 200 with errorCode",
            missing.Status == 200 && BodyJson(missing).GetProperty("errorCode").GetString() == "model_route_unavailable");
        CheckStatus("provider models needs authentication",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/providers/synthetic-provider/models")), 401);
    }

    // ───────────────────────── G. approvals ─────────────────────────

    private static async Task ApprovalChecksAsync(ZbaEndpoint endpoint)
    {
        var list = await endpoint.ProcessAsync(Req("GET", "/zba/v1/approvals", token: ReadToken));
        CheckStatus("approvals list 200", list, 200);
        var items = BodyJson(list);
        Check("approvals list is an array", items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 2,
            "unexpected shape");
        foreach (var item in items.EnumerateArray())
        {
            var names = item.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Check("approval item exposes summary only",
                names.SequenceEqual(new[] { "expiresAt", "id", "policyVersion", "session", "summary", "toolId" },
                    StringComparer.Ordinal),
                string.Join(",", names));
        }

        CheckStatus("decide unknown request 404", await DecideAsync(endpoint, "approval-missing"), 404);
        CheckErrorReason("unknown request reason", await DecideAsync(endpoint, "approval-missing"), "unknown_request");
        CheckStatus("decide not an approver 403", await DecideAsync(endpoint, "approval-not-approver"), 403);
        CheckErrorReason("not an approver reason", await DecideAsync(endpoint, "approval-not-approver"), "not_an_approver");
        CheckStatus("decide expired 409", await DecideAsync(endpoint, "approval-expired"), 409);
        CheckErrorReason("expired reason", await DecideAsync(endpoint, "approval-expired"), "expired");

        var accepted = await DecideAsync(endpoint, "approval-ok");
        CheckStatus("decide success 200", accepted, 200);
        var acceptedBody = BodyJson(accepted);
        Check("decide success reports approved",
            acceptedBody.GetProperty("ok").GetBoolean()
            && acceptedBody.GetProperty("decision").GetString() == "approved");
        CheckStatus("decide missing approve 400",
            await endpoint.ProcessAsync(Req("POST", "/zba/v1/approvals/approval-ok/decide", "{}", AdminToken)), 400);
    }

    private static ValueTask<ZbaResponse> DecideAsync(ZbaEndpoint endpoint, string requestId)
        => endpoint.ProcessAsync(Req("POST", "/zba/v1/approvals/" + requestId + "/decide", "{\"approve\":true}", AdminToken));

    // ───────────────────────── H. events ─────────────────────────

    private static async Task EventChecksAsync(SyntheticZbaBackend backend, ZbaEndpoint endpoint)
    {
        var page = await endpoint.ProcessAsync(Req("GET", "/zba/v1/events", token: ReadToken,
            query: "?after=42&limit=7"));
        CheckStatus("events 200", page, 200);
        Check("events parses after/limit", backend.LastAfter == 42 && backend.LastEventLimit == 7,
            $"after={backend.LastAfter} limit={backend.LastEventLimit}");
        var body = BodyJson(page);
        Check("events resyncRequired passthrough", body.GetProperty("resyncRequired").GetBoolean());
        Check("events items present", body.GetProperty("items").GetArrayLength() == 1);

        CheckStatus("events invalid after 400",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/events", token: ReadToken, query: "?after=-1")),
            400, "invalid_after");
        CheckStatus("events invalid limit 400",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/events", token: ReadToken, query: "?limit=0")),
            400, "invalid_limit");
        CheckStatus("events unknown query 400",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/events", token: ReadToken, query: "?foo=1")),
            400, "unknown_query_parameter");
        CheckStatus("events duplicate query 400",
            await endpoint.ProcessAsync(Req("GET", "/zba/v1/events", token: ReadToken, query: "?after=1&after=2")),
            400, "duplicate_query_parameter");
    }

    // ───────────────────────── I. HttpListener smoke ─────────────────────────

    private static async Task HttpListenerSmokeAsync(SyntheticZbaBackend backend)
    {
        var listener = new HttpListener();
        TcpListener? portProbe = null;
        try
        {
            portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            portProbe = null;

            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (Exception ex) when (ex is HttpListenerException or UnauthorizedAccessException or NotSupportedException)
            {
                _skipped++;
                Console.WriteLine("  SKIP HttpListener smoke: " + ex.GetType().Name
                    + " (loopback bind not permitted in this environment)");
                return;
            }

            var endpoint = CreateEndpoint(backend, ConfiguredOptions());
            using var stop = new CancellationTokenSource();
            var acceptLoop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (HttpListenerException)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ZbaHttpListenerAdapter.HandleAsync(context, endpoint,
                                ZbaContract.MaxBodyBytes, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                            // smoke 只关心正常路径；异常由状态断言兜底。
                        }
                        finally
                        {
                            try { context.Response.Close(); } catch { }
                        }
                    });
                }
            });

            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(10)
            };

            var health = await client.GetAsync("/zba/v1/health");
            Check("HttpListener health 200", (int)health.StatusCode == 200, ((int)health.StatusCode).ToString());
            Check("HttpListener health content type",
                health.Content.Headers.ContentType?.MediaType == "application/json",
                health.Content.Headers.ContentType?.ToString() ?? "<none>");
            Check("HttpListener health contract header",
                health.Headers.TryGetValues("X-Zba-Contract", out var healthContract)
                && healthContract.FirstOrDefault() == ZbaContract.Version);

            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/zba/v1/status");
            statusRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ReadToken);
            var status = await client.SendAsync(statusRequest);
            Check("HttpListener bearer status 200", (int)status.StatusCode == 200, ((int)status.StatusCode).ToString());
            Check("HttpListener status content type",
                status.Content.Headers.ContentType?.MediaType == "application/json",
                status.Content.Headers.ContentType?.ToString() ?? "<none>");
            Check("HttpListener status contract header",
                status.Headers.TryGetValues("X-Zba-Contract", out var statusContract)
                && statusContract.FirstOrDefault() == ZbaContract.Version);
            Check("HttpListener status body is ZbaStatus",
                (await status.Content.ReadAsStringAsync()).Contains("\"configurationApplyState\"", StringComparison.Ordinal));

            stop.Cancel();
            listener.Stop();
            try { await acceptLoop; } catch (Exception) { }
        }
        finally
        {
            try { portProbe?.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
    }
}

/// <summary>确定性宿主实现：所有结果由合成 revision / key / id 决定，无外部 IO。</summary>
internal sealed partial class SyntheticZbaBackend : IZbaBackend
{
    private readonly Dictionary<string, string> _idempotent = new(StringComparer.Ordinal);
    private int _nextTask;

    internal ManagedPageRequest? LastCapabilityPage { get; private set; }
    internal int? LastTaskLimit { get; private set; }
    internal int? LastSessionLimit { get; private set; }
    internal string? LastSessionSourceKey { get; private set; }
    internal long? LastAfter { get; private set; }
    internal int? LastEventLimit { get; private set; }
    internal ZbaConfigurationSaveRequest? LastSave { get; private set; }
    private bool _sessionEnsured;

    public ValueTask<ZbaHealth> GetHealthAsync(CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaHealth("healthy", true, true, ZbaContract.Version, Program.FixedNow));

    public ValueTask<ZbaStatus> GetStatusAsync(ConfigurationCaller caller, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaStatus(
            "running", true, "1.0.0-probe", Program.FixedNow, Program.FixedNow, "7", "applied",
            ImmutableArray.Create(new ZbaComponent(
                "driver:synthetic", "driver", "Synthetic driver", "running", true, null, Program.FixedNow)),
            ImmutableArray.Create(new ZbaErrorSummary(Program.FixedNow, "synthetic", "synthetic_error"))));

    public ValueTask<ZbaSessionPage> ListSessionsAsync(
        ConfigurationCaller caller, string sourceKey, int limit, string? cursor, CancellationToken ct = default)
    {
        LastSessionSourceKey = sourceKey;
        LastSessionLimit = limit;
        var items = ImmutableArray.Create(new ZbaSessionSummary(
            "synthetic-session-1", "工作台会话", "server", true, 2, 64,
            Program.FixedNow, Program.FixedNow, false));
        return ValueTask.FromResult(new ZbaSessionPage(sourceKey, items, null));
    }

    public ValueTask<ZbaSessionEnsureResult> EnsureSessionAsync(
        ConfigurationCaller caller, string sourceKey, CancellationToken ct = default)
    {
        var created = !_sessionEnsured;
        _sessionEnsured = true;
        return ValueTask.FromResult(new ZbaSessionEnsureResult(
            new ZbaSessionSummary("synthetic-session-1", "默认（服务器）", "server", true, 0, 0,
                Program.FixedNow, Program.FixedNow, true), created));
    }

    public ValueTask<ManagedPage> ListCapabilitiesAsync(
        ConfigurationCaller caller, ManagedPageRequest request, CancellationToken ct = default)
    {
        LastCapabilityPage = request;
        return ValueTask.FromResult(new ManagedPage(
            ManagedResultCode.Ok,
            ImmutableArray.Create(Descriptor("synthetic-1")),
            request.Cursor is null ? "cursor-1" : null,
            "rev-1"));
    }

    public ValueTask<ManagedInspectResult> InspectCapabilityAsync(
        ConfigurationCaller caller, ManagedObjectKey key, CancellationToken ct = default)
    {
        var instance = key is ManagedObjectKey.Driver driver ? driver.Key.InstanceId : "unknown";
        var code = instance switch
        {
            "forbidden" => ManagedResultCode.Forbidden,
            "unsupported" => ManagedResultCode.UnsupportedAction,
            _ => ManagedResultCode.Ok
        };
        return ValueTask.FromResult(new ManagedInspectResult(
            code, code == ManagedResultCode.Ok ? Descriptor(instance) : null));
    }

    public ValueTask<ManagedValidationResult> ValidateCapabilityAsync(
        ConfigurationCaller caller, ZbaConfigurationValidationRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(new ManagedValidationResult(
            ManagedResultCode.Ok, ImmutableArray<FieldError>.Empty));

    public ValueTask<ManagedOperationResult> ExecuteCapabilityAsync(
        ConfigurationCaller caller, ManagedExecuteRequest request, CancellationToken ct = default)
    {
        var code = request.ContractRevision switch
        {
            "zba-forbidden" => ManagedResultCode.Forbidden,
            "zba-unsupported" => ManagedResultCode.UnsupportedAction,
            _ => ManagedResultCode.Ok
        };
        return ValueTask.FromResult(new ManagedOperationResult(
            code, false, code == ManagedResultCode.Ok ? Receipt(request.Key, request.Command) : null));
    }

    public ValueTask<ManagedOperationResult> QueryCapabilityAsync(
        ConfigurationCaller caller, ManagedOperationKey key, CancellationToken ct = default)
    {
        var code = key.Nonce == Program.ForbiddenNonce ? ManagedResultCode.Forbidden
            : key.Nonce == Program.UnsupportedNonce ? ManagedResultCode.UnsupportedAction
            : ManagedResultCode.Ok;
        return ValueTask.FromResult(new ManagedOperationResult(
            code, false, code == ManagedResultCode.Ok
                ? new ManagedReceipt(key, Descriptor("synthetic-1").Key, ManagedAction.Inspect, "rev-1",
                    ManagedOperationState.Completed, null)
                : null));
    }

    public ValueTask<ManagedCancellationResult> CancelCapabilityAsync(
        ConfigurationCaller caller, ManagedOperationKey key, CancellationToken ct = default)
    {
        var code = key.Nonce == Program.ForbiddenNonce ? ManagedResultCode.Forbidden : ManagedResultCode.Ok;
        return ValueTask.FromResult(new ManagedCancellationResult(
            code, code == ManagedResultCode.Ok ? ManagedOperationState.Cancelled : ManagedOperationState.Unknown));
    }

    public ValueTask<ConfigurationReadResult> ReadConfigurationAsync(
        ConfigurationCaller caller, ConfigurationReadKind kind, CancellationToken ct = default)
        => ValueTask.FromResult(new ConfigurationReadResult(
            ConfigurationResultCode.Ok,
            new ConfigurationVersion(7),
            new ConfigurationGeneration(Program.Generation),
            ZbaContract.Version,
            Program.IssuedUnixSeconds,
            null,
            ImmutableArray.Create(new SecretMetadata(WritableSecretSlot.ModelApiKey, true, CredentialSource.Database)),
            ImmutableArray<FieldDescriptor>.Empty,
            ApplyState.Applied));

    public ValueTask<ConfigurationSaveResult> SaveConfigurationAsync(
        ConfigurationCaller caller, ZbaConfigurationSaveRequest request, CancellationToken ct = default)
    {
        LastSave = request;
        var code = request.ContractRevision switch
        {
            "zba-conflict" => ConfigurationResultCode.VersionConflict,
            "zba-idempotency-conflict" => ConfigurationResultCode.IdempotencyConflict,
            "zba-forbidden" => ConfigurationResultCode.Forbidden,
            "zba-storage-down" => ConfigurationResultCode.StorageUnavailable,
            _ => ConfigurationResultCode.Ok
        };
        var receipt = code == ConfigurationResultCode.Ok
            ? new ConfigurationReceipt(
                request.Key, ZbaContract.Version, ConfigurationDecision.Changed,
                new ConfigurationVersion(7), new ConfigurationVersion(8),
                CommitState.Committed, ApplyState.Applied,
                ImmutableArray<ApplyStepReport>.Empty, ImmutableArray<DeviceKeyAssignment>.Empty)
            : null;
        return ValueTask.FromResult(new ConfigurationSaveResult(
            code, false, receipt, new ConfigurationVersion(code == ConfigurationResultCode.Ok ? 8 : 7),
            ImmutableArray<FieldError>.Empty));
    }

    public ValueTask<ConfigurationFindResult> FindConfigurationAsync(
        ConfigurationCaller caller, ConfigurationOperationKey key, CancellationToken ct = default)
        => ValueTask.FromResult(new ConfigurationFindResult(
            ConfigurationResultCode.Ok,
            new ConfigurationReceipt(
                key, ZbaContract.Version, ConfigurationDecision.Changed,
                new ConfigurationVersion(7), new ConfigurationVersion(8),
                CommitState.Committed, ApplyState.Applied,
                ImmutableArray<ApplyStepReport>.Empty, ImmutableArray<DeviceKeyAssignment>.Empty)));

    public ValueTask<ZbaTaskAccepted> SubmitTaskAsync(
        ConfigurationCaller caller, ZbaTaskSubmission submission, string idempotencyKey,
        CancellationToken ct = default)
    {
        if (!_idempotent.TryGetValue(idempotencyKey, out var id))
        {
            _nextTask++;
            id = "task-" + _nextTask.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _idempotent[idempotencyKey] = id;
        }

        return ValueTask.FromResult(new ZbaTaskAccepted(id, ZbaTaskState.Queued, Program.FixedNow));
    }

    public ValueTask<ZbaTaskPage> ListTasksAsync(
        ConfigurationCaller caller, int limit, string? cursor, CancellationToken ct = default)
    {
        LastTaskLimit = limit;
        return ValueTask.FromResult(new ZbaTaskPage(
            ImmutableArray.Create(Summary("task-1", ZbaTaskState.Running)), "next-task-1"));
    }

    public ValueTask<ZbaTaskSummary?> GetTaskAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default)
        => ValueTask.FromResult(id switch
        {
            "task-1" => Summary("task-1", ZbaTaskState.Running),
            "task-unknown-outcome" => Summary("task-unknown-outcome", ZbaTaskState.Running),
            _ => (ZbaTaskSummary?)null
        });

    public ValueTask<ZbaTaskResult?> GetTaskResultAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default)
        => ValueTask.FromResult(id switch
        {
            "task-done" => new ZbaTaskResult("task-done", ZbaTaskState.Succeeded, Program.FixedNow, true,
                null, "合成任务完成。", false, 7, 2, 1234, null),
            "task-1" => new ZbaTaskResult("task-1", ZbaTaskState.Running, Program.FixedNow, false,
                "not_terminal", null, false, 0, 1, null, null),
            _ => (ZbaTaskResult?)null
        });

    public ValueTask<ZbaTaskFindResult> FindTaskAsync(
        ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default)
        => ValueTask.FromResult(idempotencyKey switch
        {
            "synthetic-found-01" => new ZbaTaskFindResult("found", "zi-probe-0001",
                Summary("task-done", ZbaTaskState.Succeeded), null, null),
            "synthetic-previous-01" => new ZbaTaskFindResult("accepted_unknown", "zi-probe-0001",
                null, "zt-synthetic-1", Program.FixedNow),
            "synthetic-expired-01" => new ZbaTaskFindResult("expired", "zi-probe-0001",
                null, "zt-synthetic-2", Program.FixedNow),
            _ => new ZbaTaskFindResult("unknown", "zi-probe-0001", null, null, null)
        });

    public ValueTask<ZbaTaskCancelResult> CancelTaskAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default)
        => ValueTask.FromResult(id == "task-unknown-outcome"
            ? new ZbaTaskCancelResult(ZbaTaskState.Unknown, false, "unknown")
            : new ZbaTaskCancelResult(ZbaTaskState.CancelRequested, true, "requested"));

    public ValueTask<IReadOnlyList<ZbaApprovalSummary>> ListApprovalsAsync(
        ConfigurationCaller caller, CancellationToken ct = default)
    {
        IReadOnlyList<ZbaApprovalSummary> items = ImmutableArray.Create(
            new ZbaApprovalSummary("approval-1", "synthetic.tool", "Synthetic approval one",
                "synthetic-session", Program.FixedNow.AddMinutes(5), 3),
            new ZbaApprovalSummary("approval-2", "synthetic.tool", "Synthetic approval two",
                "synthetic-session", Program.FixedNow.AddMinutes(6), 3));
        return ValueTask.FromResult(items);
    }

    public ValueTask<ZbaApprovalDecisionResult> DecideApprovalAsync(
        ConfigurationCaller caller, string requestId, bool approve, CancellationToken ct = default)
        => ValueTask.FromResult(requestId switch
        {
            "approval-missing" => new ZbaApprovalDecisionResult(false, "none", "unknown_request"),
            "approval-not-approver" => new ZbaApprovalDecisionResult(false, "none", "not_an_approver"),
            "approval-expired" => new ZbaApprovalDecisionResult(false, "none", "expired"),
            _ => new ZbaApprovalDecisionResult(true, approve ? "approved" : "rejected", "ok")
        });

    public ValueTask<ZbaEventPage> ReadEventsAsync(
        ConfigurationCaller caller, long after, int limit, CancellationToken ct = default)
    {
        LastAfter = after;
        LastEventLimit = limit;
        return ValueTask.FromResult(new ZbaEventPage(
            ImmutableArray.Create(new ZbaEvent(after + 1, "task.state", Program.FixedNow, "task-1", "running", "8", null)),
            after + 1, true, Program.FixedNow));
    }

    private static ManagedDescriptor Descriptor(string instanceId)
        => new(
            new ManagedObjectKey.Driver(new DriverKey("synthetic", "default", instanceId)),
            "Synthetic driver",
            true,
            ManagedRuntimeState.Running,
            ImmutableArray.Create(new ActionCapability(ManagedAction.Inspect, true, "ok")),
            false,
            null,
            "rev-1");

    private static ZbaTaskSummary Summary(string id, ZbaTaskState state)
        => new(
            id, state, "server", "synthetic-session", Program.FixedNow, Program.FixedNow,
            null, 1, null, state == ZbaTaskState.CancelRequested);

    private static ManagedReceipt Receipt(ManagedOperationKey key, ManagedCommand command)
    {
        ManagedObjectKey target = command switch
        {
            ManagedCommand.Driver driver => new ManagedObjectKey.Driver(driver.Key),
            ManagedCommand.Plugin plugin => new ManagedObjectKey.Plugin(plugin.Key),
            ManagedCommand.Provider provider => new ManagedObjectKey.Provider(provider.Key),
            ManagedCommand.Mcp mcp => new ManagedObjectKey.Mcp(mcp.RegisteredId),
            _ => throw new InvalidOperationException("synthetic command")
        };
        var action = command switch
        {
            ManagedCommand.Driver driver => driver.Action switch
            {
                DriverCommand.Start => ManagedAction.Start,
                DriverCommand.Stop => ManagedAction.Stop,
                _ => ManagedAction.TestConnection
            },
            ManagedCommand.Plugin plugin => plugin.Action == PluginCommand.Start
                ? ManagedAction.Start
                : ManagedAction.Stop,
            ManagedCommand.Provider => ManagedAction.TestConnection,
            ManagedCommand.Mcp mcp => mcp.Action switch
            {
                McpCommand.Connect => ManagedAction.Connect,
                McpCommand.Disconnect => ManagedAction.Disconnect,
                _ => ManagedAction.TestConnection
            },
            _ => ManagedAction.Inspect
        };
        return new ManagedReceipt(key, target, action, "rev-1", ManagedOperationState.Completed, null);
    }
}

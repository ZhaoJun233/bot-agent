using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.ZbaProbe;

internal static class HttpContributionChecks
{
    private sealed record Input([property: JsonRequired] string Value);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        const string schema = """{"SyntheticInput":{"type":"object"},"SyntheticOutput":{"type":"object"}}""";
        var route = new PluginHttpRoute("POST", "/zba/v1/synthetic-operation", true, "synthetic.execute",
            "Synthetic operation", "SyntheticInput", "SyntheticOutput");
        var routes = new List<PluginHttpRoute> { route };
        var description = new PluginHttpDescription("synthetic.owner", routes, schema);
        var calls = 0;
        PluginHttpBinding<ConfigurationCaller> Bind(PluginHttpDescription value) => new(value, (operation, caller, body, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var input = body.Read<Input>();
            calls++;
            check(operation == "synthetic.execute" && caller.Access.HasFlag(ConfigurationAccess.SaveConfiguration), "bound authenticated caller");
            return ValueTask.FromResult<object>(new { echoed = input.Value });
        });
        ZbaEndpoint Create(params PluginHttpBinding<ConfigurationCaller>[] bindings) => new(
            new SyntheticZbaBackend { CustomHttpContributions = bindings },
            new ZbaOptions { AdminToken = "synthetic-admin-token", ReadToken = "synthetic-read-token" },
            () => Program.FixedNow, "synthetic", "synthetic-instance");
        var endpoint = Create(Bind(description));
        routes.Clear(); // Caller mutation cannot alter the already-published transport catalog.
        async Task<ZbaResponse> Request(string method, string path, string? token, string body = "", string query = "")
            => await endpoint.ProcessAsync(new(method, path, query, token is null ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }, Encoding.UTF8.GetBytes(body), "203.0.113.20"));
        check((await Request("POST", route.Path, null, """{}""")).Status == 401, "extension anonymous request denied");
        check((await Request("POST", route.Path, "synthetic-read-token", """{}""")).Status == 403 && calls == 0, "extension read-only mutation denied before callback");
        var ok = await Request("POST", route.Path, "synthetic-admin-token", """{"value":"synthetic-value"}""");
        check(ok.Status == 200 && calls == 1 && JsonDocument.Parse(ok.Body).RootElement.GetProperty("echoed").GetString() == "synthetic-value", "frozen registered route dispatches typed input");
        check(ok.Headers["Cache-Control"] == "no-store" && ok.Headers.ContainsKey("X-Request-Id"), "extension preserves transport headers");
        foreach (var malformed in new[] { """{"value":"a","value":"b"}""", """{"value":"a","extra":1}""", """{"Value":"a"}""", """{}""", "null" })
            check((await Request("POST", route.Path, "synthetic-admin-token", malformed)).Status == 400, "extension strict JSON rejects invalid shape");
        check((await Request("POST", route.Path, "synthetic-admin-token", new string('x', ZbaContract.MaxBodyBytes + 1))).Status == 413 && calls == 1, "extension body limit precedes callback effects");
        check((await Request("POST", route.Path, "synthetic-admin-token", """{}""", "unexpected=1")).Status == 400 && calls == 1, "extension rejects undeclared query");
        var wrongMethod = await Request("GET", route.Path, "synthetic-admin-token");
        check(wrongMethod.Status == 405 && wrongMethod.Headers["Allow"] == "POST", "405 derives from same extension catalog");
        var meta = JsonDocument.Parse((await Request("GET", "/zba/v1/meta", "synthetic-read-token")).Body);
        check(meta.RootElement.GetProperty("features").EnumerateArray().Any(x => x.GetString() == route.OperationId), "meta discovers generic registered operation");
        var api = JsonDocument.Parse((await Request("GET", "/zba/v1/openapi.json", "synthetic-read-token")).Body);
        check(api.RootElement.GetProperty("paths").GetProperty(route.Path).GetProperty("post").GetProperty("x-zba-scope").GetString() == "admin"
            && api.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty("SyntheticInput", out _), "OpenAPI derives route authorization and schema from registration");
        var absent = Create();
        check(!absent.Routes.Any(r => r.OperationId.StartsWith("quotas.", StringComparison.Ordinal)), "generic panel has no implicit quota route");
        var missing = await absent.ProcessAsync(new("POST", route.Path, "", new Dictionary<string, string>(), [], "203.0.113.20"));
        check(missing.Status == 404, "unregistered extension is absent");

        description = description with { Routes = new[] { route } };
        void Reject(IEnumerable<PluginHttpDescription> descriptions, string label)
        {
            try { Create(descriptions.Select(Bind).ToArray()); check(false, label); }
            catch (InvalidOperationException error) { check(error.Message == "zba_contribution_invalid", label); }
        }
        Reject([description, description], "duplicate owner rejected");
        Reject([description with { Routes = new[] { route, route } }], "duplicate method/path/operation rejected");
        Reject([description with { Routes = new[] { route with { Path = "/zba/v1/health" } } }], "shell literal route cannot be overridden");
        Reject([description with { Routes = new[] { route with { Path = "/zba/v1/tasks/synthetic" } } }], "shell parameter route cannot be shadowed");
        Reject([description with { Routes = new[] { route with { OperationId = "health" } } }], "shell operation cannot be overridden");
        Reject([description with { Routes = new[] { route with { OperationId = "tasks.receipts" } } }], "shell feature cannot be impersonated");
        Reject([description with { SchemasJson = """{"ZbaHealth":{}}""" }], "shell schema cannot be overridden");
        Reject([description with { SchemasJson = """{"SyntheticInput":{},"SyntheticInput":{},"SyntheticOutput":{}}""" }], "duplicate schema JSON key rejected");
        Reject([description with { SchemasJson = """{"SyntheticInput":{"$ref":"https://example.com/schema"},"SyntheticOutput":{}}""" }], "remote schema references rejected");
        Reject([description with { SchemasJson = """{"SyntheticInput":{"$ref":"#/components/schemas/Missing"},"SyntheticOutput":{}}""" }], "unresolved local schema references rejected");
        Reject([description with { Routes = new[] { route with { ResponseSchema = "Missing" } } }], "unresolved response schema rejected");
        foreach (var path in new[] { "/api/synthetic", "/zba/v1/../health", "/zba/v1/%68ealth", "/zba/v1/a//b", "/zba/v1/{id}" })
            Reject([description with { Routes = new[] { route with { Path = path } } }], "unsupported or ambiguous route rejected");
        Reject([description with { Routes = new[] { route with { Method = "GET" } } }], "GET request body contract rejected");
        var other = description with { Owner = "synthetic.other", SchemasJson = """{}""", Routes = new[] { route with { Method = "PUT", OperationId = "synthetic.other" } } };
        Reject([description, other], "different owners cannot split one path");
    }
}

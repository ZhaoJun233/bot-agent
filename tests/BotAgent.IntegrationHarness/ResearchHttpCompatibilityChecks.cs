using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static async Task CheckResearchHttpCompatibilityAsync(HttpClient authenticated, int panelPort,
        MockOpenAi model, MockSearchHost pages, CancellationToken ct)
    {
        var endpoint = $"http://127.0.0.1:{panelPort}/api/search/test";
        var observations = new JsonArray();
        var before = (model.GroundingRequests, pages.SearxHits, pages.WikiHits, pages.PageHits);
        async Task Observe(string name, HttpClient client, HttpMethod method, string? body, int? expectedStatus, string? origin = null)
        {
            using var request = new HttpRequestMessage(method, endpoint);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (origin is not null) request.Headers.Add("Origin", origin);
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            observations.Add(new JsonObject { ["case"] = name, ["status"] = (int)response.StatusCode,
                ["contentType"] = response.Content.Headers.ContentType?.ToString(), ["body"] = text });
            if (expectedStatus is { } expected)
                Check("研究 HTTP 兼容：" + name, (int)response.StatusCode == expected, $"status={(int)response.StatusCode}");
        }
        using var anonymous = new HttpClient();
        await Observe("authentication", anonymous, HttpMethod.Post, "{\"query\":\"synthetic\"}", 401);
        await Observe("origin", authenticated, HttpMethod.Post, "{\"query\":\"synthetic\"}", 403, "https://example.com");
        await Observe("method", authenticated, HttpMethod.Get, null, 405);
        await Observe("malformed", authenticated, HttpMethod.Post, "{", 400);
        await Observe("empty", authenticated, HttpMethod.Post, "", 400);
        await Observe("null", authenticated, HttpMethod.Post, "null", 400);
        await Observe("missing", authenticated, HttpMethod.Post, "{}", 400);
        await Observe("whitespace", authenticated, HttpMethod.Post, """{"query":"  ","url":" ","ignored":true}""", 400);
        // Record the actual legacy global-exception response; do not invent a cleaner status here.
        await Observe("numeric-query", authenticated, HttpMethod.Post, "{\"query\":1}", null);
        await Observe("array", authenticated, HttpMethod.Post, "[]", null);
        await Observe("scalar", authenticated, HttpMethod.Post, "true", null);
        Check("研究 HTTP 无效请求及授权拒绝不调用诊断资源",
            before == (model.GroundingRequests, pages.SearxHits, pages.WikiHits, pages.PageHits));
        await Observe("url-priority", authenticated, HttpMethod.Post, new JsonObject
        { ["query"] = "ignored synthetic query", ["url"] = "  " + pages.PageUrl + "  ", ["ignored"] = true }.ToJsonString(), 200);
        Check("研究 HTTP 同时提供 query/url 只读取页面", model.GroundingRequests == before.Item1
            && pages.SearxHits == before.Item2 && pages.WikiHits == before.Item3 && pages.PageHits == before.Item4 + 1);
        var priority = JsonNode.Parse(observations[^1]!["body"]!.GetValue<string>())!.AsObject();
        Check("研究 HTTP 读取响应保持全部字段及 null", priority.Count == 4 && priority["mode"]!.GetValue<string>() == "read"
            && priority["ok"]!.GetValue<bool>() && priority.ContainsKey("text") && priority.ContainsKey("error") && priority["error"] is null);
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_RESEARCH_HTTP_BASELINE") is { Length: > 0 } baseline)
            Check("研究 HTTP 与原宿主逐项响应相同", JsonNode.DeepEquals(observations, JsonNode.Parse(await File.ReadAllTextAsync(baseline, ct))));
        if (Environment.GetEnvironmentVariable("BOTAGENT_IT_RESEARCH_HTTP_EVIDENCE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "observations.json"),
                observations.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);
        }
    }
}

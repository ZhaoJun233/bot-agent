using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Services.Resilience;

internal static class CacheBoundaryTransportChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        const string stable = "Synthetic stable policy.";
        const string dynamic = " Current synthetic time: later.";
        const string success = """{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":12,"output_tokens":2}}""";
        foreach (var mode in new[] { "transient", "reasoning", "empty-image" })
        {
            var payload = new JsonObject
            {
                ["model"] = "synthetic-model", ["max_tokens"] = 64, ["reasoning_effort"] = "low",
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = stable + dynamic },
                    new JsonObject { ["role"] = "user", ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "Synthetic question" },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AA==" } }
                    } }
                }
            };
            var original = payload.ToJsonString();
            var calls = 0;
            var allBoundaries = true;
            var http = new FakeHttp(async (request, token) =>
            {
                calls++;
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                var blocks = body["system"]!.AsArray();
                allBoundaries &= blocks.Count == 2 && blocks[0]?["text"]?.GetValue<string>() == stable
                    && blocks[0]?["cache_control"]?["type"]?.GetValue<string>() == "ephemeral"
                    && blocks[1]?["text"]?.GetValue<string>() == dynamic && blocks[1]?["cache_control"] is null
                    && !body["messages"]!.ToJsonString().Contains("cache_control", StringComparison.Ordinal);
                var status = HttpStatusCode.OK;
                var text = success;
                if (mode == "transient" && calls == 1) { status = HttpStatusCode.ServiceUnavailable; text = "{}"; }
                if (mode == "reasoning" && calls == 1) { status = HttpStatusCode.BadRequest; text = """{"error":"reasoning unsupported"}"""; }
                if (mode == "empty-image" && calls < 3) text = """{"content":[],"usage":{}}""";
                return new HttpResponseMessage(status) { Content = new StringContent(text) };
            });
            var transport = new ModelTransport(() => new ModelOptions(), http, http, new ImageDownloader(http), http.Timeout);
            var route = new ModelProviderRoute("synthetic", "https://example.com/v1", "synthetic-model", "synthetic-key",
                ModelProtocol.AnthropicMessages, new ModelProtocolOptions(PromptCache: true));
            var built = new BuiltRequest(payload, 1, [10001]) { StableSystemPrefixLength = stable.Length };
            var (samePayload, imageCount, imageIds) = built;
            check(ReferenceEquals(samePayload, payload) && imageCount == 1 && imageIds.Count == 1, "cache metadata preserves three-field deconstruction");
            var outcome = await transport.SendAsync(built, route, CancellationToken.None);
            check(payload.ToJsonString() == original, mode + " leaves original request payload unchanged");
            check(ModelJson.HasChoices(outcome.Json) && allBoundaries, mode + " retains stable-only breakpoint on every send");
            check(calls == (mode == "empty-image" ? 3 : 2), mode + " keeps existing bounded retry behavior");
        }

        // —— 真实 OpenAiClient -> ModelTransport -> ProviderFailoverRunner 链路 ——
        // 第一 Provider 不可用、第二 Provider 成功：备用请求仍只带 stable 断点，
        // 两段拼接等于提示词原文；改时钟后 stable 前缀逐字不变、只有动态尾巴移动。
        var failoverBodies = new List<JsonObject>();
        var primaryAttempts = 0;
        var failoverHttp = new FakeHttp(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (request.RequestUri?.Host == "primary.example.com")
            {
                primaryAttempts++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            }
            failoverBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(success) };
        });
        var plainBodies = new List<JsonObject>();
        var plainHttp = new FakeHttp(async (request, token) =>
        {
            plainBodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"reply\":\"Synthetic routed reply\"}"}}],"usage":{"prompt_tokens":8,"completion_tokens":2}}""")
            };
        });
        var routedOptions = new ModelOptions
        {
            ApiKey = "synthetic-key", BaseUrl = "https://primary.example.com/v1", Model = "synthetic-model",
            EnableAtmosphereDamping = false
        };
        var plainOptions = new ModelOptions
        {
            ApiKey = "synthetic-key", BaseUrl = "https://plain.example.com/v1", Model = "synthetic-model",
            EnableAtmosphereDamping = false
        };
        var routedImages = new ImageDownloader(failoverHttp);
        var routedTransport = new ModelTransport(() => routedOptions, failoverHttp, failoverHttp, routedImages, failoverHttp.Timeout);
        var routes = new Dictionary<string, ModelProviderRoute>(StringComparer.Ordinal)
        {
            ["primary"] = new ModelProviderRoute("primary", "https://primary.example.com/v1", "primary-model", "synthetic-key",
                ModelProtocol.AnthropicMessages, new ModelProtocolOptions(PromptCache: true)),
            ["secondary"] = new ModelProviderRoute("secondary", "https://secondary.example.com/v1", "secondary-model", "synthetic-key",
                ModelProtocol.AnthropicMessages, new ModelProtocolOptions(PromptCache: true))
        };
        var failover = new ProviderFailoverRunner(
            [new ProviderCandidate("primary", 0), new ProviderCandidate("secondary", 1)],
            clock: () => PromptCacheChecks.ProbeClock.Now);
        var routedClient = new OpenAiClient(() => routedOptions, failoverHttp, failoverHttp, routedImages, routedTransport,
            failover, routes, id => routes.TryGetValue(id, out var route) ? route : null);
        var plainImages = new ImageDownloader(plainHttp);
        var plainTransport = new ModelTransport(() => plainOptions, plainHttp, plainHttp, plainImages, plainHttp.Timeout);
        var plainClient = new OpenAiClient(() => plainOptions, plainHttp, plainHttp, plainImages, plainTransport);
        var routedWindow = new[]
        {
            new ChatMessage { Role = MessageRole.Peer, Text = "Synthetic routed question", SenderId = 10001, Timestamp = DateTimeOffset.UnixEpoch }
        };
        var savedClock = PromptCacheChecks.ProbeClock.CurrentTime;
        var firstTime = DateTimeOffset.UnixEpoch.AddHours(8);
        PromptCacheChecks.ProbeClock.CurrentTime = firstTime;
        var routedReply = await routedClient.CompleteAsync(routedWindow, promptCacheScope: "group:10001");
        _ = await plainClient.CompleteAsync(routedWindow);
        var firstBody = failoverBodies.Count > 0 ? failoverBodies[0] : null;
        var firstStable = SystemBlockText(firstBody, 0);
        var firstDynamic = SystemBlockText(firstBody, 1);
        var firstPlainSystem = PlainSystemText(plainBodies, 0);
        check(primaryAttempts > 0 && failoverBodies.Count == 1 && routedReply.FallbackHops == 1,
            "client failover: unavailable primary is skipped, backup provider answers in one hop");
        check(firstBody is { } firstObject && firstObject["model"]?.GetValue<string>() == "secondary-model",
            "client failover: fallback request carries the backup route model name");
        check(IsStableOnlyBreakpoint(firstBody),
            "client failover: fallback request keeps only the stable system breakpoint");
        check(firstStable is not null && firstDynamic is not null && firstPlainSystem is not null
            && string.Concat(firstStable, firstDynamic) == firstPlainSystem,
            "client failover: split blocks concatenate to the original system prompt");

        PromptCacheChecks.ProbeClock.CurrentTime = firstTime.AddHours(2);
        var changedReply = await routedClient.CompleteAsync(routedWindow, promptCacheScope: "group:10001");
        _ = await plainClient.CompleteAsync(routedWindow);
        PromptCacheChecks.ProbeClock.CurrentTime = savedClock;
        var secondBody = failoverBodies.Count > 1 ? failoverBodies[1] : null;
        var secondStable = SystemBlockText(secondBody, 0);
        var secondDynamic = SystemBlockText(secondBody, 1);
        var secondPlainSystem = PlainSystemText(plainBodies, 1);
        check(IsStableOnlyBreakpoint(secondBody) && changedReply.FallbackHops == 1,
            "client failover: changed clock still resolves through the backup with a stable-only breakpoint");
        check(secondStable is not null && secondStable == firstStable
            && secondDynamic is not null && secondDynamic != firstDynamic,
            "client failover: changed clock keeps the stable prefix byte-identical");
        check(secondStable is not null && secondDynamic is not null && secondPlainSystem is not null
            && string.Concat(secondStable, secondDynamic) == secondPlainSystem,
            "client failover: changed clock only moves the dynamic tail");
    }

    private static bool IsStableOnlyBreakpoint(JsonObject? body)
    {
        if (body is null || body["system"] is not JsonArray system || system.Count != 2) return false;
        if (system[0]?["cache_control"]?["type"]?.GetValue<string>() != "ephemeral") return false;
        if (system[1]?["cache_control"] is not null) return false;
        if (body["messages"] is not JsonNode messages) return false;
        return !messages.ToJsonString().Contains("cache_control", StringComparison.Ordinal)
            && !body.ContainsKey("prompt_cache_key");
    }

    private static string? SystemBlockText(JsonObject? body, int index)
    {
        if (body?["system"] is not JsonArray blocks || index < 0 || index >= blocks.Count) return null;
        return blocks[index]?["text"]?.GetValue<string>();
    }

    private static string? PlainSystemText(List<JsonObject> bodies, int index)
    {
        if (index < 0 || index >= bodies.Count) return null;
        if (bodies[index]["messages"] is not JsonArray messages || messages.Count == 0) return null;
        return messages[0]?["content"]?.GetValue<string>();
    }
}

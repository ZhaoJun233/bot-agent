using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Model.Protocols;

/// <summary>
/// #138 ① 供应商缓存控制（默认关）的线上形状断言：
///   ① 关闭时请求体与改动前逐字一致（含「payload 里已经有键、但开关关/协议不支持」这条收口路径）；
///   ② 打开时 OpenAI 兼容只多一个 32 位小写 hex 的 prompt_cache_key，且键稳定、不可反推会话 key；
///   ③ 打开时 Anthropic 只在 system 末尾与最后一条消息末尾打 cache_control，顶层不带 prompt_cache_key；
///   ④ Gemini / Ollama 永远不带这两类字段。
/// 全部用合成数据，不碰真实会话。
/// </summary>
internal static class PromptCacheChecks
{
    private const string Scope = "group:10001";
    private const string OtherScope = "group:10002";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var http = new FakeHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var transport = new ModelTransport(() => new ModelOptions(), http, http, new ImageDownloader(http), http.Timeout);
        var window = new List<ChatMessage> { new() { Role = MessageRole.Peer, Text = "Synthetic prompt", Timestamp = DateTimeOffset.UnixEpoch } };
        var plain = await transport.BuildAsync(window, "Synthetic system", [], CancellationToken.None);
        var scoped = await transport.BuildAsync(window, "Synthetic system", [], CancellationToken.None, promptCacheScope: Scope);
        var other = await transport.BuildAsync(window, "Synthetic system", [], CancellationToken.None, promptCacheScope: OtherScope);
        var scopedAgain = await transport.BuildAsync(window, "Synthetic system", [], CancellationToken.None, promptCacheScope: Scope);

        // —— 哈希键本身：只在传了作用域时出现，稳定、不可反推 ——
        check(plain.Payload["prompt_cache_key"] is null, "no scope: payload carries no prompt_cache_key");
        var key = scoped.Payload["prompt_cache_key"]?.GetValue<string>();
        check(key is { Length: 32 } && key.All(c => c is >= (char)48 and <= (char)57 or >= (char)97 and <= (char)102),
            "scope: payload prompt_cache_key is 32 lowercase hex");
        check(scopedAgain.Payload["prompt_cache_key"]?.GetValue<string>() == key, "same scope: stable prompt_cache_key");
        var otherKey = other.Payload["prompt_cache_key"]?.GetValue<string>();
        check(otherKey is not null && otherKey != key, "different scope: different prompt_cache_key");
        check(!key!.Contains("10001", StringComparison.Ordinal) && !key.Contains("group", StringComparison.Ordinal), "prompt_cache_key never contains the raw scope");

        // —— a) 默认关：即使 payload 里已经有关键，线上请求体也必须与「没有键」时逐字一致 ——
        var protocols = Enum.GetValues<ModelProtocol>();
        foreach (var protocol in protocols)
        {
            var baseline = await BodyAsync(Route(protocol, Options(protocol, false)), plain.Payload);
            var offWithKey = await BodyAsync(Route(protocol, Options(protocol, false)), scoped.Payload);
            check(baseline == offWithKey, "prompt cache off: body byte-identical (" + protocol + ")");
            check(!offWithKey.Contains("prompt_cache_key", StringComparison.Ordinal), "prompt cache off: no cache key leaks into the body (" + protocol + ")");
            check(!offWithKey.Contains("cache_control", StringComparison.Ordinal), "prompt cache off: no cache_control (" + protocol + ")");
        }

        // Anthropic 关闭时 messages content 仍是原来的 block 形状（这次改动不碰它）
        var anthOff = JsonNode.Parse(await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, false)), plain.Payload))!.AsObject();
        check(anthOff["system"] is JsonArray offSystem && offSystem.Count > 0, "anthropic off: system stays the original array");
        check(anthOff["messages"] is JsonArray offMessages && offMessages.Count > 0 && offMessages[^1]?["content"] is JsonArray offBlocks && offBlocks.Count > 0,
            "anthropic off: last message content stays block array");
        check(!anthOff.ToJsonString().Contains("cache_control", StringComparison.Ordinal), "anthropic off: no cache_control anywhere");

        // —— b) 开启 + OpenAI 兼容（chat / responses / azure）：键上线、同 scope 同值、不可反推 ——
        foreach (var protocol in new[] { ModelProtocol.OpenAiChat, ModelProtocol.OpenAiResponses, ModelProtocol.AzureOpenAi })
        {
            var on = JsonNode.Parse(await BodyAsync(Route(protocol, Options(protocol, true)), scoped.Payload))!.AsObject();
            check(on["prompt_cache_key"]?.GetValue<string>() == key, "prompt cache on: prompt_cache_key online (" + protocol + ")");
            var onText = on.ToJsonString();
            check(!onText.Contains(Scope, StringComparison.Ordinal) && !onText.Contains("10001", StringComparison.Ordinal),
                "prompt cache on: raw scope never reaches the wire (" + protocol + ")");
            var onOther = JsonNode.Parse(await BodyAsync(Route(protocol, Options(protocol, true)), other.Payload))!.AsObject();
            check(onOther["prompt_cache_key"]?.GetValue<string>() != key, "prompt cache on: different scope differs on the wire (" + protocol + ")");
        }

        // —— c) 开启 + Anthropic：两个断点带 cache_control，顶层没有 prompt_cache_key ——
        var anthOn = JsonNode.Parse(await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), scoped.Payload))!.AsObject();
        check(anthOn["prompt_cache_key"] is null, "anthropic on: no top-level prompt_cache_key");
        check(anthOn["system"] is JsonArray onSystem && onSystem.Count > 0 && onSystem[^1]?["cache_control"]?["type"]?.GetValue<string>() == "ephemeral",
            "anthropic on: system last block is ephemeral");
        check(anthOn["messages"] is JsonArray onMessages && onMessages.Count > 0 && onMessages[^1]?["content"] is JsonArray onBlocks && onBlocks.Count > 0
            && onBlocks[^1]?["cache_control"]?["type"]?.GetValue<string>() == "ephemeral",
            "anthropic on: last message last block is ephemeral");
        var marked = 0;
        foreach (var message in anthOn["messages"]!.AsArray())
        {
            if (message?["content"] is JsonArray content) marked += content.Count(block => block?["cache_control"] is not null);
        }
        check(marked == 1, "anthropic on: exactly one message breakpoint (got " + marked + ")");

        // —— d) 协议不支持：Gemini / Ollama 两种字段都不出现，且与关闭时逐字一致 ——
        foreach (var protocol in new[] { ModelProtocol.GeminiGenerateContent, ModelProtocol.Ollama })
        {
            var on = await BodyAsync(Route(protocol, Options(protocol, true)), scoped.Payload);
            var off = await BodyAsync(Route(protocol, Options(protocol, false)), scoped.Payload);
            check(!on.Contains("prompt_cache_key", StringComparison.Ordinal) && !on.Contains("cache_control", StringComparison.Ordinal),
                "unsupported protocol: cache fields stay offline (" + protocol + ")");
            check(on == off, "unsupported protocol: enabled body equals disabled body (" + protocol + ")");
        }
    }

    private static ModelProtocolOptions Options(ModelProtocol protocol, bool promptCache) => protocol switch
    {
        // Azure 的 URL 需要 deployment，所以即便比较「关闭」也带上它；两侧相同，差异只可能来自缓存键。
        ModelProtocol.AzureOpenAi => new ModelProtocolOptions(AzureDeployment: "deployment", PromptCache: promptCache),
        _ => new ModelProtocolOptions(PromptCache: promptCache)
    };

    private static ModelProviderRoute Route(ModelProtocol protocol, ModelProtocolOptions options)
        => new("synthetic", "https://example.com/v1", "example-model", protocol == ModelProtocol.Ollama ? "" : "synthetic-key", protocol, options);

    private static async Task<string> BodyAsync(ModelProviderRoute route, JsonObject payload)
    {
        using var request = ModelProtocolAdapter.CreateRequest(route, payload);
        return await request.Content!.ReadAsStringAsync();
    }
}
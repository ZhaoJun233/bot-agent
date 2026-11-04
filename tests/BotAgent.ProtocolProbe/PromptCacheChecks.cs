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
///   ④ Gemini / Ollama 永远不带这两类字段；
///   ⑤ 有合法首 system 边界时只标稳定前缀块，边界非法或首 system 非简单文本时 fail closed，无边界与关闭时线上字节不变。
/// 全部用合成数据，不碰真实会话。
/// </summary>
internal static class PromptCacheChecks
{
    private const string Scope = "group:10001";
    private const string OtherScope = "group:10002";

    /// <summary>Transport 重试退避用的假时钟。Probe 里 ModelClock 只能绑定一个实例，
    /// TransportChecks 必须复用同一实例初始化（ModelClock.cs:13-20）。</summary>
    internal static FakeClock ProbeClock { get; } = new();

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

        // —— e) 开启 + 首 system 是简单文本 + 合法边界：拆稳定/动态，只标稳定前缀块 ——
        const string stablePrefix = "Synthetic stable system prefix\n";
        const string dynamicSuffix = "Synthetic dynamic system suffix";
        var boundaryPayload = CachePayload(stablePrefix + dynamicSuffix);
        var boundaryBody = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), boundaryPayload, stablePrefix.Length);
        var boundary = Anthropic(boundaryBody);
        check(boundary["system"] is JsonArray boundarySystem && boundarySystem.Count == 2,
            "boundary split: first simple system becomes stable and dynamic text blocks");
        check(boundary["system"] is JsonArray boundaryBlocks
            && boundaryBlocks[0]?["type"]?.GetValue<string>() == "text"
            && boundaryBlocks[0]?["text"]?.GetValue<string>() == stablePrefix
            && boundaryBlocks[0]?["cache_control"]?["type"]?.GetValue<string>() == "ephemeral",
            "boundary split: stable block keeps the exact prefix and is the only system breakpoint");
        check(boundary["system"] is JsonArray boundaryDynamic
            && boundaryDynamic[1]?["text"]?.GetValue<string>() == dynamicSuffix
            && boundaryDynamic[1]?["cache_control"] is null,
            "boundary split: dynamic system block stays unmarked");
        check(CountCacheControl(boundaryBody) == 1, "boundary split: exactly one cache_control in the whole body");
        check(LastMessageBlockUnmarked(boundary), "boundary split: last user block is not marked");
        check(!boundaryBody.Contains("prompt_cache_key", StringComparison.Ordinal), "boundary split: anthropic body stays without prompt_cache_key");

        // —— f) 边界正好等于文本长度：只留一个稳定块，不产出空动态块 ——
        var exactPayload = CachePayload(stablePrefix);
        var exactBody = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), exactPayload, stablePrefix.Length);
        check(Anthropic(exactBody)["system"] is JsonArray exactSystem && exactSystem.Count == 1
            && exactSystem[0]?["text"]?.GetValue<string>() == stablePrefix
            && exactSystem[0]?["cache_control"]?["type"]?.GetValue<string>() == "ephemeral",
            "boundary at text end: single marked stable block, no empty dynamic block");
        check(CountCacheControl(exactBody) == 1, "boundary at text end: exactly one cache_control");

        // —— g) 非法边界（<=0 / 超过 UTF-16 长度）：fail closed，不标也不猜，body 与关闭时逐字一致 ——
        var anthropicOffBody = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, false)), boundaryPayload);
        foreach (var illegal in new[] { 0, -1, (stablePrefix + dynamicSuffix).Length + 1, int.MaxValue })
        {
            var rejected = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), boundaryPayload, illegal);
            check(rejected == anthropicOffBody && !rejected.Contains("cache_control", StringComparison.Ordinal),
                "illegal boundary fails closed without guessing (" + illegal + ")");
        }

        // —— h) 不传边界（null）：老的两断点形状原样保留 ——
        var legacyBody = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), boundaryPayload);
        var legacy = Anthropic(legacyBody);
        check(CountCacheControl(legacyBody) == 2
            && legacy["system"] is JsonArray legacySystem
            && legacySystem[^1]?["cache_control"]?["type"]?.GetValue<string>() == "ephemeral"
            && LastMessageBlockMarked(legacy),
            "no boundary: legacy two-breakpoint anthropic shape preserved");
        check(legacyBody == await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), boundaryPayload, null),
            "no boundary: explicit null matches the legacy overload byte for byte");

        // —— i) 关闭 + 传边界：任何协议都不受边界参数影响（off wire 完全兼容）——
        foreach (var protocol in Enum.GetValues<ModelProtocol>())
        {
            var off = await BodyAsync(Route(protocol, Options(protocol, false)), scoped.Payload);
            var offWithBoundary = await BodyAsync(Route(protocol, Options(protocol, false)), scoped.Payload, 7);
            check(off == offWithBoundary, "prompt cache off: boundary argument never changes the wire (" + protocol + ")");
        }

        // —— j) 首 system 不是简单文本：多 part / 带额外字段都不猜，fail closed ——
        var multiPart = CachePayload(new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "Synthetic stable " },
            new JsonObject { ["type"] = "text", ["text"] = "Synthetic dynamic" }));
        var multiPartBody = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), multiPart, "Synthetic stable ".Length);
        check(multiPartBody == await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, false)), multiPart)
            && !multiPartBody.Contains("cache_control", StringComparison.Ordinal),
            "multi-part first system fails closed without guessing");
        var enrichedPart = CachePayload(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Synthetic stable prefix", ["name"] = "synthetic-extra" }));
        var enrichedBody = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), enrichedPart, 8);
        check(enrichedBody == await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, false)), enrichedPart)
            && !enrichedBody.Contains("cache_control", StringComparison.Ordinal),
            "first system text block with extra fields fails closed");

        // —— k) 没有 system 消息：边界无处可用，fail closed ——
        var noSystem = CachePayloadWithoutSystem();
        var noSystemOn = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), noSystem, 7);
        var noSystemOff = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, false)), noSystem);
        check(noSystemOn == noSystemOff && !noSystemOn.Contains("cache_control", StringComparison.Ordinal),
            "boundary without a system message fails closed");

        // —— l) 边界按 UTF-16 字符数计（emoji 占两个 code unit），不是码点 ——
        const string utf16Text = "A\uD83D\uDE00BC";
        var utf16Body = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), CachePayload(utf16Text), 4);
        check(Anthropic(utf16Body)["system"] is JsonArray utf16System && utf16System.Count == 2
            && utf16System[0]?["text"]?.GetValue<string>() == "A\uD83D\uDE00B"
            && utf16System[1]?["text"]?.GetValue<string>() == "C",
            "boundary counts UTF-16 code units, not code points");

        // —— m) 多 system 消息：只拆第一条，后续 system 原样且不标记 ——
        var twoSystem = TwoSystemCachePayload(stablePrefix + dynamicSuffix, "Synthetic second system");
        var twoSystemBody = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), twoSystem, stablePrefix.Length);
        check(Anthropic(twoSystemBody)["system"] is JsonArray twoSystemBlocks && twoSystemBlocks.Count == 3
            && twoSystemBlocks[0]?["cache_control"]?["type"]?.GetValue<string>() == "ephemeral"
            && twoSystemBlocks[1]?["cache_control"] is null
            && twoSystemBlocks[2]?["text"]?.GetValue<string>() == "Synthetic second system"
            && twoSystemBlocks[2]?["cache_control"] is null,
            "only the first system message is split, later system messages stay unmarked");
        check(CountCacheControl(twoSystemBody) == 1, "multi-system split: exactly one cache_control");

        // —— n) OpenAI 兼容：边界参数被忽略，prompt_cache_key 仍按开关上线 ——
        foreach (var protocol in new[] { ModelProtocol.OpenAiChat, ModelProtocol.OpenAiResponses, ModelProtocol.AzureOpenAi })
        {
            var onWithoutBoundary = await BodyAsync(Route(protocol, Options(protocol, true)), scoped.Payload);
            var onWithBoundary = await BodyAsync(Route(protocol, Options(protocol, true)), scoped.Payload, 7);
            check(onWithBoundary == onWithoutBoundary, "openai-compatible: boundary argument never changes the wire (" + protocol + ")");
            check(onWithBoundary.Contains("prompt_cache_key", StringComparison.Ordinal), "openai-compatible: cache key still online with a boundary (" + protocol + ")");
        }

        // —— o) Gemini / Ollama：即使传了边界也保持完全离线 ——
        foreach (var protocol in new[] { ModelProtocol.GeminiGenerateContent, ModelProtocol.Ollama })
        {
            var off = await BodyAsync(Route(protocol, Options(protocol, false)), scoped.Payload);
            var onWithBoundary = await BodyAsync(Route(protocol, Options(protocol, true)), scoped.Payload, 7);
            check(onWithBoundary == off, "unsupported protocol: boundary argument stays offline (" + protocol + ")");
        }

        // —— p) stream 兼容：带边界时 stream 字段照旧由调用方决定，断点形状不变 ——
        var streamBody = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, true)), boundaryPayload, stablePrefix.Length, stream: true);
        var streamJson = Anthropic(streamBody);
        check(streamJson["stream"]?.GetValue<bool>() == true, "stream flag is still honored with a stable boundary");
        check(CountCacheControl(streamBody) == 1 && streamJson["system"]?.ToJsonString() == boundary["system"]?.ToJsonString(),
            "stream mode keeps the same boundary split shape");
        var offStream = await BodyAsync(Route(ModelProtocol.AnthropicMessages, Options(ModelProtocol.AnthropicMessages, false)), boundaryPayload, null, stream: true);
        check(!offStream.Contains("cache_control", StringComparison.Ordinal), "stream mode with cache off stays clean");

        // —— q) 真实 Transport 路径：边界元数据在重试/兜底/重建请求时不能丢（Lead 独占的 CacheBoundaryTransportChecks） ——
        // 该组会走 ModelTransport 的退避重试，退避读 ModelClock；本组先于 TransportChecks 运行，
        // 所以按同一实例约定兜底初始化（TransportChecks 复用 ProbeClock，ModelClock 不允许换绑）。
        if (ModelClockUnbound()) ModelClock.Initialize(ProbeClock);
        await CacheBoundaryTransportChecks.RunAsync(check);
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

    private static async Task<string> BodyAsync(ModelProviderRoute route, JsonObject payload, int? stableSystemPrefixLength)
    {
        using var request = ModelProtocolAdapter.CreateRequest(route, payload, stream: false, stableSystemPrefixLength: stableSystemPrefixLength);
        return await request.Content!.ReadAsStringAsync();
    }

    private static async Task<string> BodyAsync(ModelProviderRoute route, JsonObject payload, int? stableSystemPrefixLength, bool stream)
    {
        using var request = ModelProtocolAdapter.CreateRequest(route, payload, stream, stableSystemPrefixLength);
        return await request.Content!.ReadAsStringAsync();
    }

    private static JsonObject Anthropic(string body) => JsonNode.Parse(body)!.AsObject();

    private static int CountCacheControl(string body) => body.Split("cache_control", StringSplitOptions.None).Length - 1;

    private static bool LastMessageBlockMarked(JsonObject body)
        => body["messages"] is JsonArray messages && messages.Count > 0
            && messages[^1]?["content"] is JsonArray blocks && blocks.Count > 0
            && blocks[^1]?["cache_control"]?["type"]?.GetValue<string>() == "ephemeral";

    private static bool LastMessageBlockUnmarked(JsonObject body)
        => body["messages"] is JsonArray messages && messages.Count > 0
            && messages[^1]?["content"] is JsonArray blocks && blocks.Count > 0
            && blocks[^1]?["cache_control"] is null;

    private static JsonObject CachePayload(string systemText) => new()
    {
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = systemText },
            new JsonObject { ["role"] = "user", ["content"] = "Synthetic prompt" })
    };

    private static JsonObject CachePayload(JsonArray systemContent) => new()
    {
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = systemContent },
            new JsonObject { ["role"] = "user", ["content"] = "Synthetic prompt" })
    };

    private static JsonObject CachePayloadWithoutSystem() => new()
    {
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Synthetic prompt" })
    };

    private static JsonObject TwoSystemCachePayload(string first, string second) => new()
    {
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = first },
            new JsonObject { ["role"] = "system", ["content"] = second },
            new JsonObject { ["role"] = "user", ["content"] = "Synthetic prompt" })
    };

    private static bool ModelClockUnbound()
    {
        try { _ = ModelClock.Current; return false; }
        catch (InvalidOperationException) { return true; }
    }
}
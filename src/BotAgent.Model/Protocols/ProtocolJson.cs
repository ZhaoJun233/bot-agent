using System.Text.Json.Nodes;
namespace BotAgent.Model.Protocols;

internal static class ProtocolJson
{
    internal static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    internal static JsonArray Array(JsonNode? node) => node as JsonArray ?? new();
    internal static JsonNode Arguments(JsonNode? node) => node is JsonValue ? JsonNode.Parse(Text(node)) ?? new JsonObject() : node?.DeepClone() ?? new JsonObject();
    internal static JsonArray Parts(JsonNode? content) => content is JsonArray a ? (JsonArray)a.DeepClone()
        : content is null ? new() : new(new JsonObject { ["type"] = "text", ["text"] = Text(content) });
    internal static JsonObject Envelope(string text, JsonArray calls, JsonNode? usage = null, string? finish = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
        if (calls.Count > 0) message["tool_calls"] = calls;
        return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finish ?? (calls.Count > 0 ? "tool_calls" : "stop") }), ["usage"] = usage?.DeepClone() };
    }
    internal static JsonObject Call(string id, string name, JsonNode? args) => new() { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args is JsonValue ? Text(args) : args?.ToJsonString() ?? "{}" } };
    internal static (string Mime, string Data) DataImage(string url)
    {
        var comma = url.IndexOf(',');
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new ModelFault("model_image_format_unsupported");
        var mime = url[5..(comma - 7)];
        var data = url[(comma + 1)..];
        if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || data.Length == 0)
            throw new ModelFault("model_image_format_unsupported");
        return (mime, data);
    }
    internal static void Copy(JsonObject source, JsonObject target, params string[] names)
    { foreach (var name in names) if (source[name] is { } value) target[name] = value.DeepClone(); }
    /// <summary>
    /// 把各协议的 usage 归一到 OpenAI 形状。缓存侧（#98）**只在原字段真的存在时**才写：
    /// 缺字段 = 这个上游没提供 → 归一化结果里也没有该键 → 下游记「未知」；写成 0 会被误读成「明确没命中」。
    /// </summary>
    /// <param name="cacheRead">「已缓存输入」在源对象里的名字或点路径（如 <c>cache_read_input_tokens</c>、<c>input_tokens_details.cached_tokens</c>）。</param>
    /// <param name="cacheWrite">「缓存写入」在源对象里的名字或点路径。</param>
    internal static JsonObject Usage(JsonObject root, string input, string output, string? cacheRead = null, string? cacheWrite = null)
    {
        static bool TryTokens(JsonNode? node, out long tokens)
        {
            if (node is JsonValue value && value.TryGetValue<long>(out var raw))
            {
                tokens = Math.Max(0, raw);
                return true;
            }

            tokens = 0;
            return false;
        }

        static JsonNode? At(JsonObject source, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            JsonNode? node = source;
            foreach (var part in path.Split('.'))
            {
                node = node is JsonObject obj ? obj[part] : null;
                if (node is null)
                {
                    return null;
                }
            }

            return node;
        }

        var usage = new JsonObject { ["prompt_tokens"] = TryTokens(root[input], out var prompt) ? prompt : 0, ["completion_tokens"] = TryTokens(root[output], out var completion) ? completion : 0 };
        if (TryTokens(At(root, cacheRead), out var cached))
        {
            usage["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached };
        }

        if (TryTokens(At(root, cacheWrite), out var written))
        {
            usage["cache_write_tokens"] = written;
        }

        return usage;
    }

    internal static IEnumerable<JsonObject> Objects(JsonArray array)
    {
        foreach (var item in array)
            yield return item as JsonObject ?? throw new ModelFault("model_malformed_response");
    }
}

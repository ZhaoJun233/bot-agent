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
    internal static JsonObject Usage(JsonObject root, string input, string output)
    {
        static long Tokens(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var tokens) ? Math.Max(0, tokens) : 0;
        return new JsonObject { ["prompt_tokens"] = Tokens(root[input]), ["completion_tokens"] = Tokens(root[output]) };
    }

    internal static IEnumerable<JsonObject> Objects(JsonArray array)
    {
        foreach (var item in array)
            yield return item as JsonObject ?? throw new ModelFault("model_malformed_response");
    }
}

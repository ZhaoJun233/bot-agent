using System.Text;
using System.Text.Json.Nodes;
namespace BotAgent.Model.Protocols;

internal static class AnthropicProtocol
{
    internal static JsonObject Request(JsonObject chat)
    {
        var messages = new JsonArray(); var system = new JsonArray();
        foreach (var m in ProtocolJson.Array(chat["messages"]).OfType<JsonObject>())
        {
            var role = ProtocolJson.Text(m["role"]); var parts = new JsonArray();
            if (role is "system" or "developer") { foreach (var p in ProtocolJson.Parts(m["content"])) system.Add(p?.DeepClone()); continue; }
            if (role == "tool") parts.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m["tool_call_id"]?.DeepClone(), ["content"] = m["content"]?.DeepClone() });
            else foreach (var p in ProtocolJson.Parts(m["content"]).OfType<JsonObject>())
            {
                if (ProtocolJson.Text(p["type"]) == "text") { if (ProtocolJson.Text(p["text"]).Length > 0) parts.Add(p.DeepClone()); }
                else if (ProtocolJson.Text(p["type"]) == "image_url")
                {
                    var url = ProtocolJson.Text(p["image_url"]?["url"]); JsonObject source;
                    if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { var (mime, data) = ProtocolJson.DataImage(url); source = new() { ["type"] = "base64", ["media_type"] = mime, ["data"] = data }; }
                    else source = new() { ["type"] = "url", ["url"] = url };
                    parts.Add(new JsonObject { ["type"] = "image", ["source"] = source });
                }
                else throw new ModelFault("model_content_unsupported");
            }
            foreach (var call in ProtocolJson.Array(m["tool_calls"]).OfType<JsonObject>()) parts.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call["id"]?.DeepClone(), ["name"] = call["function"]?["name"]?.DeepClone(), ["input"] = ProtocolJson.Arguments(call["function"]?["arguments"]) });
            var mappedRole = role == "assistant" ? "assistant" : "user";
            if (messages.LastOrDefault() is JsonObject last && ProtocolJson.Text(last["role"]) == mappedRole)
                foreach (var part in parts) ((JsonArray)last["content"]!).Add(part?.DeepClone());
            else messages.Add(new JsonObject { ["role"] = mappedRole, ["content"] = parts });
        }
        var result = new JsonObject { ["messages"] = messages, ["max_tokens"] = chat["max_tokens"]?.DeepClone() ?? JsonValue.Create(2048), ["stream"] = false };
        if (system.Count > 0) result["system"] = system;
        ProtocolJson.Copy(chat, result, "model");
        // Messages models may reject temperature and top_p together; temperature is the primary control.
        ProtocolJson.Copy(chat, result, chat["temperature"] is null ? "top_p" : "temperature");
        if (result["temperature"] is JsonValue temperature && temperature.TryGetValue<double>(out var t))
            result["temperature"] = Math.Clamp(t, 0, 1);
        if (chat["tools"] is JsonArray tools) result["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["name"] = t?["function"]?["name"]?.DeepClone(), ["description"] = t?["function"]?["description"]?.DeepClone(), ["input_schema"] = t?["function"]?["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" } }).ToArray());
        if (chat["tool_choice"] is JsonObject choice) result["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = choice["function"]?["name"]?.DeepClone() };
        else if (chat["tool_choice"] is { } mode) result["tool_choice"] = new JsonObject { ["type"] = ProtocolJson.Text(mode) == "required" ? "any" : ProtocolJson.Text(mode) };
        return result;
    }
    internal static JsonObject Response(JsonObject root)
    {
        if (root["content"] is not JsonArray content) throw new ModelFault("model_malformed_response");
        var text = new StringBuilder(); var calls = new JsonArray();
        foreach (var part in ProtocolJson.Objects(content))
        {
            if (ProtocolJson.Text(part["type"]) == "text") text.Append(ProtocolJson.Text(part["text"]));
            if (ProtocolJson.Text(part["type"]) == "tool_use") calls.Add(ProtocolJson.Call(ProtocolJson.Text(part["id"]), ProtocolJson.Text(part["name"]), part["input"]));
        }
        if (content.Count == 0) return new JsonObject { ["choices"] = new JsonArray() };
        var usage = root["usage"] as JsonObject;
        return ProtocolJson.Envelope(text.ToString(), calls, usage is null ? null : ProtocolJson.Usage(usage, "input_tokens", "output_tokens"), ProtocolJson.Text(root["stop_reason"]) == "max_tokens" ? "length" : null);
    }
}

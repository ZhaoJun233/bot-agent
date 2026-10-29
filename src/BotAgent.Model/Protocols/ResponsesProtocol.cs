using System.Text;
using System.Text.Json.Nodes;
namespace BotAgent.Model.Protocols;

internal static class ResponsesProtocol
{
    internal static JsonObject Request(JsonObject chat)
    {
        var input = new JsonArray();
        foreach (var m in ProtocolJson.Array(chat["messages"]).OfType<JsonObject>())
        {
            var role = ProtocolJson.Text(m["role"]);
            if (role == "tool") { input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = m["tool_call_id"]?.DeepClone(), ["output"] = m["content"]?.DeepClone() }); continue; }
            var parts = new JsonArray();
            foreach (var p in ProtocolJson.Parts(m["content"]).OfType<JsonObject>())
            {
                if (ProtocolJson.Text(p["type"]) == "text")
                {
                    if (ProtocolJson.Text(p["text"]).Length > 0) parts.Add(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = p["text"]?.DeepClone() });
                }
                else if (ProtocolJson.Text(p["type"]) == "image_url") parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = p["image_url"]?["url"]?.DeepClone() });
                else throw new ModelFault("model_content_unsupported");
            }
            if (parts.Count > 0) input.Add(new JsonObject { ["role"] = role, ["content"] = parts });
            foreach (var call in ProtocolJson.Array(m["tool_calls"]).OfType<JsonObject>())
                input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call["id"]?.DeepClone(), ["name"] = call["function"]?["name"]?.DeepClone(), ["arguments"] = call["function"]?["arguments"]?.DeepClone() });
        }
        var result = new JsonObject { ["input"] = input, ["stream"] = false };
        ProtocolJson.Copy(chat, result, "model", "temperature", "top_p", "parallel_tool_calls");
        if ((chat["max_completion_tokens"] ?? chat["max_tokens"]) is { } max) result["max_output_tokens"] = max.DeepClone();
        if (chat["response_format"] is JsonObject format)
        {
            var mapped = ProtocolJson.Text(format["type"]) == "json_schema" && format["json_schema"] is JsonObject schema
                ? (JsonObject)schema.DeepClone() : (JsonObject)format.DeepClone();
            if (ProtocolJson.Text(format["type"]) == "json_schema") mapped["type"] = "json_schema";
            result["text"] = new JsonObject { ["format"] = mapped };
        }
        if (chat["reasoning_effort"] is { } effort) result["reasoning"] = new JsonObject { ["effort"] = effort.DeepClone() };
        if (chat["tools"] is JsonArray tools)
            result["tools"] = new JsonArray(tools.OfType<JsonObject>().Select(t => { var f = (JsonObject)t["function"]!.DeepClone(); f["type"] = "function"; return (JsonNode)f; }).ToArray());
        if (chat["tool_choice"] is JsonObject choice) result["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = choice["function"]?["name"]?.DeepClone() };
        else ProtocolJson.Copy(chat, result, "tool_choice");
        return result;
    }
    internal static JsonObject Response(JsonObject root)
    {
        if (ProtocolJson.Text(root["status"]) is "failed" or "cancelled") throw new ModelFault("model_upstream_error");
        if (root["output"] is not JsonArray output) throw new ModelFault("model_malformed_response");
        var text = new StringBuilder(); var calls = new JsonArray();
        foreach (var item in ProtocolJson.Objects(output))
        {
            if (ProtocolJson.Text(item["type"]) == "function_call") calls.Add(ProtocolJson.Call(ProtocolJson.Text(item["call_id"]), ProtocolJson.Text(item["name"]), item["arguments"]));
            foreach (var part in ProtocolJson.Objects(ProtocolJson.Array(item["content"]))) if (ProtocolJson.Text(part?["type"]) == "output_text") text.Append(ProtocolJson.Text(part?["text"]));
        }
        if (output.Count == 0) return new JsonObject { ["choices"] = new JsonArray() };
        var usage = root["usage"] as JsonObject;
        return ProtocolJson.Envelope(text.ToString(), calls, usage is null ? null : ProtocolJson.Usage(usage, "input_tokens", "output_tokens"), ProtocolJson.Text(root["status"]) == "incomplete" ? "length" : null);
    }
}

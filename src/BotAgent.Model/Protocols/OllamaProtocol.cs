using System.Text.Json.Nodes;
namespace BotAgent.Model.Protocols;

internal static class OllamaProtocol
{
    internal static JsonObject Request(JsonObject chat)
    {
        var messages = new JsonArray();
        var names = new Dictionary<string, string>();
        foreach (var m in ProtocolJson.Array(chat["messages"]).OfType<JsonObject>())
        {
            var mapped = (JsonObject)m.DeepClone(); var text = new System.Text.StringBuilder(); var images = new JsonArray();
            foreach (var p in ProtocolJson.Parts(m["content"]).OfType<JsonObject>())
            {
                if (ProtocolJson.Text(p["type"]) == "text") text.Append(ProtocolJson.Text(p["text"]));
                else if (ProtocolJson.Text(p["type"]) == "image_url") images.Add(ProtocolJson.DataImage(ProtocolJson.Text(p["image_url"]?["url"])).Data);
                else throw new ModelFault("model_content_unsupported");
            }
            mapped["content"] = text.ToString(); if (images.Count > 0) mapped["images"] = images;
            foreach (var call in ProtocolJson.Array(mapped["tool_calls"]).OfType<JsonObject>())
            {
                names[ProtocolJson.Text(call["id"])] = ProtocolJson.Text(call["function"]?["name"]);
                call["function"]!["arguments"] = ProtocolJson.Arguments(call["function"]?["arguments"]);
            }
            if (ProtocolJson.Text(m["role"]) == "tool") mapped["tool_name"] = names.GetValueOrDefault(ProtocolJson.Text(m["tool_call_id"]), ProtocolJson.Text(m["name"]));
            messages.Add(mapped);
        }
        var options = new JsonObject(); ProtocolJson.Copy(chat, options, "temperature", "top_p");
        if (chat["max_tokens"] is { } max) options["num_predict"] = max.DeepClone();
        var result = new JsonObject { ["messages"] = messages, ["options"] = options, ["stream"] = false };
        ProtocolJson.Copy(chat, result, "model", "tools"); return result;
    }
    internal static JsonObject Response(JsonObject root)
    {
        if (root["message"] is not JsonObject message) throw new ModelFault("model_malformed_response");
        var calls = new JsonArray();
        foreach (var call in ProtocolJson.Array(message["tool_calls"]).OfType<JsonObject>()) calls.Add(ProtocolJson.Call(ProtocolJson.Text(call["id"]) is { Length: > 0 } id ? id : "call_" + calls.Count, ProtocolJson.Text(call["function"]?["name"]), call["function"]?["arguments"]));
        return ProtocolJson.Envelope(ProtocolJson.Text(message["content"]), calls, ProtocolJson.Usage(root, "prompt_eval_count", "eval_count"), ProtocolJson.Text(root["done_reason"]) == "length" ? "length" : null);
    }
}

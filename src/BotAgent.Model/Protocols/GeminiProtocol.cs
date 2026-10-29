using System.Text;
using System.Text.Json.Nodes;
namespace BotAgent.Model.Protocols;

internal static class GeminiProtocol
{
    internal static JsonObject Request(JsonObject chat)
    {
        var contents = new JsonArray(); var system = new JsonArray(); var names = new Dictionary<string, string>();
        foreach (var m in ProtocolJson.Array(chat["messages"]).OfType<JsonObject>())
        {
            var role = ProtocolJson.Text(m["role"]); var parts = new JsonArray();
            if (role == "tool")
            {
                var id = ProtocolJson.Text(m["tool_call_id"]);
                parts.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = names.GetValueOrDefault(id, ProtocolJson.Text(m["name"])), ["response"] = new JsonObject { ["result"] = m["content"]?.DeepClone() } } });
            }
            else foreach (var p in ProtocolJson.Parts(m["content"]).OfType<JsonObject>())
            {
                if (ProtocolJson.Text(p["type"]) == "text") { if (ProtocolJson.Text(p["text"]).Length > 0) parts.Add(new JsonObject { ["text"] = p["text"]?.DeepClone() }); }
                else if (ProtocolJson.Text(p["type"]) == "image_url")
                {
                    var url = ProtocolJson.Text(p["image_url"]?["url"]);
                    var (mime, data) = ProtocolJson.DataImage(url);
                    parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = data } });
                }
                else throw new ModelFault("model_content_unsupported");
            }
            foreach (var call in ProtocolJson.Array(m["tool_calls"]).OfType<JsonObject>())
            {
                var name = ProtocolJson.Text(call["function"]?["name"]); names[ProtocolJson.Text(call["id"])] = name;
                var functionPart = new JsonObject { ["functionCall"] = new JsonObject { ["name"] = name, ["args"] = ProtocolJson.Arguments(call["function"]?["arguments"]) } };
                if (call["extra_content"]?["google"]?["thought_signature"] is { } signature)
                    functionPart["thoughtSignature"] = signature.DeepClone();
                parts.Add(functionPart);
            }
            if (role is "system" or "developer") { foreach (var p in parts) system.Add(p?.DeepClone()); }
            else
            {
                var mappedRole = role == "assistant" ? "model" : "user";
                if (contents.LastOrDefault() is JsonObject last && ProtocolJson.Text(last["role"]) == mappedRole)
                    foreach (var part in parts) ((JsonArray)last["parts"]!).Add(part?.DeepClone());
                else contents.Add(new JsonObject { ["role"] = mappedRole, ["parts"] = parts });
            }
        }
        var config = new JsonObject();
        if (chat["max_tokens"] is { } max) config["maxOutputTokens"] = max.DeepClone();
        if (chat["temperature"] is { } temp) config["temperature"] = temp.DeepClone();
        if (chat["top_p"] is { } top) config["topP"] = top.DeepClone();
        var result = new JsonObject { ["contents"] = contents, ["generationConfig"] = config };
        if (system.Count > 0) result["systemInstruction"] = new JsonObject { ["parts"] = system };
        if (chat["tools"] is JsonArray tools)
        {
            var declarations = new JsonArray();
            foreach (var tool in tools.OfType<JsonObject>())
            {
                var declaration = new JsonObject();
                ProtocolJson.Copy(tool["function"]!.AsObject(), declaration, "name", "description", "parameters");
                declarations.Add(declaration);
            }
            result["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        }
        if (chat["tool_choice"] is { } choice)
        {
            var mode = choice is JsonObject ? "ANY" : ProtocolJson.Text(choice) switch { "required" => "ANY", "none" => "NONE", _ => "AUTO" };
            var callConfig = new JsonObject { ["mode"] = mode };
            if (choice is JsonObject) callConfig["allowedFunctionNames"] = new JsonArray(choice["function"]?["name"]?.DeepClone());
            result["toolConfig"] = new JsonObject { ["functionCallingConfig"] = callConfig };
        }
        return result;
    }
    internal static JsonObject Response(JsonObject root)
    {
        if (root["candidates"] is not JsonArray candidates)
        {
            if (root["promptFeedback"] is not null) return new JsonObject { ["choices"] = new JsonArray() };
            throw new ModelFault("model_malformed_response");
        }
        if (candidates.Count == 0) return new JsonObject { ["choices"] = new JsonArray() };
        var candidate = candidates[0] as JsonObject ?? throw new ModelFault("model_malformed_response");
        if (ProtocolJson.Text(candidate["finishReason"]) is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII")
            return new JsonObject { ["choices"] = new JsonArray() };
        var text = new StringBuilder();
        var calls = new JsonArray();
        foreach (var part in ProtocolJson.Objects(ProtocolJson.Array(candidate?["content"]?["parts"])))
        {
            if (part["thought"]?.GetValue<bool>() != true) text.Append(ProtocolJson.Text(part["text"]));
            if (part["functionCall"] is JsonObject call)
            {
                var mapped = ProtocolJson.Call(ProtocolJson.Text(call["id"]) is { Length: > 0 } id ? id : "call_" + calls.Count, ProtocolJson.Text(call["name"]), call["args"]);
                if (part["thoughtSignature"] is { } signature)
                    mapped["extra_content"] = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = signature.DeepClone() } };
                calls.Add(mapped);
            }
        }
        var usage = root["usageMetadata"] as JsonObject;
        return ProtocolJson.Envelope(text.ToString(), calls, usage is null ? null : ProtocolJson.Usage(usage, "promptTokenCount", "candidatesTokenCount"), ProtocolJson.Text(candidate?["finishReason"]) == "MAX_TOKENS" ? "length" : null);
    }
}

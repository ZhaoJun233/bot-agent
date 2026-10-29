using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;

namespace BotAgent.Model.Protocols;

/// <summary>Per-stream tool indexes must survive interleaved text and multiple tool events.</summary>
internal sealed class ProtocolStreamDecoder(ModelProtocol protocol)
{
    private readonly Dictionary<int, int> _toolIndexes = new();
    private int _toolCount;

    internal JsonObject Decode(string data)
    {
        try
        {
            var root = JsonNode.Parse(data) as JsonObject ?? throw new ModelFault("model_malformed_response");
            if (root["error"] is not null || ProtocolJson.Text(root["type"]) is "error" or "response.failed")
                throw new ModelFault("model_upstream_error");
            if (protocol is ModelProtocol.OpenAiChat or ModelProtocol.AzureOpenAi) return root;
            return DecodeNative(root);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            throw new ModelFault("model_malformed_response");
        }
    }

    private JsonObject DecodeNative(JsonObject root)
    {
        var delta = new JsonObject();
        string? finish = null;
        JsonObject? usage = null;
        switch (protocol)
        {
            case ModelProtocol.OpenAiResponses:
                Responses(root, delta);
                if (ProtocolJson.Text(root["type"]) == "response.completed") finish = Finish();
                if (ProtocolJson.Text(root["type"]) == "response.incomplete") finish = "length";
                if (root["response"]?["usage"] is JsonObject responseUsage)
                    usage = ProtocolJson.Usage(responseUsage, "input_tokens", "output_tokens");
                break;
            case ModelProtocol.AnthropicMessages:
                Anthropic(root, delta);
                if (root["delta"]?["stop_reason"] is { } reason)
                    finish = ProtocolJson.Text(reason) switch { "max_tokens" => "length", "tool_use" => "tool_calls", _ => "stop" };
                if ((root["usage"] ?? root["message"]?["usage"]) is JsonObject messageUsage)
                    usage = ProtocolJson.Usage(messageUsage, "input_tokens", "output_tokens");
                break;
            case ModelProtocol.GeminiGenerateContent:
            case ModelProtocol.Ollama:
                var envelope = protocol == ModelProtocol.Ollama ? OllamaProtocol.Response(root) : GeminiProtocol.Response(root);
                var choice = ProtocolJson.Array(envelope["choices"]).FirstOrDefault();
                if (choice?["message"] is JsonObject message)
                {
                    delta = (JsonObject)message.DeepClone();
                    IndexCompleteCalls(delta);
                }
                var candidate = ProtocolJson.Array(root["candidates"]).FirstOrDefault();
                if (root["done"]?.GetValue<bool>() == true || candidate?["finishReason"] is not null)
                    finish = ProtocolJson.Text(choice?["finish_reason"]) == "length" ? "length" : Finish();
                usage = envelope["usage"] as JsonObject;
                break;
        }
        var chunk = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
        };
        if (usage is not null) chunk["usage"] = usage.DeepClone();
        return chunk;
    }

    private void Responses(JsonObject root, JsonObject delta)
    {
        switch (ProtocolJson.Text(root["type"]))
        {
            case "response.output_text.delta":
                delta["content"] = root["delta"]?.DeepClone();
                break;
            case "response.output_item.added":
                if (ProtocolJson.Text(root["item"]?["type"]) == "function_call")
                    delta["tool_calls"] = Tool(root["output_index"], root["item"]?["call_id"], root["item"]?["name"], JsonValue.Create(""));
                break;
            case "response.function_call_arguments.delta":
                delta["tool_calls"] = Tool(root["output_index"], null, null, root["delta"]);
                break;
        }
    }

    private void Anthropic(JsonObject root, JsonObject delta)
    {
        if (ProtocolJson.Text(root["type"]) == "content_block_start")
        {
            var block = root["content_block"];
            if (ProtocolJson.Text(block?["type"]) == "tool_use")
                delta["tool_calls"] = Tool(root["index"], block?["id"], block?["name"], JsonValue.Create(""));
            else if (block?["text"] is { } text) delta["content"] = text.DeepClone();
        }
        if (ProtocolJson.Text(root["type"]) == "content_block_delta")
        {
            if (root["delta"]?["text"] is { } text) delta["content"] = text.DeepClone();
            if (root["delta"]?["partial_json"] is { } args) delta["tool_calls"] = Tool(root["index"], null, null, args);
        }
    }

    private JsonArray Tool(JsonNode? wireIndex, JsonNode? id, JsonNode? name, JsonNode? args)
    {
        var sourceIndex = wireIndex?.GetValue<int>() ?? 0;
        if (!_toolIndexes.TryGetValue(sourceIndex, out var index))
            _toolIndexes[sourceIndex] = index = _toolCount++;
        var function = new JsonObject();
        if (name is not null) function["name"] = name.DeepClone();
        if (args is not null) function["arguments"] = args.DeepClone();
        var call = new JsonObject { ["index"] = index, ["function"] = function };
        if (id is not null)
        {
            call["id"] = id.DeepClone();
            call["type"] = "function";
        }
        return new JsonArray(call);
    }

    private void IndexCompleteCalls(JsonObject delta)
    {
        foreach (var call in ProtocolJson.Objects(ProtocolJson.Array(delta["tool_calls"])))
        {
            var index = _toolCount++;
            call["index"] = index;
            // Native protocols can omit ids; generated ids must be unique across frames.
            if (ProtocolJson.Text(call["id"]).StartsWith("call_", StringComparison.Ordinal)) call["id"] = "call_" + index;
        }
    }

    private string Finish() => _toolCount > 0 ? "tool_calls" : "stop";
}

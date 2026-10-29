using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Model.Protocols;

internal static class StreamingChecks
{
    internal static async Task<List<JsonObject>> Decode(ModelProtocol protocol, params string[] events)
    {
        var wire = string.Join("\r\n\r\n", events.Select(e => protocol == ModelProtocol.Ollama ? e : "data: " + e));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        var chunks = new List<JsonObject>();
        await foreach (var chunk in ModelProtocolStreaming.ReadAsync(source, protocol)) chunks.Add(chunk);
        return chunks;
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var responses = await Decode(ModelProtocol.OpenAiResponses,
            """{"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","call_id":"call_0","name":"lookup"}}""",
            """{"type":"response.function_call_arguments.delta","output_index":2,"delta":"{}"}""",
            """{"type":"response.completed","response":{"usage":{"input_tokens":3,"output_tokens":4}}}""");
        check(responses[0]["choices"]![0]!["delta"]!["tool_calls"]![0]!["index"]!.GetValue<int>() == 0, "responses dense tool index");
        check(responses[1]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>() == "{}", "responses argument delta");
        check(responses[2]["choices"]![0]!["finish_reason"]!.GetValue<string>() == "tool_calls", "responses tool completion");
        check(responses[2]["usage"]!["prompt_tokens"]!.GetValue<long>() == 3, "responses stream usage");
        var anthropic = await Decode(ModelProtocol.AnthropicMessages,
            """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"call_0","name":"lookup","input":{}}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{}"}}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"}}""");
        check(anthropic[0]["choices"]![0]!["delta"]!["tool_calls"]![0]!["index"]!.GetValue<int>() == 0, "anthropic dense tool index");
        check(anthropic[1]["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>() == "{}", "anthropic argument delta");
        check(anthropic[2]["choices"]![0]!["finish_reason"]!.GetValue<string>() == "tool_calls", "anthropic tool finish");
        foreach (var protocol in new[] { ModelProtocol.GeminiGenerateContent, ModelProtocol.Ollama })
        {
            var wire = protocol == ModelProtocol.Ollama
                ? """{"message":{"tool_calls":[{"function":{"name":"lookup","arguments":{}}}]},"done":false}"""
                : """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"lookup","args":{}}}]}}]}""";
            var chunks = await Decode(protocol, wire, wire);
            check(chunks[1]["choices"]![0]!["delta"]!["tool_calls"]![0]!["index"]!.GetValue<int>() == 1, protocol + " tool indexes across frames");
            check(chunks[0]["choices"]![0]!["delta"]!["tool_calls"]![0]!["id"]!.GetValue<string>() != chunks[1]["choices"]![0]!["delta"]!["tool_calls"]![0]!["id"]!.GetValue<string>(), protocol + " unique generated ids");
        }
        var stopped = await Decode(ModelProtocol.OpenAiChat, "[DONE]", "invalid");
        check(stopped.Count == 0, "SSE DONE stops reading");
        try { await Decode(ModelProtocol.AnthropicMessages, """{"type":"error","error":{"message":"synthetic upstream detail"}}"""); check(false, "stream error fails"); }
        catch (ModelFault fault) { check(fault.ErrorCode == "model_upstream_error" && !fault.ToString().Contains("synthetic upstream detail"), "stream errors safe"); }
        try { await Decode(ModelProtocol.Ollama, "not json"); check(false, "malformed stream fails"); }
        catch (ModelFault fault) { check(fault.ErrorCode == "model_malformed_response", "malformed stream safe"); }
        try { await Decode(ModelProtocol.OpenAiChat, new string('x', 1024 * 1024 + 1)); check(false, "large event fails"); }
        catch (ModelFault fault) { check(fault.ErrorCode == "model_response_too_large", "stream event bounded"); }
        var multiLine = "data: {\rdata: \"type\":\"response.output_text.delta\",\rdata: \"delta\":\"OK\"}\r\r";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(multiLine));
        var text = "";
        await foreach (var chunk in ModelProtocolStreaming.ReadAsync(source, ModelProtocol.OpenAiResponses)) text += chunk["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
        check(text == "OK", "multiline CR-only SSE");
        check(source.CanRead, "stream ownership retained");
    }
}

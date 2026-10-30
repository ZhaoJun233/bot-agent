using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;
using BotAgent.Model.Protocols;

var count = 0;
await ReviewModelRegression.RunAsync(Check);
void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
var payload = JsonNode.Parse("""
{"model":"example-model","max_tokens":32,"messages":[{"role":"system","content":"Synthetic system"},{"role":"user","content":[{"type":"text","text":"Synthetic prompt"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AA=="}}]},{"role":"assistant","content":null,"tool_calls":[{"id":"call_0","type":"function","function":{"name":"lookup","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_0","content":"Synthetic result"}],"tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}]}
""")!.AsObject();
var original = payload.ToJsonString();
var replies = new Dictionary<ModelProtocol,string>
{
    [ModelProtocol.OpenAiChat] = """{"choices":[{"message":{"content":"OK","tool_calls":[{"id":"call_0","type":"function","function":{"name":"lookup","arguments":"{}"}}]}}]}""",
    [ModelProtocol.OpenAiResponses] = """{"output":[{"type":"message","content":[{"type":"output_text","text":"OK"}]},{"type":"function_call","call_id":"call_0","name":"lookup","arguments":"{}"}]}""",
    [ModelProtocol.AnthropicMessages] = """{"content":[{"type":"text","text":"OK"},{"type":"tool_use","id":"call_0","name":"lookup","input":{}}]}""",
    [ModelProtocol.GeminiGenerateContent] = """{"candidates":[{"content":{"parts":[{"text":"OK"},{"functionCall":{"name":"lookup","args":{}}}]}}]}""",
    [ModelProtocol.Ollama] = """{"message":{"content":"OK","tool_calls":[{"function":{"name":"lookup","arguments":{}}}]},"done":true}"""
};
replies[ModelProtocol.AzureOpenAi] = replies[ModelProtocol.OpenAiChat];
foreach (var protocol in Enum.GetValues<ModelProtocol>())
{
    var route = new ModelProviderRoute("synthetic", "https://example.com/v1", "example-model", protocol == ModelProtocol.Ollama ? "" : "synthetic-key", protocol);
    using var request = ModelProtocolAdapter.CreateRequest(route, payload);
    var body = await request.Content!.ReadAsStringAsync();
    Check(payload.ToJsonString() == original, protocol + " input immutable");
    Check(request.RequestUri!.Host == "example.com", protocol + " endpoint");
    Check(body.Contains("lookup"), protocol + " tool mapping");
    Check(body.Contains("AA=="), protocol + " image mapping");
    Check(body.Contains("Synthetic result"), protocol + " tool result mapping");
    var result = JsonNode.Parse(ModelProtocolAdapter.NormalizeResponse(protocol, replies[protocol]))!;
    Check(result["choices"]![0]!["message"]!["content"]!.GetValue<string>() == "OK", protocol + " response text");
    Check(result["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>() == "{}", protocol + " response tools");
    using var streaming = ModelProtocolAdapter.CreateRequest(route, payload, stream:true);
    Check(protocol == ModelProtocol.GeminiGenerateContent ? streaming.RequestUri!.ToString().Contains(":streamGenerateContent?alt=sse") : JsonNode.Parse(await streaming.Content!.ReadAsStringAsync())!["stream"]!.GetValue<bool>(), protocol + " stream request");
    Check(ModelProtocolStreaming.Capabilities(protocol).Streaming, protocol + " capability");
}
var streams = new Dictionary<ModelProtocol,string>
{
    [ModelProtocol.OpenAiChat] = "data: " + """{"choices":[{"delta":{"content":"OK"}}]}""",
    [ModelProtocol.OpenAiResponses] = "data: " + """{"type":"response.output_text.delta","delta":"OK"}""",
    [ModelProtocol.AnthropicMessages] = "data: " + """{"type":"content_block_delta","delta":{"text":"OK"}}""",
    [ModelProtocol.GeminiGenerateContent] = "data: " + """{"candidates":[{"content":{"parts":[{"text":"OK"}]}}]}""",
    [ModelProtocol.Ollama] = """{"message":{"content":"OK"},"done":false}"""
};
streams[ModelProtocol.AzureOpenAi] = streams[ModelProtocol.OpenAiChat];
foreach (var (protocol, wire) in streams)
{
    using var source = new MemoryStream(Encoding.UTF8.GetBytes(wire + Environment.NewLine + Environment.NewLine));
    var text = "";
    await foreach (var chunk in ModelProtocolStreaming.ReadAsync(source, protocol)) text += chunk["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
    Check(text == "OK", protocol + " streaming text");
}
using (var source = new MemoryStream(Encoding.UTF8.GetBytes(streams[ModelProtocol.Ollama])))
{
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    var cancelled = false;
    try { await foreach(var _ in ModelProtocolStreaming.ReadAsync(source, ModelProtocol.Ollama, cancel.Token)) {} } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "stream cancellation");
}
await EndpointChecks.RunAsync(Check, payload);
await StreamingChecks.RunAsync(Check);
await TransportChecks.RunAsync(Check, payload, replies);
await DiscoveryChecks.RunAsync(Check);
await TimeoutChecks.RunAsync(Check, payload);
Console.WriteLine($"PASS {count} synthetic protocol checks");

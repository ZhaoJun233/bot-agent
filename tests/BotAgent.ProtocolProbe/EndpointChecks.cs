using System.Text.Json.Nodes;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Model.Protocols;

internal static class EndpointChecks
{
    internal static async Task RunAsync(Action<bool, string> check, JsonObject payload)
    {
        var legacy = new ModelProviderRoute("synthetic", "https://example.com/v1", "example-model", "synthetic-key");
        var (_, _, _, key) = legacy;
        check(legacy.Protocol == ModelProtocol.OpenAiChat && key == "synthetic-key", "legacy route defaults and deconstruction");
        check(!legacy.ToString().Contains(key), "route diagnostics exclude credentials");
        var paths = new Dictionary<ModelProtocol, string>
        {
            [ModelProtocol.OpenAiChat] = "/v1/chat/completions",
            [ModelProtocol.OpenAiResponses] = "/v1/responses",
            [ModelProtocol.AnthropicMessages] = "/v1/messages",
            [ModelProtocol.GeminiGenerateContent] = "/v1/models/example-model:generateContent",
            [ModelProtocol.AzureOpenAi] = "/v1/openai/deployments/example-model/chat/completions",
            [ModelProtocol.Ollama] = "/v1/api/chat"
        };
        foreach (var (protocol, path) in paths)
        {
            var route = legacy with { Protocol = protocol };
            using var request = ModelProtocolAdapter.CreateRequest(route, payload);
            check(request.RequestUri!.AbsolutePath == path, protocol + " exact native endpoint");
            var header = protocol switch { ModelProtocol.AnthropicMessages => "x-api-key", ModelProtocol.GeminiGenerateContent => "x-goog-api-key", ModelProtocol.AzureOpenAi => "api-key", _ => "Authorization" };
            check(request.Headers.Contains(header), protocol + " native auth header");
            if (header != "Authorization") check(!request.Headers.Contains("Authorization"), protocol + " no bearer credential cross-talk");
            using var full = ModelProtocolAdapter.CreateRequest(route with { BaseUrl = "https://example.com" + path + "?synthetic=1" }, payload);
            check(full.RequestUri!.AbsolutePath == path && full.RequestUri.Query.Contains("synthetic=1"), protocol + " full endpoint query preservation");
            try { using var _ = ModelProtocolAdapter.CreateRequest(route with { ApiKey = "" }, payload); check(protocol == ModelProtocol.Ollama, protocol + " empty credential accepted only for Ollama"); }
            catch (ModelFault fault) { check(protocol != ModelProtocol.Ollama && fault.ErrorCode == "model_credential_missing", protocol + " credential required"); }
        }
        using var azure = ModelProtocolAdapter.CreateRequest(legacy with { BaseUrl = "https://example.com/openai", Protocol = ModelProtocol.AzureOpenAi, Options = new(AzureDeployment: "deployment", AzureApiVersion: "2024-10-21") }, payload);
        check(azure.RequestUri!.AbsolutePath == "/openai/deployments/deployment/chat/completions", "Azure resource prefix once");
        check(azure.RequestUri.Query == "?api-version=2024-10-21", "Azure configured API version");
        using var gemini = ModelProtocolAdapter.CreateRequest(legacy with { BaseUrl = "https://example.com/v1beta/models/example-model:generateContent?synthetic=1", Protocol = ModelProtocol.GeminiGenerateContent }, payload, true);
        check(gemini.RequestUri!.Query.Contains("synthetic=1") && gemini.RequestUri.Query.Contains("alt=sse"), "Gemini streaming preserves query");
        using var ollama = ModelProtocolAdapter.CreateRequest(legacy with { BaseUrl = "http://example.com/api", ApiKey = "", Protocol = ModelProtocol.Ollama }, payload);
        check(ollama.RequestUri!.AbsolutePath == "/api/chat" && !ollama.Headers.Contains("Authorization"), "Ollama api prefix and optional auth");
        var custom = (JsonObject)payload.DeepClone(); custom["temperature"] = 1.7; custom["top_p"] = 0.8;
        using var anthropic = ModelProtocolAdapter.CreateRequest(legacy with { Protocol = ModelProtocol.AnthropicMessages, Options = new(AnthropicVersion: "2023-06-01") }, custom);
        var anthropicBody = JsonNode.Parse(await anthropic.Content!.ReadAsStringAsync())!;
        check(anthropicBody["temperature"]!.GetValue<double>() == 1 && anthropicBody["top_p"] is null, "Anthropic native sampling range");
        check(anthropic.Headers.GetValues("anthropic-version").Single() == "2023-06-01", "Anthropic version header");
        foreach (var url in new[] { "file:///synthetic", "https://synthetic:secret@example.com/v1", "https://example.com/v1#synthetic" })
        {
            try { using var _ = ModelProtocolAdapter.CreateRequest(legacy with { BaseUrl = url }, payload); check(false, "unsafe endpoint rejected"); }
            catch (ModelFault fault) { check(fault.ErrorCode == "model_route_unavailable", "unsafe endpoint rejected"); }
        }
        var response = JsonNode.Parse(ModelProtocolAdapter.NormalizeResponse(ModelProtocol.GeminiGenerateContent,
            """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"lookup","args":{}},"thoughtSignature":"synthetic-signature"}]}}]}"""))!;
        var message = response["choices"]![0]!["message"]!.DeepClone();
        var continuation = new JsonObject { ["model"] = "example-model", ["messages"] = new JsonArray(message) };
        using var continued = ModelProtocolAdapter.CreateRequest(legacy with { Protocol = ModelProtocol.GeminiGenerateContent }, continuation);
        check((await continued.Content!.ReadAsStringAsync()).Contains("thoughtSignature"), "Gemini tool thought signature round-trip");
        var normalized = ModelProtocolAdapter.NormalizeResponse(ModelProtocol.Ollama, """{"message":{"content":"OK"}}""");
        check(ModelJson.ReadUsage(normalized) == (0, 0), "missing native usage stays numeric zero");
        try { ModelProtocolAdapter.NormalizeResponse(ModelProtocol.OpenAiResponses, """{"status":"failed","output":[]}"""); check(false, "failed Responses status"); }
        catch (ModelFault fault) { check(fault.ErrorCode == "model_upstream_error", "failed Responses status"); }
    }
}

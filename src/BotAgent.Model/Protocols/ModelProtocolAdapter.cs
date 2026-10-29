using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;

namespace BotAgent.Model.Protocols;

/// <summary>
/// Converts the canonical chat envelope at the HTTP boundary; never owns retries or networking.
/// The payload model is retained for explicit auxiliary model overrides. The caller owns the returned request.
/// </summary>
public static class ModelProtocolAdapter
{
    public static HttpRequestMessage CreateRequest(ModelProviderRoute route, JsonObject payload, bool stream = false)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(payload);
        var capabilities = ModelProtocolStreaming.Capabilities(route.Protocol);
        if (capabilities.RequiresApiKey && string.IsNullOrWhiteSpace(route.ApiKey))
            throw new ModelFault("model_credential_missing");
        var body = (JsonObject)payload.DeepClone();
        if (body["model"] is null) body["model"] = route.ModelName;
        var url = ProtocolEndpoint.Build(route, ProtocolJson.Text(body["model"]), stream);
        body = route.Protocol switch
        {
            ModelProtocol.OpenAiResponses => ResponsesProtocol.Request(body),
            ModelProtocol.AnthropicMessages => AnthropicProtocol.Request(body),
            ModelProtocol.GeminiGenerateContent => GeminiProtocol.Request(body),
            ModelProtocol.Ollama => OllamaProtocol.Request(body),
            _ => body
        };
        if (route.Protocol != ModelProtocol.GeminiGenerateContent) body["stream"] = stream;
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        try
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            if (stream) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                route.Protocol == ModelProtocol.Ollama ? "application/x-ndjson" : "text/event-stream"));
            AddAuthentication(request, route);
            return request;
        }
        catch
        {
            request.Dispose();
            throw new ModelFault("model_route_unavailable");
        }
    }

    public static string NormalizeResponse(ModelProtocol protocol, string json)
    {
        try
        {
            if (protocol is ModelProtocol.OpenAiChat or ModelProtocol.AzureOpenAi)
            {
                ModelFault.ValidateEnvelope(json);
                return json;
            }
            var root = JsonNode.Parse(json) as JsonObject ?? throw new ModelFault("model_malformed_response");
            if (root["error"] is not null) throw new ModelFault("model_upstream_error");
            return (protocol switch
            {
                ModelProtocol.OpenAiResponses => ResponsesProtocol.Response(root),
                ModelProtocol.AnthropicMessages => AnthropicProtocol.Response(root),
                ModelProtocol.GeminiGenerateContent => GeminiProtocol.Response(root),
                ModelProtocol.Ollama => OllamaProtocol.Response(root),
                _ => throw new ModelFault("model_protocol_unsupported")
            }).ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new ModelFault("model_malformed_response");
        }
    }

    internal static HttpRequestMessage CreateDiscoveryRequest(ModelProviderRoute route, string? cursor)
    {
        var capabilities = ModelProtocolStreaming.Capabilities(route.Protocol);
        if (capabilities.RequiresApiKey && string.IsNullOrWhiteSpace(route.ApiKey))
            throw new ModelFault("model_credential_missing");
        var request = new HttpRequestMessage(HttpMethod.Get, ProtocolEndpoint.BuildDiscovery(route, cursor));
        try
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            AddAuthentication(request, route);
            return request;
        }
        catch
        {
            request.Dispose();
            throw new ModelFault("model_route_unavailable");
        }
    }

    private static void AddAuthentication(HttpRequestMessage request, ModelProviderRoute route)
    {
        var key = route.ApiKey.Trim();
        switch (route.Protocol)
        {
            case ModelProtocol.AnthropicMessages:
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", (route.Options ?? new()).AnthropicVersion);
                break;
            case ModelProtocol.GeminiGenerateContent:
                request.Headers.Add("x-goog-api-key", key);
                break;
            case ModelProtocol.AzureOpenAi:
                request.Headers.Add("api-key", key);
                break;
            default:
                if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                break;
        }
    }
}

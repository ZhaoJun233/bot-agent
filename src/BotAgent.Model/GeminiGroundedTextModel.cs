using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Ports;
using BotAgent.Model.Net;

namespace BotAgent.Model;

/// <summary>The existing native Gemini grounding route. Owns protocol and credentials,
/// performs exactly one request, and never puts research-specific instructions into the prompt.</summary>
public sealed class GeminiGroundedTextModel(IModelHttpFetcher http, Func<ModelOptions> options) : IGroundedTextModel
{
    public async Task<GroundedTextResult> GenerateAsync(GroundedTextRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Prompt);
        if (!double.IsFinite(request.Temperature) || request.Temperature is < 0 or > 2
            || request.MaxOutputTokens < 1) throw new ArgumentOutOfRangeException(nameof(request));
        ct.ThrowIfCancellationRequested();
        try
        {
            // Capture the route and its credential from the same current configuration snapshot.
            var settings = options();
            var uri = BuildUri(settings);
            if (uri is null) return Failure("model_route_unavailable");
            var payload = new JsonObject
            {
                ["contents"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = request.Prompt })
                }),
                ["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() }),
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = request.Temperature,
                    ["maxOutputTokens"] = request.MaxOutputTokens
                }
            };
            using var message = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.ApiKey.Trim()}");
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Failure(ModelFault.StatusError(response.StatusCode), (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(body);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failure(ModelFault.SafeCode(ex)); }
    }

    private static GroundedTextResult Parse(string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return Failure("model_malformed_response"); }
        var candidate = root?["candidates"]?[0];
        var text = new StringBuilder();
        if (candidate?["content"]?["parts"]?.AsArray() is { } parts)
            foreach (var part in parts)
                if (part?["text"]?.GetValue<string>() is { } value && !string.IsNullOrWhiteSpace(value))
                    text.Append(value.Trim()).Append('\n');
        var sources = ImmutableArray.CreateBuilder<GroundedTextSource>();
        if (candidate?["groundingMetadata"]?["groundingChunks"]?.AsArray() is { } chunks)
            foreach (var chunk in chunks)
                if (chunk?["web"] is { } web)
                    sources.Add(new(web["title"]?.GetValue<string>(), web["uri"]?.GetValue<string>()));
        var queries = ImmutableArray.CreateBuilder<string>();
        if (candidate?["groundingMetadata"]?["webSearchQueries"]?.AsArray() is { } searchQueries)
            foreach (var query in searchQueries)
                if (query?.GetValue<string>() is { } value && !string.IsNullOrWhiteSpace(value)) queries.Add(value);
        var answer = text.ToString().Trim();
        return new(answer.Length == 0 ? null : answer, sources.ToImmutable(), queries.ToImmutable());
    }

    private static Uri? BuildUri(ModelOptions settings)
    {
        var baseUrl = settings.ModelBaseUrl?.Trim();
        var model = settings.Model?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)
            || string.IsNullOrWhiteSpace(settings.ApiKey)) return null;
        if (!TryEncodeModelPath(model, out var modelPath)) return null;
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^3];
        return Uri.TryCreate($"{trimmed}/v1beta/models/{modelPath}:generateContent",
            UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? uri : null;
    }

    private static bool TryEncodeModelPath(string model, out string encoded)
    {
        encoded = string.Empty;
        var segments = model.Split('/');
        if (segments.Length is 0 or > 4) return false;
        var escaped = new string[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Length == 0 || seg is "." or ".."
                || seg.Any(c => char.IsControl(c) || c is '\\' or '?' or '#' or '%'))
                return false;
            escaped[i] = Uri.EscapeDataString(seg);
        }
        encoded = string.Join('/', escaped);
        return true;
    }

    private static GroundedTextResult Failure(string code, int? status = null) => new(null, [], [], code, status);
}

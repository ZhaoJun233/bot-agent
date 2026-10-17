using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;
using BotAgent.Model.Net;

namespace BotAgent.Model;

public sealed record ProviderConnectionTestResult(bool Succeeded, string ErrorCode, int? StatusCode);

/// <summary>
/// One synthetic, non-streaming chat request. Host must supply a registered route already authorized
/// for this purpose; this is not an arbitrary endpoint/credential validation API. No circuit mutation
/// or retry. Success proves only basic chat connectivity and consumes a small token budget.
/// </summary>
public sealed class ProviderConnectionTester
{
    private const int MaxResponseBytes = 64 * 1024;
    private readonly IModelHttpFetcher _http;

    public ProviderConnectionTester(IModelHttpFetcher http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<ProviderConnectionTestResult> TestAsync(ModelProviderRoute trustedRoute, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trustedRoute);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(trustedRoute.ProviderId) || string.IsNullOrWhiteSpace(trustedRoute.ApiKey)
            || string.IsNullOrWhiteSpace(trustedRoute.ModelName)
            || !Uri.TryCreate(trustedRoute.BaseUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            return new(false, "model_route_unavailable", null);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_http.Timeout);
        var token = budget.Token;
        int? observedStatus = null;
        try
        {
            var payload = new JsonObject
            {
                ["model"] = trustedRoute.ModelName,
                ["max_tokens"] = 64,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with OK." })
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, ModelTransport.BuildUrl(trustedRoute.BaseUrl))
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trustedRoute.ApiKey.Trim());
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            ct.ThrowIfCancellationRequested();
            var status = (int)response.StatusCode;
            observedStatus = status;
            if (!response.IsSuccessStatusCode) return new(false, ModelFault.StatusError(response.StatusCode), status);
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                return new(false, "model_response_too_large", status);
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, token);
                if (count == 0) break;
                if (buffer.Length + count > MaxResponseBytes) return new(false, "model_response_too_large", status);
                buffer.Write(chunk, 0, count);
            }
            using var doc = JsonDocument.Parse(buffer.ToArray());
            var root = doc.RootElement;
            var valid = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && HasText(content);
            ct.ThrowIfCancellationRequested();
            return new(valid, valid ? "ok" : "model_malformed_response", status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "model_timeout", null); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (JsonException) { return new(false, "model_malformed_response", observedStatus); }
        catch (HttpRequestException) { return new(false, "model_network_error", null); }
        catch (Exception) { return new(false, "model_operation_failed", null); }
    }

    private static bool HasText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return !string.IsNullOrWhiteSpace(content.GetString());
        if (content.ValueKind != JsonValueKind.Array) return false;
        return content.EnumerateArray().Any(part => part.ValueKind == JsonValueKind.Object
            && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(text.GetString()));
    }
}

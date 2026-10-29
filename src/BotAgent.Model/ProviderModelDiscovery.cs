using System.Text.Json;
using BotAgent.Domain.Model;
using BotAgent.Model.Net;
using BotAgent.Model.Protocols;

namespace BotAgent.Model;

public sealed record ProviderModelDiscoveryResult(IReadOnlyList<string> Models, string? ErrorCode);

/// <summary>
/// Reads only model identifiers from a registered route; never follows provider-supplied URLs.
/// One request budget covers at most five native pages, 256 KiB total and 500 unique identifiers.
/// Azure deployment discovery is a management-plane operation and deliberately requires manual entry.
/// </summary>
public sealed class ProviderModelDiscovery
{
    private const int MaxResponseBytes = 256 * 1024;
    private const int MaxModels = 500;
    private const int MaxPages = 5;
    private readonly IModelHttpFetcher _http;

    public ProviderModelDiscovery(IModelHttpFetcher http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<ProviderModelDiscoveryResult> ListAsync(ModelProviderRoute trustedRoute, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trustedRoute);
        ct.ThrowIfCancellationRequested();
        if (trustedRoute.Protocol == ModelProtocol.AzureOpenAi) return Failure("model_discovery_unsupported");
        if (string.IsNullOrWhiteSpace(trustedRoute.ProviderId)) return Failure("model_route_unavailable");
        try
        {
            using var budget = ProviderRequestBudget.Create(trustedRoute, TimeSpan.FromSeconds(15), ct);
            var token = budget.Token;
            var models = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var remainingBytes = MaxResponseBytes;
            for (var page = 0; page < MaxPages; page++)
            {
                using var request = ModelProtocolAdapter.CreateDiscoveryRequest(trustedRoute, cursor);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                token.ThrowIfCancellationRequested();
                if (!response.IsSuccessStatusCode) return Failure(ModelFault.StatusError(response.StatusCode));
                var bytes = await ReadBytesAsync(response, remainingBytes, token);
                remainingBytes -= bytes.Length;
                using var document = JsonDocument.Parse(bytes);
                cursor = ReadPage(document.RootElement, trustedRoute.Protocol, models);
                token.ThrowIfCancellationRequested();
                if (cursor is null) return new(models.Order(StringComparer.Ordinal).ToArray(), null);
                if (!cursors.Add(cursor)) return Failure("model_discovery_limit");
            }
            return Failure("model_discovery_limit");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Failure("model_timeout"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (ModelFault fault) { return Failure(fault.ErrorCode); }
        catch (JsonException) { return Failure("model_malformed_response"); }
        catch (HttpRequestException) { return Failure("model_network_error"); }
        catch (IOException) { return Failure("model_network_error"); }
        catch (Exception) { return Failure("model_operation_failed"); }
    }

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, int remainingBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > remainingBytes) throw new ModelFault("model_response_too_large");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, token);
            if (count == 0) break;
            if (buffer.Length + count > remainingBytes) throw new ModelFault("model_response_too_large");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static string? ReadPage(JsonElement root, ModelProtocol protocol, HashSet<string> models)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new ModelFault("model_malformed_response");
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new ModelFault("model_upstream_error");
        var arrayName = protocol is ModelProtocol.GeminiGenerateContent or ModelProtocol.Ollama ? "models" : "data";
        if (!root.TryGetProperty(arrayName, out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new ModelFault("model_malformed_response");
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new ModelFault("model_malformed_response");
            if (protocol == ModelProtocol.GeminiGenerateContent && !SupportsGenerateContent(entry)) continue;
            var id = Text(entry, protocol is ModelProtocol.GeminiGenerateContent or ModelProtocol.Ollama ? "name" : "id");
            if (protocol == ModelProtocol.Ollama && id is null) id = Text(entry, "model");
            if (protocol == ModelProtocol.GeminiGenerateContent && id?.StartsWith("models/", StringComparison.Ordinal) == true) id = id[7..];
            if (IsSafeIdentifier(id)) models.Add(id!);
            if (models.Count > MaxModels) throw new ModelFault("model_discovery_limit");
        }
        var cursor = protocol switch
        {
            ModelProtocol.GeminiGenerateContent => Text(root, "nextPageToken"),
            ModelProtocol.AnthropicMessages => AnthropicCursor(root),
            _ => null
        };
        if (string.IsNullOrEmpty(cursor)) return null;
        if (cursor.Length > 2048 || cursor.Any(c => c < ' ' || c > '~')) throw new ModelFault("model_malformed_response");
        return cursor;
    }

    private static bool SupportsGenerateContent(JsonElement entry)
    {
        if (!entry.TryGetProperty("supportedGenerationMethods", out var methods)) return true;
        if (methods.ValueKind != JsonValueKind.Array) throw new ModelFault("model_malformed_response");
        return methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String && method.GetString() == "generateContent");
    }

    private static string? AnthropicCursor(JsonElement root)
    {
        if (!root.TryGetProperty("has_more", out var more) || more.ValueKind == JsonValueKind.False) return null;
        if (more.ValueKind != JsonValueKind.True) throw new ModelFault("model_malformed_response");
        var cursor = Text(root, "last_id");
        return string.IsNullOrEmpty(cursor) ? throw new ModelFault("model_malformed_response") : cursor;
    }

    private static string? Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        return field.ValueKind == JsonValueKind.String ? field.GetString() : throw new ModelFault("model_malformed_response");
    }

    private static bool IsSafeIdentifier(string? id) => id is { Length: > 0 and <= 256 }
        && id.Any(char.IsAsciiLetterOrDigit)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '@');

    private static ProviderModelDiscoveryResult Failure(string code) => new(Array.Empty<string>(), code);
}

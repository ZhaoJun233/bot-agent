using System.Net;
using System.Text.Json;

namespace BotAgent.Model;

/// <summary>Safe model failure: never includes an upstream body, URL, credential or inner exception.</summary>
public sealed class ModelFault : HttpRequestException
{
    public string ErrorCode { get; }

    public ModelFault(string errorCode, HttpStatusCode? statusCode = null)
        : base(errorCode, null, statusCode) => ErrorCode = errorCode;

    internal static string StatusError(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => "model_auth_failed",
        429 => "model_rate_limited",
        >= 500 => "model_upstream_error",
        _ => "model_http_error"
    };

    internal static string SafeCode(Exception exception) => exception switch
    {
        ModelFault fault => fault.ErrorCode,
        OperationCanceledException => "model_timeout",
        JsonException => "model_malformed_response",
        HttpRequestException => "model_network_error",
        _ => "model_operation_failed"
    };

    internal static void ValidateEnvelope(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ModelFault("model_malformed_response");
            if (root.TryGetProperty("choices", out var choices))
            {
                if (choices.ValueKind != JsonValueKind.Array)
                    throw new ModelFault("model_malformed_response");
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.Object)
                        throw new ModelFault("model_malformed_response");
                    if (choice.TryGetProperty("message", out var message))
                    {
                        if (message.ValueKind != JsonValueKind.Object)
                            throw new ModelFault("model_malformed_response");
                        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                            foreach (var part in content.EnumerateArray())
                                if (part.ValueKind != JsonValueKind.Object ||
                                    (part.TryGetProperty("text", out var text) && text.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                                    throw new ModelFault("model_malformed_response");
                    }
                }
            }
        }
        catch (JsonException)
        {
            throw new ModelFault("model_malformed_response");
        }
    }
}

using System.Text.Json;
using BotAgent.Domain.Platforms;

namespace BotAgent.Services.Official;

internal enum OfficialApiEndpoint { GroupMessage, DirectMessage, GroupUpload, DirectUpload, GroupRecall, DirectRecall }

/// <summary>Only allowlisted response fields survive parsing; never retain the platform error body.</summary>
internal sealed record OfficialApiResult(
    DeliveryResult Delivery, int HttpStatus = 0, int? BusinessCode = null,
    string? MessageId = null, string? FileInfo = null, int? Ttl = null, bool CredentialRejected = false)
{
    public string? ReferenceIndex { get; init; }
    private const int MaxResponseBytes = 64 * 1024;
    public override string ToString() => $"official_api status={HttpStatus} code={BusinessCode} reason={Delivery.ReasonCode}";

    public static OfficialApiResult Unknown(string reason, int status = 0, int? code = null)
        => new(DeliveryResult.Unknown(reason), status, code);

    public static async Task<OfficialApiResult> ReadAsync(HttpResponseMessage response, OfficialApiEndpoint endpoint, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var retry = RetryAfter(response);
        if (status == 429)
            return new(new(DeliveryStatus.Throttled, ReasonCode: "rate_limited", RetryAfterSeconds: retry), status);
        // A server/gateway error does not establish that the outward effect failed.
        if (status >= 500 || status == 408) return Unknown("delivery_outcome_unknown", status);
        var bytes = await ReadBoundedAsync(response.Content, ct).ConfigureAwait(false);
        if (bytes is null) return Unknown("response_too_large", status);
        if (endpoint is OfficialApiEndpoint.GroupRecall or OfficialApiEndpoint.DirectRecall && status == 200 && bytes.Length == 0)
            return new(DeliveryResult.Ok(), status);
        try
        {
            using var json = JsonDocument.Parse(bytes);
            return Parse(json.RootElement, endpoint, status, retry);
        }
        catch (JsonException)
        {
            return status == 403 ? new(DeliveryResult.Rejected("permission_denied"), status) : Unknown("invalid_response", status);
        }
    }

    private static OfficialApiResult Parse(JsonElement root, OfficialApiEndpoint endpoint, int status, int retry)
    {
        if (root.ValueKind != JsonValueKind.Object) return Unknown("invalid_response", status);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (root.EnumerateObject().Any(property => !fields.Add(property.Name))) return Unknown("invalid_response", status);
        int? code = null;
        if (root.TryGetProperty("code", out var value))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
                return Unknown("invalid_response", status);
            code = number;
        }
        if ((code is > 0 || status is < 200 or >= 300)
            && (root.TryGetProperty("id", out _) || root.TryGetProperty("file_info", out _)))
            return Unknown("invalid_response", status, code);
        if (status == 403 || code == 11253)
            return new(DeliveryResult.Rejected("permission_denied"), status, code);
        if (code == 11244 && status is 400 or 401)
            return new(DeliveryResult.Rejected("credential_rejected"), status, code, CredentialRejected: true);
        if (code is > 0) return Error(endpoint, status, code.Value, retry);
        if (status is < 200 or >= 300)
            return new(DeliveryResult.Rejected(status == 401 ? "credential_rejected" : "http_rejected"), status, code);
        if (endpoint is OfficialApiEndpoint.GroupRecall or OfficialApiEndpoint.DirectRecall)
            // The documented HTTP 200 success example is {}, alongside a bodyless response.
            return status == 200 && fields.Count == 0
                ? new(DeliveryResult.Ok(), status) : Unknown("invalid_response", status, code);

        var upload = endpoint is OfficialApiEndpoint.GroupUpload or OfficialApiEndpoint.DirectUpload;
        var field = upload ? "file_info" : "id";
        var present = root.TryGetProperty(field, out var identity);
        var text = present && identity.ValueKind == JsonValueKind.String ? identity.GetString() : null;
        if (present && !ValidReference(text)) return Unknown("invalid_response", status, code);
        if (upload)
        {
            if (!present) return new(DeliveryResult.Permanent("upload_receipt_unavailable"), status, code);
            int? ttl = root.TryGetProperty("ttl", out var ttlValue) && ttlValue.ValueKind == JsonValueKind.Number
                && ttlValue.TryGetInt32(out var seconds) && seconds > 0 ? seconds : null;
            return new(DeliveryResult.Ok(), status, code, FileInfo: text, Ttl: ttl);
        }
        // Explicit business success or a valid send timestamp can confirm an id-less acknowledgement.
        if (!present && code != 0 && !ValidTimestamp(root)) return Unknown("invalid_response", status, code);
        return new(new(DeliveryStatus.Success, ReasonCode: present ? "ok" : "message_id_unavailable"),
            status, code, MessageId: text) { ReferenceIndex = OfficialReferenceFields.ReadReceipt(root) };
    }

    private static OfficialApiResult Error(OfficialApiEndpoint endpoint, int status, int code, int retry)
    {
        if (endpoint is OfficialApiEndpoint.GroupRecall or OfficialApiEndpoint.DirectRecall)
        {
            var recallReason = code switch
            {
                40062003 => "permission_denied",
                40064004 => "recall_expired",
                40061001 or 40061002 or 306009 => "invalid_parameters",
                _ => "api_rejected"
            };
            return new(DeliveryResult.Rejected(recallReason), status, code);
        }
        var direct = endpoint is OfficialApiEndpoint.DirectMessage or OfficialApiEndpoint.DirectUpload;
        var upload = endpoint is OfficialApiEndpoint.GroupUpload or OfficialApiEndpoint.DirectUpload;
        string? reason = code switch
        {
            304004 or 304036 or 304064 or 40034105 or 40034127 => "permission_denied",
            304103 or 40034005 or 40034026 when !upload => "reply_window_expired",
            40034128 when !upload => "reply_limit_reached",
            40034101 when !direct => "relationship_removed",
            40054004 when direct => "relationship_removed",
            40054013 when direct => "recipient_refused",
            40034006 => "content_rejected",
            304003 => "link_not_allowed",
            22006 or 304061 or 304080 or 305007 or 340069 or 40034008 or 40034009 or 40034010
                or 40034011 or 40034024 or 40034025 or 40034027 or 40034029 or 40034106
                or 40034108 or 40034109 or 40034124 => "invalid_parameters",
            850019 or 40034002 when upload => "media_format_unsupported",
            _ => null,
        };
        if (code == 40034100 && !upload)
            return new(new(DeliveryStatus.Throttled, ReasonCode: "rate_limited", RetryAfterSeconds: retry), status, code);
        if (code == 40054005 && direct && !upload) return Unknown("duplicate_delivery", status, code);
        return new(DeliveryResult.Rejected(reason ?? "api_rejected"), status, code);
    }

    private static bool ValidReference(string? value)
        => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool ValidTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String) return false;
        var text = timestamp.GetString()!;
        var separator = text.IndexOf('T');
        return separator > 0 && (text.EndsWith('Z') || text.LastIndexOf('+') > separator || text.LastIndexOf('-') > separator)
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _);
    }

    private static int RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var seconds = header?.Delta?.TotalSeconds ?? (header?.Date - Clock.Now)?.TotalSeconds ?? 0;
        return (int)Math.Clamp(Math.Ceiling(seconds), 0, int.MaxValue);
    }

    internal static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxResponseBytes + 1 - (int)output.Length)), ct).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaxResponseBytes) return null;
            output.Write(buffer, 0, count);
        }
    }
}

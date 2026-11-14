using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Services.Memory;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private async Task<OfficialApiResult> SendApiAsync(string url, Func<HttpContent> createContent, OfficialApiEndpoint endpoint,
        DeliveryContext delivery, OfficialDeliveryPhase phase, CancellationToken ct, bool isChatCommit = false)
    {
        var refreshToken = false;
        var tokenRetried = false;
        while (true)
        {
            if (!DeliveryIsCurrent(delivery)) return new(DeliveryResult.Rejected("delivery_source_invalid"));
            string token;
            try { token = await EnsureTokenAsync(refreshToken, ct).ConfigureAwait(false); }
            catch (OfficialAuthenticationException)
            { return new(DeliveryResult.Rejected("authentication_unavailable")); }
            catch (Exception error) when (error is HttpRequestException or IOException
                || (error is OperationCanceledException && !ct.IsCancellationRequested))
            { return new(DeliveryResult.Transient("authentication_unavailable")); }
            refreshToken = false;
            // Readiness may release scheduler capacity; never wait while holding the transport gate.
            await ReplyCommitBoundary.BeforePrepareAsync().ConfigureAwait(false);
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (isChatCommit && !ReplyCommitBoundary.TryBeforeSend()) continue;
                if (!DeliveryIsCurrent(delivery)) return new(DeliveryResult.Rejected("delivery_source_invalid"));
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = createContent() };
                Auth(request, token);
                var (result, invoked) = await InvokeApiAsync(request, endpoint, delivery, phase, ct).ConfigureAwait(false);
                Log($"official_api endpoint={endpoint} status={result.HttpStatus} code={result.BusinessCode?.ToString() ?? "none"} reason={result.Delivery.ReasonCode}");
                if (!invoked) return result;
                if (!tokenRetried && result.CredentialRejected)
                {
                    OfficialDeliveryOperation row;
                    try { row = _delivery.Value.RecordCredentialRejection(delivery.OperationId); }
                    catch (Exception error) when (IsIdentityFailure(error))
                    { RetireDelivery(); return OfficialApiResult.Unknown("delivery_receipt_unavailable"); }
                    if (row.State != OfficialDeliveryState.CredentialRejected) return new(row.Result!);
                    tokenRetried = true;
                    refreshToken = true;
                    continue; // Retry retains the body and re-enters every final commit/receipt check.
                }
                return RecordApiReceipt(delivery, phase, result);
            }
            finally { _sendGate.Release(); }
        }
    }

    private async Task<(OfficialApiResult Result, bool Invoked)> InvokeApiAsync(HttpRequestMessage request, OfficialApiEndpoint endpoint,
        DeliveryContext delivery, OfficialDeliveryPhase phase, CancellationToken ct)
    {
        var invoked = false;
        OfficialApiResult? denied = null;
        try
        {
            var pending = MemoryUseScope.UseForDelivery(() =>
            {
                ReplyCommitBoundary.ValidateCurrent();
                var permission = BeginDeliveryAttempt(delivery, phase, ct);
                if (!permission.Delivery.IsSuccess) { denied = permission; return null; }
                // A durable commit can block; source cancellation during that write must still prevent transport.
                if (ct.IsCancellationRequested || !DeliveryIsCurrent(delivery))
                {
                    denied = RecordApiReceipt(delivery, phase, new(DeliveryResult.Rejected("delivery_source_invalid")));
                    return null;
                }
                // Persisting an attempt may block while plugin or platform permissions change.
                ReplyCommitBoundary.ValidateCurrent();
                invoked = true;
                return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            });
            if (pending is null) return (denied!, false);
            using var response = await pending.ConfigureAwait(false);
            return (await OfficialApiResult.ReadAsync(response, endpoint, ct).ConfigureAwait(false), true);
        }
        catch (OperationCanceledException) when (!invoked)
        {
            return (RecordApiReceipt(delivery, phase, new(DeliveryResult.Rejected("delivery_authorization_changed"))), false);
        }
        catch (Exception error) when (invoked && error is (OperationCanceledException or HttpRequestException or IOException))
        {
            return (OfficialApiResult.Unknown("delivery_outcome_unknown"), true);
        }
    }

    private static async Task<(string Token, int Expires)> ReadTokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new OfficialAuthenticationException();
        var bytes = await OfficialApiResult.ReadBoundedAsync(response.Content, ct).ConfigureAwait(false)
            ?? throw new OfficialAuthenticationException();
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => !fields.Add(property.Name)))
                throw new OfficialAuthenticationException();
            if (root.TryGetProperty("code", out var code)
                && (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number) || number != 0))
                throw new OfficialAuthenticationException();
            if (!root.TryGetProperty("access_token", out var value) || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Any(char.IsControl))
                throw new OfficialAuthenticationException();
            var expires = 7200;
            if (root.TryGetProperty("expires_in", out var lifetime)
                && !TryReadTokenLifetime(lifetime, out expires))
                throw new OfficialAuthenticationException();
            return (value.GetString()!, expires);
        }
        catch (JsonException) { throw new OfficialAuthenticationException(); }
    }

    private static bool TryReadTokenLifetime(JsonElement value, out int seconds)
    {
        seconds = 0;
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt32(out seconds) && seconds > 0;
        // The official field table says number; its example and live API also use decimal strings.
        return value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
            && seconds > 0;
    }
}

internal sealed class OfficialAuthenticationException() : Exception("authentication_unavailable");

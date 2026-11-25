using System.Globalization;
using BotAgent.Adapters.Official;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private sealed record RecallContext(PlatformSourceLease Source, ConversationId Conversation, long MessageAlias,
        bool IsGroup, string Target, string Message)
    { public override string ToString() => "OfficialRecallContext"; }

    public async Task<DeliveryResult> RecallOwnMessageAsync(bool isGroup, long targetId, long messageId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Guid operation = default;
        try
        {
            var (context, denied) = PrepareRecall(isGroup, targetId, messageId);
            if (context is null) return DeliveryResult.Rejected(denied);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Source.Lifetime);
            ct = lifetime.Token;
            var retry = false;
            while (true)
            {
                if (!RecallIsCurrent(context)) return DeliveryResult.Rejected("recall_source_invalid");
                var eligible = _delivery.Value.InspectRecall(context.Conversation, messageId, retry);
                if (!eligible.Allowed) return eligible.Result ?? DeliveryResult.Rejected(eligible.ReasonCode);
                string token;
                try { token = await EnsureTokenAsync(retry, ct).ConfigureAwait(false); }
                catch (OfficialAuthenticationException) { return DeliveryResult.Rejected("authentication_unavailable"); }
                catch (Exception error) when (error is HttpRequestException or IOException
                    || error is OperationCanceledException && !ct.IsCancellationRequested)
                { return DeliveryResult.Transient("authentication_unavailable"); }
                await _sendGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!RecallIsCurrent(context)) return DeliveryResult.Rejected("recall_source_invalid");
                    var grant = _delivery.Value.BeginRecall(context.Conversation, messageId, retry);
                    if (!grant.Allowed) return grant.Result ?? DeliveryResult.Rejected(grant.ReasonCode);
                    operation = grant.OperationId;
                    var timeInvalid = Clock.Now.UtcTicks < grant.CheckedAt || Clock.Now.UtcTicks >= grant.ExpiresAt;
                    if (ct.IsCancellationRequested || !RecallIsCurrent(context) || timeInvalid)
                    {
                        var cancelled = DeliveryResult.Rejected(timeInvalid ? "recall_expired" : "recall_source_invalid");
                        _delivery.Value.CompleteRecall(operation, cancelled);
                        return cancelled;
                    }
                    var result = await InvokeRecallAsync(context, token, ct).ConfigureAwait(false);
                    var credentialRetry = result.CredentialRejected && !retry;
                    try { _delivery.Value.CompleteRecall(operation, result.Delivery, credentialRetry); }
                    catch (Exception error) when (IsIdentityFailure(error))
                    { RetireDelivery(); return DeliveryResult.Unknown("delivery_receipt_unavailable"); }
                    if (!credentialRetry) return result.Delivery;
                    retry = true;
                }
                finally { _sendGate.Release(); }
            }
        }
        catch (Exception error) when (IsIdentityFailure(error))
        { RetireDelivery(); return DeliveryResult.Rejected("delivery_store_unavailable"); }
        finally
        {
            if (operation != Guid.Empty && !_deliveryRetired)
                try { _delivery.Value.EndRecall(operation); }
                catch (Exception error) when (IsIdentityFailure(error)) { RetireDelivery(); }
        }
    }

    private (RecallContext? Context, string Reason) PrepareRecall(bool group, long targetId, long messageId)
    {
        var source = PlatformSourceExecution.Current;
        var expected = new ConversationId(PlatformId.QqOfficial, AccountScope.Legacy,
            group ? ConversationKind.GroupChat : ConversationKind.PrivateChat, targetId.ToString(CultureInfo.InvariantCulture));
        if (_disposed || _deliveryRetired || !ApplicationIsCurrent() || source is null || !source.IsCurrent
            || !ReferenceEquals(source.Source, this) || source.Context.PlatformId != PlatformId.QqOfficial
            || source.Context.AccountScope != AccountScope.Legacy || source.Origin.Trigger?.Conversation != expected)
            return (null, "recall_source_invalid");
        var target = ResolveTarget(group, targetId);
        if (target is null) return (null, "recall_message_scope_mismatch");
        var conversation = NativeConversation(group, target);
        var message = _ids.Value.OriginalOf(messageId, new(_applicationId, OfficialIdentityKind.Message,
            conversation, OfficialMessageDirection.Outbound));
        if (message is null) return (null, "recall_message_scope_mismatch");
        if (!TryLoadDelivery()) return (null, "delivery_store_unavailable");
        return (new(source, conversation, messageId, group, target, message), "recall_ready");
    }

    private bool RecallIsCurrent(RecallContext context)
        => !_disposed && !_deliveryRetired && ApplicationIsCurrent() && context.Source.IsCurrent
            && ReferenceEquals(PlatformSourceExecution.Current, context.Source) && ReferenceEquals(context.Source.Source, this);

    private async Task<OfficialApiResult> InvokeRecallAsync(RecallContext context, string token, CancellationToken ct)
    {
        var url = $"{ApiBase}/v2/{(context.IsGroup ? "groups" : "users")}/{Uri.EscapeDataString(context.Target)}"
            + $"/messages/{Uri.EscapeDataString(context.Message)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        Auth(request, token);
        var endpoint = context.IsGroup ? OfficialApiEndpoint.GroupRecall : OfficialApiEndpoint.DirectRecall;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var result = await OfficialApiResult.ReadAsync(response, endpoint, ct).ConfigureAwait(false);
            Log($"official_api endpoint={endpoint} status={result.HttpStatus} code={result.BusinessCode?.ToString() ?? "none"} reason={result.Delivery.ReasonCode}");
            return result;
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidOperationException)
        { return OfficialApiResult.Unknown("delivery_outcome_unknown"); }
    }
}

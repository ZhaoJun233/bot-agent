using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private readonly Lazy<OfficialDeliveryLedger> _delivery;
    private volatile bool _deliveryRetired;

    // Raw identifiers and media credentials exist only inside this live operation, never in its snapshot.
    private sealed record DeliveryContext(PlatformSourceLease Source, ReplyDeliveryBinding? Binding,
        OfficialIdentityKey Trigger, bool IsGroup, string Target, Guid OperationId, int Sequence,
        string? ReferenceIndex, bool ReferenceUnavailable)
    {
        public ContentChanges Changes { get; init; }
        public override string ToString() => "OfficialDeliveryContext";
    }

    private bool TryLoadDelivery()
    {
        if (_deliveryRetired || !ApplicationIsCurrent()) return false;
        try { _ = _delivery.Value; return true; }
        catch (Exception error) when (IsIdentityFailure(error)) { RetireDelivery(); return false; }
    }

    private void RetireDelivery()
    {
        _deliveryRetired = true;
        LastErrorCode = "delivery_store_unavailable";
        Log("official_delivery reason=delivery_store_unavailable");
    }

    private bool AcceptInboundDelivery(OfficialIdentityKey message, DateTimeOffset? eventTime)
    {
        if (eventTime is null) { Log("official_delivery reason=trigger_time_invalid"); return false; }
        if (!TryLoadDelivery()) return false;
        try
        {
            var accepted = _delivery.Value.AcceptTrigger(message, eventTime.Value);
            if (!accepted.Allowed) Log("official_delivery reason=" + accepted.ReasonCode);
            return accepted.Allowed;
        }
        catch (Exception error) when (IsIdentityFailure(error)) { RetireDelivery(); return false; }
    }

    private void RevokeDeliveryScope(bool isGroup, string target)
    {
        if (!TryLoadDelivery()) return;
        try { _delivery.Value.RevokeScope(NativeConversation(isGroup, target)); }
        catch (Exception error) when (IsIdentityFailure(error)) { RetireDelivery(); }
    }

    private (DeliveryContext? Context, string Reason) PrepareDelivery(bool isGroup, long targetId, string digest, bool upload, long? quoteAlias = null)
    {
        if (!ApplicationIsCurrent()) return (null, "identity_source_changed");
        var binding = ReplyDeliveryExecution.Current;
        var source = PlatformSourceExecution.Current;
        var trigger = binding is null ? source?.Origin.Trigger : binding.Trigger;
        if (source is null || !source.IsCurrent || !ReferenceEquals(source.Source, this)
            || binding is not null && (!binding.IsCurrent || !ReferenceEquals(binding.Source, source)))
            return (null, "delivery_source_invalid");
        if (trigger is null) return (null, "passive_trigger_required");
        var expected = new ConversationId(PlatformId.QqOfficial, AccountScope.Legacy,
            isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, targetId.ToString(CultureInfo.InvariantCulture));
        if (trigger.Conversation != expected || source.Context.PlatformId != PlatformId.QqOfficial
            || source.Context.AccountScope != AccountScope.Legacy
            || !long.TryParse(trigger.NativeMessageId, NumberStyles.None, CultureInfo.InvariantCulture, out var messageAlias))
            return (null, "delivery_trigger_scope_mismatch");
        var target = ResolveTarget(isGroup, targetId);
        if (target is null) return (null, "identity_target_mismatch");
        var scope = new OfficialIdentityScope(_applicationId, OfficialIdentityKind.Message,
            NativeConversation(isGroup, target), OfficialMessageDirection.Inbound);
        var key = _ids.Value.IdentityOf(messageAlias, scope);
        if (key is null) return (null, "delivery_trigger_scope_mismatch");
        string? reference = null;
        if (quoteAlias is long quote)
        {
            var resolved = ResolveReference(scope.Conversation!, quote);
            if (!resolved.Known) return (null, "reference_scope_mismatch");
            reference = resolved.Index;
            digest = ContentDigest(8, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[]
                { digest, quote.ToString(CultureInfo.InvariantCulture), reference ?? string.Empty }));
        }
        if (!TryLoadDelivery()) return (null, "delivery_store_unavailable");
        try
        {
            var id = Guid.NewGuid();
            var prepared = _delivery.Value.Prepare(key, id, digest, upload);
            return prepared.Allowed
                ? (new(source, binding, key, isGroup, target, id, prepared.Operation!.Sequence,
                    reference, quoteAlias is not null && reference is null), "operation_prepared")
                : (null, prepared.ReasonCode);
        }
        catch (Exception error) when (IsIdentityFailure(error)) { RetireDelivery(); return (null, "delivery_store_unavailable"); }
    }

    private bool DeliveryIsCurrent(DeliveryContext delivery)
        => !_disposed && !_deliveryRetired && ApplicationIsCurrent() && delivery.Source.IsCurrent
            && ReferenceEquals(PlatformSourceExecution.Current, delivery.Source)
            && ReferenceEquals(ReplyDeliveryExecution.Current, delivery.Binding)
            && ReferenceEquals(delivery.Source.Source, this);

    private static string ContentDigest(int type, byte[] content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([(byte)type]); hash.AppendData(content);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static SendResult RefusedDelivery(string reason)
        => new(false) { Delivery = DeliveryResult.Rejected(reason) };

    private OfficialApiResult BeginDeliveryAttempt(DeliveryContext delivery, OfficialDeliveryPhase phase, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!DeliveryIsCurrent(delivery)) return new(DeliveryResult.Rejected("delivery_source_invalid"));
        try
        {
            var decision = _delivery.Value.BeginAttempt(delivery.OperationId, phase);
            return new(decision.Allowed ? DeliveryResult.Ok() : DeliveryResult.Rejected(decision.ReasonCode));
        }
        catch (Exception error) when (IsIdentityFailure(error)) { RetireDelivery(); return new(DeliveryResult.Rejected("delivery_store_unavailable")); }
    }

    private OfficialApiResult RecordApiReceipt(DeliveryContext delivery, OfficialDeliveryPhase phase, OfficialApiResult result)
    {
        try
        {
            if (phase == OfficialDeliveryPhase.Upload && result.Delivery.IsSuccess)
            {
                var expiry = result.Ttl is > 0 ? Clock.Now.UtcTicks + Math.Min(TimeSpan.FromSeconds(result.Ttl.Value).Ticks,
                    DateTimeOffset.MaxValue.UtcTicks - Clock.Now.UtcTicks) : 0;
                var row = _delivery.Value.CompleteUpload(delivery.OperationId,
                    ContentDigest(7, Encoding.UTF8.GetBytes(result.FileInfo!)), expiry);
                return row.State == OfficialDeliveryState.Uploaded ? result : new(row.Result!);
            }
            long alias = 0;
            if (result.Delivery.IsSuccess && result.MessageId is not null)
            {
                try { alias = _ids.Value.AliasFor(MessageIdentity(delivery.IsGroup, delivery.Target, result.MessageId, OfficialMessageDirection.Outbound)); }
                catch (Exception error) when (IsIdentityFailure(error))
                {
                    LastErrorCode = "identity_receipt_unavailable";
                    Log(LastErrorCode);
                    result = result with { Delivery = new(DeliveryStatus.Success, ReasonCode: "message_id_unavailable"), MessageId = null };
                }
            }
            if (alias > 0) RememberReference(delivery.Trigger.Scope.Conversation!, alias, result.ReferenceIndex);
            var receipt = result.Delivery with { MessageId = alias > 0 ? alias.ToString(CultureInfo.InvariantCulture) : null };
            receipt = ApplyContentChanges(receipt, delivery.Changes | (delivery.ReferenceUnavailable ? ContentChanges.Quote : ContentChanges.None));
            _delivery.Value.Complete(delivery.OperationId, receipt, alias);
            return result with { Delivery = receipt, MessageId = receipt.MessageId };
        }
        catch (Exception error) when (IsIdentityFailure(error))
        {
            RetireDelivery();
            return OfficialApiResult.Unknown("delivery_receipt_unavailable");
        }
    }

    private void EndDelivery(DeliveryContext delivery)
    {
        if (_deliveryRetired) return;
        try
        {
            var row = _delivery.Value.Read(delivery.OperationId);
            if (row?.State is OfficialDeliveryState.Attempting or OfficialDeliveryState.Uploading)
                _delivery.Value.Complete(delivery.OperationId, DeliveryResult.Unknown("delivery_outcome_unknown"));
            else if (row?.State is OfficialDeliveryState.Prepared or OfficialDeliveryState.Uploaded or OfficialDeliveryState.CredentialRejected)
                _delivery.Value.Abandon(delivery.OperationId, "delivery_abandoned");
        }
        catch (Exception error) when (IsIdentityFailure(error)) { RetireDelivery(); }
    }
}

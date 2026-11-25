using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Platforms;

namespace BotAgent.Adapters.Official;

public sealed partial class OfficialDeliveryLedger
{
    /// <summary>Ends an operation that has no HTTP attempt in flight. Reserved quota is never refunded.</summary>
    public OfficialDeliveryOperation Abandon(Guid operationId, string reason)
    {
        ValidateOperationId(operationId);
        if (!ValidReason(reason)) throw new ArgumentException("delivery_reason_invalid");
        return Write(now =>
        {
            var row = Find(operationId) ?? throw new InvalidOperationException("operation_missing");
            if (row.State is OfficialDeliveryState.Completed or OfficialDeliveryState.Abandoned) return row;
            if (row.State is OfficialDeliveryState.Attempting or OfficialDeliveryState.Uploading)
                throw new InvalidOperationException("operation_still_attempting");
            Finish(operationId, OfficialDeliveryState.Abandoned, DeliveryResult.Rejected(reason), 0, now);
            return Find(operationId)!;
        });
    }

    /// <summary>Records a confirmed credential rejection; it does not grant a retry.</summary>
    public OfficialDeliveryOperation RecordCredentialRejection(Guid operationId)
    {
        ValidateOperationId(operationId);
        return Write(now =>
        {
            if (!_state.Operations.TryGetValue(operationId, out var row)) throw new InvalidOperationException("operation_missing");
            if (!(row.State == OfficialDeliveryState.Attempting && row.MessageAttempts == 1
                || row.State == OfficialDeliveryState.Uploading && row.Upload?.Attempts == 1))
                throw new InvalidOperationException("credential_retry_unavailable");
            var trigger = _state.Triggers[row.TriggerKey];
            if (trigger.Revoked || trigger.ExpiresAt <= now)
                Finish(operationId, OfficialDeliveryState.Abandoned, DeliveryResult.Rejected("trigger_invalidated"), 0, now);
            else _state.Operations[operationId] = row with { State = OfficialDeliveryState.CredentialRejected, UpdatedAt = now };
            return Find(operationId)!;
        });
    }

    /// <summary>Stores only a receipt digest. Raw media credentials remain in the current send operation.</summary>
    public OfficialDeliveryOperation CompleteUpload(Guid operationId, string receiptDigest, long expiresAt = 0)
    {
        ValidateOperationId(operationId);
        if (!OfficialIdentityCodec.IsDigest(receiptDigest) || expiresAt < 0 || expiresAt > DateTimeOffset.MaxValue.UtcTicks)
            throw new ArgumentException("upload_receipt_invalid");
        return Write(now =>
        {
            if (!_state.Operations.TryGetValue(operationId, out var row)) throw new InvalidOperationException("operation_missing");
            if (row.State != OfficialDeliveryState.Uploading || row.Upload is null) throw new InvalidOperationException("operation_not_uploading");
            var trigger = _state.Triggers[row.TriggerKey];
            if (trigger.Revoked || trigger.ExpiresAt <= now || expiresAt > 0 && expiresAt <= now)
                Finish(operationId, OfficialDeliveryState.Abandoned, DeliveryResult.Rejected("media_reference_invalidated"), 0, now);
            else _state.Operations[operationId] = row with { State = OfficialDeliveryState.Uploaded, UpdatedAt = now,
                Upload = row.Upload with { ReceiptDigest = receiptDigest, ExpiresAt = expiresAt } };
            return Find(operationId)!;
        });
    }

    private static bool CanBegin(OperationRow row, OfficialDeliveryPhase phase)
    {
        if (phase == OfficialDeliveryPhase.Upload)
            return row.MessageAttempts == 0 && row.Upload is { ReceiptDigest: null } upload
                && (row.State == OfficialDeliveryState.Prepared && upload.Attempts == 0
                    || row.State == OfficialDeliveryState.CredentialRejected && upload.Attempts == 1);
        if (row.Upload is { ReceiptDigest: null }) return false;
        return row.MessageAttempts == 0 && (row.State == OfficialDeliveryState.Prepared && row.Upload is null
                || row.State == OfficialDeliveryState.Uploaded)
            || row.MessageAttempts == 1 && row.State == OfficialDeliveryState.CredentialRejected;
    }

    private static void ValidateAttempts(OperationRow row)
    {
        if (row.MessageAttempts is < 0 or > 2) throw InvalidStore();
        var upload = row.Upload;
        if (upload is not null && (upload.Attempts is < 0 or > 2
                || upload.ExpiresAt < 0 || upload.ExpiresAt > DateTimeOffset.MaxValue.UtcTicks
                || upload.ReceiptDigest is null && upload.ExpiresAt != 0
                || upload.ReceiptDigest is not null && (!OfficialIdentityCodec.IsDigest(upload.ReceiptDigest) || upload.Attempts == 0)
                || row.MessageAttempts > 0 && upload.ReceiptDigest is null)) throw InvalidStore();
        var valid = row.State switch
        {
            OfficialDeliveryState.Prepared => row.MessageAttempts == 0 && (upload is null || upload is { Attempts: 0, ReceiptDigest: null }),
            OfficialDeliveryState.Uploading => row.MessageAttempts == 0 && upload is { Attempts: > 0, ReceiptDigest: null },
            OfficialDeliveryState.Uploaded => row.MessageAttempts == 0 && upload is { ReceiptDigest: not null },
            OfficialDeliveryState.Attempting => row.MessageAttempts > 0,
            OfficialDeliveryState.CredentialRejected => row.MessageAttempts == 1
                || row.MessageAttempts == 0 && upload is { Attempts: 1, ReceiptDigest: null },
            OfficialDeliveryState.Completed => row.MessageAttempts > 0 || upload is { Attempts: > 0 }
                && row.Status is not (DeliveryStatus.Success or DeliveryStatus.Degraded or DeliveryStatus.PartialSuccess),
            OfficialDeliveryState.Abandoned => row.MessageAttempts <= 1,
            _ => false
        };
        if (!valid) throw InvalidStore();
    }
}

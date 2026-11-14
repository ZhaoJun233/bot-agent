using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;

namespace BotAgent.Adapters.Official;

public sealed record OfficialRecallDecision(bool Allowed, string ReasonCode, Guid OperationId = default, DeliveryResult? Result = null)
{
    public long ExpiresAt { get; init; }
    public long CheckedAt { get; init; }
}

public sealed partial class OfficialDeliveryLedger
{
    public OfficialRecallDecision InspectRecall(ConversationId conversation, long messageAlias, bool credentialRetry = false)
    {
        var scope = ScopeKey(conversation);
        lock (_gate)
        {
            EnsureOpen();
            var now = _clock().UtcTicks;
            if (now < _state.ClockFloor) throw new InvalidOperationException("official_delivery_clock_regressed");
            return RecallDecision(scope, messageAlias, now, credentialRetry);
        }
    }

    /// <summary>The grant is published before any DELETE; a second caller cannot borrow it.</summary>
    public OfficialRecallDecision BeginRecall(ConversationId conversation, long messageAlias, bool credentialRetry = false)
    {
        var scope = ScopeKey(conversation);
        return Write(now =>
        {
            var decision = RecallDecision(scope, messageAlias, now, credentialRetry);
            if (!decision.Allowed) return decision;
            var row = _state.Operations[decision.OperationId];
            _state.Operations[decision.OperationId] = row with
            { Recall = new((row.Recall?.Attempts ?? 0) + 1, now, null, null, 0, false) };
            return decision;
        });
    }

    public void CompleteRecall(Guid operationId, DeliveryResult result, bool credentialRejected = false)
    {
        ValidateOperationId(operationId);
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(result.Status) || !ValidReason(result.ReasonCode) || result.RetryAfterSeconds < 0
            || result.Status is DeliveryStatus.Degraded or DeliveryStatus.PartialSuccess
            || credentialRejected && (result.Status != DeliveryStatus.Rejected || result.ReasonCode != "credential_rejected"))
            throw new ArgumentException("recall_receipt_invalid");
        Write(now =>
        {
            if (!_state.Operations.TryGetValue(operationId, out var row)
                || row.Recall is not { Status: null, CredentialRejected: false } recall
                || credentialRejected && recall.Attempts != 1) throw new InvalidOperationException("recall_not_attempting");
            FinishRecall(operationId, result, credentialRejected, now);
            return true;
        });
    }

    private OfficialRecallDecision RecallDecision(string scope, long alias, long now, bool credentialRetry)
    {
        var matches = _state.Operations.Where(pair => pair.Value.MessageAlias == alias && alias > 0
            && _state.Triggers[pair.Value.TriggerKey].ScopeKey == scope).Take(2).ToArray();
        if (matches.Length != 1) return new(false, "own_message_unverified");
        var (id, row) = matches[0];
        if (row.State != OfficialDeliveryState.Completed || row.Status is not (DeliveryStatus.Success or DeliveryStatus.Degraded))
            return new(false, "own_message_unverified");
        if (_state.Triggers[row.TriggerKey].Revoked) return new(false, "trigger_revoked");
        if (row.Recall is { Status: { } status } completed)
            return new(false, completed.Reason!, id, new(status, ReasonCode: completed.Reason!, RetryAfterSeconds: completed.RetryAfter));
        if (row.Recall is { } prior && !(credentialRetry && prior.CredentialRejected && prior.Attempts == 1))
            return new(false, "recall_in_progress", id, DeliveryResult.Unknown("recall_in_progress"));
        if (credentialRetry && row.Recall is null) return new(false, "credential_retry_unavailable");
        if (row.MessageAttemptedAt == 0) return new(false, "own_message_time_unverified");
        if (now - row.MessageAttemptedAt >= TimeSpan.FromMinutes(2).Ticks) return new(false, "recall_expired");
        return new(true, "recall_ready", id) { CheckedAt = now, ExpiresAt = row.MessageAttemptedAt
            + Math.Min(TimeSpan.FromMinutes(2).Ticks, DateTimeOffset.MaxValue.UtcTicks - row.MessageAttemptedAt) };
    }

    public void EndRecall(Guid operationId)
    {
        ValidateOperationId(operationId);
        Write(now =>
        {
            if (_state.Operations.TryGetValue(operationId, out var row) && row.Recall is { Status: null } pending)
                FinishRecall(operationId, pending.CredentialRejected ? DeliveryResult.Rejected("credential_rejected")
                    : DeliveryResult.Unknown("delivery_outcome_unknown"), false, now);
            return true;
        });
    }

    private void FinishRecall(Guid id, DeliveryResult result, bool credentialRejected, long now)
    {
        var row = _state.Operations[id];
        _state.Operations[id] = row with { Recall = row.Recall! with { UpdatedAt = now,
            Status = credentialRejected ? null : result.Status, Reason = credentialRejected ? null : result.ReasonCode,
            RetryAfter = credentialRejected ? 0 : result.RetryAfterSeconds, CredentialRejected = credentialRejected } };
    }

    private void ValidateRecall(OperationRow row)
    {
        if (row.MessageAttemptedAt < 0 || row.MessageAttemptedAt > row.UpdatedAt
            || row.MessageAttemptedAt > 0 && (row.MessageAttempts == 0
                || row.MessageAttemptedAt < _state.Triggers[row.TriggerKey].EventAt)) throw InvalidStore();
        if (row.Recall is not { } recall) return;
        if (row.State != OfficialDeliveryState.Completed || row.Status is not (DeliveryStatus.Success or DeliveryStatus.Degraded)
            || row.MessageAlias == 0 || row.MessageAttemptedAt == 0 || recall.Attempts is < 1 or > 2
            || recall.UpdatedAt < row.MessageAttemptedAt || recall.UpdatedAt > _state.ClockFloor
            || recall.RetryAfter < 0 || recall.CredentialRejected && (recall.Attempts != 1 || recall.Status is not null)) throw InvalidStore();
        if (recall.Status is { } status)
        {
            if (!Enum.IsDefined(status) || status is DeliveryStatus.Degraded or DeliveryStatus.PartialSuccess
                || !ValidReason(recall.Reason)) throw InvalidStore();
        }
        else if (recall.Reason is not null || recall.RetryAfter != 0) throw InvalidStore();
    }
}

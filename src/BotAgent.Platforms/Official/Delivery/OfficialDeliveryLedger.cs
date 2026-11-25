using System.Security.Cryptography;
using System.Text;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services;

namespace BotAgent.Adapters.Official;

/// <summary>Application-scoped delivery rules. Every grant follows a durable snapshot commit.</summary>
public sealed partial class OfficialDeliveryLedger : IDisposable
{
    private const int MaxTriggers = 10000, MaxOperations = 50000, MaxScopes = 10000;
    private readonly object _gate = new();
    private readonly string _appId;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IOfficialDeliverySnapshot _storage;
    private Snapshot _state = null!;
    private bool _disposed, _retired;

    /// <summary>Takes ownership of storage, including when initialization fails.</summary>
    public OfficialDeliveryLedger(IOfficialDeliverySnapshot storage, string appId, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _appId = appId;
        _clock = clock ?? (() => PlatformClock.Current.UtcNow);
        try
        {
            _ = new OfficialIdentityScope(appId, OfficialIdentityKind.Group);
            Initialize();
            Recover();
        }
        catch
        {
            storage.Dispose();
            throw InvalidStore();
        }
    }

    public OfficialDeliveryDecision AcceptTrigger(OfficialIdentityKey trigger, DateTimeOffset eventTime)
    {
        var (key, scope, window, budget) = TriggerParts(trigger);
        var eventKey = IndexedEventKey(trigger);
        return Write(now =>
        {
            var occurred = eventTime.UtcTicks;
            if (occurred > now || occurred <= now - window.Ticks || occurred > DateTimeOffset.MaxValue.UtcTicks - window.Ticks)
                return Denied("trigger_expired");
            if (occurred <= _state.RevokedThrough) return Denied("trigger_revoked");
            if (_state.Scopes.TryGetValue(scope, out var revoked) && revoked >= occurred) return Denied("trigger_revoked");
            if (_state.Triggers.TryGetValue(key, out var row))
            {
                if (row.EventKeys is null && eventKey is null || eventKey is not null && row.EventKeys?.Contains(eventKey) == true)
                    return Denied("trigger_duplicate");
                if (row.Revoked) return Denied("trigger_revoked");
                if (row.ExpiresAt <= now) return Denied("trigger_expired");
                if (row.EventKeys is null || eventKey is null)
                    return Denied(row.EventKeys is null && eventKey is null ? "trigger_duplicate" : "trigger_identity_ambiguous");
                if (EventCapacityReached()) return Denied("ledger_capacity");
                // Reordered events may shorten the grant, but can never replenish or extend it.
                _state.Triggers[key] = row with { EventKeys = [.. row.EventKeys, eventKey],
                    EventAt = Math.Min(row.EventAt, occurred), ExpiresAt = Math.Min(row.ExpiresAt, occurred + window.Ticks) };
            }
            else
            {
                if (EventCapacityReached()) return Denied("ledger_capacity");
                _state.Triggers.Add(key, new(scope, occurred, occurred + window.Ticks, budget, 0, false, eventKey is null ? null : [eventKey]));
            }
            return new(true, "trigger_accepted");
        });
    }

    public OfficialDeliveryDecision Prepare(OfficialIdentityKey trigger, Guid operationId, string contentDigest, bool requiresUpload = false)
    {
        var (key, _, _, _) = TriggerParts(trigger);
        ValidateOperationId(operationId);
        if (!OfficialIdentityCodec.IsDigest(contentDigest)) throw new ArgumentException("delivery_digest_invalid");
        return Write(now =>
        {
            if (_state.Operations.TryGetValue(operationId, out var existing))
            {
                if (existing.TriggerKey != key || existing.ContentDigest != contentDigest
                    || (existing.Upload is not null) != requiresUpload) return Denied("operation_conflict");
                return new(false, "operation_exists", Find(operationId));
            }
            if (!_state.Triggers.TryGetValue(key, out var row)) return Denied("trigger_missing");
            if (!EventWasAccepted(row, trigger)) return Denied("trigger_identity_ambiguous");
            if (row.Revoked) return Denied("trigger_revoked");
            if (row.ExpiresAt <= now) return Denied("trigger_expired");
            var next = row.NextSequence + 1;
            if (next > row.Budget) return Denied("trigger_budget_exhausted");
            if (_state.Operations.Count >= MaxOperations) return Denied("ledger_capacity");
            _state.Triggers[key] = row with { NextSequence = next };
            _state.Operations.Add(operationId, new(key, contentDigest, next, OfficialDeliveryState.Prepared, now, null, null, 0, 0,
                0, requiresUpload ? new(0, null, 0) : null));
            return new(true, "operation_prepared", Find(operationId));
        });
    }

    public OfficialDeliveryDecision BeginAttempt(Guid operationId, OfficialDeliveryPhase phase = OfficialDeliveryPhase.Message)
    {
        ValidateOperationId(operationId);
        if (!Enum.IsDefined(phase)) throw new ArgumentException("delivery_phase_invalid");
        return Write(now =>
        {
            var existing = Find(operationId);
            if (existing is null) return Denied("operation_missing");
            var operation = _state.Operations[operationId];
            if (!CanBegin(operation, phase)) return new(false, "operation_not_prepared", existing);
            var row = _state.Triggers[operation.TriggerKey];
            if (row.Revoked || row.ExpiresAt <= now)
            {
                Finish(operationId, OfficialDeliveryState.Abandoned, DeliveryResult.Rejected("trigger_invalidated"), 0, now);
                return new(false, "trigger_invalidated", Find(operationId));
            }
            if (phase == OfficialDeliveryPhase.Message && operation.Upload is { ExpiresAt: > 0 } media && media.ExpiresAt <= now)
            {
                Finish(operationId, OfficialDeliveryState.Abandoned, DeliveryResult.Rejected("media_reference_expired"), 0, now);
                return new(false, "media_reference_expired", Find(operationId));
            }
            _state.Operations[operationId] = phase == OfficialDeliveryPhase.Upload
                ? operation with { State = OfficialDeliveryState.Uploading, UpdatedAt = now,
                    Upload = operation.Upload! with { Attempts = operation.Upload!.Attempts + 1 } }
                : operation with { State = OfficialDeliveryState.Attempting, UpdatedAt = now, MessageAttempts = operation.MessageAttempts + 1,
                    MessageAttemptedAt = operation.MessageAttempts == 0 ? now : operation.MessageAttemptedAt };
            return new(true, "attempt_committed", Find(operationId));
        });
    }

    public OfficialDeliveryOperation Complete(Guid operationId, DeliveryResult result, long messageAlias = 0)
    {
        ValidateOperationId(operationId);
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(result.Status) || !ValidReason(result.ReasonCode) || result.RetryAfterSeconds < 0
            || !ValidAlias(messageAlias, result.Status)) throw new ArgumentException("delivery_receipt_invalid");
        return Write(now =>
        {
            var existing = Find(operationId) ?? throw new InvalidOperationException("operation_missing");
            if (existing.State is not (OfficialDeliveryState.Attempting or OfficialDeliveryState.Uploading))
                throw new InvalidOperationException("operation_not_attempting");
            if (existing.State == OfficialDeliveryState.Uploading
                && result.Status is DeliveryStatus.Success or DeliveryStatus.Degraded or DeliveryStatus.PartialSuccess)
                throw new InvalidOperationException("upload_receipt_required");
            Finish(operationId, OfficialDeliveryState.Completed, result, messageAlias, now);
            return Find(operationId)!;
        });
    }

    public void RevokeScope(ConversationId conversation)
    {
        var scope = ScopeKey(conversation);
        Write(now =>
        {
            var all = !_state.Scopes.ContainsKey(scope) && _state.Scopes.Count >= MaxScopes;
            if (all)
            {
                _state = _state with { RevokedThrough = Math.Max(_state.RevokedThrough, now) };
                _state.Scopes.Clear();
            }
            else _state.Scopes[scope] = Math.Max(_state.Scopes.GetValueOrDefault(scope), now);
            foreach (var (key, row) in _state.Triggers.ToArray())
                if (all || row.ScopeKey == scope) _state.Triggers[key] = row with { Revoked = true };
            foreach (var (id, row) in _state.Operations.ToArray())
                if (row.State is OfficialDeliveryState.Prepared or OfficialDeliveryState.Uploaded or OfficialDeliveryState.CredentialRejected
                    && (all || _state.Triggers[row.TriggerKey].ScopeKey == scope))
                    Finish(id, OfficialDeliveryState.Abandoned, DeliveryResult.Rejected("trigger_revoked"), 0, now);
            return true;
        });
    }

    public OfficialDeliveryOperation? Read(Guid operationId)
    {
        ValidateOperationId(operationId);
        lock (_gate) { EnsureOpen(); return Find(operationId); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _storage.Dispose();
        }
    }

    private (string Key, string Scope, TimeSpan Window, int Budget) TriggerParts(OfficialIdentityKey trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (trigger.Scope.AppId != _appId || trigger.Scope.Kind != OfficialIdentityKind.Message
            || trigger.Scope.Direction != OfficialMessageDirection.Inbound || trigger.Scope.Conversation is null)
            throw new ArgumentException("delivery_trigger_scope_invalid");
        var group = trigger.Scope.Conversation.Kind == ConversationKind.GroupChat;
        var credential = new OfficialIdentityKey(trigger.Scope, trigger.NativeId);
        return (Digest(OfficialIdentityCodec.Encode(credential)), ScopeKey(trigger.Scope.Conversation),
            TimeSpan.FromMinutes(group ? 5 : 60), group ? 5 : 4);
    }

    private bool EventCapacityReached() => _state.Triggers.Values.Sum(row => row.EventKeys?.Length ?? 1) >= MaxTriggers;
    private static string? IndexedEventKey(OfficialIdentityKey trigger)
        => trigger.MessageIndex is null ? null : Digest(OfficialIdentityCodec.Encode(trigger));
    private static bool EventWasAccepted(TriggerRow row, OfficialIdentityKey trigger)
        => trigger.MessageIndex is null ? row.EventKeys is null : row.EventKeys?.Contains(IndexedEventKey(trigger)!) == true;

    private string ScopeKey(ConversationId conversation)
    {
        if (conversation.PlatformId != PlatformId.QqOfficial || conversation.AccountScope != _appId || conversation.ThreadId is not null
            || conversation.Kind is not (ConversationKind.GroupChat or ConversationKind.PrivateChat))
            throw new ArgumentException("delivery_conversation_invalid");
        return Digest(OfficialIdentityCodec.Encode(new(new(_appId, conversation.Kind == ConversationKind.GroupChat
            ? OfficialIdentityKind.Group : OfficialIdentityKind.C2cUser), conversation.NativeTargetId)));
    }

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static OfficialDeliveryDecision Denied(string reason) => new(false, reason);
    private static void ValidateOperationId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("delivery_operation_invalid");
    }
    private static bool ValidReason(string? reason) => reason is { Length: > 0 and <= 64 }
        && reason.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static bool ValidAlias(long alias, DeliveryStatus status) => alias == 0
        || alias is >= Channels.AliasBase and <= 9_007_199_254_740_991L && status is DeliveryStatus.Success or DeliveryStatus.Degraded;
}

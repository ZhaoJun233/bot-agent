using System.Globalization;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Platforms;
using BotAgent.Services;

namespace BotAgent.Adapters.Official;

public sealed partial class OfficialDeliveryLedger
{
    private void Initialize()
    {
        var raw = _storage.Load();
        if (raw is null)
            _state = new(4, Digest(_appId), _clock().UtcTicks, 0, new(), new(), new());
        else
        {
            if (Encoding.UTF8.GetByteCount(raw) > IOfficialDeliverySnapshot.MaxBytes) throw InvalidStore();
            using var document = JsonDocument.Parse(raw);
            RejectDuplicateFields(document.RootElement);
            ValidateEventFields(document.RootElement);
            _state = document.RootElement.Deserialize<Snapshot>(SnapshotJson) ?? throw InvalidStore();
        }
        ValidateSnapshot();
        _state = _state with { Version = 4 };
    }

    private void Recover()
    {
        var now = Math.Max(_clock().UtcTicks, _state.ClockFloor);
        foreach (var (id, row) in _state.Operations.ToArray())
        {
            if (row.Recall is { Status: null } recall)
                FinishRecall(id, recall.CredentialRejected ? DeliveryResult.Rejected("credential_rejected")
                    : DeliveryResult.Unknown("delivery_outcome_unknown"), false, now);
            if (row.State is OfficialDeliveryState.Attempting or OfficialDeliveryState.Uploading)
                Finish(id, OfficialDeliveryState.Completed, DeliveryResult.Unknown("delivery_outcome_unknown"), 0, now);
            else if (row.State == OfficialDeliveryState.Prepared)
                Finish(id, OfficialDeliveryState.Abandoned, DeliveryResult.Rejected("abandoned_before_attempt"), 0, now);
            else if (row.State is OfficialDeliveryState.Uploaded or OfficialDeliveryState.CredentialRejected)
                Finish(id, OfficialDeliveryState.Abandoned, DeliveryResult.Rejected("abandoned_before_next_attempt"), 0, now);
        }
        _state = _state with { ClockFloor = now };
        Commit();
    }

    private T Write<T>(Func<long, T> action)
    {
        lock (_gate)
        {
            EnsureOpen();
            var now = _clock().UtcTicks;
            if (now < _state.ClockFloor) throw new InvalidOperationException("official_delivery_clock_regressed");
            var prior = _state;
            _state = prior with { Triggers = new(prior.Triggers), Operations = new(prior.Operations), Scopes = new(prior.Scopes) };
            T result;
            try
            {
                Prune(now);
                result = action(now);
                _state = _state with { ClockFloor = now };
            }
            catch { _state = prior; throw; }
            Commit();
            return result;
        }
    }

    private void Commit()
    {
        try
        {
            var snapshot = JsonSerializer.Serialize(_state, SnapshotJson);
            if (Encoding.UTF8.GetByteCount(snapshot) > IOfficialDeliverySnapshot.MaxBytes) throw InvalidStore();
            _storage.Commit(snapshot);
        }
        catch
        {
            // A publication failure may be ambiguous. This instance must never issue another grant.
            _retired = true;
            throw InvalidStore();
        }
    }

    private void Prune(long now)
    {
        var day = now - TimeSpan.FromDays(1).Ticks;
        var week = now - TimeSpan.FromDays(7).Ticks;
        foreach (var (id, row) in _state.Operations.ToArray())
            if (row.State is OfficialDeliveryState.Completed or OfficialDeliveryState.Abandoned
                && row.UpdatedAt < day && (row.Status != DeliveryStatus.Unknown || row.UpdatedAt < week)
                && _state.Triggers[row.TriggerKey].ExpiresAt < day) _state.Operations.Remove(id);
        var referenced = _state.Operations.Values.Select(row => row.TriggerKey).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, row) in _state.Triggers.ToArray())
            if (row.ExpiresAt < week && !referenced.Contains(key)) _state.Triggers.Remove(key);
        foreach (var (key, through) in _state.Scopes.ToArray())
            if (through < now - TimeSpan.FromHours(1).Ticks) _state.Scopes.Remove(key);
    }

    private OfficialDeliveryOperation? Find(Guid operationId)
    {
        if (!_state.Operations.TryGetValue(operationId, out var row)) return null;
        DeliveryResult? result = row.Status is { } status
            ? new(status, row.MessageAlias == 0 ? null : row.MessageAlias.ToString(CultureInfo.InvariantCulture),
                row.Reason!, RetryAfterSeconds: row.RetryAfter) : null;
        return new(operationId, row.Sequence, row.State, result)
        { MessageAttempts = row.MessageAttempts, UploadAttempts = row.Upload?.Attempts ?? 0 };
    }

    private void Finish(Guid operationId, OfficialDeliveryState state, DeliveryResult result, long alias, long now)
        => _state.Operations[operationId] = _state.Operations[operationId] with
        { State = state, Status = result.Status, Reason = result.ReasonCode, MessageAlias = alias, RetryAfter = result.RetryAfterSeconds, UpdatedAt = now };

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_retired) throw InvalidStore();
    }
    private static InvalidOperationException InvalidStore() => new("official_delivery_store_unavailable");
}

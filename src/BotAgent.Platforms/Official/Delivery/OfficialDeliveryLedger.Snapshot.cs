using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Platforms;

namespace BotAgent.Adapters.Official;

public sealed partial class OfficialDeliveryLedger
{
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private sealed record Snapshot(int Version, string ApplicationHash, long ClockFloor, long RevokedThrough,
        Dictionary<string, TriggerRow> Triggers, Dictionary<Guid, OperationRow> Operations, Dictionary<string, long> Scopes);
    private sealed record TriggerRow(string ScopeKey, long EventAt, long ExpiresAt, int Budget, int NextSequence, bool Revoked, string[]? EventKeys = null);
    private sealed record OperationRow(string TriggerKey, string ContentDigest, int Sequence, OfficialDeliveryState State,
        long UpdatedAt, DeliveryStatus? Status, string? Reason, long MessageAlias, int RetryAfter, int MessageAttempts, UploadRow? Upload,
        long MessageAttemptedAt = 0, RecallRow? Recall = null);
    private sealed record UploadRow(int Attempts, string? ReceiptDigest, long ExpiresAt);
    private sealed record RecallRow(int Attempts, long UpdatedAt, DeliveryStatus? Status, string? Reason, int RetryAfter, bool CredentialRejected);

    private void ValidateSnapshot()
    {
        if (_state.Version is not (2 or 3 or 4) || _state.ApplicationHash != Digest(_appId)
            || _state.ClockFloor <= 0 || _state.ClockFloor > DateTimeOffset.MaxValue.UtcTicks
            || _state.RevokedThrough < 0 || _state.RevokedThrough > _state.ClockFloor
            || _state.Triggers is null || _state.Operations is null || _state.Scopes is null
            || _state.Triggers.Count > MaxTriggers || _state.Operations.Count > MaxOperations || _state.Scopes.Count > MaxScopes)
            throw InvalidStore();
        var eventKeys = new HashSet<string>(StringComparer.Ordinal);
        var eventCount = 0;
        foreach (var (key, row) in _state.Triggers)
        {
            if (!OfficialIdentityCodec.IsDigest(key) || row is null || !OfficialIdentityCodec.IsDigest(row.ScopeKey)
                || row.EventAt <= 0 || row.EventAt > _state.ClockFloor || row.ExpiresAt > DateTimeOffset.MaxValue.UtcTicks
                || row.Budget is not (4 or 5) || row.NextSequence < 0 || row.NextSequence > row.Budget
                || row.ExpiresAt - row.EventAt != TimeSpan.FromMinutes(row.Budget == 5 ? 5 : 60).Ticks
                || !row.Revoked && (row.EventAt <= _state.RevokedThrough || row.EventAt <= _state.Scopes.GetValueOrDefault(row.ScopeKey)))
                throw InvalidStore();
            eventCount += row.EventKeys?.Length ?? 1;
            if (eventCount > MaxTriggers || row.EventKeys is { Length: 0 }
                || row.EventKeys?.Any(value => !OfficialIdentityCodec.IsDigest(value) || !eventKeys.Add(value)) == true)
                throw InvalidStore();
        }
        var sequences = new HashSet<(string, int)>();
        foreach (var (id, row) in _state.Operations)
        {
            if (id == Guid.Empty || row is null || row.TriggerKey is null || !_state.Triggers.TryGetValue(row.TriggerKey, out var trigger)
                || !OfficialIdentityCodec.IsDigest(row.ContentDigest) || row.Sequence <= 0 || row.Sequence > trigger.NextSequence
                || !sequences.Add((row.TriggerKey, row.Sequence)) || !Enum.IsDefined(row.State)
                || row.UpdatedAt < trigger.EventAt || row.UpdatedAt > _state.ClockFloor || row.RetryAfter < 0) throw InvalidStore();
            ValidateAttempts(row);
            ValidateRecall(row);
            if (row.State is not (OfficialDeliveryState.Completed or OfficialDeliveryState.Abandoned))
            {
                if (row.Status is not null || row.Reason is not null || row.MessageAlias != 0 || row.RetryAfter != 0
                    || row.State is OfficialDeliveryState.Prepared or OfficialDeliveryState.Uploaded or OfficialDeliveryState.CredentialRejected
                        && trigger.Revoked) throw InvalidStore();
            }
            else if (row.Status is not { } status || !Enum.IsDefined(status) || !ValidReason(row.Reason)
                || !ValidAlias(row.MessageAlias, status) || row.State == OfficialDeliveryState.Abandoned && status != DeliveryStatus.Rejected)
                throw InvalidStore();
        }
        foreach (var (key, through) in _state.Scopes)
            if (!OfficialIdentityCodec.IsDigest(key) || through <= 0 || through > _state.ClockFloor) throw InvalidStore();
    }

    private static void ValidateEventFields(JsonElement root)
    {
        var version = root.GetProperty("Version").GetInt32();
        if (version is not (2 or 3 or 4)) throw InvalidStore();
        foreach (var row in root.GetProperty("Triggers").EnumerateObject())
            if (row.Value.TryGetProperty("EventKeys", out _) != (version >= 3)) throw InvalidStore();
        foreach (var row in root.GetProperty("Operations").EnumerateObject())
            if (row.Value.TryGetProperty("MessageAttemptedAt", out _) != (version == 4)
                || row.Value.TryGetProperty("Recall", out _) != (version == 4)) throw InvalidStore();
    }

    private static void RejectDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in element.EnumerateObject())
            {
                if (!fields.Add(field.Name)) throw InvalidStore();
                RejectDuplicateFields(field.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateFields(item);
    }
}

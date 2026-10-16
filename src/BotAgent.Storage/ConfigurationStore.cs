using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Ports;
using Microsoft.Data.Sqlite;

namespace BotAgent.Storage;

/// <summary>C3 transaction and recovery module. Host projection participates through a private typed seam.</summary>
public sealed class ConfigurationStore : IConfigurationChanges
{
    private const string Revision = "cfg-0.3.2";
    private const string AliasCodecRevision = "cfg-0.3.2/alias-1";
    private const long LifetimeSeconds = 86400;
    private const int FutureSeconds = 300;
    private const string ReadFloorSql = "SELECT floor FROM configuration_state WHERE id=1";
    private const string SaveFloorSql = "UPDATE configuration_state SET floor=$floor WHERE id=1";
    private const string SaveVersionSql = "UPDATE configuration_state SET version=$version WHERE id=1";
    private const string PruneReceiptsSql = "DELETE FROM configuration_operations WHERE terminal=1 AND expires_unix<=$floor";
    private const string ReadOperationSql = "SELECT o.receipt,o.fingerprint,g.codec_revision,o.namespace,o.terminal FROM configuration_operations o " +
        "LEFT JOIN configuration_generations g ON g.generation=o.generation " +
        "WHERE o.authority=$a AND o.subject=$s AND o.resource=$r AND o.operation_key=$k";
    private const string InsertOperationSql = "INSERT INTO configuration_operations(authority,subject,resource,operation_key,namespace," +
        "generation,expires_unix,fingerprint,receipt,terminal) VALUES($a,$s,$r,$k,$namespace,$g,$expires,$fp,$receipt,$terminal)";
    private const string UpdateOperationSql = "UPDATE configuration_operations SET receipt=$receipt,terminal=$terminal " +
        "WHERE authority=$a AND subject=$s AND resource=$r AND operation_key=$k";
    private const string ReadGenerationSql = "SELECT hmac_key,codec_revision FROM configuration_generations WHERE generation=$generation";
    private static readonly object ActivationGate = new();
    private static ConfigurationStore? _active;
    private static int _activationInProgress;
    internal static bool RequiresCoordinatedConfigurationWrites
        => Volatile.Read(ref _activationInProgress) != 0 || Volatile.Read(ref _active) is not null;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Codec = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly IClock _time;
    private readonly ConfigurationStorageParticipant _participant;
    private readonly ConfigurationGeneration _generation;
    private readonly long _anchorFloor;
    private readonly long _anchorTicks;

    private ConfigurationStore(IClock time, ConfigurationStorageParticipant participant,
        ConfigurationGeneration generation, long floor, long ticks)
    { _time = time; _participant = participant; _generation = generation; _anchorFloor = floor; _anchorTicks = ticks; }

    internal static ConfigurationStore Activate(IClock clock, ConfigurationStorageParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(participant);
        lock (ActivationGate)
        {
            if (_active is not null)
            {
                if (!ReferenceEquals(clock, _active._time) || !ReferenceEquals(participant, _active._participant))
                    throw new InvalidOperationException("configuration_already_activated");
                return _active;
            }
            var generation = new ConfigurationGeneration(Guid.NewGuid());
            var key = RandomNumberGenerator.GetBytes(32);
            var floor = 0L;
            var ticks = clock.TickCount;
            try
            {
                Volatile.Write(ref _activationInProgress, 1);
                AppDatabase.Write(conn =>
                {
                    AppDatabase.EnsureConfigurationModule(conn);
                    floor = Math.Max(ReadLong(conn, ReadFloorSql),
                        clock.Now.ToUnixTimeSeconds());
                    if (floor < 0 || ticks < 0) throw new InvalidOperationException("configuration_clock_invalid");
                    var initialVersion = Version(conn);
                    if (participant.Initialize(conn, floor))
                    {
                        var migratedVersion = new ConfigurationVersion(checked(initialVersion.Value + 1));
                        AppDatabase.Exec(conn, SaveVersionSql, ("$version", migratedVersion.Value));
                        participant.AppendInitializationAudit(conn, initialVersion, migratedVersion);
                    }
                    AppDatabase.Exec(conn, SaveFloorSql, ("$floor", floor));
                    AppDatabase.Exec(conn, PruneReceiptsSql, ("$floor", floor));
                    AppDatabase.Exec(conn, "DELETE FROM configuration_generations WHERE generation NOT IN " +
                        "(SELECT generation FROM configuration_operations)");
                    if (ReadLong(conn, "SELECT COUNT(*) FROM configuration_generations") >= 4097)
                        throw new InvalidOperationException("configuration_generation_capacity");
                    AppDatabase.Exec(conn,
                        "INSERT INTO configuration_generations(generation,hmac_key,codec_revision) VALUES($gen,$key,$codec)",
                        ("$gen", generation.Value.ToString("N")), ("$key", key), ("$codec", AliasCodecRevision));
                });
                return _active = new(clock, participant, generation, floor, ticks);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                Volatile.Write(ref _activationInProgress, 0);
            }
        }
    }

    public ValueTask<ConfigurationReadResult> ReadAsync(ConfigurationCaller caller, ConfigurationReadKind kind,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind) || !Allowed(caller, kind == ConfigurationReadKind.Overview
                ? ConfigurationAccess.ReadOverview : ConfigurationAccess.ReadEditable))
            return ValueTask.FromResult(EmptyRead(ConfigurationResultCode.Forbidden));
        ct.ThrowIfCancellationRequested();
        try
        {
            using var connection = AppDatabase.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var version = Version(connection);
            if (!Allowed(caller, kind == ConfigurationReadKind.Overview
                    ? ConfigurationAccess.ReadOverview : ConfigurationAccess.ReadEditable))
                return ValueTask.FromResult(EmptyRead(ConfigurationResultCode.Forbidden));
            var serverTime = _time.Now.ToUnixTimeSeconds();
            var result = _participant.Read(connection, caller, kind, version, _generation,
                serverTime, LatestApply(connection)) with { Version = version, Generation = _generation,
                    ContractRevision = Revision, ServerUnixSeconds = serverTime };
            transaction.Commit();
            return ValueTask.FromResult(result);
        }
        catch (Exception) { return ValueTask.FromResult(EmptyRead(ConfigurationResultCode.StorageUnavailable)); }
    }

    public ValueTask<ConfigurationSaveResult> SaveAsync(ConfigurationCaller caller, ConfigurationSaveRequest request,
        CancellationToken ct = default)
        => SaveCore(caller, request, ConfigurationStorageProfile.Strict, ct);

    internal static void AppendDeviceRuntimeAudit(SqliteConnection connection, DeviceKey key)
        => (_active ?? throw new InvalidOperationException("configuration_not_activated"))
            ._participant.AppendDeviceRuntimeAudit(connection, key);

    internal ValueTask<ConfigurationSaveResult> SaveLegacyAsync(ConfigurationCaller caller,
        ConfigurationOperationKey key, ConfigurationPatch changes, ImmutableArray<SecretEdit> secrets,
        CancellationToken ct = default)
        => SaveCore(caller, new ConfigurationSaveRequest(Revision, new(0), key, changes, secrets),
            ConfigurationStorageProfile.Legacy, ct);

    internal async ValueTask<LegacySaveOutcome> SaveLegacyWithMoodAsync(ConfigurationCaller caller,
        ConfigurationOperationKey key, ConfigurationPatch changes, ImmutableArray<SecretEdit> secrets,
        FieldEdit<string> mood, CancellationToken ct = default)
    {
        var result = await SaveCore(caller, new(Revision, new(0), key, changes, secrets),
            ConfigurationStorageProfile.Legacy, ct, mood);
        if (result.Code is not (ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges))
            return new(result, ImmutableArray<LegacyActionResult>.Empty);
        // This only reads the reservation. The trusted Host separately decides first-request execution.
        var found = await FindLegacyAsync(caller, key, CancellationToken.None);
        return new(result, found.ConfigurationResult.Code == ConfigurationResultCode.Ok
            ? found.LegacyActionResults : ImmutableArray.Create(MoodResult(LegacyActionState.Unknown, "storage_unavailable")));
    }

    internal ValueTask<LegacyFindOutcome> FindLegacyAsync(ConfigurationCaller caller,
        ConfigurationOperationKey key, CancellationToken ct = default)
    {
        var empty = ImmutableArray<LegacyActionResult>.Empty;
        if (!Allowed(caller, ConfigurationAccess.SaveConfiguration))
            return ValueTask.FromResult(new LegacyFindOutcome(new(ConfigurationResultCode.Forbidden, null), empty));
        if (!ValidKey(key))
            return ValueTask.FromResult(new LegacyFindOutcome(new(ConfigurationResultCode.ValidationFailed, null), empty));
        ct.ThrowIfCancellationRequested();
        var result = new LegacyFindOutcome(new(ConfigurationResultCode.StorageUnavailable, null), empty);
        try
        {
            AppDatabase.Write(conn =>
            {
                if (!Allowed(caller, ConfigurationAccess.SaveConfiguration))
                { result = new(new(ConfigurationResultCode.Forbidden, null), empty); return; }
                var code = Govern(conn, key, out _);
                var receipt = ReadReceipt(conn, caller, key, out var fingerprint);
                if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
                if (code == ConfigurationResultCode.KeyExpired)
                { result = new(new(code, null), empty); return; }
                if (receipt is null)
                {
                    result = new(new(key.Generation != _generation ? ConfigurationResultCode.KeyGenerationMismatch
                        : code == ConfigurationResultCode.Ok ? ConfigurationResultCode.NotFound : code, null), empty);
                    return;
                }
                var action = ReadMoodReceipt(conn, caller, key, out var actionFingerprint);
                if (actionFingerprint is not null) CryptographicOperations.ZeroMemory(actionFingerprint);
                result = new(new(ConfigurationResultCode.Ok, receipt), action is null ? empty : ImmutableArray.Create(action));
            });
        }
        catch (Exception) { result = new(new(ConfigurationResultCode.StorageUnavailable, null), empty); }
        return ValueTask.FromResult(result);
    }

    internal LegacyActionResult ApplyLegacyMood(ConfigurationCaller caller, ConfigurationOperationKey key,
        FieldEdit<string> mood, MoodStore instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!Allowed(caller, ConfigurationAccess.SaveConfiguration)) return MoodResult(LegacyActionState.Failed, "forbidden");
        if (!ValidKey(key)) return MoodResult(LegacyActionState.Failed, "validation_failed");
        try { mood = NormalizeMood(mood); }
        catch (ArgumentException) { return MoodResult(LegacyActionState.Failed, "validation_failed"); }
        if (mood.Kind == EditKind.Keep) return MoodResult(LegacyActionState.Failed, "validation_failed");
        var result = MoodResult(LegacyActionState.Unknown, "storage_unavailable");
        var canRun = false;
        try
        {
            AppDatabase.Write(conn =>
            {
                if (!Allowed(caller, ConfigurationAccess.SaveConfiguration))
                { result = MoodResult(LegacyActionState.Failed, "forbidden"); return; }
                var code = Govern(conn, key, out _);
                if (code != ConfigurationResultCode.Ok)
                { result = MoodResult(LegacyActionState.Failed, "operation_unavailable"); return; }
                var configuration = ReadReceipt(conn, caller, key, out var configurationFingerprint);
                try
                {
                    if (configuration is null || configurationFingerprint is null)
                    { result = MoodResult(LegacyActionState.Failed, "operation_unavailable"); return; }
                    var previous = ReadMoodReceipt(conn, caller, key, out var previousFingerprint);
                    var expected = MoodFingerprint(conn, caller, key, mood, configurationFingerprint);
                    try
                    {
                        if (previous is null || previousFingerprint is null || expected is null
                            || !CryptographicOperations.FixedTimeEquals(previousFingerprint, expected))
                        { result = MoodResult(LegacyActionState.Failed, "idempotency_conflict"); return; }
                        result = previous;
                        if (previous.State != LegacyActionState.NotStarted) return;
                        var known = configuration.Decision == ConfigurationDecision.NoChanges
                            ? configuration.CommitState == CommitState.NotCommitted
                            : configuration.CommitState == CommitState.Committed;
                        if (!known) return;
                        result = MoodResult(LegacyActionState.Unknown, "action_unknown");
                        UpdateMoodReceipt(conn, caller, key, result);
                        canRun = true;
                    }
                    finally
                    {
                        if (previousFingerprint is not null) CryptographicOperations.ZeroMemory(previousFingerprint);
                        if (expected is not null) CryptographicOperations.ZeroMemory(expected);
                    }
                }
                finally { if (configurationFingerprint is not null) CryptographicOperations.ZeroMemory(configurationFingerprint); }
            });
        }
        catch (Exception) { return MoodResult(LegacyActionState.Unknown, "storage_unavailable"); }
        if (!canRun) return result;
        // No database writer is held while acquiring MoodStore's existing gate.
        try
        {
            var observed = instance.ApplyLegacyTextObserved(mood.TryGetValue(out var text) ? text : null, _time.Now);
            result = observed.State switch
            {
                MoodStore.MoodTextWriteState.Committed or MoodStore.MoodTextWriteState.Unchanged => MoodResult(LegacyActionState.Applied),
                MoodStore.MoodTextWriteState.Failed or MoodStore.MoodTextWriteState.Rejected => MoodResult(LegacyActionState.Failed, "action_failed"),
                _ => MoodResult(LegacyActionState.Unknown, "action_unknown")
            };
            // Persist actual completion even if request authorization was revoked after the effect.
            AppDatabase.Write(conn => UpdateMoodReceipt(conn, caller, key, result));
            return result;
        }
        catch (Exception) { return MoodResult(LegacyActionState.Unknown, "storage_unavailable"); }
    }

    private static LegacyActionResult MoodResult(LegacyActionState state, string? code = null) => new("mood", state, code);
    private static FieldEdit<string> NormalizeMood(FieldEdit<string> mood)
    {
        if (mood.Kind == EditKind.Keep) return FieldEdit<string>.Keep();
        if (mood.Kind == EditKind.Clear) return FieldEdit<string>.Clear();
        if (mood.Kind != EditKind.Set || !mood.TryGetValue(out var raw) || !MoodStore.TryNormalizeText(raw, out var text))
            throw new ArgumentException("invalid_mood");
        _ = StrictUtf8.GetByteCount(text);
        return FieldEdit<string>.Set(text);
    }
    private static LegacyActionResult? ReadMoodReceipt(SqliteConnection conn, ConfigurationCaller caller,
        ConfigurationOperationKey key, out byte[]? fingerprint)
    {
        var json = ReadOperation(conn, caller, key, true, out fingerprint);
        return json is null ? null : DecodeMoodReceipt(json);
    }
    private static LegacyActionResult DecodeMoodReceipt(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("legacy_action_invalid");
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("action" or "state" or "errorCode") || !seen.Add(property.Name))
                throw new InvalidDataException("legacy_action_invalid");
        if (seen.Count != 3 || !root.GetProperty("state").TryGetInt32(out var state))
            throw new InvalidDataException("legacy_action_invalid");
        var result = JsonSerializer.Deserialize<LegacyActionResult>(json, Codec);
        if (result is null || result.Action != "mood" || !Enum.IsDefined(result.State) || (int)result.State != state
            || result.ErrorCode != (result.State switch { LegacyActionState.Unknown => "action_unknown",
                LegacyActionState.Failed => "action_failed", _ => null }))
            throw new InvalidDataException("legacy_action_invalid");
        return result;
    }
    private static void UpdateMoodReceipt(SqliteConnection conn, ConfigurationCaller caller,
        ConfigurationOperationKey key, LegacyActionResult result)
        => UpdateOperation(conn, caller, key, JsonSerializer.Serialize(result, Codec),
            result.State is LegacyActionState.Applied or LegacyActionState.Failed, true);
    private static byte[]? MoodFingerprint(SqliteConnection conn, ConfigurationCaller caller,
        ConfigurationOperationKey key, FieldEdit<string> mood, byte[] configurationFingerprint)
    {
        using var command = conn.CreateCommand();
        command.CommandText = ReadGenerationSql;
        command.Parameters.AddWithValue("$generation", key.Generation.Value.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !KnownCodec(reader.GetString(1))) return null;
        var secret = (byte[])reader.GetValue(0);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new object?[] { caller.AuthorityId, caller.SubjectId,
            caller.ResourceId, "legacy-mood1/1", EncodeKey(key), Convert.ToHexString(configurationFingerprint),
            (int)mood.Kind, mood.TryGetValue(out var text) ? text : null }, Codec);
        try { return HMACSHA256.HashData(secret, payload); }
        finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(payload); }
    }

    private ValueTask<ConfigurationSaveResult> SaveCore(ConfigurationCaller caller, ConfigurationSaveRequest request,
        ConfigurationStorageProfile profile, CancellationToken ct, FieldEdit<string> mood = default)
    {
        if (!Allowed(caller, ConfigurationAccess.SaveConfiguration))
            return ValueTask.FromResult(Rejected(ConfigurationResultCode.Forbidden));
        if (request is null || request.Changes is null || request.Secrets.IsDefault)
            return ValueTask.FromResult(Rejected(ConfigurationResultCode.ValidationFailed));
        if (request.ContractRevision != Revision)
            return ValueTask.FromResult(Rejected(ConfigurationResultCode.UnsupportedContractRevision));
        if (request.Secrets.Any(s => s is null || s.Kind != SecretEditKind.Keep)
            && !Allowed(caller, ConfigurationAccess.EditAllowedSecrets))
            return ValueTask.FromResult(Rejected(ConfigurationResultCode.Forbidden));
        if (!ValidKey(request.Key) || request.ExpectedVersion.Value < 0)
            return ValueTask.FromResult(Rejected(ConfigurationResultCode.ValidationFailed));
        if (ct.IsCancellationRequested) return ValueTask.FromResult(Rejected(ConfigurationResultCode.CancelledBeforeCommit));
        OwnedIntent? owned = null;
        try
        {
            mood = NormalizeMood(mood);
            owned = Own(request);
            var outcome = Rejected(ConfigurationResultCode.StorageUnavailable);
            var callbackComplete = false;
            try
            {
                AppDatabase.Write(conn =>
                {
                    if (!Allowed(caller, ConfigurationAccess.SaveConfiguration)
                        || (owned.Secrets.Any(s => s.Kind != SecretEditKind.Keep)
                            && !Allowed(caller, ConfigurationAccess.EditAllowedSecrets)))
                        outcome = Rejected(ConfigurationResultCode.Forbidden);
                    else outcome = SaveInTransaction(conn, caller, request, owned, profile, ct, mood);
                    callbackComplete = true;
                });
            }
            catch (Exception)
            {
                outcome = callbackComplete
                    ? outcome with { Code = ConfigurationResultCode.UnknownCommit,
                        Receipt = outcome.Receipt is null ? null : outcome.Receipt with
                            { CommitState = CommitState.Unknown, CommittedVersion = null, ApplyState = ApplyState.Unknown } }
                    : Rejected(ConfigurationResultCode.CommitFailed);
            }
            return ValueTask.FromResult(outcome);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or ObjectDisposedException or OverflowException)
        { return ValueTask.FromResult(Rejected(ConfigurationResultCode.ValidationFailed)); }
        finally { owned?.Dispose(); }
    }

    private ConfigurationSaveResult SaveInTransaction(SqliteConnection conn, ConfigurationCaller caller,
        ConfigurationSaveRequest request, OwnedIntent owned, ConfigurationStorageProfile profile, CancellationToken ct,
        FieldEdit<string> mood = default)
    {
        var version = Version(conn);
        var governance = Govern(conn, request.Key, out var floor);
        byte[]? protectedFingerprint = null;
        var writesStarted = false;
        byte[]? fingerprint = null;
        try
        {
            var record = ReadReceipt(conn, caller, request.Key, out protectedFingerprint);
            fingerprint = Fingerprint(conn, caller, request, owned, profile, out owned);
            if (record is not null && (fingerprint is null || protectedFingerprint is null
                    || !CryptographicOperations.FixedTimeEquals(fingerprint, protectedFingerprint)))
                return Rejected(ConfigurationResultCode.IdempotencyConflict, version);
            if (governance == ConfigurationResultCode.KeyExpired) return Rejected(governance, version);
            var previousMood = ReadMoodReceipt(conn, caller, request.Key, out var previousMoodFingerprint);
            try
            {
                if (record is not null && (mood.Kind == EditKind.Keep) != (previousMood is null))
                    return Rejected(ConfigurationResultCode.IdempotencyConflict, version);
                if (previousMood is not null)
                {
                    var expected = MoodFingerprint(conn, caller, request.Key, mood, fingerprint!);
                    try
                    {
                        if (expected is null || previousMoodFingerprint is null
                            || !CryptographicOperations.FixedTimeEquals(expected, previousMoodFingerprint))
                            return Rejected(ConfigurationResultCode.IdempotencyConflict, version);
                    }
                    finally { if (expected is not null) CryptographicOperations.ZeroMemory(expected); }
                }
            }
            finally { if (previousMoodFingerprint is not null) CryptographicOperations.ZeroMemory(previousMoodFingerprint); }
            if (record is not null)
                return new(record.Decision == ConfigurationDecision.NoChanges ? ConfigurationResultCode.NoChanges
                    : ConfigurationResultCode.Ok, true, record, version, ImmutableArray<FieldError>.Empty);
            if (request.Key.Generation != _generation)
                return Rejected(ConfigurationResultCode.KeyGenerationMismatch, version);
            if (governance != ConfigurationResultCode.Ok) return Rejected(governance, version);
            if (!HasCapacity(conn, caller, floor, mood.Kind == EditKind.Keep ? 1 : 2))
                return Rejected(ConfigurationResultCode.CapacityExceeded, version);
            if (profile == ConfigurationStorageProfile.Strict && request.ExpectedVersion != version)
                return Rejected(ConfigurationResultCode.VersionConflict, version);
            if (ct.IsCancellationRequested) return Rejected(ConfigurationResultCode.CancelledBeforeCommit, version);
            var mutations = owned.Secrets.Select(s => s.Slot == WritableSecretSlot.ModelApiKey
                ? s.Kind == SecretEditKind.Keep ? ConfigurationStorageBridge.ObserveModelApiKey(conn)
                    : ConfigurationStorageBridge.ProjectModelApiKeyMutation(s.Kind, s.NormalizedValue)
                : new SecretMutation(s.Slot, s.Kind)).ToImmutableArray();
            var proposed = _participant.SecretSources(conn, mutations);
            if (proposed.IsDefault || proposed.Length != 6
                || proposed.Select(s => s.Slot).Distinct().Count() != 6)
                throw new InvalidOperationException("invalid_secret_projection");
            var prepared = _participant.Prepare(conn, owned.Patch, proposed, mutations, profile);
            if (prepared.Errors.IsDefault || prepared.DeviceKeyAssignments.IsDefault || prepared.Errors.Length > 32
                || prepared.FailureCode is not (ConfigurationResultCode.ValidationFailed or ConfigurationResultCode.LegacyNeedsReview))
                throw new InvalidOperationException("invalid_configuration_participant");
            if (prepared.Errors.Length != 0)
                return new(prepared.FailureCode, false, null, version, prepared.Errors);
            var changed = prepared.Changed || SecretsChanged(conn, owned) || ModesChanged(conn, owned);
            if (changed && version.Value == long.MaxValue)
                return Rejected(ConfigurationResultCode.ValidationFailed, version);
            var nextVersion = changed ? new ConfigurationVersion(checked(version.Value + 1)) : version;
            var receipt = new ConfigurationReceipt(request.Key, Revision,
                changed ? ConfigurationDecision.Changed : ConfigurationDecision.NoChanges, version,
                changed ? nextVersion : null, changed ? CommitState.Committed : CommitState.NotCommitted,
                ApplyState.NotStarted, ImmutableArray<ApplyStepReport>.Empty,
                changed ? prepared.DeviceKeyAssignments : ImmutableArray<DeviceKeyAssignment>.Empty);
            if (!ReceiptFits(receipt)) return Rejected(ConfigurationResultCode.CapacityExceeded, version);
            if (ct.IsCancellationRequested) return Rejected(ConfigurationResultCode.CancelledBeforeCommit, version);
            writesStarted = true;
            if (changed)
            {
                prepared.Persist(conn);
                PersistSecrets(conn, owned, floor);
                PersistModes(conn, owned);
                AppDatabase.Exec(conn, SaveVersionSql, ("$version", nextVersion.Value));
            }
            _participant.AppendAudit(conn, receipt, owned.Fields);
            InsertReceipt(conn, caller, receipt, fingerprint!);
            if (mood.Kind != EditKind.Keep)
            {
                var moodFingerprint = MoodFingerprint(conn, caller, request.Key, mood, fingerprint!)
                    ?? throw new ReceiptCodecException();
                try { InsertOperation(conn, caller, request.Key, JsonSerializer.Serialize(
                    new LegacyActionResult("mood", LegacyActionState.NotStarted, null), Codec), moodFingerprint, false, true); }
                finally { CryptographicOperations.ZeroMemory(moodFingerprint); }
            }
            return new(changed ? ConfigurationResultCode.Ok : ConfigurationResultCode.NoChanges,
                false, receipt, nextVersion, ImmutableArray<FieldError>.Empty);
        }
        catch (AliasIntentException ex)
        {
            return new(ConfigurationResultCode.ValidationFailed, false, null, version,
                ImmutableArray.Create(new FieldError(new FieldAddress(ex.Field, null), ex.Code)));
        }
        // Before user writes a codec rejection may persist governance only; afterwards it must abort the TX.
        catch (ReceiptCodecException) when (!writesStarted) { return Rejected(ConfigurationResultCode.StorageUnavailable, version); }
        finally
        {
            if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
            if (protectedFingerprint is not null) CryptographicOperations.ZeroMemory(protectedFingerprint);
        }
    }

    public ValueTask<ConfigurationFindResult> FindAsync(ConfigurationCaller caller, ConfigurationOperationKey key,
        CancellationToken ct = default)
    {
        if (!Allowed(caller, ConfigurationAccess.SaveConfiguration))
            return ValueTask.FromResult(new ConfigurationFindResult(ConfigurationResultCode.Forbidden, null));
        if (!ValidKey(key)) return ValueTask.FromResult(new ConfigurationFindResult(ConfigurationResultCode.ValidationFailed, null));
        ct.ThrowIfCancellationRequested();
        var result = new ConfigurationFindResult(ConfigurationResultCode.StorageUnavailable, null);
        try
        {
            AppDatabase.Write(conn =>
            {
                if (!Allowed(caller, ConfigurationAccess.SaveConfiguration))
                { result = new(ConfigurationResultCode.Forbidden, null); return; }
                var code = Govern(conn, key, out _);
                var record = ReadReceipt(conn, caller, key, out var fingerprint);
                if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
                if (code == ConfigurationResultCode.KeyExpired) result = new(code, null);
                else if (record is not null) result = new(ConfigurationResultCode.Ok, record);
                else if (key.Generation != _generation) result = new(ConfigurationResultCode.KeyGenerationMismatch, null);
                else result = new(code == ConfigurationResultCode.Ok ? ConfigurationResultCode.NotFound : code, null);
            });
        }
        catch (Exception) { result = new(ConfigurationResultCode.StorageUnavailable, null); }
        return ValueTask.FromResult(result);
    }

    /// <summary>Persist an uncertain stage claim before executing any effect; an existing claim is never rerun.</summary>
    internal (bool CanRun, ConfigurationFindResult Result) TryStartApplyStep(ConfigurationCaller caller,
        ConfigurationOperationKey key, ApplyStep step)
    {
        if (!ValidKey(key) || !Enum.IsDefined(step))
            return (false, new(ConfigurationResultCode.ValidationFailed, null));
        if (!Allowed(caller, ConfigurationAccess.SaveConfiguration))
            return (false, new(ConfigurationResultCode.Forbidden, null));
        var result = new ConfigurationFindResult(ConfigurationResultCode.StorageUnavailable, null);
        var canRun = false;
        try
        {
            AppDatabase.Write(conn =>
            {
                if (!Allowed(caller, ConfigurationAccess.SaveConfiguration))
                { result = new(ConfigurationResultCode.Forbidden, null); return; }
                var receipt = ReadReceipt(conn, caller, key, out var fingerprint);
                if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
                if (receipt is null) { result = new(ConfigurationResultCode.NotFound, null); return; }
                if (receipt.Decision != ConfigurationDecision.Changed || receipt.CommitState != CommitState.Committed)
                { result = new(ConfigurationResultCode.ValidationFailed, receipt); return; }
                var previous = receipt.Steps.SingleOrDefault(r => r.Step == step);
                if (previous is { State: not ApplyState.NotStarted })
                { result = new(ConfigurationResultCode.Ok, receipt); return; }
                var current = receipt.CommittedVersion == Version(conn);
                var report = new ApplyStepReport(step, current ? ApplyState.Unknown : ApplyState.Failed,
                    current ? "apply_unknown" : "configuration_superseded");
                var reports = receipt.Steps.Where(r => r.Step != step).Append(report).OrderBy(r => r.Step).ToImmutableArray();
                var updated = receipt with { Steps = reports, ApplyState = AggregateApply(reports) };
                PersistApplyReceipt(conn, caller, key, updated);
                result = new(ConfigurationResultCode.Ok, updated);
                canRun = current;
            });
            return (canRun, result);
        }
        catch { return (false, new(ConfigurationResultCode.StorageUnavailable, null)); }
    }

    /// <summary>Record actual post-commit stages; this never runs a stage or repeats configuration writes.</summary>
    internal ConfigurationFindResult RecordApply(ConfigurationCaller caller, ConfigurationOperationKey key,
        ImmutableArray<ApplyStepReport> reports)
    {
        var allowedErrors = new HashSet<string>(new[] { "apply_failed", "restart_required", "apply_unknown",
            "storage_unavailable", "projection_unavailable", "configuration_superseded" }, StringComparer.Ordinal);
        if (!ValidKey(key) || reports.IsDefault || reports.Length > 8
            || reports.Any(r => r is null || !Enum.IsDefined(r.Step) || !Enum.IsDefined(r.State)
                || (r.ErrorCode is not null && !allowedErrors.Contains(r.ErrorCode)))
            || reports.Select(r => r.Step).Distinct().Count() != reports.Length)
            return new(ConfigurationResultCode.ValidationFailed, null);
        var result = new ConfigurationFindResult(ConfigurationResultCode.StorageUnavailable, null);
        try
        {
            AppDatabase.Write(conn =>
            {
                var receipt = ReadReceipt(conn, caller, key, out var fingerprint);
                if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
                if (receipt is null) { result = new(ConfigurationResultCode.NotFound, null); return; }
                if (receipt.Decision != ConfigurationDecision.Changed || receipt.CommitState != CommitState.Committed)
                { result = new(ConfigurationResultCode.ValidationFailed, receipt); return; }
                if (reports.Any(report => report.State == ApplyState.NotStarted && receipt.Steps.Any(previous =>
                    previous.Step == report.Step && previous.State != ApplyState.NotStarted)))
                { result = new(ConfigurationResultCode.ValidationFailed, receipt); return; }
                var combined = receipt.Steps.Concat(reports).GroupBy(r => r.Step).Select(g => g.Last())
                    .OrderBy(r => r.Step).ToImmutableArray();
                var state = AggregateApply(combined);
                var updated = receipt with { Steps = combined, ApplyState = state };
                PersistApplyReceipt(conn, caller, key, updated);
                result = new(ConfigurationResultCode.Ok, updated);
            });
        }
        catch (Exception) { result = new(ConfigurationResultCode.StorageUnavailable, null); }
        return result;
    }

    private static void PersistApplyReceipt(SqliteConnection connection, ConfigurationCaller caller,
        ConfigurationOperationKey key, ConfigurationReceipt receipt)
    {
        // An aggregate failure/restart does not finish other pending stages.
        var terminal = receipt.Steps.Length == 8 && receipt.Steps.All(r =>
            r.State is ApplyState.Applied or ApplyState.Failed or ApplyState.RestartRequired);
        UpdateOperation(connection, caller, key, JsonSerializer.Serialize(receipt, Codec), terminal, false);
    }

    private static ApplyState AggregateApply(ImmutableArray<ApplyStepReport> steps)
    {
        if (steps.Any(s => s.State == ApplyState.Unknown)) return ApplyState.Unknown;
        if (steps.Any(s => s.State == ApplyState.Failed)) return ApplyState.Failed;
        if (steps.Any(s => s.State == ApplyState.RestartRequired)) return ApplyState.RestartRequired;
        if (steps.Length == 8 && steps.All(s => s.State == ApplyState.Applied)) return ApplyState.Applied;
        return steps.Any(s => s.State is ApplyState.Applied or ApplyState.PartiallyApplied)
            ? ApplyState.PartiallyApplied : ApplyState.NotStarted;
    }

    private ConfigurationResultCode Govern(SqliteConnection conn, ConfigurationOperationKey key, out long floor)
    {
        floor = ReadLong(conn, ReadFloorSql);
        var now = _time.Now.ToUnixTimeSeconds();
        var ticks = _time.TickCount;
        var regression = ticks < _anchorTicks;
        try
        {
            if (!regression) floor = Math.Max(floor, checked(_anchorFloor + checked(ticks - _anchorTicks) / 1000));
            floor = Math.Max(floor, now);
            AppDatabase.Exec(conn, SaveFloorSql, ("$floor", floor));
            if (floor >= checked(key.IssuedUnixSeconds + LifetimeSeconds)) return ConfigurationResultCode.KeyExpired;
            if (regression || checked(floor - now) > FutureSeconds) return ConfigurationResultCode.ClockRegression;
            if (key.IssuedUnixSeconds > checked(floor + FutureSeconds)) return ConfigurationResultCode.ValidationFailed;
            return ConfigurationResultCode.Ok;
        }
        catch (OverflowException) { return ConfigurationResultCode.ClockRegression; }
    }

    private bool Allowed(ConfigurationCaller? caller, ConfigurationAccess access)
    {
        if (caller is null || caller.ResourceId != "bot" || string.IsNullOrWhiteSpace(caller.AuthorityId)
            || string.IsNullOrWhiteSpace(caller.SubjectId) || (caller.Access & access) != access) return false;
        try { return _participant.Reauthorize(caller, access); }
        catch (Exception) { return false; }
    }
    private static bool ValidKey(ConfigurationOperationKey key)
        => key.Generation.Value != Guid.Empty && key.Nonce != Guid.Empty && key.IssuedUnixSeconds >= 0
            && key.IssuedUnixSeconds <= long.MaxValue - LifetimeSeconds;
    internal static ConfigurationVersion Version(SqliteConnection conn)
        => new(ReadLong(conn, "SELECT version FROM configuration_state WHERE id=1"));
    private static long ReadLong(SqliteConnection conn, string sql)
    { using var command = conn.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static string EncodeKey(ConfigurationOperationKey key)
        => $"cfg1:{key.Generation.Value:N}:{key.IssuedUnixSeconds.ToString(CultureInfo.InvariantCulture)}:{key.Nonce:N}";
    private static ConfigurationSaveResult Rejected(ConfigurationResultCode code, ConfigurationVersion version = default)
        => new(code, false, null, version, ImmutableArray<FieldError>.Empty);
    private ConfigurationReadResult EmptyRead(ConfigurationResultCode code)
        => new(code, default, default, Revision, 0, null,
            ImmutableArray<SecretMetadata>.Empty, ImmutableArray<FieldDescriptor>.Empty, ApplyState.Unknown);

    private static ConfigurationReceipt? ReadReceipt(SqliteConnection conn, ConfigurationCaller caller,
        ConfigurationOperationKey key, out byte[]? fingerprint)
    {
        var json = ReadOperation(conn, caller, key, false, out fingerprint);
        return json is null ? null : JsonSerializer.Deserialize<ConfigurationReceipt>(json, Codec)
            ?? throw new InvalidDataException("configuration_receipt_invalid");
    }
    private static string? ReadOperation(SqliteConnection conn, ConfigurationCaller caller,
        ConfigurationOperationKey key, bool mood, out byte[]? fingerprint)
    {
        using var command = conn.CreateCommand();
        command.CommandText = ReadOperationSql;
        Scope(command, caller, key, mood);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { fingerprint = null; return null; }
        if (reader.IsDBNull(2) || !KnownCodec(reader.GetString(2))) throw new ReceiptCodecException();
        if (reader.GetString(3) != (mood ? "legacy-mood1" : "cfg1")) throw new ReceiptCodecException();
        var json = reader.GetString(0);
        if (mood && (DecodeMoodReceipt(json).State is LegacyActionState.Applied or LegacyActionState.Failed)
            != (reader.GetInt32(4) == 1)) throw new InvalidDataException("legacy_action_invalid");
        fingerprint = ((byte[])reader["fingerprint"]).ToArray();
        return json;
    }
    private static void Scope(SqliteCommand command, ConfigurationCaller caller, ConfigurationOperationKey key, bool mood = false)
    {
        command.Parameters.AddWithValue("$a", caller.AuthorityId);
        command.Parameters.AddWithValue("$s", caller.SubjectId);
        command.Parameters.AddWithValue("$r", caller.ResourceId);
        command.Parameters.AddWithValue("$k", mood ? "mood1:" + EncodeKey(key)[5..] : EncodeKey(key));
    }
    private static void InsertReceipt(SqliteConnection conn, ConfigurationCaller caller,
        ConfigurationReceipt receipt, byte[] fingerprint)
        => InsertOperation(conn, caller, receipt.Key, JsonSerializer.Serialize(receipt, Codec), fingerprint,
            receipt.Decision == ConfigurationDecision.NoChanges, false);
    private static void InsertOperation(SqliteConnection conn, ConfigurationCaller caller, ConfigurationOperationKey key,
        string json, byte[] fingerprint, bool terminal, bool mood)
    {
        using var command = conn.CreateCommand();
        command.CommandText = InsertOperationSql;
        Scope(command, caller, key, mood);
        command.Parameters.AddWithValue("$namespace", mood ? "legacy-mood1" : "cfg1");
        command.Parameters.AddWithValue("$g", key.Generation.Value.ToString("N"));
        command.Parameters.AddWithValue("$expires", checked(key.IssuedUnixSeconds + LifetimeSeconds));
        command.Parameters.AddWithValue("$fp", fingerprint);
        command.Parameters.AddWithValue("$receipt", json);
        command.Parameters.AddWithValue("$terminal", terminal ? 1 : 0);
        command.ExecuteNonQuery();
    }
    private static void UpdateOperation(SqliteConnection conn, ConfigurationCaller caller, ConfigurationOperationKey key,
        string json, bool terminal, bool mood)
    {
        using var command = conn.CreateCommand();
        command.CommandText = UpdateOperationSql;
        Scope(command, caller, key, mood);
        command.Parameters.AddWithValue("$receipt", json);
        command.Parameters.AddWithValue("$terminal", terminal ? 1 : 0);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("configuration_receipt_missing");
    }
    private static bool HasCapacity(SqliteConnection conn, ConfigurationCaller caller, long floor, int required = 1)
    {
        AppDatabase.Exec(conn, PruneReceiptsSql, ("$floor", floor));
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(authority=$a AND subject=$s AND resource=$r),0) FROM configuration_operations";
        command.Parameters.AddWithValue("$a", caller.AuthorityId); command.Parameters.AddWithValue("$s", caller.SubjectId);
        command.Parameters.AddWithValue("$r", caller.ResourceId);
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt64(0) <= 4096 - required && reader.GetInt64(1) <= 256 - required;
    }
    private static bool ReceiptFits(ConfigurationReceipt receipt)
    {
        if (receipt.DeviceKeyAssignments.Length > 128 || receipt.DeviceKeyAssignments.Any(a => a.Key.Value == Guid.Empty || a.InputOrdinal < 0)
            || receipt.DeviceKeyAssignments.Select(a => a.InputOrdinal).Distinct().Count() != receipt.DeviceKeyAssignments.Length
            || receipt.DeviceKeyAssignments.Select(a => a.Key).Distinct().Count() != receipt.DeviceKeyAssignments.Length) return false;
        var worst = receipt with { ApplyState = ApplyState.PartiallyApplied,
            Steps = Enum.GetValues<ApplyStep>().Select(step => new ApplyStepReport(step, ApplyState.PartiallyApplied,
                new string('x', 64))).ToImmutableArray() };
        return JsonSerializer.SerializeToUtf8Bytes(worst, Codec).Length <= 8192;
    }
    private static ApplyState LatestApply(SqliteConnection conn)
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT receipt FROM configuration_operations WHERE namespace='cfg1' AND json_extract(receipt,'$.decision')=0 " +
            "ORDER BY rowid DESC LIMIT 1";
        var json = command.ExecuteScalar() as string;
        return json is null ? ApplyState.NotStarted
            : JsonSerializer.Deserialize<ConfigurationReceipt>(json, Codec)?.ApplyState ?? ApplyState.Unknown;
    }
    private static bool SecretsChanged(SqliteConnection conn, OwnedIntent owned)
    {
        foreach (var secret in owned.Secrets.Where(s => s.Kind != SecretEditKind.Keep))
        {
            if (!string.Equals(SecretsStore.LoadInTransaction(conn, secret.Slot), secret.NormalizedValue, StringComparison.Ordinal))
                return true;
            if (secret.Slot == WritableSecretSlot.FeishuVerificationToken)
            {
                var expected = secret.Kind == SecretEditKind.Delete ? "deleted" : "initialized";
                if (!string.Equals(AppDatabase.GetMetaInTransaction(conn, "verification_token_initialized"), expected, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }
    private static void PersistSecrets(SqliteConnection conn, OwnedIntent owned, long now)
    {
        foreach (var edit in owned.Secrets)
            if (edit.Kind != SecretEditKind.Keep) SecretsStore.SaveInTransaction(conn, edit.Slot, edit.NormalizedValue, now);
    }
    private static byte[]? Fingerprint(SqliteConnection conn, ConfigurationCaller caller,
        ConfigurationSaveRequest request, OwnedIntent owned, ConfigurationStorageProfile profile, out OwnedIntent effective)
    {
        effective = owned;
        using var command = conn.CreateCommand();
        command.CommandText = ReadGenerationSql;
        command.Parameters.AddWithValue("$generation", request.Key.Generation.Value.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!KnownCodec(reader.GetString(1))) throw new ReceiptCodecException();
        var hmac = (byte[])reader.GetValue(0);
        byte[]? payload = null;
        using var buffer = new MemoryStream();
        try
        {
            // Interpretation belongs to the receipt's generation, never the current process default.
            if (profile == ConfigurationStorageProfile.Strict && reader.GetString(1) == AliasCodecRevision)
                effective = CanonicalizePrimaryAliases(owned);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(caller.AuthorityId); writer.WriteStringValue(caller.SubjectId);
                writer.WriteStringValue(caller.ResourceId); writer.WriteStringValue("cfg1");
                writer.WriteStringValue(request.ContractRevision);
                // Keep the original strict encoding stable for retained receipts.
                if (profile == ConfigurationStorageProfile.Strict) writer.WriteNumberValue(request.ExpectedVersion.Value);
                else
                {
                    writer.WriteStringValue("Legacy");
                    writer.WriteStringValue("NoExpectedVersion");
                }
                JsonSerializer.Serialize(writer, effective.FingerprintPatch ?? effective.Patch, Codec);
                foreach (var secret in owned.Secrets)
                { writer.WriteNumberValue((int)secret.Slot); writer.WriteNumberValue((int)secret.Kind); writer.WriteStringValue(secret.NormalizedValue); }
                writer.WriteEndArray();
            }
            payload = buffer.ToArray();
            return HMACSHA256.HashData(hmac, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmac);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (buffer.TryGetBuffer(out var contents)) CryptographicOperations.ZeroMemory(contents.AsSpan());
        }
    }

    private sealed class OwnedIntent : IDisposable
    {
        internal required ConfigurationPatch Patch { get; init; }
        internal ConfigurationPatch? FingerprintPatch { get; init; }
        internal required ImmutableArray<OwnedSecret> Secrets { get; init; }
        internal required ImmutableArray<ConfigurationFieldId> Fields { get; init; }
        internal required ImmutableArray<(ConfigurationFieldId Field, UserValueKind Mode)> Modes { get; init; }
        public void Dispose() { foreach (var secret in Secrets) secret.Bytes?.Dispose(); }
    }
    private sealed record OwnedSecret(WritableSecretSlot Slot, SecretEditKind Kind,
        SensitiveInput? Bytes, string? NormalizedValue)
    { public override string ToString() => "[redacted]"; }

    private static bool KnownCodec(string revision) => revision is Revision or AliasCodecRevision;
    private sealed class ReceiptCodecException : Exception;
    private sealed class AliasIntentException(ConfigurationFieldId field, string code) : Exception
    {
        internal ConfigurationFieldId Field { get; } = field;
        internal string Code { get; } = code;
    }

    private static OwnedIntent CanonicalizePrimaryAliases(OwnedIntent raw)
    {
        var registered = raw.Patch.Registered!;
        var primary = registered.Providers.FirstOrDefault(provider => provider.Key.Value == "primary");
        if (primary is null) return raw;
        var fields = raw.Fields.ToBuilder();
        FieldEdit<string> Merge(ConfigurationFieldId field, FieldEdit<string> direct, FieldEdit<string> alias)
        {
            if (alias.Kind == EditKind.Keep) return direct;
            try { alias = FreezeEdit(field, alias, ImmutableArray.CreateBuilder<ConfigurationFieldId>()); }
            catch (ArgumentException) { throw new AliasIntentException(field, "invalid_alias_intent"); }
            if (direct.Kind != EditKind.Keep && (direct.Kind != alias.Kind
                || direct.Kind == EditKind.Set && (!direct.TryGetValue(out var value)
                    || !alias.TryGetValue(out var other) || !string.Equals(value, other, StringComparison.Ordinal))))
                throw new AliasIntentException(field, "alias_conflict");
            return direct.Kind == EditKind.Keep ? alias : direct;
        }
        var model = raw.Patch.Model!;
        var mergedModel = Merge(ConfigurationFieldId.Model, model.Model, primary.Changes.ModelName);
        var mergedUrl = Merge(ConfigurationFieldId.ModelBaseUrl, model.ModelBaseUrl, primary.Changes.BaseUrl);
        var canonicalModel = new ModelChanges
        {
            AdaptiveSamplingEnabled = model.AdaptiveSamplingEnabled, DefaultTemperature = model.DefaultTemperature,
            DefaultTopP = model.DefaultTopP, EmotionalTemperature = model.EmotionalTemperature,
            EmotionalTopP = model.EmotionalTopP, FastModel = model.FastModel, FastReply = model.FastReply,
            FastThinkingBudget = model.FastThinkingBudget, FastThinkingCustomBudget = model.FastThinkingCustomBudget,
            MaxTokens = model.MaxTokens, Model = mergedModel, ModelBaseUrl = mergedUrl,
            RationalTemperature = model.RationalTemperature, RationalTopP = model.RationalTopP,
            ThinkingBudget = model.ThinkingBudget, ThinkingCustomBudget = model.ThinkingCustomBudget
        };
        var marker = primary with { Changes = primary.Changes with { ModelName = default, BaseUrl = default } };
        var providers = registered.Providers.Select(provider => provider.Key == primary.Key ? marker : provider).ToImmutableArray();
        var canonicalPatch = raw.Patch with { Model = canonicalModel, Registered = registered with { Providers = providers } };
        var fingerprintProviders = providers.Where(provider => !EmptyPrimaryMarker(provider)).ToImmutableArray();
        fields.Remove(ConfigurationFieldId.RegisteredProviders);
        if (!fingerprintProviders.IsEmpty) fields.Add(ConfigurationFieldId.RegisteredProviders);
        if (mergedModel.Kind != EditKind.Keep) fields.Add(ConfigurationFieldId.Model);
        if (mergedUrl.Kind != EditKind.Keep) fields.Add(ConfigurationFieldId.ModelBaseUrl);
        return new()
        {
            Patch = canonicalPatch,
            FingerprintPatch = canonicalPatch with { Registered = registered with { Providers = fingerprintProviders } },
            Fields = fields.Distinct().OrderBy(field => field).ToImmutableArray(),
            Modes = DesiredModes(canonicalPatch), Secrets = raw.Secrets
        };
    }

    private static bool EmptyPrimaryMarker(ProviderEdit provider)
        => provider.Key.Value == "primary" && provider.Changes.NameRaw.Kind == EditKind.Keep
            && provider.Changes.Priority.Kind == EditKind.Keep && provider.Changes.BaseUrl.Kind == EditKind.Keep
            && provider.Changes.ModelName.Kind == EditKind.Keep && provider.Changes.Enabled.Kind == EditKind.Keep
            && provider.Changes.Credential.Kind == CredentialBindingEditKind.KeepExisting;

    private static OwnedIntent Own(ConfigurationSaveRequest request)
    {
        var secrets = ImmutableArray.CreateBuilder<OwnedSecret>();
        try
        {
            var slots = new HashSet<WritableSecretSlot>();
            foreach (var source in request.Secrets)
            {
                if (source is null || !Enum.IsDefined(source.Slot) || !Enum.IsDefined(source.Kind) || !slots.Add(source.Slot))
                    throw new ArgumentException("invalid_secret_edit");
                SensitiveInput? copy = null;
                string? normalized = null;
                try
                {
                    if (source.Kind == SecretEditKind.Replace)
                    {
                        copy = source.Input?.OwnCopy() ?? throw new ArgumentException("invalid_secret_input");
                        var bytes = copy.CopyBytes();
                        try
                        {
                            if (bytes.Length == 0 || bytes.Length > 16384) throw new ArgumentException("invalid_secret_size");
                            normalized = StrictUtf8.GetString(bytes).Trim();
                            if (normalized.Length == 0 || normalized.Contains('\0')) throw new ArgumentException("invalid_secret_replace");
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                    secrets.Add(new(source.Slot, source.Kind, copy, normalized));
                    copy = null;
                }
                finally { copy?.Dispose(); }
            }
            foreach (var slot in Enum.GetValues<WritableSecretSlot>())
                if (!slots.Contains(slot)) secrets.Add(new(slot, SecretEditKind.Keep, null, null));
            var fields = ImmutableArray.CreateBuilder<ConfigurationFieldId>();
            var patch = FreezePatch(request.Changes, fields);
            if (secrets.Any(s => s.Kind != SecretEditKind.Keep)) fields.Add(ConfigurationFieldId.AllowedSecretEdits);
            return new() { Patch = patch, Secrets = secrets.OrderBy(s => s.Slot).ToImmutableArray(),
                Fields = fields.Distinct().OrderBy(f => f).ToImmutableArray(), Modes = DesiredModes(patch) };
        }
        catch
        { foreach (var secret in secrets) secret.Bytes?.Dispose(); throw; }
    }
    private static ConfigurationPatch FreezePatch(ConfigurationPatch source, ImmutableArray<ConfigurationFieldId>.Builder fields)
    {
        var agentSource = source.Agent ?? new AgentChanges();
        var agent = new AgentChanges
        {
            AgentAllowedUsers = FreezeEdit(ConfigurationFieldId.AgentAllowedUsers, agentSource.AgentAllowedUsers, fields),
            AgentDevices = FreezeDevices(ConfigurationFieldId.AgentDevices, agentSource.AgentDevices, fields),
            AgentMaxQueued = FreezeEdit(ConfigurationFieldId.AgentMaxQueued, agentSource.AgentMaxQueued, fields),
            AgentModel = FreezeEdit(ConfigurationFieldId.AgentModel, agentSource.AgentModel, fields),
            AgentPrefix = FreezeEdit(ConfigurationFieldId.AgentPrefix, agentSource.AgentPrefix, fields),
            AgentProgressSeconds = FreezeEdit(ConfigurationFieldId.AgentProgressSeconds, agentSource.AgentProgressSeconds, fields),
            AgentPrompt = FreezeEdit(ConfigurationFieldId.AgentPrompt, agentSource.AgentPrompt, fields),
            AgentReasoningEffort = FreezeEdit(ConfigurationFieldId.AgentReasoningEffort, agentSource.AgentReasoningEffort, fields),
            AgentReasoningLevels = FreezeEdit(ConfigurationFieldId.AgentReasoningLevels, agentSource.AgentReasoningLevels, fields),
            AgentReplyMaxChars = FreezeEdit(ConfigurationFieldId.AgentReplyMaxChars, agentSource.AgentReplyMaxChars, fields),
            AgentServerBaseUrl = FreezeEdit(ConfigurationFieldId.AgentServerBaseUrl, agentSource.AgentServerBaseUrl, fields),
            AgentServerCommandTimeoutSeconds = FreezeEdit(ConfigurationFieldId.AgentServerCommandTimeoutSeconds, agentSource.AgentServerCommandTimeoutSeconds, fields),
            AgentServerDocker = FreezeEdit(ConfigurationFieldId.AgentServerDocker, agentSource.AgentServerDocker, fields),
            AgentServerKeepContext = FreezeEdit(ConfigurationFieldId.AgentServerKeepContext, agentSource.AgentServerKeepContext, fields),
            AgentServerMaxSteps = FreezeEdit(ConfigurationFieldId.AgentServerMaxSteps, agentSource.AgentServerMaxSteps, fields),
            AgentServerModel = FreezeEdit(ConfigurationFieldId.AgentServerModel, agentSource.AgentServerModel, fields),
            AgentServerQqActions = FreezeEdit(ConfigurationFieldId.AgentServerQqActions, agentSource.AgentServerQqActions, fields),
            AgentServerTools = FreezeEdit(ConfigurationFieldId.AgentServerTools, agentSource.AgentServerTools, fields),
            AgentServerUseGate = FreezeEdit(ConfigurationFieldId.AgentServerUseGate, agentSource.AgentServerUseGate, fields),
            AgentServerWorkDir = FreezeEdit(ConfigurationFieldId.AgentServerWorkDir, agentSource.AgentServerWorkDir, fields),
            AgentTarget = FreezeEdit(ConfigurationFieldId.AgentTarget, agentSource.AgentTarget, fields),
            AgentTimeoutSeconds = FreezeEdit(ConfigurationFieldId.AgentTimeoutSeconds, agentSource.AgentTimeoutSeconds, fields),
            AgentTools = FreezeEdit(ConfigurationFieldId.AgentTools, agentSource.AgentTools, fields),
            AgentWorkDir = FreezeEdit(ConfigurationFieldId.AgentWorkDir, agentSource.AgentWorkDir, fields),
            EnableAgentBridge = FreezeEdit(ConfigurationFieldId.EnableAgentBridge, agentSource.EnableAgentBridge, fields),
            EnableAgentMask = FreezeEdit(ConfigurationFieldId.EnableAgentMask, agentSource.EnableAgentMask, fields),
            EnableHostAgent = FreezeEdit(ConfigurationFieldId.EnableHostAgent, agentSource.EnableHostAgent, fields),
            EnableServerAgent = FreezeEdit(ConfigurationFieldId.EnableServerAgent, agentSource.EnableServerAgent, fields),
            PanelDeployEnabled = FreezeEdit(ConfigurationFieldId.PanelDeployEnabled, agentSource.PanelDeployEnabled, fields),
            PanelDeployUrl = FreezeEdit(ConfigurationFieldId.PanelDeployUrl, agentSource.PanelDeployUrl, fields),
        };
        var behaviorSource = source.Behavior ?? new BehaviorChanges();
        var behavior = new BehaviorChanges
        {
            AiDesire = FreezeEdit(ConfigurationFieldId.AiDesire, behaviorSource.AiDesire, fields),
            AiModeEnabled = FreezeEdit(ConfigurationFieldId.AiModeEnabled, behaviorSource.AiModeEnabled, fields),
            ApprovalApprovers = FreezeEdit(ConfigurationFieldId.ApprovalApprovers, behaviorSource.ApprovalApprovers, fields),
            BotPersona = FreezeEdit(ConfigurationFieldId.BotPersona, behaviorSource.BotPersona, fields),
            EnableApprovals = FreezeEdit(ConfigurationFieldId.EnableApprovals, behaviorSource.EnableApprovals, fields),
            EnableAtmosphereDamping = FreezeEdit(ConfigurationFieldId.EnableAtmosphereDamping, behaviorSource.EnableAtmosphereDamping, fields),
            EnableLinkPreview = FreezeEdit(ConfigurationFieldId.EnableLinkPreview, behaviorSource.EnableLinkPreview, fields),
            EnableParticipationGating = FreezeEdit(ConfigurationFieldId.EnableParticipationGating, behaviorSource.EnableParticipationGating, fields),
            EnablePoke = FreezeEdit(ConfigurationFieldId.EnablePoke, behaviorSource.EnablePoke, fields),
            EnableProactive = FreezeEdit(ConfigurationFieldId.EnableProactive, behaviorSource.EnableProactive, fields),
            EnableProfileSummary = FreezeEdit(ConfigurationFieldId.EnableProfileSummary, behaviorSource.EnableProfileSummary, fields),
            EnableQuestions = FreezeEdit(ConfigurationFieldId.EnableQuestions, behaviorSource.EnableQuestions, fields),
            EnableStickers = FreezeEdit(ConfigurationFieldId.EnableStickers, behaviorSource.EnableStickers, fields),
            EnableWebSearch = FreezeEdit(ConfigurationFieldId.EnableWebSearch, behaviorSource.EnableWebSearch, fields),
            FilterActionNarration = FreezeEdit(ConfigurationFieldId.FilterActionNarration, behaviorSource.FilterActionNarration, fields),
            GroupCooldownSeconds = FreezeEdit(ConfigurationFieldId.GroupCooldownSeconds, behaviorSource.GroupCooldownSeconds, fields),
            IdleFallbackSeconds = FreezeEdit(ConfigurationFieldId.IdleFallbackSeconds, behaviorSource.IdleFallbackSeconds, fields),
            IgnoreBracketMessages = FreezeEdit(ConfigurationFieldId.IgnoreBracketMessages, behaviorSource.IgnoreBracketMessages, fields),
            LinkPreviewMax = FreezeEdit(ConfigurationFieldId.LinkPreviewMax, behaviorSource.LinkPreviewMax, fields),
            LinkPreviewTimeoutSeconds = FreezeEdit(ConfigurationFieldId.LinkPreviewTimeoutSeconds, behaviorSource.LinkPreviewTimeoutSeconds, fields),
            MaxAgentSteps = FreezeEdit(ConfigurationFieldId.MaxAgentSteps, behaviorSource.MaxAgentSteps, fields),
            MaxConcurrentReplies = FreezeEdit(ConfigurationFieldId.MaxConcurrentReplies, behaviorSource.MaxConcurrentReplies, fields),
            MaxContextMessages = FreezeEdit(ConfigurationFieldId.MaxContextMessages, behaviorSource.MaxContextMessages, fields),
            MaxMessagesPerConversation = FreezeEdit(ConfigurationFieldId.MaxMessagesPerConversation, behaviorSource.MaxMessagesPerConversation, fields),
            MaxProfileChars = FreezeEdit(ConfigurationFieldId.MaxProfileChars, behaviorSource.MaxProfileChars, fields),
            MoodTtlSeconds = FreezeEdit(ConfigurationFieldId.MoodTtlSeconds, behaviorSource.MoodTtlSeconds, fields),
            ParticipationCooldownSeconds = FreezeEdit(ConfigurationFieldId.ParticipationCooldownSeconds, behaviorSource.ParticipationCooldownSeconds, fields),
            ParticipationMaxActiveLifetimeSeconds = FreezeEdit(ConfigurationFieldId.ParticipationMaxActiveLifetimeSeconds, behaviorSource.ParticipationMaxActiveLifetimeSeconds, fields),
            ParticipationMaxConsecutiveReplies = FreezeEdit(ConfigurationFieldId.ParticipationMaxConsecutiveReplies, behaviorSource.ParticipationMaxConsecutiveReplies, fields),
            ParticipationMaxExitingLifetimeSeconds = FreezeEdit(ConfigurationFieldId.ParticipationMaxExitingLifetimeSeconds, behaviorSource.ParticipationMaxExitingLifetimeSeconds, fields),
            ParticipationProbingMaxReplies = FreezeEdit(ConfigurationFieldId.ParticipationProbingMaxReplies, behaviorSource.ParticipationProbingMaxReplies, fields),
            PokeCooldownSeconds = FreezeEdit(ConfigurationFieldId.PokeCooldownSeconds, behaviorSource.PokeCooldownSeconds, fields),
            PrivateCooldownSeconds = FreezeEdit(ConfigurationFieldId.PrivateCooldownSeconds, behaviorSource.PrivateCooldownSeconds, fields),
            ProactiveCooldownSeconds = FreezeEdit(ConfigurationFieldId.ProactiveCooldownSeconds, behaviorSource.ProactiveCooldownSeconds, fields),
            ProactiveQuietSeconds = FreezeEdit(ConfigurationFieldId.ProactiveQuietSeconds, behaviorSource.ProactiveQuietSeconds, fields),
            ProfileLookupCount = FreezeEdit(ConfigurationFieldId.ProfileLookupCount, behaviorSource.ProfileLookupCount, fields),
            ProfileSummaryIntervalSeconds = FreezeEdit(ConfigurationFieldId.ProfileSummaryIntervalSeconds, behaviorSource.ProfileSummaryIntervalSeconds, fields),
            ProfileSummaryLines = FreezeEdit(ConfigurationFieldId.ProfileSummaryLines, behaviorSource.ProfileSummaryLines, fields),
            ProfileSummaryMaxChars = FreezeEdit(ConfigurationFieldId.ProfileSummaryMaxChars, behaviorSource.ProfileSummaryMaxChars, fields),
            ProfileSummaryThreshold = FreezeEdit(ConfigurationFieldId.ProfileSummaryThreshold, behaviorSource.ProfileSummaryThreshold, fields),
            ScenarioPreset = FreezeEdit(ConfigurationFieldId.ScenarioPreset, behaviorSource.ScenarioPreset, fields),
            SegmentDelayMs = FreezeEdit(ConfigurationFieldId.SegmentDelayMs, behaviorSource.SegmentDelayMs, fields),
            SplitReplies = FreezeEdit(ConfigurationFieldId.SplitReplies, behaviorSource.SplitReplies, fields),
            StickerCandidates = FreezeEdit(ConfigurationFieldId.StickerCandidates, behaviorSource.StickerCandidates, fields),
            StickerCooldownSeconds = FreezeEdit(ConfigurationFieldId.StickerCooldownSeconds, behaviorSource.StickerCooldownSeconds, fields),
            StickerCurateIntervalSeconds = FreezeEdit(ConfigurationFieldId.StickerCurateIntervalSeconds, behaviorSource.StickerCurateIntervalSeconds, fields),
            StickerLibraryMax = FreezeEdit(ConfigurationFieldId.StickerLibraryMax, behaviorSource.StickerLibraryMax, fields),
            SuitabilityThreshold = FreezeEdit(ConfigurationFieldId.SuitabilityThreshold, behaviorSource.SuitabilityThreshold, fields),
            WebSearchCooldownSeconds = FreezeEdit(ConfigurationFieldId.WebSearchCooldownSeconds, behaviorSource.WebSearchCooldownSeconds, fields),
            WebSearchMaxResults = FreezeEdit(ConfigurationFieldId.WebSearchMaxResults, behaviorSource.WebSearchMaxResults, fields),
            WebSearchSources = FreezeEdit(ConfigurationFieldId.WebSearchSources, behaviorSource.WebSearchSources, fields),
            WebSearchTimeoutSeconds = FreezeEdit(ConfigurationFieldId.WebSearchTimeoutSeconds, behaviorSource.WebSearchTimeoutSeconds, fields),
            WebSearchUseModelSearch = FreezeEdit(ConfigurationFieldId.WebSearchUseModelSearch, behaviorSource.WebSearchUseModelSearch, fields),
        };
        var modelSource = source.Model ?? new ModelChanges();
        var model = new ModelChanges
        {
            AdaptiveSamplingEnabled = FreezeEdit(ConfigurationFieldId.AdaptiveSamplingEnabled, modelSource.AdaptiveSamplingEnabled, fields),
            DefaultTemperature = FreezeEdit(ConfigurationFieldId.DefaultTemperature, modelSource.DefaultTemperature, fields),
            DefaultTopP = FreezeEdit(ConfigurationFieldId.DefaultTopP, modelSource.DefaultTopP, fields),
            EmotionalTemperature = FreezeEdit(ConfigurationFieldId.EmotionalTemperature, modelSource.EmotionalTemperature, fields),
            EmotionalTopP = FreezeEdit(ConfigurationFieldId.EmotionalTopP, modelSource.EmotionalTopP, fields),
            FastModel = FreezeEdit(ConfigurationFieldId.FastModel, modelSource.FastModel, fields),
            FastReply = FreezeEdit(ConfigurationFieldId.FastReply, modelSource.FastReply, fields),
            FastThinkingBudget = FreezeEdit(ConfigurationFieldId.FastThinkingBudget, modelSource.FastThinkingBudget, fields),
            FastThinkingCustomBudget = FreezeEdit(ConfigurationFieldId.FastThinkingCustomBudget, modelSource.FastThinkingCustomBudget, fields),
            MaxTokens = FreezeEdit(ConfigurationFieldId.MaxTokens, modelSource.MaxTokens, fields),
            Model = FreezeEdit(ConfigurationFieldId.Model, modelSource.Model, fields),
            ModelBaseUrl = FreezeEdit(ConfigurationFieldId.ModelBaseUrl, modelSource.ModelBaseUrl, fields),
            RationalTemperature = FreezeEdit(ConfigurationFieldId.RationalTemperature, modelSource.RationalTemperature, fields),
            RationalTopP = FreezeEdit(ConfigurationFieldId.RationalTopP, modelSource.RationalTopP, fields),
            ThinkingBudget = FreezeEdit(ConfigurationFieldId.ThinkingBudget, modelSource.ThinkingBudget, fields),
            ThinkingCustomBudget = FreezeEdit(ConfigurationFieldId.ThinkingCustomBudget, modelSource.ThinkingCustomBudget, fields),
        };
        var musicSource = source.Music ?? new MusicChanges();
        var music = new MusicChanges
        {
            EnableMusic = FreezeEdit(ConfigurationFieldId.EnableMusic, musicSource.EnableMusic, fields),
            MusicBitrate = FreezeEdit(ConfigurationFieldId.MusicBitrate, musicSource.MusicBitrate, fields),
            MusicKeepAudio = FreezeEdit(ConfigurationFieldId.MusicKeepAudio, musicSource.MusicKeepAudio, fields),
            MusicLibraryMax = FreezeEdit(ConfigurationFieldId.MusicLibraryMax, musicSource.MusicLibraryMax, fields),
            MusicListenCooldownSeconds = FreezeEdit(ConfigurationFieldId.MusicListenCooldownSeconds, musicSource.MusicListenCooldownSeconds, fields),
            MusicMaxAnalysisSeconds = FreezeEdit(ConfigurationFieldId.MusicMaxAnalysisSeconds, musicSource.MusicMaxAnalysisSeconds, fields),
            MusicMaxDownloadMb = FreezeEdit(ConfigurationFieldId.MusicMaxDownloadMb, musicSource.MusicMaxDownloadMb, fields),
            MusicNoteTtlDays = FreezeEdit(ConfigurationFieldId.MusicNoteTtlDays, musicSource.MusicNoteTtlDays, fields),
            MusicSendAudioToModel = FreezeEdit(ConfigurationFieldId.MusicSendAudioToModel, musicSource.MusicSendAudioToModel, fields),
            MusicSources = FreezeEdit(ConfigurationFieldId.MusicSources, musicSource.MusicSources, fields),
            MusicUnderstandModel = FreezeEdit(ConfigurationFieldId.MusicUnderstandModel, musicSource.MusicUnderstandModel, fields),
            NeteaseBaseUrl = FreezeEdit(ConfigurationFieldId.NeteaseBaseUrl, musicSource.NeteaseBaseUrl, fields),
        };
        var platformSource = source.Platform ?? new PlatformChanges();
        var platform = new PlatformChanges
        {
            FeishuApiBase = FreezeEdit(ConfigurationFieldId.FeishuApiBase, platformSource.FeishuApiBase, fields),
            FeishuAppId = FreezeEdit(ConfigurationFieldId.FeishuAppId, platformSource.FeishuAppId, fields),
            FeishuEnabled = FreezeEdit(ConfigurationFieldId.FeishuEnabled, platformSource.FeishuEnabled, fields),
            FeishuWhitelist = FreezeEdit(ConfigurationFieldId.FeishuWhitelist, platformSource.FeishuWhitelist, fields),
            LocalChannelIds = FreezeEdit(ConfigurationFieldId.LocalChannelIds, platformSource.LocalChannelIds, fields),
            MessageWhitelist = FreezeEdit(ConfigurationFieldId.MessageWhitelist, platformSource.MessageWhitelist, fields),
            OfficialAppId = FreezeEdit(ConfigurationFieldId.OfficialAppId, platformSource.OfficialAppId, fields),
            OfficialChatEnabled = FreezeEdit(ConfigurationFieldId.OfficialChatEnabled, platformSource.OfficialChatEnabled, fields),
            OfficialEnabled = FreezeEdit(ConfigurationFieldId.OfficialEnabled, platformSource.OfficialEnabled, fields),
            OfficialSandbox = FreezeEdit(ConfigurationFieldId.OfficialSandbox, platformSource.OfficialSandbox, fields),
            OfficialWhitelistGroups = FreezeEdit(ConfigurationFieldId.OfficialWhitelistGroups, platformSource.OfficialWhitelistGroups, fields),
            OfficialWhitelistPrivates = FreezeEdit(ConfigurationFieldId.OfficialWhitelistPrivates, platformSource.OfficialWhitelistPrivates, fields),
            PlatformPolicies = FreezePolicies(ConfigurationFieldId.PlatformPolicies, platformSource.PlatformPolicies, fields),
            PrivateChatEnabled = FreezeEdit(ConfigurationFieldId.PrivateChatEnabled, platformSource.PrivateChatEnabled, fields),
            WhitelistGroups = FreezeEdit(ConfigurationFieldId.WhitelistGroups, platformSource.WhitelistGroups, fields),
            WhitelistPrivates = FreezeEdit(ConfigurationFieldId.WhitelistPrivates, platformSource.WhitelistPrivates, fields),
        };
        var reportingSource = source.Reporting ?? new ReportingChanges();
        var reporting = new ReportingChanges
        {
            HealthReportEnabled = FreezeEdit(ConfigurationFieldId.HealthReportEnabled, reportingSource.HealthReportEnabled, fields),
            HealthReportTargets = FreezeEdit(ConfigurationFieldId.HealthReportTargets, reportingSource.HealthReportTargets, fields),
            HealthReportTime = FreezeEdit(ConfigurationFieldId.HealthReportTime, reportingSource.HealthReportTime, fields),
        };
        var voiceSource = source.Voice ?? new VoiceChanges();
        var voice = new VoiceChanges
        {
            EnableVoice = FreezeEdit(ConfigurationFieldId.EnableVoice, voiceSource.EnableVoice, fields),
            TtsApiBase = FreezeEdit(ConfigurationFieldId.TtsApiBase, voiceSource.TtsApiBase, fields),
            TtsModel = FreezeEdit(ConfigurationFieldId.TtsModel, voiceSource.TtsModel, fields),
            TtsProvider = FreezeEdit(ConfigurationFieldId.TtsProvider, voiceSource.TtsProvider, fields),
            TtsServiceUrl = FreezeEdit(ConfigurationFieldId.TtsServiceUrl, voiceSource.TtsServiceUrl, fields),
            VoiceEagerness = FreezeEdit(ConfigurationFieldId.VoiceEagerness, voiceSource.VoiceEagerness, fields),
            VoiceEmotion = FreezeEdit(ConfigurationFieldId.VoiceEmotion, voiceSource.VoiceEmotion, fields),
            VoiceMaxChars = FreezeEdit(ConfigurationFieldId.VoiceMaxChars, voiceSource.VoiceMaxChars, fields),
            VoiceName = FreezeEdit(ConfigurationFieldId.VoiceName, voiceSource.VoiceName, fields),
            VoicePitch = FreezeEdit(ConfigurationFieldId.VoicePitch, voiceSource.VoicePitch, fields),
            VoiceSpeed = FreezeEdit(ConfigurationFieldId.VoiceSpeed, voiceSource.VoiceSpeed, fields),
            VoiceVol = FreezeEdit(ConfigurationFieldId.VoiceVol, voiceSource.VoiceVol, fields),
        };
        var registered = FreezeRegistered(source.Registered, fields);
        if (platform.PlatformPolicies.Kind != EditKind.Keep && registered.Policies.Length != 0)
            throw new ArgumentException("alias_conflict");
        return new(behavior, model, agent, platform, voice, music, reporting, registered);
    }

    private static FieldEdit<T> FreezeEdit<T>(ConfigurationFieldId field, FieldEdit<T> edit,
        ImmutableArray<ConfigurationFieldId>.Builder fields)
    {
        if (!Enum.IsDefined(edit.Kind)) throw new ArgumentException("invalid_edit");
        if (edit.Kind == EditKind.Keep) return default;
        fields.Add(field);
        if (edit.Kind == EditKind.Clear)
        {
            if (typeof(T) != typeof(string)) throw new ArgumentException("field_cannot_clear");
            return edit;
        }
        if (!edit.TryGetValue(out var value) || value is null) throw new ArgumentException("invalid_edit_value");
        var range = Range(field);
        if (value is int number && (number < range.Min || number > range.Max)) throw new ArgumentException("out_of_range");
        if (value is double fraction && (!double.IsFinite(fraction) || fraction < range.Min || fraction > range.Max))
            throw new ArgumentException("out_of_range");
        if (value is string text) return FieldEdit<T>.Set((T)(object)NormalizeText(field, text));
        return FieldEdit<T>.Set(value);
    }

    private static (double Min, double Max) Range(ConfigurationFieldId field) => field switch
    {
        ConfigurationFieldId.AgentMaxQueued => (1, 20),
        ConfigurationFieldId.AgentProgressSeconds => (0, 3600),
        ConfigurationFieldId.AgentReplyMaxChars => (200, 3000),
        ConfigurationFieldId.AgentServerCommandTimeoutSeconds => (5, 300),
        ConfigurationFieldId.AgentServerMaxSteps => (1, 30),
        ConfigurationFieldId.AgentTimeoutSeconds => (30, 7200),
        ConfigurationFieldId.AiDesire => (0, 100),
        ConfigurationFieldId.DefaultTemperature => (0.0, 2.0),
        ConfigurationFieldId.DefaultTopP => (0.0, 1.0),
        ConfigurationFieldId.EmotionalTemperature => (0.0, 2.0),
        ConfigurationFieldId.EmotionalTopP => (0.0, 1.0),
        ConfigurationFieldId.GroupCooldownSeconds => (0, int.MaxValue),
        ConfigurationFieldId.IdleFallbackSeconds => (0, int.MaxValue),
        ConfigurationFieldId.LinkPreviewMax => (0, 5),
        ConfigurationFieldId.LinkPreviewTimeoutSeconds => (2, 30),
        ConfigurationFieldId.MaxAgentSteps => (1, 3),
        ConfigurationFieldId.MaxConcurrentReplies => (1, 16),
        ConfigurationFieldId.MaxContextMessages => (10, 1000),
        ConfigurationFieldId.MaxMessagesPerConversation => (20, 100000),
        ConfigurationFieldId.MaxProfileChars => (0, 20000),
        ConfigurationFieldId.MaxTokens => (64, 32000),
        ConfigurationFieldId.MoodTtlSeconds => (0, 604800),
        ConfigurationFieldId.MusicBitrate => (32, 320),
        ConfigurationFieldId.MusicLibraryMax => (10, 5000),
        ConfigurationFieldId.MusicListenCooldownSeconds => (0, 86400),
        ConfigurationFieldId.MusicMaxAnalysisSeconds => (20, 600),
        ConfigurationFieldId.MusicMaxDownloadMb => (1, 64),
        ConfigurationFieldId.MusicNoteTtlDays => (1, 365),
        ConfigurationFieldId.ParticipationCooldownSeconds => (0, 600),
        ConfigurationFieldId.ParticipationMaxActiveLifetimeSeconds => (30, 3600),
        ConfigurationFieldId.ParticipationMaxConsecutiveReplies => (1, 10),
        ConfigurationFieldId.ParticipationMaxExitingLifetimeSeconds => (10, 3600),
        ConfigurationFieldId.ParticipationProbingMaxReplies => (1, 3),
        ConfigurationFieldId.PokeCooldownSeconds => (0, 86400),
        ConfigurationFieldId.PrivateCooldownSeconds => (0, int.MaxValue),
        ConfigurationFieldId.ProactiveCooldownSeconds => (60, 86400),
        ConfigurationFieldId.ProactiveQuietSeconds => (1, 3600),
        ConfigurationFieldId.ProfileLookupCount => (0, 50),
        ConfigurationFieldId.ProfileSummaryIntervalSeconds => (0, 86400),
        ConfigurationFieldId.ProfileSummaryLines => (0, 50),
        ConfigurationFieldId.ProfileSummaryMaxChars => (40, 2000),
        ConfigurationFieldId.ProfileSummaryThreshold => (5, 500),
        ConfigurationFieldId.RationalTemperature => (0.0, 2.0),
        ConfigurationFieldId.RationalTopP => (0.0, 1.0),
        ConfigurationFieldId.SegmentDelayMs => (0, int.MaxValue),
        ConfigurationFieldId.StickerCandidates => (0, 20),
        ConfigurationFieldId.StickerCooldownSeconds => (0, 86400),
        ConfigurationFieldId.StickerCurateIntervalSeconds => (0, 86400),
        ConfigurationFieldId.StickerLibraryMax => (0, 2000),
        ConfigurationFieldId.SuitabilityThreshold => (0, 100),
        ConfigurationFieldId.VoiceEagerness => (0, 100),
        ConfigurationFieldId.VoiceMaxChars => (10, 300),
        ConfigurationFieldId.VoicePitch => (-12, 12),
        ConfigurationFieldId.VoiceSpeed => (50, 200),
        ConfigurationFieldId.VoiceVol => (0, 1000),
        ConfigurationFieldId.WebSearchCooldownSeconds => (0, 86400),
        ConfigurationFieldId.WebSearchMaxResults => (1, 10),
        ConfigurationFieldId.WebSearchTimeoutSeconds => (5, 60),
        _ => (double.MinValue, double.MaxValue)
    };

    private static string NormalizeText(ConfigurationFieldId field, string text)
    {
        if (StrictUtf8.GetByteCount(text) > 32768) throw new ArgumentException("string_too_large");
        if (field is
            ConfigurationFieldId.AgentModel
            or ConfigurationFieldId.AgentPrefix
            or ConfigurationFieldId.AgentPrompt
            or ConfigurationFieldId.AgentReasoningEffort
            or ConfigurationFieldId.AgentServerBaseUrl
            or ConfigurationFieldId.AgentServerModel
            or ConfigurationFieldId.AgentServerQqActions
            or ConfigurationFieldId.AgentServerTools
            or ConfigurationFieldId.AgentServerWorkDir
            or ConfigurationFieldId.AgentTarget
            or ConfigurationFieldId.AgentTools
            or ConfigurationFieldId.AgentWorkDir
            or ConfigurationFieldId.ApprovalApprovers
            or ConfigurationFieldId.FastModel
            or ConfigurationFieldId.FastThinkingBudget
            or ConfigurationFieldId.FastThinkingCustomBudget
            or ConfigurationFieldId.FeishuApiBase
            or ConfigurationFieldId.FeishuAppId
            or ConfigurationFieldId.FeishuWhitelist
            or ConfigurationFieldId.HealthReportTargets
            or ConfigurationFieldId.HealthReportTime
            or ConfigurationFieldId.Model
            or ConfigurationFieldId.ModelBaseUrl
            or ConfigurationFieldId.MusicUnderstandModel
            or ConfigurationFieldId.NeteaseBaseUrl
            or ConfigurationFieldId.OfficialAppId
            or ConfigurationFieldId.OfficialWhitelistGroups
            or ConfigurationFieldId.OfficialWhitelistPrivates
            or ConfigurationFieldId.PanelDeployUrl
            or ConfigurationFieldId.ScenarioPreset
            or ConfigurationFieldId.ThinkingBudget
            or ConfigurationFieldId.ThinkingCustomBudget
            or ConfigurationFieldId.TtsApiBase
            or ConfigurationFieldId.TtsModel
            or ConfigurationFieldId.TtsProvider
            or ConfigurationFieldId.TtsServiceUrl
            or ConfigurationFieldId.VoiceEmotion
            or ConfigurationFieldId.VoiceName
            or ConfigurationFieldId.WebSearchSources) text = text.Trim();
        if (field is ConfigurationFieldId.AgentReasoningEffort or ConfigurationFieldId.ThinkingBudget
            or ConfigurationFieldId.FastThinkingBudget) text = text.ToLowerInvariant();
        if (field == ConfigurationFieldId.AgentReasoningEffort && text is "" or "default") text = "auto";
        var limit = field switch { ConfigurationFieldId.AgentPrompt => 8000,
            ConfigurationFieldId.AgentReasoningLevels => 1000, ConfigurationFieldId.ApprovalApprovers => 300, _ => int.MaxValue };
        if (text.Length > limit) text = text[..limit];
        StrictUtf8.GetByteCount(text);
        if (field is ConfigurationFieldId.Model or ConfigurationFieldId.AgentModel or ConfigurationFieldId.AgentServerModel
            or ConfigurationFieldId.FastModel or ConfigurationFieldId.MusicUnderstandModel or ConfigurationFieldId.TtsModel)
            if (text.Length > 1024) throw new ArgumentException("model_name_too_large");
        if (field is ConfigurationFieldId.ModelBaseUrl or ConfigurationFieldId.AgentServerBaseUrl or ConfigurationFieldId.FeishuApiBase
            or ConfigurationFieldId.TtsApiBase or ConfigurationFieldId.TtsServiceUrl or ConfigurationFieldId.NeteaseBaseUrl
            or ConfigurationFieldId.PanelDeployUrl) ValidateUrl(text);
        return text;
    }

    private static FieldEdit<ImmutableArray<DeviceConfiguration>> FreezeDevices(ConfigurationFieldId field,
        FieldEdit<ImmutableArray<DeviceConfiguration>> edit, ImmutableArray<ConfigurationFieldId>.Builder fields)
    {
        if (edit.Kind == EditKind.Keep) return default;
        fields.Add(field);
        if (edit.Kind == EditKind.Clear) return edit;
        if (!edit.TryGetValue(out var array) || array.IsDefault || array.Length > 128)
            throw new ArgumentException("invalid_devices");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<DeviceKey>();
        var copy = ImmutableArray.CreateBuilder<DeviceConfiguration>();
        foreach (var device in array)
        {
            if (device is null || string.IsNullOrWhiteSpace(device.NameRaw) || device.NameRaw.Length > 128
                || device.NameRaw.Contains('\0') || !names.Add(device.NameRaw.Trim())
                || (device.Key is { } key && (key.Value == Guid.Empty || !keys.Add(key)))
                || (device.TimeoutSeconds != 0 && (device.TimeoutSeconds < 30 || device.TimeoutSeconds > 7200)))
                throw new ArgumentException("invalid_device");
            StrictUtf8.GetByteCount(device.NameRaw);
            foreach (var text in new[] { device.Model, device.WorkDir, device.Tools })
                if (text is not null && StrictUtf8.GetByteCount(text) > 32768) throw new ArgumentException("device_string_too_large");
            copy.Add(device with { NameRaw = device.NameRaw.Trim() });
        }
        return FieldEdit<ImmutableArray<DeviceConfiguration>>.Set(copy.ToImmutable());
    }

    private static FieldEdit<ImmutableArray<PlatformPolicyEntry>> FreezePolicies(ConfigurationFieldId field,
        FieldEdit<ImmutableArray<PlatformPolicyEntry>> edit, ImmutableArray<ConfigurationFieldId>.Builder fields)
    {
        if (edit.Kind == EditKind.Keep) return default;
        fields.Add(field);
        if (edit.Kind == EditKind.Clear) return edit;
        if (!edit.TryGetValue(out var array) || array.IsDefault || array.Length > 32)
            throw new ArgumentException("invalid_policies");
        var copy = array.Select(p => p is null ? throw new ArgumentException("invalid_policy")
            : new PlatformPolicyEntry(FreezeTarget(p.Target), FreezePolicy(p.Configuration))).ToImmutableArray();
        if (copy.Select(p => p.Target).Distinct().Count() != copy.Length) throw new ArgumentException("duplicate_policy");
        return FieldEdit<ImmutableArray<PlatformPolicyEntry>>.Set(copy.OrderBy(p => TargetOrder(p.Target), StringComparer.Ordinal).ToImmutableArray());
    }

    private static RegisteredConfigurationChanges FreezeRegistered(RegisteredConfigurationChanges? source,
        ImmutableArray<ConfigurationFieldId>.Builder fields)
    {
        if (source is null) return new(ImmutableArray<PluginEnabledEdit>.Empty,
            ImmutableArray<ProviderEdit>.Empty, ImmutableArray<PolicyEdit>.Empty);
        if (source.Plugins.IsDefault || source.Providers.IsDefault || source.Policies.IsDefault
            || source.Plugins.Length > 128 || source.Providers.Length > 64 || source.Policies.Length > 32)
            throw new ArgumentException("invalid_registered_configuration");
        var plugins = source.Plugins.Select(p => p is null ? throw new ArgumentException("invalid_plugin")
            : new PluginEnabledEdit(new(CheckKey(p.Key.Value)), p.Enabled)).OrderBy(p => p.Key.Value, StringComparer.Ordinal).ToImmutableArray();
        var providers = source.Providers.Select(p => FreezeProvider(p, fields)).OrderBy(p => p.Key.Value, StringComparer.Ordinal).ToImmutableArray();
        var policies = source.Policies.Select(p => p is null ? throw new ArgumentException("invalid_policy")
            : new PolicyEdit(FreezeTarget(p.Target), FreezePolicy(p.Configuration))).OrderBy(p => TargetOrder(p.Target), StringComparer.Ordinal).ToImmutableArray();
        if (plugins.Select(p => p.Key).Distinct().Count() != plugins.Length
            || providers.Select(p => p.Key).Distinct().Count() != providers.Length
            || policies.Select(p => p.Target).Distinct().Count() != policies.Length)
            throw new ArgumentException("duplicate_registered_object");
        if (plugins.Length != 0) fields.Add(ConfigurationFieldId.RegisteredPlugins);
        if (providers.Length != 0) fields.Add(ConfigurationFieldId.RegisteredProviders);
        if (policies.Length != 0) fields.Add(ConfigurationFieldId.PlatformPolicies);
        return new(plugins, providers, policies);
    }

    private static ProviderEdit FreezeProvider(ProviderEdit provider, ImmutableArray<ConfigurationFieldId>.Builder fields)
    {
        if (provider is null || provider.Changes is not { } changes || changes.Credential is null)
            throw new ArgumentException("invalid_provider");
        var field = ConfigurationFieldId.RegisteredProviders;
        var result = new ProviderMetadataPatch(FreezeEdit(field, changes.NameRaw, fields),
            FreezeEdit(field, changes.Priority, fields), FreezeEdit(field, changes.BaseUrl, fields),
            FreezeEdit(field, changes.ModelName, fields), FreezeEdit(field, changes.Enabled, fields), changes.Credential);
        if (result.BaseUrl.TryGetValue(out var url)) ValidateUrl(url);
        return new(new(CheckKey(provider.Key.Value)), result);
    }

    private static string CheckKey(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128) throw new ArgumentException("invalid_object_key");
        StrictUtf8.GetByteCount(text); return text;
    }
    internal static PolicyTarget FreezeTarget(PolicyTarget target)
    {
        string Normalize(string platform)
        {
            CheckKey(platform);
            return BotAgent.Domain.Platforms.PlatformId.Normalize(platform);
        }
        return target switch
        {
            PolicyTarget.AccountDefault account => new PolicyTarget.AccountDefault(Normalize(account.PlatformId), CheckKey(account.AccountScope)),
            PolicyTarget.Instance instance => new PolicyTarget.Instance(new DriverKey(Normalize(instance.Key.PlatformId),
                CheckKey(instance.Key.AccountScope), instance.Key.InstanceId is null ? throw new ArgumentException("invalid_instance") : instance.Key.InstanceId)),
            _ => throw new ArgumentException("invalid_policy_target")
        };
    }
    internal static string TargetOrder(PolicyTarget target) => JsonSerializer.Serialize(target, Codec);
    internal static PlatformPolicyConfiguration FreezePolicy(PlatformPolicyConfiguration policy)
    {
        if (policy is null || !Enum.IsDefined(policy.Enabled) || !Enum.IsDefined(policy.ChatEnabled)
            || policy.AllowedActions is null || !Enum.IsDefined(policy.AllowedActions.Kind)
            || policy.AllowedActions.Actions.IsDefault || policy.Features.IsDefault || policy.Features.Length > 32
            || policy.AllowedActions.Actions.Length > 64) throw new ArgumentException("invalid_policy");
        var knownActions = new HashSet<string>(new[] { "like", "poke", "emoji_like", "recall", "ban", "kick", "card", "group_name", "leave", "send" }, StringComparer.Ordinal);
        var actions = policy.AllowedActions.Actions.ToArray();
        if (actions.Any(a => !knownActions.Contains(a.Value)) || actions.Distinct().Count() != actions.Length
            || (policy.AllowedActions.Kind == ActionRuleKind.Inherit && actions.Length != 0))
            throw new ArgumentException("invalid_policy_actions");
        var features = policy.Features.Select(f => f is null || !Enum.IsDefined(f.Feature)
            ? throw new ArgumentException("invalid_feature") : f with { }).OrderBy(f => f.Feature).ToImmutableArray();
        if (features.Select(f => f.Feature).Distinct().Count() != features.Length) throw new ArgumentException("duplicate_feature");
        return new(policy.Enabled, policy.ChatEnabled, FreezeList(policy.GroupWhitelist), FreezeList(policy.PrivateWhitelist),
            new(policy.AllowedActions.Kind, actions.OrderBy(a => a.Value, StringComparer.Ordinal).ToImmutableArray()), features);
    }
    private static ListRule FreezeList(ListRule rule)
    {
        if (rule is null || !Enum.IsDefined(rule.Kind)) throw new ArgumentException("invalid_list_rule");
        if (rule.TryGetExplicitValue(out var text))
        {
            if (text.Length > 4096) throw new ArgumentException("list_too_large");
            StrictUtf8.GetByteCount(text); return ListRule.Explicit(text);
        }
        return rule.Kind == ListRuleKind.Inherit ? ListRule.Inherit() : ListRule.AllowAll();
    }

    private static void ValidateUrl(string text)
    {
        if (text.Length == 0) return;
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new ArgumentException("invalid_url");
    }
    private static ImmutableArray<(ConfigurationFieldId Field, UserValueKind Mode)> DesiredModes(ConfigurationPatch patch)
    {
        var modes = ImmutableArray.CreateBuilder<(ConfigurationFieldId, UserValueKind)>();
        void Add<T>(ConfigurationFieldId field, FieldEdit<T> edit)
        {
            if (edit.Kind == EditKind.Keep) return;
            var inherit = edit.Kind == EditKind.Clear && field is ConfigurationFieldId.Model or ConfigurationFieldId.ModelBaseUrl;
            modes.Add((field, inherit ? UserValueKind.Inherited : UserValueKind.Explicit));
        }
        Add(ConfigurationFieldId.AgentAllowedUsers, patch.Agent!.AgentAllowedUsers);
        Add(ConfigurationFieldId.AgentDevices, patch.Agent!.AgentDevices);
        Add(ConfigurationFieldId.AgentMaxQueued, patch.Agent!.AgentMaxQueued);
        Add(ConfigurationFieldId.AgentModel, patch.Agent!.AgentModel);
        Add(ConfigurationFieldId.AgentPrefix, patch.Agent!.AgentPrefix);
        Add(ConfigurationFieldId.AgentProgressSeconds, patch.Agent!.AgentProgressSeconds);
        Add(ConfigurationFieldId.AgentPrompt, patch.Agent!.AgentPrompt);
        Add(ConfigurationFieldId.AgentReasoningEffort, patch.Agent!.AgentReasoningEffort);
        Add(ConfigurationFieldId.AgentReasoningLevels, patch.Agent!.AgentReasoningLevels);
        Add(ConfigurationFieldId.AgentReplyMaxChars, patch.Agent!.AgentReplyMaxChars);
        Add(ConfigurationFieldId.AgentServerBaseUrl, patch.Agent!.AgentServerBaseUrl);
        Add(ConfigurationFieldId.AgentServerCommandTimeoutSeconds, patch.Agent!.AgentServerCommandTimeoutSeconds);
        Add(ConfigurationFieldId.AgentServerDocker, patch.Agent!.AgentServerDocker);
        Add(ConfigurationFieldId.AgentServerKeepContext, patch.Agent!.AgentServerKeepContext);
        Add(ConfigurationFieldId.AgentServerMaxSteps, patch.Agent!.AgentServerMaxSteps);
        Add(ConfigurationFieldId.AgentServerModel, patch.Agent!.AgentServerModel);
        Add(ConfigurationFieldId.AgentServerQqActions, patch.Agent!.AgentServerQqActions);
        Add(ConfigurationFieldId.AgentServerTools, patch.Agent!.AgentServerTools);
        Add(ConfigurationFieldId.AgentServerUseGate, patch.Agent!.AgentServerUseGate);
        Add(ConfigurationFieldId.AgentServerWorkDir, patch.Agent!.AgentServerWorkDir);
        Add(ConfigurationFieldId.AgentTarget, patch.Agent!.AgentTarget);
        Add(ConfigurationFieldId.AgentTimeoutSeconds, patch.Agent!.AgentTimeoutSeconds);
        Add(ConfigurationFieldId.AgentTools, patch.Agent!.AgentTools);
        Add(ConfigurationFieldId.AgentWorkDir, patch.Agent!.AgentWorkDir);
        Add(ConfigurationFieldId.EnableAgentBridge, patch.Agent!.EnableAgentBridge);
        Add(ConfigurationFieldId.EnableAgentMask, patch.Agent!.EnableAgentMask);
        Add(ConfigurationFieldId.EnableHostAgent, patch.Agent!.EnableHostAgent);
        Add(ConfigurationFieldId.EnableServerAgent, patch.Agent!.EnableServerAgent);
        Add(ConfigurationFieldId.PanelDeployEnabled, patch.Agent!.PanelDeployEnabled);
        Add(ConfigurationFieldId.PanelDeployUrl, patch.Agent!.PanelDeployUrl);
        Add(ConfigurationFieldId.AiDesire, patch.Behavior!.AiDesire);
        Add(ConfigurationFieldId.AiModeEnabled, patch.Behavior!.AiModeEnabled);
        Add(ConfigurationFieldId.ApprovalApprovers, patch.Behavior!.ApprovalApprovers);
        Add(ConfigurationFieldId.BotPersona, patch.Behavior!.BotPersona);
        Add(ConfigurationFieldId.EnableApprovals, patch.Behavior!.EnableApprovals);
        Add(ConfigurationFieldId.EnableAtmosphereDamping, patch.Behavior!.EnableAtmosphereDamping);
        Add(ConfigurationFieldId.EnableLinkPreview, patch.Behavior!.EnableLinkPreview);
        Add(ConfigurationFieldId.EnableParticipationGating, patch.Behavior!.EnableParticipationGating);
        Add(ConfigurationFieldId.EnablePoke, patch.Behavior!.EnablePoke);
        Add(ConfigurationFieldId.EnableProactive, patch.Behavior!.EnableProactive);
        Add(ConfigurationFieldId.EnableProfileSummary, patch.Behavior!.EnableProfileSummary);
        Add(ConfigurationFieldId.EnableQuestions, patch.Behavior!.EnableQuestions);
        Add(ConfigurationFieldId.EnableStickers, patch.Behavior!.EnableStickers);
        Add(ConfigurationFieldId.EnableWebSearch, patch.Behavior!.EnableWebSearch);
        Add(ConfigurationFieldId.FilterActionNarration, patch.Behavior!.FilterActionNarration);
        Add(ConfigurationFieldId.GroupCooldownSeconds, patch.Behavior!.GroupCooldownSeconds);
        Add(ConfigurationFieldId.IdleFallbackSeconds, patch.Behavior!.IdleFallbackSeconds);
        Add(ConfigurationFieldId.IgnoreBracketMessages, patch.Behavior!.IgnoreBracketMessages);
        Add(ConfigurationFieldId.LinkPreviewMax, patch.Behavior!.LinkPreviewMax);
        Add(ConfigurationFieldId.LinkPreviewTimeoutSeconds, patch.Behavior!.LinkPreviewTimeoutSeconds);
        Add(ConfigurationFieldId.MaxAgentSteps, patch.Behavior!.MaxAgentSteps);
        Add(ConfigurationFieldId.MaxConcurrentReplies, patch.Behavior!.MaxConcurrentReplies);
        Add(ConfigurationFieldId.MaxContextMessages, patch.Behavior!.MaxContextMessages);
        Add(ConfigurationFieldId.MaxMessagesPerConversation, patch.Behavior!.MaxMessagesPerConversation);
        Add(ConfigurationFieldId.MaxProfileChars, patch.Behavior!.MaxProfileChars);
        Add(ConfigurationFieldId.MoodTtlSeconds, patch.Behavior!.MoodTtlSeconds);
        Add(ConfigurationFieldId.ParticipationCooldownSeconds, patch.Behavior!.ParticipationCooldownSeconds);
        Add(ConfigurationFieldId.ParticipationMaxActiveLifetimeSeconds, patch.Behavior!.ParticipationMaxActiveLifetimeSeconds);
        Add(ConfigurationFieldId.ParticipationMaxConsecutiveReplies, patch.Behavior!.ParticipationMaxConsecutiveReplies);
        Add(ConfigurationFieldId.ParticipationMaxExitingLifetimeSeconds, patch.Behavior!.ParticipationMaxExitingLifetimeSeconds);
        Add(ConfigurationFieldId.ParticipationProbingMaxReplies, patch.Behavior!.ParticipationProbingMaxReplies);
        Add(ConfigurationFieldId.PokeCooldownSeconds, patch.Behavior!.PokeCooldownSeconds);
        Add(ConfigurationFieldId.PrivateCooldownSeconds, patch.Behavior!.PrivateCooldownSeconds);
        Add(ConfigurationFieldId.ProactiveCooldownSeconds, patch.Behavior!.ProactiveCooldownSeconds);
        Add(ConfigurationFieldId.ProactiveQuietSeconds, patch.Behavior!.ProactiveQuietSeconds);
        Add(ConfigurationFieldId.ProfileLookupCount, patch.Behavior!.ProfileLookupCount);
        Add(ConfigurationFieldId.ProfileSummaryIntervalSeconds, patch.Behavior!.ProfileSummaryIntervalSeconds);
        Add(ConfigurationFieldId.ProfileSummaryLines, patch.Behavior!.ProfileSummaryLines);
        Add(ConfigurationFieldId.ProfileSummaryMaxChars, patch.Behavior!.ProfileSummaryMaxChars);
        Add(ConfigurationFieldId.ProfileSummaryThreshold, patch.Behavior!.ProfileSummaryThreshold);
        Add(ConfigurationFieldId.ScenarioPreset, patch.Behavior!.ScenarioPreset);
        Add(ConfigurationFieldId.SegmentDelayMs, patch.Behavior!.SegmentDelayMs);
        Add(ConfigurationFieldId.SplitReplies, patch.Behavior!.SplitReplies);
        Add(ConfigurationFieldId.StickerCandidates, patch.Behavior!.StickerCandidates);
        Add(ConfigurationFieldId.StickerCooldownSeconds, patch.Behavior!.StickerCooldownSeconds);
        Add(ConfigurationFieldId.StickerCurateIntervalSeconds, patch.Behavior!.StickerCurateIntervalSeconds);
        Add(ConfigurationFieldId.StickerLibraryMax, patch.Behavior!.StickerLibraryMax);
        Add(ConfigurationFieldId.SuitabilityThreshold, patch.Behavior!.SuitabilityThreshold);
        Add(ConfigurationFieldId.WebSearchCooldownSeconds, patch.Behavior!.WebSearchCooldownSeconds);
        Add(ConfigurationFieldId.WebSearchMaxResults, patch.Behavior!.WebSearchMaxResults);
        Add(ConfigurationFieldId.WebSearchSources, patch.Behavior!.WebSearchSources);
        Add(ConfigurationFieldId.WebSearchTimeoutSeconds, patch.Behavior!.WebSearchTimeoutSeconds);
        Add(ConfigurationFieldId.WebSearchUseModelSearch, patch.Behavior!.WebSearchUseModelSearch);
        Add(ConfigurationFieldId.AdaptiveSamplingEnabled, patch.Model!.AdaptiveSamplingEnabled);
        Add(ConfigurationFieldId.DefaultTemperature, patch.Model!.DefaultTemperature);
        Add(ConfigurationFieldId.DefaultTopP, patch.Model!.DefaultTopP);
        Add(ConfigurationFieldId.EmotionalTemperature, patch.Model!.EmotionalTemperature);
        Add(ConfigurationFieldId.EmotionalTopP, patch.Model!.EmotionalTopP);
        Add(ConfigurationFieldId.FastModel, patch.Model!.FastModel);
        Add(ConfigurationFieldId.FastReply, patch.Model!.FastReply);
        Add(ConfigurationFieldId.FastThinkingBudget, patch.Model!.FastThinkingBudget);
        Add(ConfigurationFieldId.FastThinkingCustomBudget, patch.Model!.FastThinkingCustomBudget);
        Add(ConfigurationFieldId.MaxTokens, patch.Model!.MaxTokens);
        Add(ConfigurationFieldId.Model, patch.Model!.Model);
        Add(ConfigurationFieldId.ModelBaseUrl, patch.Model!.ModelBaseUrl);
        Add(ConfigurationFieldId.RationalTemperature, patch.Model!.RationalTemperature);
        Add(ConfigurationFieldId.RationalTopP, patch.Model!.RationalTopP);
        Add(ConfigurationFieldId.ThinkingBudget, patch.Model!.ThinkingBudget);
        Add(ConfigurationFieldId.ThinkingCustomBudget, patch.Model!.ThinkingCustomBudget);
        Add(ConfigurationFieldId.EnableMusic, patch.Music!.EnableMusic);
        Add(ConfigurationFieldId.MusicBitrate, patch.Music!.MusicBitrate);
        Add(ConfigurationFieldId.MusicKeepAudio, patch.Music!.MusicKeepAudio);
        Add(ConfigurationFieldId.MusicLibraryMax, patch.Music!.MusicLibraryMax);
        Add(ConfigurationFieldId.MusicListenCooldownSeconds, patch.Music!.MusicListenCooldownSeconds);
        Add(ConfigurationFieldId.MusicMaxAnalysisSeconds, patch.Music!.MusicMaxAnalysisSeconds);
        Add(ConfigurationFieldId.MusicMaxDownloadMb, patch.Music!.MusicMaxDownloadMb);
        Add(ConfigurationFieldId.MusicNoteTtlDays, patch.Music!.MusicNoteTtlDays);
        Add(ConfigurationFieldId.MusicSendAudioToModel, patch.Music!.MusicSendAudioToModel);
        Add(ConfigurationFieldId.MusicSources, patch.Music!.MusicSources);
        Add(ConfigurationFieldId.MusicUnderstandModel, patch.Music!.MusicUnderstandModel);
        Add(ConfigurationFieldId.NeteaseBaseUrl, patch.Music!.NeteaseBaseUrl);
        Add(ConfigurationFieldId.FeishuApiBase, patch.Platform!.FeishuApiBase);
        Add(ConfigurationFieldId.FeishuAppId, patch.Platform!.FeishuAppId);
        Add(ConfigurationFieldId.FeishuEnabled, patch.Platform!.FeishuEnabled);
        Add(ConfigurationFieldId.FeishuWhitelist, patch.Platform!.FeishuWhitelist);
        Add(ConfigurationFieldId.LocalChannelIds, patch.Platform!.LocalChannelIds);
        Add(ConfigurationFieldId.MessageWhitelist, patch.Platform!.MessageWhitelist);
        Add(ConfigurationFieldId.OfficialAppId, patch.Platform!.OfficialAppId);
        Add(ConfigurationFieldId.OfficialChatEnabled, patch.Platform!.OfficialChatEnabled);
        Add(ConfigurationFieldId.OfficialEnabled, patch.Platform!.OfficialEnabled);
        Add(ConfigurationFieldId.OfficialSandbox, patch.Platform!.OfficialSandbox);
        Add(ConfigurationFieldId.OfficialWhitelistGroups, patch.Platform!.OfficialWhitelistGroups);
        Add(ConfigurationFieldId.OfficialWhitelistPrivates, patch.Platform!.OfficialWhitelistPrivates);
        Add(ConfigurationFieldId.PlatformPolicies, patch.Platform!.PlatformPolicies);
        if (patch.Platform.PlatformPolicies.Kind == EditKind.Keep && patch.Registered!.Policies.Length != 0)
            modes.Add((ConfigurationFieldId.PlatformPolicies, UserValueKind.Explicit));
        Add(ConfigurationFieldId.PrivateChatEnabled, patch.Platform!.PrivateChatEnabled);
        Add(ConfigurationFieldId.WhitelistGroups, patch.Platform!.WhitelistGroups);
        Add(ConfigurationFieldId.WhitelistPrivates, patch.Platform!.WhitelistPrivates);
        Add(ConfigurationFieldId.HealthReportEnabled, patch.Reporting!.HealthReportEnabled);
        Add(ConfigurationFieldId.HealthReportTargets, patch.Reporting!.HealthReportTargets);
        Add(ConfigurationFieldId.HealthReportTime, patch.Reporting!.HealthReportTime);
        Add(ConfigurationFieldId.EnableVoice, patch.Voice!.EnableVoice);
        Add(ConfigurationFieldId.TtsApiBase, patch.Voice!.TtsApiBase);
        Add(ConfigurationFieldId.TtsModel, patch.Voice!.TtsModel);
        Add(ConfigurationFieldId.TtsProvider, patch.Voice!.TtsProvider);
        Add(ConfigurationFieldId.TtsServiceUrl, patch.Voice!.TtsServiceUrl);
        Add(ConfigurationFieldId.VoiceEagerness, patch.Voice!.VoiceEagerness);
        Add(ConfigurationFieldId.VoiceEmotion, patch.Voice!.VoiceEmotion);
        Add(ConfigurationFieldId.VoiceMaxChars, patch.Voice!.VoiceMaxChars);
        Add(ConfigurationFieldId.VoiceName, patch.Voice!.VoiceName);
        Add(ConfigurationFieldId.VoicePitch, patch.Voice!.VoicePitch);
        Add(ConfigurationFieldId.VoiceSpeed, patch.Voice!.VoiceSpeed);
        Add(ConfigurationFieldId.VoiceVol, patch.Voice!.VoiceVol);
        return modes.ToImmutable();
    }

    private static bool ModesChanged(SqliteConnection conn, OwnedIntent owned)
    {
        foreach (var (field, mode) in owned.Modes)
        {
            var current = ConfigurationStorageBridge.ReadFieldState(conn, field, legacyPropertyPresent: false);
            if (!current.Tracked || current.UserMode != mode) return true;
        }
        return false;
    }

    private static void PersistModes(SqliteConnection conn, OwnedIntent owned)
    {
        foreach (var (field, mode) in owned.Modes)
            AppDatabase.Exec(conn, "INSERT INTO configuration_field_modes(field_id,user_mode) VALUES($field,$mode) " +
                "ON CONFLICT(field_id) DO UPDATE SET user_mode=excluded.user_mode", ("$field", (int)field), ("$mode", (int)mode));
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;

namespace BotAgent.Storage;

public sealed partial class ConfigurationStore
{
    private readonly object _managedGate = new();
    private ManagedCapabilityService? _managedService;

    internal IManagedCapabilities CreateManagedCapabilities(IManagedAuthorization authorization,
        Func<ManagedCatalogSnapshot> catalog,
        Func<ManagedCommand, CancellationToken, ValueTask<ManagedDispatchResult>> dispatch)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dispatch);
        lock (_managedGate)
        {
            if (_managedService is not null)
            {
                if (!_managedService.HasBindings(authorization, catalog, dispatch))
                    throw new InvalidOperationException("managed_capabilities_already_bound");
                return _managedService;
            }
            return _managedService = new(this, authorization, catalog, dispatch);
        }
    }

    internal ManagedOperationResult AcceptManaged(ConfigurationCaller caller, ManagedExecuteRequest request,
        ManagedObjectKey target, ManagedAction action)
    {
        if (!ValidManagedCaller(caller) || !ValidKey(ConfigurationKey(request.Key)))
            return ManagedResult(ManagedResultCode.ValidationFailed);
        var result = ManagedResult(ManagedResultCode.StorageUnavailable);
        var callbackComplete = false;
        try
        {
            AppDatabase.Write(connection =>
            {
                var governance = MapManagedCode(Govern(connection, ConfigurationKey(request.Key), out var floor));
                var previous = ReadManaged(connection, caller, request.Key, out var priorFingerprint);
                byte[]? fingerprint = null;
                try
                {
                    fingerprint = ManagedFingerprint(connection, caller, request);
                    if (previous is not null && (fingerprint is null || priorFingerprint is null
                        || !CryptographicOperations.FixedTimeEquals(fingerprint, priorFingerprint)))
                        result = ManagedResult(ManagedResultCode.IdempotencyConflict);
                    else if (governance == ManagedResultCode.KeyExpired)
                        result = ManagedResult(governance);
                    else if (previous is not null)
                        result = new(ManagedResultCode.Ok, true, RecoverManagedReceipt(previous));
                    else if (request.Key.Generation != _generation)
                        result = ManagedResult(ManagedResultCode.KeyGenerationMismatch);
                    else if (governance != ManagedResultCode.Ok)
                        result = ManagedResult(governance);
                    else if (!HasCapacity(connection, caller, floor))
                        result = ManagedResult(ManagedResultCode.CapacityExceeded);
                    else if (fingerprint is null)
                        result = ManagedResult(ManagedResultCode.StorageUnavailable);
                    else
                    {
                        var receipt = new ManagedReceipt(request.Key, target, action, request.ExpectedDescriptorRevision,
                            ManagedOperationState.NotStarted, null);
                        var json = JsonSerializer.Serialize(receipt, Codec);
                        if (StrictUtf8.GetByteCount(json) > 8192)
                            result = ManagedResult(ManagedResultCode.CapacityExceeded);
                        else
                        {
                            using var command = connection.CreateCommand();
                            command.CommandText = InsertOperationSql;
                            ScopeManaged(command, caller, request.Key);
                            command.Parameters.AddWithValue("$namespace", "mg1");
                            command.Parameters.AddWithValue("$g", request.Key.Generation.Value.ToString("N"));
                            command.Parameters.AddWithValue("$expires", checked(request.Key.IssuedUnixSeconds + LifetimeSeconds));
                            command.Parameters.AddWithValue("$fp", fingerprint);
                            command.Parameters.AddWithValue("$receipt", json);
                            command.Parameters.AddWithValue("$terminal", 0);
                            command.ExecuteNonQuery();
                            result = new(ManagedResultCode.Ok, false, receipt);
                        }
                    }
                    callbackComplete = true;
                }
                finally
                {
                    if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
                    if (priorFingerprint is not null) CryptographicOperations.ZeroMemory(priorFingerprint);
                }
            });
            return result;
        }
        catch (Exception)
        { return ManagedResult(callbackComplete ? ManagedResultCode.Unknown : ManagedResultCode.StorageUnavailable); }
    }

    /// <summary>Only the trusted coordinator may inspect the original target before performing Query/Cancel authorization.</summary>
    internal ManagedReceipt? PeekManaged(ConfigurationCaller caller, ManagedOperationKey key)
    {
        if (!ValidManagedCaller(caller) || !ValidKey(ConfigurationKey(key))) return null;
        using var connection = AppDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var receipt = ReadManaged(connection, caller, key, out var fingerprint);
        if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
        return receipt;
    }

    internal ManagedOperationResult FindManaged(ConfigurationCaller caller, ManagedOperationKey key)
        => MutateManaged(caller, key, receipt => receipt);

    internal ManagedOperationResult ClaimManaged(ConfigurationCaller caller, ManagedOperationKey key)
        => MutateManaged(caller, key, receipt => receipt.State == ManagedOperationState.NotStarted
            && receipt.Key.Generation == _generation
                ? receipt with { State = ManagedOperationState.Running } : receipt);

    internal ManagedOperationResult CancelManaged(ConfigurationCaller caller, ManagedOperationKey key)
        => MutateManaged(caller, key, receipt => receipt.State is ManagedOperationState.NotStarted or ManagedOperationState.Running
            ? receipt with { State = ManagedOperationState.CancelRequested } : receipt);

    internal ManagedOperationResult FinishManaged(ConfigurationCaller caller, ManagedOperationKey key,
        ManagedOperationState state, string? errorCode)
        => MutateManaged(caller, key, receipt => ManagedTerminal(receipt.State) || receipt.Key.Generation != _generation
            ? receipt : receipt with { State = state, ErrorCode = errorCode });

    private ManagedOperationResult MutateManaged(ConfigurationCaller caller, ManagedOperationKey key,
        Func<ManagedReceipt, ManagedReceipt> update)
    {
        var result = ManagedResult(ManagedResultCode.StorageUnavailable);
        var callbackComplete = false;
        try
        {
            AppDatabase.Write(connection =>
            {
                var code = MapManagedCode(Govern(connection, ConfigurationKey(key), out _));
                var receipt = ReadManaged(connection, caller, key, out var fingerprint);
                if (fingerprint is not null) CryptographicOperations.ZeroMemory(fingerprint);
                if (receipt is null) result = ManagedResult(ManagedResultCode.Forbidden);
                else if (code != ManagedResultCode.Ok) result = ManagedResult(code);
                else
                {
                    var recovered = RecoverManagedReceipt(receipt);
                    var next = update(recovered);
                    var changed = next != receipt;
                    if (changed) WriteManaged(connection, caller, next);
                    result = new(ManagedResultCode.Ok, !changed, next);
                }
                callbackComplete = true;
            });
            return result;
        }
        catch (Exception)
        { return ManagedResult(callbackComplete ? ManagedResultCode.Unknown : ManagedResultCode.StorageUnavailable); }
    }

    private ManagedReceipt RecoverManagedReceipt(ManagedReceipt receipt)
        => receipt.Key.Generation != _generation && !ManagedTerminal(receipt.State)
            ? receipt with { State = ManagedOperationState.Unknown, ErrorCode = "restart_state_unknown" } : receipt;

    private static ManagedReceipt? ReadManaged(Microsoft.Data.Sqlite.SqliteConnection connection,
        ConfigurationCaller caller, ManagedOperationKey key, out byte[]? fingerprint)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ReadOperationSql;
        ScopeManaged(command, caller, key);
        using var reader = command.ExecuteReader();
        fingerprint = null;
        if (!reader.Read()) return null;
        if (reader.IsDBNull(2) || !KnownCodec(reader.GetString(2)) || reader.GetString(3) != "mg1")
            throw new ReceiptCodecException();
        var receipt = JsonSerializer.Deserialize<ManagedReceipt>(reader.GetString(0), Codec)
            ?? throw new ReceiptCodecException();
        if (receipt.Key != key || !Enum.IsDefined(receipt.Action) || !Enum.IsDefined(receipt.State)
            || receipt.Target is null || string.IsNullOrWhiteSpace(receipt.DescriptorRevision)
            || ManagedTerminal(receipt.State) != (reader.GetInt32(4) == 1)) throw new ReceiptCodecException();
        fingerprint = ((byte[])reader["fingerprint"]).ToArray();
        return receipt;
    }

    private static void WriteManaged(Microsoft.Data.Sqlite.SqliteConnection connection,
        ConfigurationCaller caller, ManagedReceipt receipt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = UpdateOperationSql;
        ScopeManaged(command, caller, receipt.Key);
        command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(receipt, Codec));
        command.Parameters.AddWithValue("$terminal", ManagedTerminal(receipt.State) ? 1 : 0);
        if (command.ExecuteNonQuery() != 1) throw new ReceiptCodecException();
    }

    private static byte[]? ManagedFingerprint(Microsoft.Data.Sqlite.SqliteConnection connection,
        ConfigurationCaller caller, ManagedExecuteRequest request)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ReadGenerationSql;
        command.Parameters.AddWithValue("$generation", request.Key.Generation.Value.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!KnownCodec(reader.GetString(1))) throw new ReceiptCodecException();
        var secret = (byte[])reader.GetValue(0);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new object[]
            { caller.AuthorityId, caller.SubjectId, caller.ResourceId, "mg1", request }, Codec);
        try { return HMACSHA256.HashData(secret, payload); }
        finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(payload); }
    }

    private static void ScopeManaged(Microsoft.Data.Sqlite.SqliteCommand command,
        ConfigurationCaller caller, ManagedOperationKey key)
    {
        command.Parameters.AddWithValue("$a", caller.AuthorityId);
        command.Parameters.AddWithValue("$s", caller.SubjectId);
        command.Parameters.AddWithValue("$r", caller.ResourceId);
        command.Parameters.AddWithValue("$k", $"mg1:{key.Generation.Value:N}:" +
            key.IssuedUnixSeconds.ToString(CultureInfo.InvariantCulture) + $":{key.Nonce:N}");
    }

    internal static bool ValidManagedCaller(ConfigurationCaller? caller)
        => caller is not null && caller.ResourceId == "bot" && !string.IsNullOrWhiteSpace(caller.AuthorityId)
            && !string.IsNullOrWhiteSpace(caller.SubjectId);
    internal static bool ValidManagedKey(ManagedOperationKey key) => ValidKey(ConfigurationKey(key));
    private static ConfigurationOperationKey ConfigurationKey(ManagedOperationKey key)
        => new(key.Generation, key.IssuedUnixSeconds, key.Nonce);
    private static bool ManagedTerminal(ManagedOperationState state)
        => state is ManagedOperationState.Completed or ManagedOperationState.Failed or ManagedOperationState.Cancelled;
    private static ManagedOperationResult ManagedResult(ManagedResultCode code) => new(code, false, null);
    private static ManagedResultCode MapManagedCode(ConfigurationResultCode code) => code switch
    {
        ConfigurationResultCode.Ok => ManagedResultCode.Ok,
        ConfigurationResultCode.KeyExpired => ManagedResultCode.KeyExpired,
        ConfigurationResultCode.ClockRegression => ManagedResultCode.ClockRegression,
        ConfigurationResultCode.KeyGenerationMismatch => ManagedResultCode.KeyGenerationMismatch,
        ConfigurationResultCode.CapacityExceeded => ManagedResultCode.CapacityExceeded,
        ConfigurationResultCode.UnsupportedContractRevision => ManagedResultCode.UnsupportedContractRevision,
        ConfigurationResultCode.ValidationFailed => ManagedResultCode.ValidationFailed,
        _ => ManagedResultCode.StorageUnavailable
    };
}

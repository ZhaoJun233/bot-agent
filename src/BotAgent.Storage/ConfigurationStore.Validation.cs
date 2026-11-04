using System.Collections.Immutable;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;

namespace BotAgent.Storage;

public sealed partial class ConfigurationStore
{
    /// <summary>Uses the save planner without accepting an operation or invoking its persistence callback.</summary>
    internal ValueTask<ManagedValidationResult> ValidateAsync(ConfigurationCaller caller,
        ConfigurationValidationRequest request, CancellationToken ct = default)
    {
        ManagedValidationResult Result(ManagedResultCode code)
            => new(code, ImmutableArray<FieldError>.Empty);
        if (!Allowed(caller, ConfigurationAccess.ValidateConfiguration))
            return ValueTask.FromResult(Result(ManagedResultCode.Forbidden));
        if (request is null || request.Changes is null || request.Secrets.IsDefault
            || request.ExpectedVersion.Value < 0)
            return ValueTask.FromResult(Result(ManagedResultCode.ValidationFailed));
        if (request.ContractRevision != Revision)
            return ValueTask.FromResult(Result(ManagedResultCode.UnsupportedContractRevision));
        if (request.Secrets.Any(secret => secret is null || secret.Kind != SecretEditKind.Keep)
            && !Allowed(caller, ConfigurationAccess.EditAllowedSecrets))
            return ValueTask.FromResult(Result(ManagedResultCode.Forbidden));
        ct.ThrowIfCancellationRequested();
        OwnedIntent? owned = null;
        try
        {
            // Synchronous ownership precedes every callback (and any future asynchronous boundary).
            owned = Own(new ConfigurationSaveRequest(request.ContractRevision, request.ExpectedVersion,
                default, request.Changes, request.Secrets));
            owned = CanonicalizePrimaryAliases(owned);
            using var connection = AppDatabase.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            if (!Allowed(caller, ConfigurationAccess.ValidateConfiguration)
                || (owned.Secrets.Any(secret => secret.Kind != SecretEditKind.Keep)
                    && !Allowed(caller, ConfigurationAccess.EditAllowedSecrets)))
                return ValueTask.FromResult(Result(ManagedResultCode.Forbidden));
            if (Version(connection) != request.ExpectedVersion)
                return ValueTask.FromResult(Result(ManagedResultCode.VersionConflict));
            ct.ThrowIfCancellationRequested();
            var mutations = owned.Secrets.Select(secret => secret.CredentialKey is { } key
                ? new SecretMutation(secret.Slot, secret.Kind) { CredentialKey = key, NormalizedValue = secret.NormalizedValue }
                : secret.Slot == WritableSecretSlot.ModelApiKey
                    ? secret.Kind == SecretEditKind.Keep ? ConfigurationStorageBridge.ObserveModelApiKey(connection)
                        : ConfigurationStorageBridge.ProjectModelApiKeyMutation(secret.Kind, secret.NormalizedValue)
                    : new SecretMutation(secret.Slot, secret.Kind)).ToImmutableArray();
            var proposed = _participant.SecretSources(connection, mutations);
            if (proposed.IsDefault || proposed.Length != 6
                || proposed.Select(secret => secret.Slot).Distinct().Count() != 6)
                return ValueTask.FromResult(Result(ManagedResultCode.StorageUnavailable));
            var prepared = _participant.Prepare(connection, owned.Patch, proposed, mutations,
                ConfigurationStorageProfile.Strict);
            if (prepared.Errors.IsDefault || prepared.DeviceKeyAssignments.IsDefault || prepared.Errors.Length > 32
                || prepared.FailureCode is not (ConfigurationResultCode.ValidationFailed or ConfigurationResultCode.LegacyNeedsReview))
                return ValueTask.FromResult(Result(ManagedResultCode.StorageUnavailable));
            ct.ThrowIfCancellationRequested();
            if (!Allowed(caller, ConfigurationAccess.ValidateConfiguration)
                || (owned.Secrets.Any(secret => secret.Kind != SecretEditKind.Keep)
                    && !Allowed(caller, ConfigurationAccess.EditAllowedSecrets)))
                return ValueTask.FromResult(Result(ManagedResultCode.Forbidden));
            // No commit: dispose rolls the read transaction back. In particular, never run prepared.Persist.
            return ValueTask.FromResult(new ManagedValidationResult(prepared.Errors.IsEmpty
                ? ManagedResultCode.Ok : ManagedResultCode.ValidationFailed, prepared.Errors));
        }
        catch (AliasIntentException error)
        {
            return ValueTask.FromResult(new ManagedValidationResult(ManagedResultCode.ValidationFailed,
                ImmutableArray.Create(new FieldError(new FieldAddress(error.Field, null), error.Code))));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is ArgumentException or JsonException or ObjectDisposedException or OverflowException)
        { return ValueTask.FromResult(Result(ManagedResultCode.ValidationFailed)); }
        catch (Exception)
        { return ValueTask.FromResult(Result(ManagedResultCode.StorageUnavailable)); }
        finally { owned?.Dispose(); }
    }
}

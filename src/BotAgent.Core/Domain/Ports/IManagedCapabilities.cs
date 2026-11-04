using System.Collections.Immutable;
using BotAgent.Domain.Configuration;

namespace BotAgent.Domain.Management;

public interface IManagedAuthorization
{
    ValueTask<bool> IsAllowedAsync(ManagedAuthorizationRequest request,
        CancellationToken ct = default);
}
public interface IManagedCapabilities
{
    ValueTask<ManagedPage> ListAsync(ConfigurationCaller caller,
        ManagedPageRequest request, CancellationToken ct = default);
    ValueTask<ManagedInspectResult> InspectAsync(ConfigurationCaller caller,
        ManagedObjectKey key, CancellationToken ct = default);
    ValueTask<ManagedValidationResult> ValidateAsync(ConfigurationCaller caller,
        ConfigurationValidationRequest request,
        CancellationToken ct = default);
    ValueTask<ManagedOperationResult> ExecuteAsync(ConfigurationCaller caller,
        ManagedExecuteRequest request, CancellationToken ct = default);
    ValueTask<ManagedOperationResult> QueryAsync(ConfigurationCaller caller,
        ManagedOperationKey key, CancellationToken ct = default);
    ValueTask<ManagedCancellationResult> CancelAsync(ConfigurationCaller caller,
        ManagedOperationKey key, CancellationToken ct = default);
}

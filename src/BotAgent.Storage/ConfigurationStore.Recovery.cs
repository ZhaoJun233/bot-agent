using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;

namespace BotAgent.Storage;

public sealed partial class ConfigurationStore
{
    /// <summary>Query the original receipt with its original target permission; no runtime dispatcher is needed.</summary>
    internal async ValueTask<ManagedOperationResult> QueryManagedAsync(ConfigurationCaller caller, ManagedOperationKey key,
        IManagedAuthorization authorization, CancellationToken ct = default)
    {
        if (!ValidManagedCaller(caller)) return ManagedResult(ManagedResultCode.Forbidden);
        if (!ValidManagedKey(key)) return ManagedResult(ManagedResultCode.ValidationFailed);
        try
        {
            var original = PeekManaged(caller, key);
            if (original is null) return ManagedResult(ManagedResultCode.Forbidden);
            ct.ThrowIfCancellationRequested();
            bool? allowed;
            try { allowed = await authorization.IsAllowedAsync(new(caller, original.Target, ManagedPermission.Query, original.Action), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { allowed = null; }
            return allowed is true ? FindManaged(caller, key)
                : ManagedResult(allowed is null ? ManagedResultCode.Unknown : ManagedResultCode.Forbidden);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return ManagedResult(ManagedResultCode.StorageUnavailable); }
    }
}

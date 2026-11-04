using System.Collections.Immutable;

namespace BotAgent.Domain.Configuration;

public interface IConfigurationChanges
{
    ValueTask<ConfigurationReadResult> ReadAsync(
        ConfigurationCaller caller, ConfigurationReadKind kind,
        CancellationToken ct = default);
    ValueTask<ConfigurationSaveResult> SaveAsync(
        ConfigurationCaller caller, ConfigurationSaveRequest request,
        CancellationToken ct = default);
    ValueTask<ConfigurationFindResult> FindAsync(
        ConfigurationCaller caller, ConfigurationOperationKey key,
        CancellationToken ct = default);
}

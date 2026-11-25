extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility type; Storage owns independent token metering.</summary>
public sealed class TokenUsageStore : StorageModule::BotAgent.Adapters.Persistence.TokenUsageStore
{
}

extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility type; all persistence behavior lives in Storage.</summary>
public sealed class HostMetrics : StorageModule::BotAgent.Adapters.Persistence.HostMetrics
{
}
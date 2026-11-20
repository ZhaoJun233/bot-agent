extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility type; the same audit chain implementation is owned by Storage.</summary>
public sealed class AuditLogStore : StorageModule::BotAgent.Adapters.Persistence.AuditLogStore
{
}

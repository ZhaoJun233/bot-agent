extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility type; all persistence behavior lives in Storage.</summary>
public sealed class ConversationStore : StorageModule::BotAgent.Adapters.Persistence.ConversationStore
{
}
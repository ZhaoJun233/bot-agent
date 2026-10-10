extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility type; all persistence behavior lives in Storage.</summary>
public sealed class AgentSessionStore : StorageModule::BotAgent.Adapters.Persistence.AgentSessionStore
{
    public AgentSessionStore(string path, Action<string> log) : base(path, log) { }
}
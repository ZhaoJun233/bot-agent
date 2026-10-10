extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility type; all persistence behavior lives in Storage.</summary>
public sealed class MemberRoleStore : StorageModule::BotAgent.Adapters.Persistence.MemberRoleStore
{
    public MemberRoleStore(Action<string> log) : base(log) { }
}
extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility type; all persistence behavior lives in Storage.</summary>
internal sealed class PanelPasswordStore : StorageModule::BotAgent.Adapters.Persistence.PanelPasswordStore
{
    public PanelPasswordStore(string path) : base(path) { }
}
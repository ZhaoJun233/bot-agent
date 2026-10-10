namespace BotAgent.Storage;

public interface IPluginStore
{
    IReadOnlyDictionary<string, bool> LoadPluginStates();
    void SetPluginEnabled(string pluginId, bool enabled);
}

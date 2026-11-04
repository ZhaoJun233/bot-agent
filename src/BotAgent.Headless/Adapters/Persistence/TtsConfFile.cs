extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility entry; preserves the existing external-file apply policy.</summary>
public static class TtsConfFile
{
    public static bool TryWrite(string body)
        => StorageModule::BotAgent.Adapters.Persistence.TtsConfFile.TryWrite(body);
}
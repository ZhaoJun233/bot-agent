extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility entry; file access is owned by Storage.</summary>
public static class SecretFiles
{
    public static string? TryRead(string path, out string? error)
        => StorageModule::BotAgent.Adapters.Persistence.SecretFiles.TryRead(path, out error);
}
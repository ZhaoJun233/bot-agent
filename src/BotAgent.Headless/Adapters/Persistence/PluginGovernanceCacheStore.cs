extern alias StorageModule;
using System.Text;
using TextSnapshotFile = StorageModule::BotAgent.Adapters.Persistence.TextSnapshotFile;

namespace BotAgent.Adapters.Persistence;

/// <summary>Bounded local snapshots via the shared atomic store; this adapter owns only the cache path.</summary>
internal sealed class PluginGovernanceCacheStore(string root)
{
    private readonly string _path = Path.Combine(root, ".governance.json");

    public byte[]? Load(int maxBytes)
    {
        var content = TextSnapshotFile.Read(_path, maxBytes);
        return content is null ? null : Encoding.UTF8.GetBytes(content);
    }

    public Task SaveAsync(byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return TextSnapshotFile.WriteAsync(_path, Encoding.UTF8.GetString(bytes), ct);
    }
}

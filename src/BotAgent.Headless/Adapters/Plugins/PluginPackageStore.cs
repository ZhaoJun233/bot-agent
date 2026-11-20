extern alias StorageModule;

using System.Text;
using BotAgent.Domain.Plugins;
using TextSnapshotFile = StorageModule::BotAgent.Adapters.Persistence.TextSnapshotFile;

namespace BotAgent.Adapters.Plugins;

internal sealed partial class PluginPackageStore(string root, TrustedPluginDistribution? distribution = null)
{
    internal TrustedPluginDistribution Distribution { get; } = distribution ?? TrustedPluginDistribution.Empty;
    internal string Root { get; } = Path.GetFullPath(root);
    internal string PackageDirectory(string id) => Path.Combine(Root, id);
    internal string ArchivePath(PluginPackageManifest manifest) => Path.Combine(PackageDirectory(manifest.Id),
        "releases", manifest.Version + "-" + manifest.Sha256.ToLowerInvariant() + ".zip");

    internal bool HasArchive(PluginPackageManifest manifest) => File.Exists(ArchivePath(manifest));

    internal Task<byte[]> ReadArchiveAsync(PluginPackageManifest manifest, CancellationToken ct)
        => PluginPackageFiles.ReadRequiredAsync(ArchivePath(manifest), PluginPackageArchive.MaxArchiveBytes, ct);

    // Staging is deliberately outside package-ID directories until the final admission guard succeeds.
    internal async Task<string> StageArchiveAsync(string directory, byte[] bytes, CancellationToken ct)
    {
        var path = Path.Combine(directory, "archive.tmp");
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        return path;
    }

    internal void CommitArchive(PluginPackageManifest manifest, string stagedArchive, bool overwrite)
    {
        var archive = ArchivePath(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.Move(stagedArchive, archive, overwrite);
    }

    internal async Task SaveAsync(InstalledPluginPackage state, CancellationToken ct)
    {
        Distribution.ValidateId(state.Id);
        if (state.MetadataFailure is not null) throw new InvalidDataException("Unreadable package state cannot be saved.");
        var directory = PackageDirectory(state.Id);
        Directory.CreateDirectory(directory);
        await TextSnapshotFile.WriteAsync(Path.Combine(directory, "installed.json"),
            Encoding.UTF8.GetString(PluginStateEnvelope.Write(state)), ct).ConfigureAwait(false);
    }

    internal IEnumerable<string> Ids() => Directory.EnumerateDirectories(Root)
        .Select(Path.GetFileName).Where(id => id != null && !id.StartsWith('.')).Select(id => id!);
}

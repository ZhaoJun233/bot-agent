using System.Text.Json;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

internal sealed class PluginPackageStore(string root)
{
    internal string Root { get; } = Path.GetFullPath(root);
    internal string PackageDirectory(string id) => Path.Combine(Root, id);
    internal string ArchivePath(PluginPackageManifest manifest) => Path.Combine(PackageDirectory(manifest.Id),
        "releases", manifest.Version + "-" + manifest.Sha256.ToLowerInvariant() + ".zip");

    internal async Task<InstalledPluginPackage> ReadAsync(string id, CancellationToken ct)
    {
        PluginPackageValidation.ValidateId(id);
        var path = Path.Combine(PackageDirectory(id), "installed.json");
        if (!File.Exists(path)) return new InstalledPluginPackage(id);
        if (new FileInfo(path).Length > 131072) throw new InvalidDataException("Metadata too large.");
        var state = JsonSerializer.Deserialize<InstalledPluginPackage>(await File.ReadAllTextAsync(path, ct),
            PluginPackageValidation.JsonOptions) ?? throw new InvalidDataException("Invalid package metadata.");
        if (state.Id != id) throw new InvalidDataException("Package metadata identity mismatch.");
        foreach (var manifest in new[] { state.Active, state.Previous, state.Pending })
        {
            if (manifest == null) continue;
            PluginPackageValidation.Validate(manifest);
            if (manifest.Id != id) throw new InvalidDataException("Release metadata identity mismatch.");
        }
        return state;
    }

    internal async Task SaveAsync(InstalledPluginPackage state, CancellationToken ct)
    {
        var directory = PackageDirectory(state.Id);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "installed-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, PluginPackageValidation.JsonOptions), ct);
            File.Move(temporary, Path.Combine(directory, "installed.json"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal IEnumerable<string> Ids() => Directory.EnumerateDirectories(Root)
        .Select(Path.GetFileName).Where(id => id != null && !id.StartsWith('.')).Select(id => id!);
}

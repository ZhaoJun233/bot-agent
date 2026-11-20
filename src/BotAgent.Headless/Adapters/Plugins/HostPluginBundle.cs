extern alias StorageModule;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>Host-compiled authority with physically removable business archives.</summary>
internal sealed class HostPluginBundle(TrustedPluginDistribution distribution, string directory)
{
    internal TrustedPluginDistribution Distribution { get; } = distribution;
    internal IReadOnlyList<PluginPackageManifest> Releases => Distribution.Releases;

    internal static HostPluginBundle Read()
    {
        var host = typeof(HostPluginBundle).Assembly;
        var metadata = host.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        var index = Convert.FromBase64String(metadata["BotAgent.PluginDistributionIndex"]!);
        return new(TrustedPluginDistribution.Read(index, metadata["BotAgent.PluginDistribution"]!),
            Path.Combine(Path.GetDirectoryName(host.Location)!, "plugin-distribution"));
    }

    internal Task<byte[]> ReadArchiveAsync(PluginPackageManifest manifest, CancellationToken ct)
    {
        Distribution.EnsureApproved(manifest);
        var path = Path.Combine(directory, manifest.Sha256.ToLowerInvariant() + ".zip");
        return PluginPackageFiles.ReadRequiredAsync(path, PluginPackageArchive.MaxArchiveBytes, ct);
    }

    internal async Task<IReadOnlyList<PluginPackageFailure>> StageFirstUseAsync(string root, PluginPackageService packages, CancellationToken ct)
    {
        var installed = (await packages.GetInstalledAsync(ct)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var failures = new List<PluginPackageFailure>();
        var physicalRoot = StorageModule::BotAgent.Adapters.Persistence.AppDatabase.ResolvePhysicalPath(root);
        if (OperatingSystem.IsWindows()) physicalRoot = physicalRoot.ToUpperInvariant();
        var keyByRelease = Releases.ToDictionary(
            manifest => manifest.Id,
            manifest => "host-distribution:adopted:" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(physicalRoot + "\0" + manifest.Id))),
            StringComparer.Ordinal);
        var existingMeta = StorageModule::BotAgent.Adapters.Persistence.AppDatabase.GetMetaBatch(keyByRelease.Values);
        var keysToMark = new List<(string Key, string Value)>();
        var toStage = new List<(PluginPackageManifest Manifest, byte[] Archive)>();
        foreach (var manifest in Releases)
        {
            var key = keyByRelease[manifest.Id];
            var adopted = existingMeta.GetValueOrDefault(key);
            if (adopted == "1") continue;
            if (adopted is not null) { failures.Add(new(manifest.Id, "plugin_distribution_adoption_corrupt")); continue; }
            // Even an uninstalled Previous is explicit user state. Never replace or resurrect it.
            if (installed.TryGetValue(manifest.Id, out var previous))
            {
                if (previous.MetadataFailure is null) keysToMark.Add((key, "1"));
                continue;
            }
            try
            {
                var archive = await ReadArchiveAsync(manifest, ct).ConfigureAwait(false);
                toStage.Add((manifest, archive));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { failures.Add(new(manifest.Id, "plugin_distribution_adoption_failed")); }
        }
        if (toStage.Count > 0)
        {
            var outcomes = await packages.StageDistributionBatchAsync(toStage, ct).ConfigureAwait(false);
            foreach (var (id, succeeded) in outcomes)
            {
                if (succeeded) keysToMark.Add((keyByRelease[id], "1"));
                else failures.Add(new(id, "plugin_distribution_adoption_failed"));
            }
        }
        if (keysToMark.Count > 0)
            StorageModule::BotAgent.Adapters.Persistence.AppDatabase.SetMetaBatch(keysToMark);
        return failures;
    }
}

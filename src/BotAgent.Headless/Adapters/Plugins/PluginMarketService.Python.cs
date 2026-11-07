using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

public sealed partial class PluginMarketService
{
    private static async Task<IReadOnlyList<ZbaPluginPreviewCheck>> WithPythonCheckAsync(
        IReadOnlyList<ZbaPluginPreviewCheck> checks, PluginPackageManifest? manifest, CancellationToken ct)
    {
        if (manifest?.Runtime is null) return checks;
        var reason = await PluginPythonPackage.CompatibilityAsync(manifest, ct).ConfigureAwait(false);
        return checks.Append(new ZbaPluginPreviewCheck("python-runtime", reason is null ? "passed" : "blocked",
            reason ?? "Python interpreter compatible; stdlib-only; handshake checked only at enable. Local process is not a sandbox.")).ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, string?>> ReadCompatibilityAsync(
        IEnumerable<PluginPackageManifest> manifests, CancellationToken ct)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var versions = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            var key = manifest.Runtime?.MinimumPythonVersion ?? "dotnet";
            if (!PluginPackageValidation.IsCompatible(manifest)) { result[manifest.Id + "@" + manifest.Version] = "Host runtime/version incompatible."; continue; }
            if (!versions.TryGetValue(key, out var reason))
            {
                reason = await PluginPythonPackage.CompatibilityAsync(manifest, ct).ConfigureAwait(false);
                versions[key] = reason;
            }
            result[manifest.Id + "@" + manifest.Version] = reason;
        }
        return result;
    }
}

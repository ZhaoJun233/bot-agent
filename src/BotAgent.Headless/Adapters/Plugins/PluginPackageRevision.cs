using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

internal static class PluginPackageRevision
{
    // Keep the original p + 16 hex representation and installed.json format.
    internal static string Of(IReadOnlyList<InstalledPluginPackage> installed)
    {
        var builder = new StringBuilder();
        foreach (var package in installed.OrderBy(item => item.Id, StringComparer.Ordinal))
            builder.Append(package.Id).Append('@').Append(package.Active?.Version ?? "-").Append('/')
                .Append(package.Pending?.Version ?? "-").Append('/').Append(package.PendingUninstall ? 'u' : '-').Append(';');
        return "p" + Hash(builder.ToString())[..16].ToLowerInvariant();
    }

    // Revision is a precondition, not intent: original committed requests replay even after it advances.
    internal static string Intent(string kind, string id, PluginPackageManifest? manifest, bool trust, string caller)
    {
        var normalized = manifest is null ? null : manifest with
        {
            Sha256 = manifest.Sha256.ToLowerInvariant(),
            DownloadUrl = new Uri(manifest.DownloadUrl).AbsoluteUri,
            Permissions = (manifest.Permissions ?? []).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Dependencies = (manifest.Dependencies ?? []).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(),
            Settings = (manifest.Settings ?? []).OrderBy(value => value.Key, StringComparer.Ordinal).ToArray()
        };
        return Hash(JsonSerializer.Serialize(new { Format = 1, kind, id, Manifest = normalized, trust, caller }));
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

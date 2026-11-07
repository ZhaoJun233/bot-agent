using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>Import-only rules; existing trusted package lifecycle policy remains unchanged.</summary>
internal static class PluginImportPolicy
{
    internal static void Validate(PluginPackageManifest manifest)
    {
        static void Exact(string? value)
        {
            if (value is null || value != value.Trim()) throw new InvalidDataException("Manifest values must have exact boundaries.");
        }
        Exact(manifest.Id); Exact(manifest.Version); Exact(manifest.Sha256); Exact(manifest.DownloadUrl);
        foreach (var permission in manifest.Permissions ?? []) Exact(permission);
        foreach (var dependency in manifest.Dependencies ?? []) { Exact(dependency.Id); Exact(dependency.Version); }
        var artifact = PluginPackageValidation.ValidateUrl(manifest.DownloadUrl);
        if (artifact.Query.Length != 0 || !artifact.IsDefaultPort)
            throw new InvalidDataException("Import artifact URL must be query-free public HTTPS on port 443.");

    }

    internal static void ValidateRepository(IReadOnlyList<PluginPackageManifest> manifests)
    {
        var candidates = manifests.ToDictionary(m => (m.Id, m.Version));
        var active = new HashSet<(string, string)>();
        var visited = new HashSet<(string, string)>();
        foreach (var manifest in manifests)
        {
            Validate(manifest);
            if ((manifest.Dependencies ?? []).Any(d => candidates.TryGetValue((d.Id, d.Version), out var dependency) && dependency.ApiVersion != manifest.ApiVersion))
                throw new InvalidDataException("Cross-runtime dependency is unsupported.");
            Visit((manifest.Id, manifest.Version));
        }
        void Visit((string, string) key)
        {
            if (active.Contains(key)) throw new InvalidDataException("Repository dependency cycle.");
            if (!visited.Add(key)) return;
            active.Add(key);
            foreach (var dependency in candidates[key].Dependencies ?? [])
                if (candidates.ContainsKey((dependency.Id, dependency.Version))) Visit((dependency.Id, dependency.Version));
            active.Remove(key);
        }
    }
}
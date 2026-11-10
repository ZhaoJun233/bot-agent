using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>Separate, closed Python feed. Never weakens the legacy repository schema.</summary>
internal static class PluginPythonRepository
{
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, MaxDepth = 32 };
    private sealed record Feed(int SchemaVersion, IReadOnlyList<Group>? Plugins);
    private sealed record Group(string Id, IReadOnlyList<PluginPackageManifest>? Versions);

    internal static IReadOnlyList<PluginPackageManifest> Parse(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new PluginCatalogException("index-too-large");
        Feed feed;
        try { feed = JsonSerializer.Deserialize<Feed>(bytes, Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new PluginCatalogException("invalid-python-repository"); }
        if (feed.SchemaVersion != 2 || feed.Plugins is null || feed.Plugins.Count > 256)
            throw new PluginCatalogException("invalid-python-repository");
        var result = new List<PluginPackageManifest>();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in feed.Plugins)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Id) || !groups.Add(group.Id) || group.Versions is null || group.Versions.Count is 0 or > 128)
                throw new PluginCatalogException("invalid-python-repository");
            foreach (var manifest in group.Versions)
            {
                if (manifest is null || manifest.ApiVersion != 2 || manifest.Id != group.Id ||
                    !keys.Add(manifest.Id + "@" + manifest.Version) || result.Count >= 1024)
                    throw new PluginCatalogException("invalid-python-repository");
                try { PluginPackageValidation.Validate(manifest, checkCompatibility: false); }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                { throw new PluginCatalogException("invalid-python-repository"); }
                result.Add(manifest);
            }
        }
        // A Python feed is self-contained: dependencies cannot silently resolve to another runtime.
        foreach (var manifest in result)
            if (manifest.Dependencies is not null && manifest.Dependencies.Any(dep =>
                !result.Any(candidate => candidate.Id == dep.Id &&
                    candidate.Version == dep.Version)))
                throw new PluginCatalogException("python-dependency-missing");
        PluginImportPolicy.ValidateRepository(result);
        return result;
    }
}

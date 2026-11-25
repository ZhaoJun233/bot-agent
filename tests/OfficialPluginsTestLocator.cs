using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using BotAgent.Domain.Plugins;

namespace BotAgent.TestFixtures;

/// <summary>
/// Test-only locator for official marketplace artifacts (bot-agent-plugins checkout outside this repo root).
/// Resolves BOTAGENT_OFFICIAL_PLUGINS_DIR or sibling ../bot-agent-plugins. Never modifies host runtime state.
/// </summary>
internal static class OfficialPluginsTestLocator
{
    internal static string ResolveMarketplaceRoot(string repoRoot)
    {
        var configured = Environment.GetEnvironmentVariable("BOTAGENT_OFFICIAL_PLUGINS_DIR")?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            var full = Path.GetFullPath(configured);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException("BOTAGENT_OFFICIAL_PLUGINS_DIR does not exist: " + full);
            return full;
        }
        var sibling = Path.GetFullPath(Path.Combine(repoRoot, "..", "bot-agent-plugins"));
        if (Directory.Exists(sibling)) return sibling;
        throw new DirectoryNotFoundException("Official plugin marketplace directory not found (set BOTAGENT_OFFICIAL_PLUGINS_DIR).");
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BotAgent.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("BotAgent.slnx not found.");
    }

    internal static (PluginPackageManifest Manifest, byte[] ArchiveBytes, string Directory) ReadOfficialRelease(
        string marketplaceRoot, string featureFolder, string pluginId, string version = "1.1.0")
    {
        var releasesRoot = Path.Combine(marketplaceRoot, "artifacts", "official-releases");
        var releaseDir = Directory.Exists(Path.Combine(releasesRoot, pluginId))
            ? Path.Combine(releasesRoot, pluginId)
            : Path.Combine(releasesRoot, $"{pluginId}-{version}");
        var manifestPath = Path.Combine(releaseDir, "manifest.json");
        var zipPath = Path.Combine(releaseDir, $"{pluginId}-{version}.zip");
        if (!File.Exists(manifestPath) || !File.Exists(zipPath))
            throw new FileNotFoundException($"Missing official release bundle for {pluginId}@{version} under {releaseDir}.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(File.ReadAllBytes(manifestPath), options)
            ?? throw new InvalidDataException($"Invalid manifest JSON: {manifestPath}");
        return (manifest, File.ReadAllBytes(zipPath), releaseDir);
    }

    internal static string PluginBackendSourceDirectory(string marketplaceRoot, string featureFolder)
        => Path.Combine(marketplaceRoot, "plugins-src", featureFolder, "backend");

    internal static string BuiltPluginAssemblyPath(string marketplaceRoot, string featureFolder, string assemblyName)
    {
        var path = Path.Combine(marketplaceRoot, "plugins-src", featureFolder, "backend", "bin", "Release", "net10.0", assemblyName + ".dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Built official plugin assembly not found: {path}. Build OfficialPlugins.slnx Release first.");
        return path;
    }

    /// <summary>Loads an official plugin assembly into an isolated load context referencing host Default contracts.</summary>
    internal static (AssemblyLoadContext Context, Assembly Assembly) LoadIsolatedPlugin(
        string marketplaceRoot, string featureFolder, string assemblyName)
    {
        var dllPath = BuiltPluginAssemblyPath(marketplaceRoot, featureFolder, assemblyName);
        var hostBinDir = Path.GetDirectoryName(typeof(IBotPlugin).Assembly.Location)!;
        var context = new IsolatedOfficialContext(Path.GetDirectoryName(dllPath)!, hostBinDir);
        var assembly = context.LoadFromAssemblyPath(dllPath);
        return (context, assembly);
    }

    private sealed class IsolatedOfficialContext(string pluginDir, string hostBinDir) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName.Name)) return null;
            if (string.Equals(assemblyName.Name, typeof(IBotPlugin).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return typeof(IBotPlugin).Assembly;
            if (string.Equals(assemblyName.Name, typeof(IPluginContribution).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return typeof(IPluginContribution).Assembly;
            var hostMatch = Default.Assemblies.FirstOrDefault(a =>
                string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            if (hostMatch is not null) return hostMatch;
            var hostContract = Path.Combine(hostBinDir, assemblyName.Name + ".dll");
            if (assemblyName.Name.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase) && File.Exists(hostContract))
                return Default.LoadFromAssemblyPath(hostContract);
            var local = Path.Combine(pluginDir, assemblyName.Name + ".dll");
            return File.Exists(local) ? LoadFromAssemblyPath(local) : null;
        }
    }
}

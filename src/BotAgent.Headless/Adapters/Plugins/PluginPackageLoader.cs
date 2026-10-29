using System.Reflection;
using System.Runtime.Loader;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

internal sealed class PluginPackageLoader(string directory) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        // Share the host contract assembly to preserve IBotPlugin type identity.
        if (name.Name == typeof(IBotPlugin).Assembly.GetName().Name) return typeof(IBotPlugin).Assembly;
        var candidate = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    internal static async Task<IBotPlugin> StartAsync(string directory, string configDirectory,
        PluginPackageManifest manifest, PluginContext context, CancellationToken ct, string? settings = null)
    {
        var loader = new PluginPackageLoader(directory);
        IBotPlugin? plugin = null;
        try
        {
            var assembly = loader.LoadFromAssemblyPath(Path.Combine(directory, manifest.EntryAssembly));
            var type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
            plugin = Activator.CreateInstance(type) as IBotPlugin ?? throw new InvalidDataException("Entry does not implement IBotPlugin.");
            if (plugin.Id != manifest.Id || plugin.Version != manifest.Version)
                throw new InvalidDataException("Plugin identity differs from approved manifest.");
            var env = new Dictionary<string, string>(context.EnvironmentInfo)
            { ["pluginConfigDirectory"] = configDirectory, ["pluginPackageDirectory"] = directory };
            // Declarative settings are host-resolved; a plugin never reads the host database itself.
            if (settings is not null) env["pluginSettings"] = settings;
            await plugin.InitializeAsync(new PluginContext(context.Services, env), ct);
            return new StartedPlugin(plugin, loader);
        }
        catch
        {
            if (plugin != null) { try { await plugin.ShutdownAsync(CancellationToken.None); } catch { } }
            loader.Unload();
            throw;
        }
    }

    // PluginManager may call InitializeAsync again; initialization already succeeded before promotion.
    private sealed class StartedPlugin(IBotPlugin plugin, PluginPackageLoader loader) : IBotPlugin
    {
        public string Id => plugin.Id;
        public string Name => plugin.Name;
        public string Version => plugin.Version;
        public string Description => plugin.Description;
        public PluginCategory Category => plugin.Category;
        public bool IsPreset => false;
        public async Task ShutdownAsync(CancellationToken ct)
        {
            try { await plugin.ShutdownAsync(ct); }
            finally { loader.Unload(); }
        }
    }
}

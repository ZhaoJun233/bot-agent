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
        PluginPackageManifest manifest, PluginContext context, CancellationToken ct, string? settings = null,
        bool enabled = true)
    {
        var loader = new PluginPackageLoader(directory);
        IBotPlugin? plugin = null;
        var initializationAttempted = false;
        try
        {
            var assembly = loader.LoadFromAssemblyPath(Path.Combine(directory, manifest.EntryAssembly));
            var type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
            plugin = Activator.CreateInstance(type) as IBotPlugin ?? throw new InvalidDataException("Entry does not implement IBotPlugin.");
            if (plugin.Id != manifest.Id || plugin.Version != manifest.Version)
                throw new InvalidDataException("Plugin identity differs from approved manifest.");
            if (enabled)
            {
                var env = new Dictionary<string, string>(context.EnvironmentInfo)
                { ["pluginConfigDirectory"] = configDirectory, ["pluginPackageDirectory"] = directory };
                if (settings is not null) env["pluginSettings"] = settings;
                initializationAttempted = true;
                await plugin.InitializeAsync(new PluginContext(context.Services, env), ct).ConfigureAwait(false);
            }
            // Disabled packages are metadata-only. Enabling always prepares a fresh instance.
            return plugin is IReplyPolicyPlugin
                ? new StartedPolicyPlugin(plugin, loader, directory, enabled)
                : new StartedPlugin(plugin, loader, directory, enabled);
        }
        catch
        {
            if (initializationAttempted && plugin is not null)
                try { await plugin.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            loader.Unload();
            TryDelete(directory);
            throw;
        }
    }

    private static void TryDelete(string directory)
    {
        // Windows can retain mapped DLLs until the collectible context is collected.
        // Never force GC or delete the pinned archive; a leftover shadow is harmless.
        try { Directory.Delete(directory, recursive: true); } catch { }
    }

    private class StartedPlugin : IBotPlugin
    {
        protected IBotPlugin? Inner;
        private PluginPackageLoader? _loader;
        private readonly string _directory;
        private readonly bool _initialized;
        public string Id { get; }
        public string Name { get; }
        public string Version { get; }
        public string Description { get; }
        public PluginCategory Category { get; }
        public bool IsPreset => false;

        public StartedPlugin(IBotPlugin plugin, PluginPackageLoader loader, string directory, bool initialized)
        {
            Inner = plugin;
            _loader = loader;
            _directory = directory;
            _initialized = initialized;
            Id = plugin.Id; Name = plugin.Name; Version = plugin.Version;
            Description = plugin.Description; Category = plugin.Category;
        }

        public Task InitializeAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task ShutdownAsync(CancellationToken ct)
        {
            var plugin = Interlocked.Exchange(ref Inner, null);
            if (plugin is null) return;
            try { if (_initialized) await plugin.ShutdownAsync(ct).ConfigureAwait(false); }
            finally
            {
                Interlocked.Exchange(ref _loader, null)?.Unload();
                TryDelete(_directory);
            }
        }
    }

    private sealed class StartedPolicyPlugin(IBotPlugin plugin, PluginPackageLoader loader, string directory, bool initialized)
        : StartedPlugin(plugin, loader, directory, initialized), IReplyPolicyPlugin
    {
        public string? BlockReason(ReplyPolicyContext context)
            => (Volatile.Read(ref Inner) as IReplyPolicyPlugin)?.BlockReason(context);
    }
}

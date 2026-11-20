using System.Reflection;
using System.Runtime.Loader;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Tools;

namespace BotAgent.Adapters.Plugins;

internal sealed class PluginPackageLoader(string directory, IReadOnlyDictionary<string, Assembly> contracts, bool isCollectible)
    : AssemblyLoadContext(isCollectible)
{
    internal PluginPackageLoader(string directory, IReadOnlyDictionary<string, Assembly> contracts)
        : this(directory, contracts, isCollectible: true) { }
    protected override Assembly? Load(AssemblyName name)
    {
        if (string.IsNullOrWhiteSpace(name.Name)) return null;
        // Share the host contract assembly to preserve IBotPlugin type identity.
        if (string.Equals(name.Name, typeof(IBotPlugin).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
            return typeof(IBotPlugin).Assembly;
        if (string.Equals(name.Name, typeof(IPluginContribution).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
        {
            var sdk = typeof(IPluginContribution).Assembly;
            if (name.FullName != sdk.FullName) throw new FileLoadException("plugin_sdk_contract_reference_mismatch");
            return sdk;
        }
        var shared = PluginSharedContractRegistry.Resolve(name, contracts);
        if (shared is not null) return shared;
        if (name.Name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || name.Name.Contains("..", StringComparison.Ordinal))
            return null;
        var root = Path.GetFullPath(directory);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, name.Name + ".dll"));
        if (!candidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        return new FileInfo(candidate).Exists ? LoadFromAssemblyPath(candidate) : null;
    }

    internal static async Task<IBotPlugin> StartAsync(string directory, string configDirectory,
        PluginPackageManifest manifest, PluginContext context, CancellationToken ct, string? settings = null,
        bool enabled = true, bool coldStart = false, PluginContext? controlContext = null)
    {
        var collectible = !string.Equals(manifest.Adoption, PluginAdoptionMode.Cold, StringComparison.Ordinal);
        var loader = new PluginPackageLoader(directory, PluginSharedContractRegistry.Bind(directory, manifest), collectible);
        IBotPlugin? plugin = null;
        var initializationAttempted = false;
        var controlAttempted = false;
        try
        {
            var assembly = loader.LoadFromAssemblyPath(Path.Combine(directory, manifest.EntryAssembly!));
            var type = assembly.GetType(manifest.EntryType!, throwOnError: true)!;
            plugin = Activator.CreateInstance(type) as IBotPlugin ?? throw new InvalidDataException("Entry does not implement IBotPlugin.");
            if (plugin.Id != manifest.Id || plugin.Version != manifest.Version)
                throw new InvalidDataException("Plugin identity differs from approved manifest.");
            if (manifest.Control != (plugin is IPluginControlModule))
                throw new InvalidDataException("plugin_control_declaration_mismatch");
            if (manifest.ToolApiVersion is not null && plugin is not (IPluginContributionSource or IToolContribution))
                throw new InvalidDataException("plugin_tools_missing");
            if ((plugin is IPluginContributionSource or IToolContribution or IPluginTwoPhaseLifecycle || manifest.Control
                || PluginUiValidation.RequiresColdStart(manifest)) && !coldStart)
                throw new BotAgent.Services.Plugins.PluginRestartRequiredException();
            var env = new Dictionary<string, string>(context.EnvironmentInfo)
            { ["pluginConfigDirectory"] = configDirectory, ["pluginPackageDirectory"] = directory };
            if (settings is not null) env["pluginSettings"] = settings;
            if (manifest.Control)
            {
                if (controlContext is null) throw new InvalidOperationException("plugin_control_context_required");
                controlAttempted = true;
                await ((IPluginControlModule)plugin).PrepareControlAsync(new PluginContext(controlContext.Services, env), ct).ConfigureAwait(false);
            }
            if (enabled)
            {
                initializationAttempted = true;
                if (plugin is IPluginTwoPhaseLifecycle twoPhase)
                    await twoPhase.PrepareAsync(new PluginContext(context.Services, env), ct).ConfigureAwait(false);
                else
                    await plugin.InitializeAsync(new PluginContext(context.Services, env), ct).ConfigureAwait(false);
            }
            IToolContribution? tools = null;
            if (enabled)
            {
                tools = plugin is IPluginContributionSource source
                    ? source.GetContribution(typeof(IToolContribution)) as IToolContribution : plugin as IToolContribution;
                if (manifest.ToolApiVersion is null && tools is not null)
                    throw new InvalidDataException("plugin_tools_undeclared");
                if (manifest.ToolApiVersion is not null && tools is null)
                    throw new InvalidDataException("plugin_tools_missing");
            }
            StartedPlugin started = manifest.Control
                ? plugin is IReplyPolicyPlugin
                    ? new StartedControlPolicyPlugin(plugin, loader, directory, enabled)
                    : new StartedControlPlugin(plugin, loader, directory, enabled)
                : plugin is IReplyPolicyPlugin
                ? new StartedPolicyPlugin(plugin, loader, directory, enabled)
                : new StartedPlugin(plugin, loader, directory, enabled);
            // Resolve this contract once during preparation; manager discovery sees that same executor.
            started.Tools = tools;
            return started;
        }
        catch
        {
            if (initializationAttempted && plugin is not null)
                try { await plugin.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            if (controlAttempted && plugin is IPluginControlModule control)
                try { await control.ShutdownControlAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            if (loader.IsCollectible) loader.Unload();
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

    private class StartedPlugin : IBotPlugin, IPluginContributionSource, IPluginTwoPhaseLifecycle
    {
        protected IBotPlugin? Inner;
        private PluginPackageLoader? _loader;
        private readonly string _directory;
        private readonly bool _initialized;
        internal IToolContribution? Tools;
        public string Id { get; }
        public string Name { get; }
        public string Version { get; }
        public string Description { get; }
        public PluginCategory Category { get; }
        public bool IsPreset => false;
        public object? GetContribution(Type contract)
            => contract == typeof(IToolContribution) ? Volatile.Read(ref Tools)
                : (Volatile.Read(ref Inner) as IPluginContributionSource)?.GetContribution(contract);
        public bool RequiresTwoPhaseActivation => Volatile.Read(ref Inner) is IPluginTwoPhaseLifecycle inner && inner.RequiresTwoPhaseActivation;
        public Task PrepareAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;
        public Task ActivateAsync(CancellationToken ct)
            => Volatile.Read(ref Inner) is IPluginTwoPhaseLifecycle twoPhase ? twoPhase.ActivateAsync(ct) : Task.CompletedTask;

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
            Interlocked.Exchange(ref Tools, null);
            if (plugin is null) return;
            try
            {
                try { if (_initialized) await plugin.ShutdownAsync(ct).ConfigureAwait(false); }
                finally
                {
                    if (this is IPluginControlSource && plugin is IPluginControlModule control)
                        await control.ShutdownControlAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                var loader = Interlocked.Exchange(ref _loader, null);
                if (loader is { IsCollectible: true }) loader.Unload();
                TryDelete(_directory);
            }
        }
    }

    private class StartedControlPlugin(IBotPlugin plugin, PluginPackageLoader loader, string directory, bool initialized)
        : StartedPlugin(plugin, loader, directory, initialized), IPluginControlSource
    {
        public object? GetControlContribution(Type contract)
            => (Volatile.Read(ref Inner) as IPluginControlModule)?.GetControlContribution(contract);
    }

    private sealed class StartedControlPolicyPlugin(IBotPlugin plugin, PluginPackageLoader loader, string directory, bool initialized)
        : StartedControlPlugin(plugin, loader, directory, initialized), IReplyPolicyPlugin
    {
        public string? BlockReason(ReplyPolicyContext context)
            => (Volatile.Read(ref Inner) as IReplyPolicyPlugin)?.BlockReason(context);
    }

    private sealed class StartedPolicyPlugin(IBotPlugin plugin, PluginPackageLoader loader, string directory, bool initialized)
        : StartedPlugin(plugin, loader, directory, initialized), IReplyPolicyPlugin
    {
        public string? BlockReason(ReplyPolicyContext context)
            => (Volatile.Read(ref Inner) as IReplyPolicyPlugin)?.BlockReason(context);
    }
}

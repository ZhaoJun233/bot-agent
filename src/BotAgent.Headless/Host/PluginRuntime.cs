extern alias StorageModule;

using System.Collections.Immutable;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;
using BotAgent.Services.Plugins.Presets;
using PluginStateStore = StorageModule::BotAgent.Storage.PluginStore;

namespace BotAgent.Host;

/// <summary>Owns package generations and serializes startup, reconciliation and terminal shutdown.</summary>
internal sealed class PluginRuntime : IAsyncDisposable
{
    private readonly HttpFetcher _http;
    private readonly Action<string> _log;
    private readonly HashSet<string> _presetIds = new(StringComparer.Ordinal);
    private ImmutableDictionary<string, string> _failures = ImmutableDictionary<string, string>.Empty;
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly AsyncLocal<bool> _insideLifecycle = new();
    private Task? _startTask;
    private Task? _stopTask;
    private readonly string _root;

    internal PluginRuntime(string rootDirectory, Action<string> log, BotAgent.Domain.Ports.ITenantQuotaLedger quotas)
    {
        _log = log;
        _root = rootDirectory;
        _http = new HttpFetcher(TimeSpan.FromSeconds(60), _ => { }, "plugin-marketplace",
            allowAutoRedirect: false, rejectPrivateDestinations: true);
        Packages = new PluginPackageService(rootDirectory, _http);
        Settings = new PluginSettingsStore();
        Catalog = new PluginCatalogSource(_http, log);
        Registry = new PluginManager(log);
        var desired = LoadDesiredStates();
        IBotPlugin[] presets = [new MusicPresetPlugin(), new VoicePresetPlugin(), new StickersPresetPlugin(),
            new PokePresetPlugin(), new VibesPresetPlugin(), new ProfilesPresetPlugin(), new WebSearchPresetPlugin(),
            new TokenQuotaPresetPlugin(quotas)];
        foreach (var plugin in presets)
        {
            _presetIds.Add(plugin.Id);
            Registry.Register(plugin, !desired.TryGetValue(plugin.Id, out var enabled) || enabled);
        }
    }

    internal PluginPackageService Packages { get; }
    internal PluginSettingsStore Settings { get; }
    internal PluginCatalogSource Catalog { get; }
    internal PluginMarketService Market => _market ??= new PluginMarketService(Packages, Settings, Catalog,
        () => BotAgent.Adapters.Time.Clock.Now, Path.Combine(_root, "operations.json"), LoadDesiredStates, _log,
        reload: ReloadAsync, registrySnapshot: () => Registry.GetAll().Where(p => _presetIds.Contains(p.Id)).ToArray());
    private PluginMarketService? _market;
    internal PluginManager Registry { get; }
    internal BotAgent.Adapters.Net.IHttpFetcher Http => _http;

    internal ImmutableArray<PluginConfigurationView> ReadConfiguration(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var desired = PluginStateStore.LoadPluginStatesInTransaction(connection);
        return DeclaredIds().Select(id => new PluginConfigurationView(new(id), DesiredEnabled(id, desired)))
            .ToImmutableArray();
    }

    internal ImmutableArray<ManagedDescriptor> Describe()
    {
        var desired = LoadDesiredStates();
        var runtime = Registry.GetAll().ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);
        var packages = Packages.InstalledSnapshot.ToDictionary(package => package.Id, StringComparer.Ordinal);
        return DeclaredIds().Select(id =>
        {
            runtime.TryGetValue(id, out var loaded);
            packages.TryGetValue(id, out var package);
            var enabled = DesiredEnabled(id, desired);
            var state = loaded is null ? ManagedRuntimeState.NotLoaded
                : loaded.IsEnabled ? ManagedRuntimeState.Running : ManagedRuntimeState.Disabled;
            var pending = package?.Pending is not null || package?.PendingUninstall == true
                || enabled != (loaded?.IsEnabled ?? false);
            var error = _failures.TryGetValue(id, out var failure) ? failure : pending ? "plugin_reload_pending" : null;
            return new ManagedDescriptor(new ManagedObjectKey.Plugin(new(id)), "plugin", enabled, state,
                ImmutableArray.Create(new ActionCapability(ManagedAction.Start, false, "configuration_required"),
                    new ActionCapability(ManagedAction.Stop, false, "configuration_required")), true, error, "");
        }).ToImmutableArray();
    }

    internal Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfReentrant();
        lock (_lifecycleGate)
        {
            if (_stopTask is not null) throw new ObjectDisposedException(nameof(PluginRuntime));
            return _startTask ??= StartCoreAsync(ct);
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        await _reloadGate.WaitAsync(ct).ConfigureAwait(false);
        _insideLifecycle.Value = true;
        try
        {
            await Registry.StartAllAsync(ct).ConfigureAwait(false);
            await ReconcileAsync(null, startup: true, ct).ConfigureAwait(false);
        }
        finally { _insideLifecycle.Value = false; _reloadGate.Release(); }
    }

    internal async Task ReloadAsync(string? pluginId = null, CancellationToken ct = default)
    {
        ThrowIfReentrant();
        await StartAsync(ct).ConfigureAwait(false);
        await _reloadGate.WaitAsync(ct).ConfigureAwait(false);
        _insideLifecycle.Value = true;
        try
        {
            lock (_lifecycleGate)
                if (_stopTask is not null) throw new ObjectDisposedException(nameof(PluginRuntime));
            await ReconcileAsync(pluginId, startup: false, ct).ConfigureAwait(false);
        }
        finally { _insideLifecycle.Value = false; _reloadGate.Release(); }
    }

    private async Task ReconcileAsync(string? pluginId, bool startup, CancellationToken ct)
    {
        var desired = LoadDesiredStates();
        foreach (var id in _presetIds.Where(id => pluginId is null || id == pluginId))
            Registry.SetEnabled(id, DesiredEnabled(id, desired));
        await Registry.StartAllAsync(ct).ConfigureAwait(false);
        var packages = await Packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var context = new PluginContext(new PluginServices(_log));
        var failed = false;
        foreach (var package in packages.Where(package => pluginId is null || package.Id == pluginId))
        {
            ct.ThrowIfCancellationRequested();
            var id = package.Id;
            var enabled = DesiredEnabled(id, desired);
            var loaded = Registry.GetAll().FirstOrDefault(plugin => plugin.Id == id);
            try
            {
                if (!package.PendingUninstall && package.Pending is null && package.Active is { } active
                    && loaded is not null && loaded.IsEnabled == enabled
                    && (!enabled || !Settings.ReadManifest(active).RestartRequired))
                {
                    _failures = _failures.Remove(id);
                    continue;
                }
                long? preparedRevision = null;
                string? Resolve(PluginPackageManifest manifest)
                {
                    var json = Settings.ResolveRuntimeValues(manifest, out var revision);
                    preparedRevision = revision;
                    return json;
                }
                var applied = await Packages.ApplyAsync(id, context, Registry, enabled, ct, Resolve).ConfigureAwait(false);
                if (enabled && applied is not null && preparedRevision is { } revision)
                    Settings.MarkApplied(applied, revision);
                _failures = _failures.Remove(id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                failed = true;
                _failures = _failures.SetItem(id, "plugin_reload_failed");
                // Startup can serve the last known release while retaining a broken staged update.
                if (startup && loaded is null && package.Active is not null && package.Pending is not null
                    && !package.PendingUninstall)
                {
                    try
                    {
                        long? revision = null;
                        var active = await Packages.ApplyAsync(id, context, Registry, enabled, ct, manifest =>
                        {
                            var json = Settings.ResolveRuntimeValues(manifest, out var value);
                            revision = value;
                            return json;
                        }, activeFallback: true).ConfigureAwait(false);
                        if (enabled && active is not null && revision is { } value) Settings.MarkApplied(active, value);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* The per-package failure remains visible; other packages still reconcile. */ }
                }
            }
        }
        if (failed && !startup) throw new InvalidOperationException("plugin_reload_failed");
    }

    private IEnumerable<string> DeclaredIds()
        => _presetIds.Concat(Packages.InstalledSnapshot
                .Where(package => package.Active is not null || package.Pending is not null)
                .Select(package => package.Id))
            .Concat(Packages.PreparedInstallId is { } id ? new[] { id } : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);

    private bool DesiredEnabled(string id, IReadOnlyDictionary<string, bool> desired)
        => desired.TryGetValue(id, out var enabled) ? enabled : _presetIds.Contains(id);

    private static IReadOnlyDictionary<string, bool> LoadDesiredStates()
    {
        using var connection = AppDatabase.Open();
        return PluginStateStore.LoadPluginStatesInTransaction(connection);
    }

    private void ThrowIfReentrant()
    {
        if (_insideLifecycle.Value) throw new InvalidOperationException("plugin_reload_reentrant");
    }

    public ValueTask DisposeAsync()
    {
        ThrowIfReentrant();
        lock (_lifecycleGate) return new(_stopTask ??= StopCoreAsync());
    }

    private async Task StopCoreAsync()
    {
        if (_startTask is not null)
            try { await _startTask.ConfigureAwait(false); } catch { }
        await _reloadGate.WaitAsync().ConfigureAwait(false);
        _insideLifecycle.Value = true;
        try { await Registry.StopAllAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { _http.Dispose(); _insideLifecycle.Value = false; _reloadGate.Release(); }
    }

    // Only deliberate extension services are exposed; no ambient session/member stores or credentials.
    private sealed class PluginServices(Action<string> log) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(Action<string>) ? log : null;
    }
}
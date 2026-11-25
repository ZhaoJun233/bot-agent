extern alias StorageModule;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;
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
    private int _started;

    internal bool HasStarted => Volatile.Read(ref _started) != 0;
    private readonly string _root;
    private readonly bool _presetIntentNeedsReview;
    private readonly HostPluginBundle _bundle;
    private readonly ConcurrentDictionary<string, Func<IReadOnlyDictionary<string, System.Text.Json.JsonElement>, bool>> _liveSettingsConsumers = new(StringComparer.Ordinal);
    private IServiceProvider? _services;

    // Retained for existing host test/tool construction. No legacy ledger is registered or used.
    internal PluginRuntime(string rootDirectory, Action<string> log)
    {
        _log = log;
        _root = rootDirectory;
        _http = new HttpFetcher(TimeSpan.FromSeconds(60), _ => { }, "plugin-marketplace",
            allowAutoRedirect: false, rejectPrivateDestinations: true);
        _bundle = HostPluginBundle.Read();
        Packages = new PluginPackageService(rootDirectory, _http, _bundle.Distribution);
        Settings = new PluginSettingsStore(_bundle.Distribution);
        Catalog = new PluginCatalogSource(_http, log);
        Registry = new PluginManager(log);
        // All 8 presets (P01..P08) are delivered as independent packages in HostPluginBundle.
        var defaults = _bundle.Releases.Select(p => p.Id).ToArray();
        IReadOnlyDictionary<string, bool> desired;
        try
        {
            desired = PluginStateStore.PreservePresetIntent(
                defaults.ToDictionary(id => id, _ => true, StringComparer.Ordinal));
        }
        catch (System.Text.Json.JsonException)
        {
            // Keep management available when migration evidence is damaged. Existing explicit
            // choices remain authoritative; absent choices may no longer default to enabled.
            desired = LoadDesiredStates();
            _presetIntentNeedsReview = true;
        }
        _presetIds.UnionWith(defaults);
        if (_presetIntentNeedsReview)
            foreach (var id in _presetIds) _failures = _failures.SetItem(id, "plugin_preset_intent_needs_review");
        _market = new Lazy<PluginMarketService>(CreateMarket, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal PluginPackageService Packages { get; }
    internal PluginSettingsStore Settings { get; }
    internal PluginCatalogSource Catalog { get; }
    internal PluginMarketService Market => _market.Value;
    private PluginMarketService CreateMarket()
    {
        var market = new PluginMarketService(Packages, Settings, Catalog,
            () => BotAgent.Adapters.Time.Clock.Now, Path.Combine(_root, "operations.json"), LoadDesiredStates, _log,
            reload: ReloadAsync, registrySnapshot: Registry.GetAll,
            canReload: () => RuntimeAdoptionEnabled)
        {
            TryApplyLiveSettings = TryApplyLiveSettings
        };
        market.UiAssets = new PluginUiAssetService(Packages, Registry, () => BotAgent.Adapters.Time.Clock.Now);
        return market;
    }
    internal bool RuntimeAdoptionEnabled { get; set; } = true;
    private readonly Lazy<PluginMarketService> _market;
    internal PluginManager Registry { get; }
    internal BotAgent.Adapters.Net.IHttpFetcher Http => _http;

    /// <summary>注册实时设置消费者：保存后不重编程序集直接把最新声明参数推给宿主组件，并标记已应用。</summary>
    internal void RegisterLiveSettingsConsumer(
        string pluginId,
        Func<IReadOnlyDictionary<string, System.Text.Json.JsonElement>, bool> consumer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(consumer);
        _liveSettingsConsumers[pluginId] = consumer;
    }

    private bool TryApplyLiveSettings(string pluginId, PluginPackageManifest manifest)
    {
        if (!_liveSettingsConsumers.TryGetValue(pluginId, out var consumer)) return false;
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> values;
        try { values = Settings.ReadEffectiveValues(manifest); }
        catch { return false; }
        if (!consumer(values)) return false;
        _ = Settings.ResolveRuntimeValues(manifest, out var revision);
        return Settings.MarkApplied(manifest, revision);
    }

    /// <summary>按插件 ID 实时读取当前生效清单的声明式设置快照（不存在或未安装返回 null）。</summary>
    internal IReadOnlyDictionary<string, System.Text.Json.JsonElement>? TryReadEffectiveValues(string pluginId)
    {
        var manifest = Packages.InstalledSnapshot.FirstOrDefault(p => p.Id == pluginId)?.Active
            ?? _bundle.Releases.FirstOrDefault(r => r.Id == pluginId);
        if (manifest is null || manifest.Settings is not { Count: > 0 }) return null;
        try { return Settings.ReadEffectiveValues(manifest); }
        catch { return null; }
    }

    internal void BindServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (_lifecycleGate)
        {
            if (_startTask is not null || _stopTask is not null || _services is not null)
                throw new InvalidOperationException("plugin_services_already_bound");
            _services = services;
        }
    }

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
                : !loaded.IsEnabled ? ManagedRuntimeState.Disabled
                : Registry.IsReady(id) ? ManagedRuntimeState.Running : ManagedRuntimeState.Faulted;
            var pending = package?.Pending is not null || package?.PendingUninstall == true
                || enabled != (loaded?.IsEnabled ?? false);
            var error = (_failures.TryGetValue(id, out var failure) && failure == "plugin_governance_revoked")
                ? failure
                : package?.MetadataFailure?.Code
                ?? (_presetIntentNeedsReview && _presetIds.Contains(id) ? "plugin_preset_intent_needs_review"
                    : failure
                    ?? (state == ManagedRuntimeState.Faulted ? "plugin_dependency_unavailable"
                        : pending ? "plugin_reload_pending" : null));
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
            foreach (var failure in await _bundle.StageFirstUseAsync(_root, Packages, ct, _log).ConfigureAwait(false))
                _failures = _failures.SetItem(failure.Id, failure.Code);
            await Registry.StartAllAsync(ct).ConfigureAwait(false);
            await ReconcileAsync(null, startup: true, ct).ConfigureAwait(false);
            Volatile.Write(ref _started, 1);
        }
        finally
        {
            Packages.ClearStagedDistributionBytes();
            _insideLifecycle.Value = false;
            _reloadGate.Release();
        }
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
        var toggleIds = _presetIds
            .Concat(Registry.GetAll()
                .Where(plugin => OfficialPresetPolicy.IsOfficialPresetId(plugin.Id))
                .Select(plugin => plugin.Id))
            .Distinct(StringComparer.Ordinal)
            .Where(id => pluginId is null || id == pluginId);
        foreach (var id in toggleIds)
        {
            var wanted = DesiredEnabled(id, desired);
            // Never flip an uninitialized cold-start package to Enabled=true in-place (which would cause
            // split-brain Enabled=true/Initialized=false and trigger BlockReason("plugin_not_ready") outages).
            // Already-initialized presets may still toggle Enabled live without restart.
            if (!Registry.IsRuntimePrepared(id) || Registry.IsInitialized(id))
                Registry.SetEnabled(id, wanted);
        }
        await Registry.StartAllAsync(ct).ConfigureAwait(false);
        var packages = await Packages.GetInstalledAsync(ct).ConfigureAwait(false);
        var context = new PluginContext(new PluginServices(_log, _services));
        var failed = false;
        var restartRequired = false;
        foreach (var step in PluginRuntimeOrder.Create(packages, pluginId, id => DesiredEnabled(id, desired)))
        {
            var package = step.Package;
            ct.ThrowIfCancellationRequested();
            var id = package.Id;
            if (package.MetadataFailure is { } metadataFailure)
            {
                if (!_failures.TryGetValue(id, out var existingFailure) || existingFailure != "plugin_governance_revoked")
                    _failures = _failures.SetItem(id, metadataFailure.Code);
                // A previously running generation retains its truthful runtime state. Do not attempt
                // a reload from unreadable state or disrupt independent, readable packages.
                if (pluginId == id) failed = true;
                continue;
            }
            var enabled = DesiredEnabled(id, desired);
            var loaded = Registry.GetAll().FirstOrDefault(plugin => plugin.Id == id);
            try
            {
                if (step.Cycle) throw new InvalidOperationException("plugin_dependency_cycle");
                if (!package.PendingUninstall && (package.Pending is null || step.ActiveOnly) && package.Active is { } active
                    && loaded is not null && loaded.IsEnabled == enabled
                    && (!enabled || Registry.IsReady(id)) && (!active.Control || Registry.IsControlReady(id)))
                {
                    var settingsPending = (enabled || active.Control) && Settings.ReadManifest(active).RestartRequired;
                    var skipColdBroadcast = settingsPending
                        && pluginId is null
                        && string.Equals(active.Adoption, "cold", StringComparison.OrdinalIgnoreCase);
                    if (!settingsPending || skipColdBroadcast)
                    {
                        _failures = _failures.Remove(id);
                        continue;
                    }
                }
                long? preparedRevision = null;
                string? Resolve(PluginPackageManifest manifest)
                {
                    var json = Settings.ResolveRuntimeValues(manifest, out var revision);
                    preparedRevision = revision;
                    return json;
                }
                var applied = startup
                    ? await Packages.ApplyAtStartupAsync(id, context, Registry, enabled, ct, Resolve, step.ActiveOnly).ConfigureAwait(false)
                    : await Packages.ApplyAsync(id, context, Registry, enabled, ct, Resolve, step.ActiveOnly).ConfigureAwait(false);
                if (applied is not null && (enabled || applied.Control) && preparedRevision is { } revision)
                    Settings.MarkApplied(applied, revision);
                _failures = _failures.Remove(id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (PluginRestartRequiredException) when (!startup)
            {
                restartRequired = true;
            }
            catch (Exception ex)
            {
                failed = true;
                var code = ex is InvalidOperationException && ex.Message is "plugin_dependency_unavailable" or "plugin_dependency_cycle"
                    ? ex.Message : "plugin_reload_failed";
                _failures = _failures.SetItem(id, code);
                // Startup can serve the last known release while retaining a broken staged update.
                if (startup && loaded is null && package.Active is not null && package.Pending is not null
                    && !package.PendingUninstall)
                {
                    try
                    {
                        long? revision = null;
                        var active = await Packages.ApplyAtStartupAsync(id, context, Registry, enabled, ct, manifest =>
                        {
                            var json = Settings.ResolveRuntimeValues(manifest, out var value);
                            revision = value;
                            return json;
                        }, activeFallback: true).ConfigureAwait(false);
                        if (active is not null && (enabled || active.Control) && revision is { } value) Settings.MarkApplied(active, value);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* The per-package failure remains visible; other packages still reconcile. */ }
                }
            }
        }
        if (failed && !startup) throw new InvalidOperationException("plugin_reload_failed");
        if (restartRequired) throw new PluginRestartRequiredException();
    }

    private IEnumerable<string> DeclaredIds()
        => _presetIds.Concat(Packages.InstalledSnapshot
                .Where(package => package.Active is not null || package.Pending is not null
                    || package.MetadataFailure is { Code: not "plugin_metadata_invalid_id" })
                .Select(package => package.Id))
            .Concat(Packages.PreparedInstallId is { } id ? new[] { id } : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);

    private bool DesiredEnabled(string id, IReadOnlyDictionary<string, bool> desired)
        => desired.TryGetValue(id, out var enabled) ? enabled : !_presetIntentNeedsReview && _presetIds.Contains(id);

    private static IReadOnlyDictionary<string, bool> LoadDesiredStates()
    {
        using var connection = AppDatabase.Open();
        return PluginStateStore.LoadPluginStatesInTransaction(connection);
    }

    private void ThrowIfReentrant()
    {
        if (_insideLifecycle.Value) throw new InvalidOperationException("plugin_reload_reentrant");
    }

    internal async Task QuiesceAsync()
    {
        RuntimeAdoptionEnabled = false;
        await _reloadGate.WaitAsync().ConfigureAwait(false);
        _insideLifecycle.Value = true;
        try { await Registry.StopAllAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { _insideLifecycle.Value = false; _reloadGate.Release(); }
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
        finally
        {
            try { (_services as IDisposable)?.Dispose(); }
            finally { _http.Dispose(); _insideLifecycle.Value = false; _reloadGate.Release(); }
        }
    }

    // Only deliberate extension services are exposed; no ambient session/member stores or credentials.
    private sealed class PluginServices(Action<string> log, IServiceProvider? services) : IServiceProvider, IOwnedHostServices
    {
        public object? GetService(Type serviceType) => serviceType == typeof(Action<string>) ? log
            : serviceType == typeof(BotAgent.Domain.Ports.IClock) ? Clock.Current : services?.GetService(serviceType);

        public object? GetServiceForPlugin(string pluginId, bool approvedHostDistribution, Type serviceType)
        {
            if (serviceType == typeof(Action<string>)) return log;
            if (serviceType == typeof(BotAgent.Domain.Ports.IClock)) return Clock.Current;
            return services is IOwnedHostServices owned
                ? owned.GetServiceForPlugin(pluginId, approvedHostDistribution, serviceType)
                : null;
        }
    }
}

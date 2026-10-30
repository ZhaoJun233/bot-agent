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

/// <summary>Owns the restart-bound plugin registry, package transport and startup/shutdown lifecycle.</summary>
internal sealed class PluginRuntime : IAsyncDisposable
{
    private readonly HttpFetcher _http;
    private readonly Action<string> _log;
    private readonly HashSet<string> _presetIds = new(StringComparer.Ordinal);
    private ImmutableDictionary<string, string> _failures = ImmutableDictionary<string, string>.Empty;
    private readonly object _lifecycleGate = new();
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

    /// <summary>
    /// 市场服务：目录、暂存与声明式设置共用同一套包/设置实例。启用态一次读全表
    /// （每次请求一次查询），不让目录逐条开连接。
    /// </summary>
    internal PluginMarketService Market => _market ??= new PluginMarketService(Packages, Settings, Catalog,
        () => BotAgent.Adapters.Time.Clock.Now, Path.Combine(_root, "operations.json"), LoadDesiredStates, _log);

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
            var restart = package?.Pending is not null || package?.PendingUninstall == true
                || enabled != (loaded?.IsEnabled ?? false);
            var error = _failures.TryGetValue(id, out var failure) ? failure : restart ? "plugin_restart_required" : null;
            return new ManagedDescriptor(new ManagedObjectKey.Plugin(new(id)), "plugin", enabled, state,
                ImmutableArray.Create(new ActionCapability(ManagedAction.Start, false, "configuration_required"),
                    new ActionCapability(ManagedAction.Stop, false, "configuration_required")), true, error, "");
        }).ToImmutableArray();
    }

    internal Task StartAsync(CancellationToken ct = default)
    {
        lock (_lifecycleGate)
        {
            if (_stopTask is not null) throw new ObjectDisposedException(nameof(PluginRuntime));
            return _startTask ??= StartCoreAsync(ct);
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        var desired = LoadDesiredStates();
        var context = new PluginContext(new PluginServices(_log));
        var prepared = new Dictionary<string, long>(StringComparer.Ordinal);
        var result = await Packages.LoadAtStartupAsync(context, id => Registry.Find(id) is not null,
            id => desired.TryGetValue(id, out var enabled) && enabled, ct,
            manifest =>
            {
                var json = Settings.ResolveRuntimeValues(manifest, out var revision);
                prepared[manifest.Id] = revision;
                return json;
            }).ConfigureAwait(false);
        // Only a package that actually initialized may be stamped as applied; failures keep asking for a restart.
        foreach (var plugin in result.Loaded)
            if (prepared.TryGetValue(plugin.Id, out var revision)
                && Packages.InstalledSnapshot.FirstOrDefault(package => package.Id == plugin.Id)?.Active is { } manifest)
                Settings.MarkApplied(manifest, revision);
        _failures = result.Failures.GroupBy(failure => failure.Id, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.First().Code, StringComparer.Ordinal);
        foreach (var plugin in result.Loaded) Registry.Register(plugin, defaultEnabled: true);
        foreach (var failure in result.Failures) _log($"[Plugin] {failure.Id}: {failure.Code}");
        await Registry.StartAllAsync(ct).ConfigureAwait(false);
    }

    private IEnumerable<string> DeclaredIds()
        => _presetIds.Concat(Packages.InstalledSnapshot
                .Where(package => package.Active is not null || package.Pending is not null)
                .Select(package => package.Id))
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);

    private bool DesiredEnabled(string id, IReadOnlyDictionary<string, bool> desired)
        => desired.TryGetValue(id, out var enabled) ? enabled : _presetIds.Contains(id);

    private static IReadOnlyDictionary<string, bool> LoadDesiredStates()
    {
        using var connection = AppDatabase.Open();
        return PluginStateStore.LoadPluginStatesInTransaction(connection);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate) return new(_stopTask ??= StopCoreAsync());
    }

    private async Task StopCoreAsync()
    {
        try
        {
            if (_startTask is not null)
            {
                try { await _startTask.ConfigureAwait(false); } catch { /* Startup already reported its failure. */ }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await Registry.StopAllAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        finally { _http.Dispose(); }
    }

    // Only deliberate extension services are exposed; no ambient session/member stores or credentials.
    private sealed class PluginServices(Action<string> log) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(Action<string>) ? log : null;
    }
}

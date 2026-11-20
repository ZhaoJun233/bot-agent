using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

/// <summary>Owns plugin generations, lifecycle ordering and invocation leases.</summary>
public sealed partial class PluginManager : IPluginRegistry
{
    // Never hold this lock while calling plugin code. Lifecycle operations may await a drain,
    // but lookups, dispatch and SetEnabled must remain available for the new generation.
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly AsyncLocal<int> _callbackDepth = new();
    private readonly Dictionary<string, Entry> _plugins = new(StringComparer.Ordinal);
    private readonly Action<string> _log;
    private bool _stopped;

    public PluginManager(Action<string> log) => _log = log;

    public void Register(IBotPlugin plugin, bool defaultEnabled = true)
    {
        var entry = new Entry(this, plugin, defaultEnabled, initialized: false);
        lock (_sync)
        {
            ThrowIfStopped();
            // Registration is for startup; replacing a live instance must drain it asynchronously.
            if (!_plugins.TryAdd(entry.Info.Id, entry))
                throw new InvalidOperationException("plugin_already_registered");
        }
        Log($"[Plugin] 注册插件: {entry.Info.Name} ({entry.Info.Id}) v{entry.Info.Version}");
    }

    public IReadOnlyList<PluginInfo> GetAll()
    {
        lock (_sync)
            return _plugins.Values.Select(e => e.Info with { IsEnabled = e.Enabled }).ToArray();
    }

    /// <summary>Returns a generation-bound facade, never the unloadable plugin instance.</summary>
    public IBotPlugin? Find(string pluginId)
    {
        lock (_sync) return _plugins.TryGetValue(pluginId, out var entry) ? entry.Proxy : null;
    }

    public bool IsEnabled(string pluginId)
    {
        lock (_sync) return _plugins.TryGetValue(pluginId, out var entry) && entry.Enabled;
    }

    public bool IsInitialized(string pluginId)
    {
        lock (_sync) return _plugins.TryGetValue(pluginId, out var entry) && entry.Initialized;
    }

    public bool IsRuntimePrepared(string pluginId)
    {
        lock (_sync) return _plugins.TryGetValue(pluginId, out var entry) && entry.RuntimePrepared;
    }

    /// <summary>For package-managed plugins, capability gating matches runtime readiness; legacy in-process registrations use Enabled.</summary>
    public bool IsOperational(string pluginId)
    {
        lock (_sync)
            return _plugins.TryGetValue(pluginId, out var entry)
                && (entry.RuntimePrepared ? Ready(entry, new()) : entry.Enabled);
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        lock (_sync)
        {
            if (_plugins.TryGetValue(pluginId, out var entry)) entry.Enabled = enabled;
        }
    }

    /// <summary>
    /// Publishes an already prepared candidate (initialized by the runtime when enabled).
    /// Ownership transfers at publication. Cancellation only applies before that point; after
    /// publication this waits for old calls and Shutdown, with shutdown failures logged only.
    /// </summary>
    public Task ReplaceAsync(IBotPlugin plugin, bool enabled, CancellationToken ct = default)
        => ReplaceCoreAsync(plugin, enabled, null, 1, ct);

    public Task ReplaceAsync(IBotPlugin plugin, bool enabled, int apiVersion, CancellationToken ct = default)
        => ReplaceCoreAsync(plugin, enabled, null, apiVersion, ct);

    public Task ReplaceAsync(IBotPlugin plugin, bool enabled, DependencyLease dependencies, int apiVersion,
        CancellationToken ct = default)
        => ReplaceCoreAsync(plugin, enabled, dependencies, apiVersion, ct);

    public Task<InvocationGate> ReplaceAndBindAsync(IBotPlugin plugin, bool enabled, int apiVersion,
        DependencyLease? dependencies = null, CancellationToken ct = default)
        => ReplaceCoreAsync(plugin, enabled, dependencies, apiVersion, ct);

    /// <summary>Installs trusted adapter bindings before Ready can become observable. The gate remains closed until publication.</summary>
    public Task<InvocationGate> ReplaceWithBindingsAsync(IBotPlugin plugin, bool enabled, int apiVersion,
        Action<InvocationGate> bind, DependencyLease? dependencies = null, CancellationToken ct = default)
        => ReplaceCoreAsync(plugin, enabled, dependencies, apiVersion, ct, bind);

    private async Task<InvocationGate> ReplaceCoreAsync(IBotPlugin plugin, bool enabled, DependencyLease? dependencies, int apiVersion,
        CancellationToken ct, Action<InvocationGate>? bind = null)
    {
        var twoPhaseActivation = enabled && plugin is IPluginTwoPhaseLifecycle { RequiresTwoPhaseActivation: true };
        var candidate = new Entry(this, plugin, enabled, initialized: enabled && !twoPhaseActivation)
        {
            RuntimePrepared = true,
            Starting = twoPhaseActivation
        };
        candidate.ApiVersion = apiVersion;
        if (dependencies is not null)
        {
            if (!ReferenceEquals(dependencies.Owner, this) || dependencies.ConsumerId != candidate.Info.Id)
                throw new InvalidOperationException("plugin_dependency_lease_owner");
            if (dependencies.ApiVersion != apiVersion) throw new InvalidOperationException("plugin_dependency_unavailable");
            candidate.Dependencies = dependencies.Bindings.ToArray();
        }
        ThrowIfCallbackReentry();
        ct.ThrowIfCancellationRequested();
        if (enabled) PrepareTools(candidate);
        bind?.Invoke(new InvocationGate(this, candidate));
        // A provider may be draining this preparation lease while holding lifecycle ownership.
        // Never wait behind it: the caller cleans the unpublished candidate under its lease,
        // then releases it. A later reconciliation can prepare against the new generation.
        if (dependencies is null) await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        else if (!await _lifecycle.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("plugin_dependency_busy");
        try
        {
            Entry? previous;
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                ThrowIfStopped();
                if (dependencies?.IsDisposed == true) throw new InvalidOperationException("plugin_dependency_lease_expired");
                if ((enabled || plugin is IPluginControlSource) && !DependenciesReady(candidate))
                    throw new InvalidOperationException("plugin_dependency_unavailable");
                CheckToolConflicts(candidate);
                _plugins.TryGetValue(candidate.Info.Id, out previous);
                if (ReferenceEquals(previous?.Plugin, plugin))
                    throw new InvalidOperationException("plugin_same_generation");
                previous?.Retire();
                _plugins[candidate.Info.Id] = candidate;
            }
            var previousShutdownOk = previous is null || await ShutdownRetiredAsync(previous).ConfigureAwait(false);
            if (twoPhaseActivation && plugin is IPluginTwoPhaseLifecycle twoPhase)
            {
                if (!previousShutdownOk)
                {
                    lock (_sync)
                    {
                        candidate.Starting = false;
                        candidate.Initialized = false;
                        candidate.Enabled = false;
                        candidate.Tools = [];
                        candidate.ToolTarget = null;
                    }
                    try
                    {
                        _callbackDepth.Value++;
                        try { await plugin.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
                        finally { _callbackDepth.Value--; }
                    }
                    catch { }
                    Log($"[Plugin] 跳过新代激活（旧代停止失败）: {candidate.Info.Id}");
                }
                else
                {
                    try
                    {
                        _callbackDepth.Value++;
                        try { await twoPhase.ActivateAsync(CancellationToken.None).ConfigureAwait(false); }
                        finally { _callbackDepth.Value--; }
                        lock (_sync)
                        {
                            candidate.Initialized = true;
                            candidate.Starting = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (_sync)
                        {
                            candidate.Starting = false;
                            candidate.Initialized = false;
                            candidate.Enabled = false;
                            candidate.Tools = [];
                            candidate.ToolTarget = null;
                        }
                        try
                        {
                            _callbackDepth.Value++;
                            try { await plugin.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
                            finally { _callbackDepth.Value--; }
                        }
                        catch { }
                        Log($"[Plugin] 新代激活失败: {candidate.Info.Id}, {ex.GetType().Name}");
                    }
                }
            }
            return new InvocationGate(this, candidate);
        }
        finally { _lifecycle.Release(); }
    }

    public Task RemoveAsync(string id, CancellationToken ct = default) => RemoveAsync(id, null, ct);

    private async Task RemoveAsync(string id, Entry? expected, CancellationToken ct)
    {
        ThrowIfCallbackReentry();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Entry? previous;
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                if (!_plugins.TryGetValue(id, out previous) ||
                    (expected is not null && !ReferenceEquals(previous, expected))) return;
                previous.Retire();
                _plugins.Remove(id);
            }
            await ShutdownRetiredAsync(previous).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StartAllAsync(CancellationToken ct)
    {
        ThrowIfCallbackReentry();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Entry[] entries;
            lock (_sync)
            {
                ThrowIfStopped();
                entries = _plugins.Values.ToArray();
            }
            foreach (var entry in entries)
            {
                IBotPlugin plugin;
                lock (_sync)
                {
                    if (entry.RuntimePrepared || !entry.Enabled || entry.Initialized) continue;
                    entry.Starting = true;
                    plugin = entry.Plugin!;
                }
                try
                {
                    _callbackDepth.Value++;
                    try { await plugin.InitializeAsync(ct).ConfigureAwait(false); }
                    finally { _callbackDepth.Value--; }
                    PrepareTools(entry);
                    lock (_sync) { CheckToolConflicts(entry); entry.Initialized = true; }
                    Log($"[Plugin] 插件初始化完成: {entry.Info.Name}");
                }
                catch (Exception ex)
                {
                    lock (_sync) { entry.Enabled = false; entry.Tools = []; entry.ToolTarget = null; }
                    Log($"[Plugin] 插件初始化失败: {entry.Info.Name}, {ex.GetType().Name}");
                }
                finally { lock (_sync) entry.Starting = false; }
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Terminally closes registration and drains every generation, including disabled ones.</summary>
    public async Task StopAllAsync(CancellationToken ct)
    {
        ThrowIfCallbackReentry();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Entry[] entries;
            lock (_sync)
            {
                _stopped = true;
                entries = _plugins.Values.ToArray();
                foreach (var entry in entries) entry.Retire();
                _plugins.Clear();
            }
            // Do not let a caller's timeout unload assemblies that still have executing code.
            foreach (var entry in ShutdownOrder(entries)) await ShutdownRetiredAsync(entry).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<bool> ShutdownRetiredAsync(Entry entry)
    {
        InvalidateUiSessions(entry);
        await entry.Drained.Task.ConfigureAwait(false);
        var ok = true;
        try
        {
            if (entry.Plugin is { } plugin)
            {
                _callbackDepth.Value++;
                try { await plugin.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { _callbackDepth.Value--; }
            }
        }
        catch (Exception ex)
        {
            ok = false;
            Log($"[Plugin] 插件停止异常: {entry.Info.Name}, {ex.GetType().Name}");
        }
        finally
        {
            // A stale Find facade may outlive the package. Do not let it root the old ALC.
            lock (_sync) { entry.Plugin = null; entry.ToolTarget = null; }
        }
        return ok;
    }

    private string? BlockReason(Entry entry, ReplyPolicyContext context)
    {
        IReplyPolicyPlugin policy;
        Entry[] leases;
        lock (_sync)
        {
            // A lookup can race replacement. Lease the current generation rather than silently bypass its policy.
            if (entry.Retired && !_plugins.TryGetValue(entry.Info.Id, out entry!)) return null;
            if (!entry.Enabled || entry.Starting || entry.Plugin is not IReplyPolicyPlugin target)
                return null;
            if (entry.RuntimePrepared && !entry.Initialized) return "plugin_not_ready";
            if (!DependenciesReady(entry)) return "plugin_dependency_unavailable";
            policy = target;
            leases = DependencyClosure(entry.Dependencies).Append(entry).Distinct().ToArray();
            foreach (var leased in leases) leased.InFlight++;
        }
        _callbackDepth.Value++;
        try { return policy.BlockReason(context); }
        finally
        {
            lock (_sync)
            {
                _callbackDepth.Value--;
                ReleaseLeases(leases);
            }
        }
    }

    private void ThrowIfCallbackReentry()
    {
        if (_callbackDepth.Value != 0) throw new InvalidOperationException("plugin_lifecycle_reentry");
    }

    private void ThrowIfStopped()
    {
        if (_stopped) throw new InvalidOperationException("plugin_registry_stopped");
    }

    private void Log(string message)
    {
        // Logging cannot turn an already published replacement into an apparent failed transfer.
        try { _log(message); }
        catch { }
    }

    // All mutable entry state is protected by _sync. Only the serialized lifecycle owner
    // invokes Initialize/Shutdown; dispatch holds a counted lease instead of that semaphore.
    internal sealed class Entry
    {
        public Entry(PluginManager owner, IBotPlugin plugin, bool enabled, bool initialized)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            Info = new(plugin.Id, plugin.Name, plugin.Version, plugin.Description, plugin.Category, plugin.IsPreset, enabled);
            if (string.IsNullOrWhiteSpace(Info.Id)) throw new ArgumentException("plugin_id_required", nameof(plugin));
            Plugin = plugin;
            Enabled = enabled;
            Initialized = initialized;
            Proxy = plugin is IReplyPolicyPlugin ? new PolicyProxy(owner, this) : new PluginProxy(owner, this);
        }

        public PluginInfo Info { get; }
        public IBotPlugin Proxy { get; }
        public IBotPlugin? Plugin { get; set; }
        public bool Enabled { get; set; }
        public bool Initialized { get; set; }
        // Disabled business remains metadata-only; separately prepared control has its own admission gate.
        public bool RuntimePrepared { get; init; }
        public bool Starting { get; set; }
        public int ApiVersion { get; set; } = 1;
        public Entry[] Dependencies { get; set; } = [];
        public BotAgent.Services.Tools.ToolDefinition[] Tools { get; set; } = [];
        public string ToolGeneration { get; set; } = string.Empty;
        public BotAgent.Services.Tools.IToolContribution? ToolTarget { get; set; }
        public string GenerationId { get; } = "gen-" + Guid.NewGuid().ToString("N")[..12];
        public bool Retired { get; private set; }
        public int InFlight { get; set; }
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Retire()
        {
            Retired = true;
            if (InFlight == 0) Drained.TrySetResult();
        }
    }

    private class PluginProxy(PluginManager owner, Entry entry) : IBotPlugin
    {
        public string Id => entry.Info.Id;
        public string Name => entry.Info.Name;
        public string Version => entry.Info.Version;
        public string Description => entry.Info.Description;
        public PluginCategory Category => entry.Info.Category;
        public bool IsPreset => entry.Info.IsPreset;
        // Facades are already registered; initialization is owned by StartAll/Runtime, not callers.
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken ct) => owner.RemoveAsync(Id, entry, ct);
        protected string? EvaluatePolicy(ReplyPolicyContext context) => owner.BlockReason(entry, context);
    }

    private sealed class PolicyProxy(PluginManager owner, Entry entry) : PluginProxy(owner, entry), IReplyPolicyPlugin
    {
        public string? BlockReason(ReplyPolicyContext context) => EvaluatePolicy(context);
    }
}

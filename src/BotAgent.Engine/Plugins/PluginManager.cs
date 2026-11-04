using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

/// <summary>Owns plugin generations, lifecycle ordering and invocation leases.</summary>
public sealed class PluginManager : IPluginRegistry
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
    public async Task ReplaceAsync(IBotPlugin plugin, bool enabled, CancellationToken ct = default)
    {
        var candidate = new Entry(this, plugin, enabled, initialized: true);
        ThrowIfCallbackReentry();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Entry? previous;
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                ThrowIfStopped();
                _plugins.TryGetValue(candidate.Info.Id, out previous);
                if (ReferenceEquals(previous?.Plugin, plugin))
                    throw new InvalidOperationException("plugin_same_generation");
                previous?.Retire();
                _plugins[candidate.Info.Id] = candidate;
            }
            if (previous is not null) await ShutdownRetiredAsync(previous).ConfigureAwait(false);
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
                    if (!entry.Enabled || entry.Initialized) continue;
                    entry.Starting = true;
                    plugin = entry.Plugin!;
                }
                try
                {
                    _callbackDepth.Value++;
                    try { await plugin.InitializeAsync(ct).ConfigureAwait(false); }
                    finally { _callbackDepth.Value--; }
                    lock (_sync) entry.Initialized = true;
                    Log($"[Plugin] 插件初始化完成: {entry.Info.Name}");
                }
                catch (Exception ex)
                {
                    lock (_sync) entry.Enabled = false;
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
            foreach (var entry in entries) await ShutdownRetiredAsync(entry).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ShutdownRetiredAsync(Entry entry)
    {
        await entry.Drained.Task.ConfigureAwait(false);
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
            Log($"[Plugin] 插件停止异常: {entry.Info.Name}, {ex.GetType().Name}");
        }
        finally
        {
            // A stale Find facade may outlive the package. Do not let it root the old ALC.
            lock (_sync) entry.Plugin = null;
        }
    }

    private string? BlockReason(Entry entry, ReplyPolicyContext context)
    {
        IReplyPolicyPlugin policy;
        lock (_sync)
        {
            // A lookup can race replacement. Lease the current generation rather than silently bypass its policy.
            if (entry.Retired && !_plugins.TryGetValue(entry.Info.Id, out entry!)) return null;
            if (!entry.Enabled || entry.Starting || entry.Plugin is not IReplyPolicyPlugin target)
                return null;
            policy = target;
            entry.InFlight++;
        }
        _callbackDepth.Value++;
        try { return policy.BlockReason(context); }
        finally
        {
            lock (_sync)
            {
                _callbackDepth.Value--;
                entry.InFlight--;
                if (entry.Retired && entry.InFlight == 0) entry.Drained.TrySetResult();
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
    private sealed class Entry
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
        public bool Starting { get; set; }
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

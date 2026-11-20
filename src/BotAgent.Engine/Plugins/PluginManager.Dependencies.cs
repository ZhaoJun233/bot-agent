using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

public sealed partial class PluginManager
{
    /// <summary>Generation-bound admission for contribution adapters that dispatch outside Find.</summary>
    public sealed class InvocationGate
    {
        private readonly PluginManager _owner;
        private readonly Entry _entry;
        private readonly bool _control;
        internal InvocationGate(PluginManager owner, Entry entry, bool control = false)
        { _owner = owner; _entry = entry; _control = control; }
        public InvocationGate ForControl() => new(_owner, _entry, control: true);
        private bool Ready() => _control ? _owner.ControlReady(_entry) : _owner.Ready(_entry, new HashSet<Entry>());
        public bool IsReady { get { lock (_owner._sync) return Ready(); } }
        public T GetDependency<T>(string providerId) where T : class, IPluginContribution
            => _owner.Contribution<T>(providerId, _entry.Info.Id, _entry, controlCaller: _control);
        public IDisposable? TryAcquire()
        {
            lock (_owner._sync)
            {
                if (!Ready()) return null;
                var leases = DependencyClosure(_entry.Dependencies).Append(_entry).Distinct().ToArray();
                foreach (var entry in leases) entry.InFlight++;
                return new InvocationLease(_owner, leases);
            }
        }
    }

    private sealed class InvocationLease(PluginManager owner, Entry[] leases) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (owner._sync)
            {
                if (_disposed) return;
                ReleaseLeases(leases);
                _disposed = true;
            }
        }
    }

    /// <summary>Enabled intent alone is not readiness. Every bound dependency must still be current.</summary>
    public bool IsReady(string id)
    {
        lock (_sync) return _plugins.TryGetValue(id, out var entry) && Ready(entry, new HashSet<Entry>());
    }

    /// <summary>Pins exact ready provider generations while a consumer initializes. Publication
    /// rechecks these same generations; it never silently rebinds a prepared consumer.</summary>
    public DependencyLease PrepareDependencies(string consumerId, IReadOnlyList<PluginPackageDependency>? dependencies,
        int apiVersion = 1)
    {
        lock (_sync)
        {
            ThrowIfStopped();
            var bindings = new List<Entry>();
            foreach (var dependency in dependencies ?? [])
            {
                if (!_plugins.TryGetValue(dependency.Id, out var provider) ||
                    provider.Info.Version != dependency.Version || provider.ApiVersion != apiVersion ||
                    !Ready(provider, new HashSet<Entry>()))
                    throw new InvalidOperationException("plugin_dependency_unavailable");
                bindings.Add(provider);
            }
            var closure = DependencyClosure(bindings).ToArray();
            if (closure.Any(entry => entry.Info.Id == consumerId))
                throw new InvalidOperationException("plugin_dependency_cycle");
            foreach (var entry in closure) entry.InFlight++;
            return new DependencyLease(this, consumerId, apiVersion, bindings.ToArray(), closure);
        }
    }

    // Called only under _sync. Path membership also fails closed for a malformed cycle.
    private bool Ready(Entry entry, HashSet<Entry> path, HashSet<Entry>? confirmed = null)
    {
        confirmed ??= new HashSet<Entry>();
        if (confirmed.Contains(entry)) return true;
        if (!entry.Enabled || !entry.Initialized || entry.Starting || entry.Retired ||
            !_plugins.TryGetValue(entry.Info.Id, out var current) || !ReferenceEquals(entry, current) || !path.Add(entry))
            return false;
        var ready = entry.Dependencies.All(provider => Ready(provider, path, confirmed));
        path.Remove(entry);
        if (ready) confirmed.Add(entry);
        return ready;
    }

    public bool IsControlReady(string id)
    {
        lock (_sync) return _plugins.TryGetValue(id, out var entry) && ControlReady(entry);
    }

    private bool ControlReady(Entry entry) => entry.RuntimePrepared && entry.Plugin is IPluginControlSource
        && !entry.Starting && !entry.Retired && _plugins.TryGetValue(entry.Info.Id, out var current)
        && ReferenceEquals(entry, current) && DependenciesReady(entry);

    private bool DependenciesReady(Entry entry) => entry.Dependencies.All(provider => Ready(provider, new HashSet<Entry>()));

    private static IEnumerable<Entry> DependencyClosure(IEnumerable<Entry> bindings)
    {
        var seen = new HashSet<Entry>();
        var pending = new Stack<Entry>(bindings);
        while (pending.TryPop(out var entry))
        {
            if (!seen.Add(entry)) continue;
            yield return entry;
            foreach (var provider in entry.Dependencies) pending.Push(provider);
        }
    }

    private static void ReleaseLeases(IEnumerable<Entry> leases)
    {
        foreach (var entry in leases)
        {
            entry.InFlight--;
            if (entry.Retired && entry.InFlight == 0) entry.Drained.TrySetResult();
        }
    }

    private static IEnumerable<Entry> ShutdownOrder(Entry[] entries)
    {
        var current = entries.ToHashSet();
        var seen = new HashSet<Entry>();
        var providersFirst = new List<Entry>();
        void Visit(Entry entry)
        {
            if (!current.Contains(entry) || !seen.Add(entry)) return;
            foreach (var provider in entry.Dependencies) Visit(provider);
            providersFirst.Add(entry);
        }
        foreach (var entry in entries) Visit(entry);
        providersFirst.Reverse();
        return providersFirst;
    }

    public sealed class DependencyLease : IDisposable
    {
        internal PluginManager Owner { get; }
        internal string ConsumerId { get; }
        internal int ApiVersion { get; }
        internal Entry[] Bindings { get; }
        private Entry[]? _leases;
        internal bool IsDisposed => _leases is null;

        internal DependencyLease(PluginManager owner, string consumerId, int apiVersion, Entry[] bindings, Entry[] leases)
        {
            Owner = owner; ConsumerId = consumerId; ApiVersion = apiVersion; Bindings = bindings; _leases = leases;
        }

        public void Dispose()
        {
            lock (Owner._sync)
            {
                if (_leases is null) return;
                ReleaseLeases(_leases);
                _leases = null;
            }
        }
    }
}

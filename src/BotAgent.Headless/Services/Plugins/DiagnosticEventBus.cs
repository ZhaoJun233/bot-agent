using System.Collections.Concurrent;
using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

public sealed class DiagnosticEventBus : IDiagnosticEventBus
{
    private readonly ConcurrentBag<PluginDiagnosticReport> _reports = new();
    private readonly ConcurrentDictionary<Guid, Action<PluginDiagnosticReport>> _handlers = new();

    public void Publish(PluginDiagnosticReport report)
    {
        _reports.Add(report);
        foreach (var (_, handler) in _handlers)
        {
            try
            {
                handler(report);
            }
            catch
            {
                // 隔离监听器异常
            }
        }
    }

    public IDisposable Subscribe(Action<PluginDiagnosticReport> handler)
    {
        var id = Guid.NewGuid();
        _handlers[id] = handler;
        return new Subscription(() => _handlers.TryRemove(id, out _));
    }

    public IReadOnlyList<PluginDiagnosticReport> GetReports() => _reports.ToArray();

    private sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }
}
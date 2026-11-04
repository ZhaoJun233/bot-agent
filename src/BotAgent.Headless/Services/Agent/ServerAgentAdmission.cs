using System.Collections.Concurrent;

namespace BotAgent.Services.Agent;

/// <summary>One admission owns setup, the current cancel handle, and the busy reservation.</summary>
internal sealed class ServerAgentAdmission : IDisposable
{
    private readonly string _sourceKey;
    private readonly ConcurrentDictionary<string, bool> _busy;
    private readonly ConcurrentDictionary<string, (AgentTask Task, CancellationTokenSource Cts)> _current;
    private (AgentTask Task, CancellationTokenSource Cts)? _owned;
    private int _released;

    private ServerAgentAdmission(string sourceKey, ConcurrentDictionary<string, bool> busy,
        ConcurrentDictionary<string, (AgentTask Task, CancellationTokenSource Cts)> current)
    {
        _sourceKey = sourceKey;
        _busy = busy;
        _current = current;
    }

    public static ServerAgentAdmission? TryAcquire(string sourceKey, ConcurrentDictionary<string, bool> busy,
        ConcurrentDictionary<string, (AgentTask Task, CancellationTokenSource Cts)> current)
    {
        var admission = new ServerAgentAdmission(sourceKey, busy, current);
        return busy.TryAdd(sourceKey, true) ? admission : null;
    }

    public void Bind(AgentTask task)
    {
        if (_owned is not null || Volatile.Read(ref _released) != 0)
            throw new InvalidOperationException("server_agent_admission_already_bound_or_released");
        var cts = task.SourceLease is { } lease
            ? CancellationTokenSource.CreateLinkedTokenSource(lease.Lifetime) : new CancellationTokenSource();
        _owned = (task, cts);
        if (!_current.TryAdd(_sourceKey, _owned.Value))
            throw new InvalidOperationException("server_agent_current_already_owned");
    }

    public CancellationToken Token => _owned?.Cts.Token
        ?? throw new InvalidOperationException("server_agent_admission_not_bound");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        try
        {
            if (_owned is { } owned)
            {
                // ConcurrentDictionary's pair removal compares the value atomically: never remove a successor.
                ((ICollection<KeyValuePair<string, (AgentTask Task, CancellationTokenSource Cts)>>)_current)
                    .Remove(new(_sourceKey, owned));
                owned.Cts.Dispose();
            }
        }
        finally
        {
            // Removal and CTS disposal finish before another command can acquire this source.
            _busy.TryRemove(_sourceKey, out _);
        }
    }
}

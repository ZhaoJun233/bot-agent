namespace BotAgent.Engine.Runtime;

public sealed partial class AgentTaskRuntime
{
    // _gate protects both state and the outbox. Enqueue at the state commit, NOT at
    // the eventual Save call: a mutex around Save alone still permits stale writes.
    private readonly Queue<(AgentTaskSnapshot Snapshot, bool Notify)> _publications = new();
    private bool _publishing;
    private int _publisherThreadId;
    private TaskCompletionSource? _publicationCompletion;

    private Task PublicationCompletion()
    {
        lock (_gate)
        {
            return _publicationCompletion?.Task ?? Task.CompletedTask;
        }
    }

    // Caller holds _gate. Acceptance keeps the existing no-Changed-event contract.
    private void QueuePublication(AgentTaskSnapshot snapshot, bool notify = true)
        => _publications.Enqueue((snapshot, notify));

    /// <summary>
    /// One caller drains committed snapshots in order, with no I/O or callbacks under
    /// _gate. Concurrent/reentrant callers only enqueue; waiting for them here could
    /// deadlock a subscriber which calls back into the runtime. Thus a contended call
    /// can return before its publication, but cannot publish ahead of earlier commits.
    /// </summary>
    private void DrainPublications()
    {
        lock (_gate)
        {
            if (_publishing || _publications.Count == 0) return;
            _publishing = true;
            _publisherThreadId = Environment.CurrentManagedThreadId;
            _publicationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        while (true)
        {
            (AgentTaskSnapshot Snapshot, bool Notify) publication;
            lock (_gate)
            {
                if (_publications.Count == 0)
                {
                    _publishing = false;
                    _publisherThreadId = 0;
                    _publicationCompletion!.TrySetResult();
                    return;
                }
                publication = _publications.Dequeue();
            }

            Persist(publication.Snapshot);
            if (publication.Notify) NotifyChanged(publication.Snapshot);
        }
    }

    private void NotifyChanged(AgentTaskSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action<AgentTaskSnapshot> handler in handlers.GetInvocationList())
        {
            // One subscriber must not lose later publications or change task outcome.
            try { handler(snapshot); }
            catch (Exception exception) { LogPublicationFailure("状态通知失败", exception); }
        }
    }

    private void LogPublicationFailure(string operation, Exception exception)
    {
        try { _log?.Invoke($"[Task] {operation}（任务状态不受影响）：{exception.Message}"); }
        catch { /* Observability must not strand the publication drain. */ }
    }
}

using BotAgent.Domain.Agent;

namespace BotAgent.Engine.Runtime;

public sealed partial class AgentTaskRuntime
{
    private TaskCompletionSource? _shutdownCompletion;
    private int _shutdownDriverThreadId;

    public ValueTask DisposeAsync()
    {
        TaskRecord[]? tasks = null;
        TaskCompletionSource completion;
        bool reentrantCallback;
        lock (_gate)
        {
            // Capture reentry BEFORE any await. A continuation may run on a different
            // thread, but the synchronous publisher callback still cannot wait for itself.
            reentrantCallback = _publisherThreadId == Environment.CurrentManagedThreadId
                || _shutdownDriverThreadId == Environment.CurrentManagedThreadId;
            if (_shutdownCompletion is null)
            {
                // Install the shared completion before callbacks can reenter disposal.
                _shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposed = true;
                tasks = _records.Values.ToArray();
                foreach (var task in tasks.Where(task => !AgentTaskTransitions.IsTerminal(task.State)))
                {
                    task.CancellationRequested = true;
                    if (task.State == AgentTaskState.WaitingApproval)
                    {
                        task.State = AgentTaskState.Cancelled;
                        task.ErrorCode = "cancelled";
                    }
                    task.UpdatedAt = _now();
                    QueuePublication(Project(task));
                }
            }
            completion = _shutdownCompletion;
        }

        if (tasks is not null) _ = CompleteDisposalAsync(tasks, completion);
        // Callbacks request shutdown and return to the drainer. All external callers,
        // including later Dispose calls, join the same complete shutdown barrier.
        return reentrantCallback ? ValueTask.CompletedTask : new ValueTask(completion.Task);
    }

    private async Task CompleteDisposalAsync(TaskRecord[] tasks, TaskCompletionSource completion)
    {
        try
        {
            Task publications;
            // Cancellation callbacks run synchronously too. Mark only this stack, not
            // async continuations, so unrelated external callers still join shutdown.
            lock (_gate) _shutdownDriverThreadId = Environment.CurrentManagedThreadId;
            try
            {
                DrainPublications();
                publications = PublicationCompletion();
                foreach (var task in tasks)
                {
                    if (task.CancelSource is { } cancel) TryCancel(cancel);
                    if (task.TimeoutSource is { } timeout) TryCancel(timeout);
                }
            }
            finally { lock (_gate) _shutdownDriverThreadId = 0; }

            var work = tasks.Where(task => task.Work is not null).Select(task => task.Work!).ToArray();
            if (work.Length > 0)
            {
                try { await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* Executor shutdown is bounded; committed publications must still finish. */ }
            }
            await publications.ConfigureAwait(false);
            await PublicationCompletion().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
}

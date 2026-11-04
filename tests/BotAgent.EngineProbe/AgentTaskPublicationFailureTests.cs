using System.Collections.Concurrent;
using BotAgent.Domain.Agent;
using BotAgent.Engine.Runtime;

namespace BotAgent.EngineProbe;

internal static partial class AgentTaskPublicationTests
{
    internal static async Task DisposalWaitsForPublicationTests()
    {
        var ledger = new PublicationLedger { Block = entry => entry.State == AgentTaskState.WaitingApproval };
        var executor = new PublicationExecutor(_ => Task.FromResult(new AgentTaskExecution { Ok = true }));
        var runtime = new AgentTaskRuntime(executor, () => Epoch, ledger: ledger,
            approvalGate: new PublicationApprovalGate(() => Epoch));
        var submit = Task.Run(() => runtime.Submit(Request("dispose-waiting")));
        Task? disposal = null;
        try
        {
            await ledger.Entered.Task.WaitAsync(Deadline);
            disposal = runtime.DisposeAsync().AsTask();
            Check(!disposal.IsCompleted, "shutdown must wait for the active ledger publisher");
        }
        finally { ledger.Release.Set(); }
        await submit.WaitAsync(Deadline);
        if (disposal is not null) await disposal.WaitAsync(Deadline);
        Check(ledger.LoadRecent(1).Single().State == AgentTaskState.Cancelled,
            "shutdown completes only after waiting-task terminal persistence");
        Check(executor.Calls == 0, "shutdown never dispatches waiting work");
    }

    internal static async Task CallbackDisposalTests(AgentTaskState state)
    {
        var ledger = new PublicationLedger();
        var executor = new PublicationExecutor(_ => Task.FromResult(new AgentTaskExecution { Ok = true }));
        var runtime = new AgentTaskRuntime(executor, () => Epoch, ledger: ledger);
        var callbackReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Changed += snapshot =>
        {
            if (snapshot.State != state) return;
            runtime.DisposeAsync().GetAwaiter().GetResult();
            callbackReturned.TrySetResult();
        };
        var submit = Task.Run(() => runtime.Submit(Request("callback-dispose-" + state)));
        await callbackReturned.Task.WaitAsync(Deadline);
        var accepted = await submit.WaitAsync(Deadline);
        await runtime.DisposeAsync().AsTask().WaitAsync(Deadline);
        Check(AgentTaskTransitions.IsTerminal(ledger.LoadRecent(1).Single().State),
            "external shutdown joins callback-initiated shutdown through terminal publication");
    }

    internal static async Task CancellationCallbackDisposalTests(bool cooperative = false)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new PublicationLedger();
        AgentTaskRuntime? runtime = null;
        var executor = new PublicationExecutor(async token =>
        {
            using var registration = token.Register(() => runtime!.DisposeAsync().GetAwaiter().GetResult());
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (cooperative)
            {
                return new AgentTaskExecution { Ok = false };
            }
            return new AgentTaskExecution { Ok = true };
        });
        runtime = new AgentTaskRuntime(executor, () => Epoch, ledger: ledger);
        runtime.Submit(Request("cancel-callback-dispose"));
        await entered.Task.WaitAsync(Deadline);
        await Task.Run(async () => await runtime.DisposeAsync()).WaitAsync(Deadline);
        var terminal = ledger.LoadRecent(1).Single();
        Check(terminal.State == AgentTaskState.Cancelled && terminal.ErrorCode == "cancelled",
            "cancellation callback disposal persists cancellation, not timeout or failure");
    }

    internal static async Task PublicationFailureTests()
    {
        var ledger = new PublicationLedger
        {
            BeforeSave = entry =>
            {
                if (entry.State == AgentTaskState.Queued)
                    throw new InvalidOperationException("synthetic ledger failure");
            }
        };
        var events = new ConcurrentQueue<AgentTaskSnapshot>();
        using var terminal = new SemaphoreSlim(0);
        var executor = new PublicationExecutor(_ => Task.FromResult(new AgentTaskExecution { Ok = true }));
        await using var runtime = new AgentTaskRuntime(executor, () => Epoch, ledger: ledger,
            log: message =>
            {
                if (message.Contains("失败")) throw new InvalidOperationException("synthetic logging failure");
            });
        runtime.Changed += _ => throw new InvalidOperationException("synthetic subscriber failure");
        runtime.Changed += snapshot =>
        {
            events.Enqueue(snapshot);
            if (AgentTaskTransitions.IsTerminal(snapshot.State)) terminal.Release();
        };
        foreach (var id in new[] { "first", "second" })
        {
            Check(runtime.Submit(Request(id)).Accepted, "publication failure must not reject later work");
            Check(await terminal.WaitAsync(Deadline), "publication failures must not strand the drainer");
            Check(ledger.LoadRecent(10).Single(entry => entry.Id == id).State == AgentTaskState.Succeeded,
                "later terminal persistence must still succeed after failed acceptance Save");
            Check(events.Where(snapshot => snapshot.Id == id).Select(snapshot => snapshot.State).SequenceEqual(
                new[] { AgentTaskState.Running, AgentTaskState.Succeeded }), "later Changed subscriber stays ordered");
        }
        Check(executor.Calls == 2, "publication failure must not change execution outcome");
    }
}

using System.Collections.Concurrent;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Runtime;

namespace BotAgent.EngineProbe;

internal static partial class AgentTaskPublicationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Epoch = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    internal static Task SubmitTests() => BlockedSuccessTests(resume: false);
    internal static Task ResumeTests() => BlockedSuccessTests(resume: true);

    // A replacement ledger deliberately holds an older Save while the worker commits
    // success. A write mutex alone cannot order snapshots captured before entering it.
    private static async Task BlockedSuccessTests(bool resume)
    {
        var ledger = new PublicationLedger();
        var events = new ConcurrentQueue<AgentTaskSnapshot>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new PublicationExecutor(async _ =>
        {
            await ledger.Entered.Task.WaitAsync(Deadline);
            return new AgentTaskExecution { Ok = true, ToolCalls = 2, DurationMs = 7, Text = "synthetic" };
        });
        await using var runtime = new AgentTaskRuntime(executor, () => Epoch, ledger: ledger, bootSeq: 1,
            approvalGate: resume ? new PublicationApprovalGate(() => Epoch) : null,
            log: message =>
            {
                // Keep the submit worker outside its state lock until acceptance Save
                // owns the barrier. Otherwise the worker could itself be the drainer.
                if (!resume && message.Contains("state=running")) ledger.Entered.Task.Wait(Deadline);
            });
        runtime.Changed += snapshot =>
        {
            events.Enqueue(snapshot);
            if (AgentTaskTransitions.IsTerminal(snapshot.State)) terminal.TrySetResult();
        };
        if (resume) Check(runtime.Submit(Request("race")).Accepted, "waiting submit accepted");
        ledger.Block = entry => entry.State == (resume ? AgentTaskState.Running : AgentTaskState.Queued);
        var caller = Task.Run(() =>
        {
            if (resume) Check(runtime.Resume("race") == AgentTaskApprovalDecision.Approved, "resume approved");
            else Check(runtime.Submit(Request("race")).Accepted, "submit accepted");
        });
        try
        {
            await ledger.Entered.Task.WaitAsync(Deadline);
            // Also detects I/O under the state lock: Get on another thread must complete.
            await Task.Run(async () =>
            {
                while (runtime.Get("race")?.State != AgentTaskState.Succeeded) await Task.Delay(1);
            }).WaitAsync(Deadline);
        }
        finally { ledger.Release.Set(); }
        await caller.WaitAsync(Deadline);
        await terminal.Task.WaitAsync(Deadline);
        AssertPublicationOrder(ledger.Saved.ToArray(), events.ToArray());
        var row = ledger.LoadRecent(1).Single();
        Check(row.State == AgentTaskState.Succeeded && row.ToolCalls == 2 && row.DurationMs == 7,
            $"success and metrics must remain durable; got {row.State}/{row.ToolCalls}/{row.DurationMs}");
        await using var restored = new AgentTaskRuntime(executor, () => Epoch, ledger: ledger, bootSeq: 2);
        Check(restored.Get("race") is { State: AgentTaskState.Succeeded, ToolCalls: 2, DurationMs: 7 },
            "restart must preserve success and metrics, not classify a stale nonterminal row");
        Check(executor.Calls == 1, "restart must not execute again");
    }

    internal static async Task CancelTests()
    {
        var ledger = new PublicationLedger();
        var events = new ConcurrentQueue<AgentTaskSnapshot>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var terminal = new ManualResetEventSlim();
        bool terminalBeforeCancelReturns = false;
        var executor = new PublicationExecutor(ct =>
        {
            var completion = new TaskCompletionSource<AgentTaskExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Cancel invokes this synchronously. Force the worker to publish its terminal
            // outcome before token cancellation returns to the Cancel caller.
            ct.Register(() =>
            {
                completion.TrySetCanceled(ct);
                terminalBeforeCancelReturns = terminal.Wait(Deadline);
            });
            started.TrySetResult();
            return completion.Task;
        });
        await using var runtime = new AgentTaskRuntime(executor, () => Epoch, ledger: ledger);
        runtime.Changed += snapshot =>
        {
            events.Enqueue(snapshot);
            if (AgentTaskTransitions.IsTerminal(snapshot.State)) terminal.Set();
        };
        Check(runtime.Submit(Request("cancel")).Accepted, "submit accepted");
        await started.Task.WaitAsync(Deadline);
        Check(runtime.Cancel("cancel") == AgentTaskCancelOutcome.Requested, "cancel requested");
        Check(terminalBeforeCancelReturns, "worker must finish inside controlled token cancellation");
        AssertPublicationOrder(ledger.Saved.ToArray(), events.ToArray());
        Check(ledger.LoadRecent(1).Single().State == AgentTaskState.Cancelled,
            "late CancelRequested snapshot must not overwrite durable Cancelled");
        await using var restored = new AgentTaskRuntime(executor, () => Epoch, ledger: ledger, bootSeq: 2);
        Check(restored.Get("cancel")?.State == AgentTaskState.Cancelled, "restart must retain Cancelled, not Unknown");
        Check(executor.Calls == 1, "cancelled task must not execute again on restart");
    }

    internal static async Task ReentrantChangedTests()
    {
        var events = new ConcurrentQueue<AgentTaskSnapshot>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new AgentTaskRuntime(new PublicationExecutor(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentTaskExecution { Ok = true };
        }), () => Epoch);
        runtime.Changed += snapshot =>
        {
            if (snapshot.State == AgentTaskState.Running) runtime.Cancel(snapshot.Id);
        };
        runtime.Changed += snapshot =>
        {
            Check(Task.Run(() => runtime.Get(snapshot.Id)).Wait(Deadline), "Changed runs outside state lock");
            events.Enqueue(snapshot);
            if (AgentTaskTransitions.IsTerminal(snapshot.State)) terminal.TrySetResult();
        };
        runtime.Submit(Request("reentrant"));
        await terminal.Task.WaitAsync(Deadline);
        Check(events.Select(x => x.State).SequenceEqual(new[]
            { AgentTaskState.Running, AgentTaskState.CancelRequested, AgentTaskState.Cancelled }),
            "all subscribers must see Running before reentrant CancelRequested, then Cancelled");
    }

    internal static async Task WaitingCancelTests()
    {
        var ledger = new PublicationLedger { Block = entry => entry.State == AgentTaskState.WaitingApproval };
        var events = new ConcurrentQueue<AgentTaskSnapshot>();
        var executor = new PublicationExecutor(_ => Task.FromResult(new AgentTaskExecution { Ok = true }));
        await using var runtime = new AgentTaskRuntime(executor, () => Epoch,
            ledger: ledger, approvalGate: new PublicationApprovalGate(() => Epoch));
        runtime.Changed += events.Enqueue;
        var submit = Task.Run(() => runtime.Submit(Request("waiting")));
        try
        {
            await ledger.Entered.Task.WaitAsync(Deadline);
            var cancelled = await Task.Run(() => runtime.Cancel("waiting")).WaitAsync(Deadline);
            Check(cancelled == AgentTaskCancelOutcome.Requested, "waiting cancellation commits while Save is blocked");
        }
        finally { ledger.Release.Set(); }
        await submit.WaitAsync(Deadline);
        AssertPublicationOrder(ledger.Saved.ToArray(), events.ToArray());
        Check(ledger.LoadRecent(1).Single().State == AgentTaskState.Cancelled, "waiting cancellation remains durable");
        Check(executor.Calls == 0, "waiting cancellation must not execute");
    }

    private static void AssertPublicationOrder(AgentTaskLedgerEntry[] saved, AgentTaskSnapshot[] changed)
    {
        // Acceptance deliberately has no Changed event (existing public behavior).
        Check(saved.Length > 0 && saved.Length == changed.Length + 1, "each change after acceptance is saved and notified once");
        for (int i = 1; i < saved.Length; i++)
        {
            Check(saved[i].State == changed[i - 1].State && saved[i].CancellationRequested == changed[i - 1].CancellationRequested,
                "ledger and Changed must share commit order");
            Check(!saved[i - 1].CancellationRequested || saved[i].CancellationRequested, "cancel flag must never regress");
            Check(!AgentTaskTransitions.IsTerminal(saved[i - 1].State), "nothing may overwrite a terminal row");
            Check(AgentTaskTransitions.CanTransition(saved[i - 1].State, saved[i].State),
                "durable changes must follow transition order, even with identical timestamps");
        }
    }

    private static AgentTaskRequest Request(string id) => new()
        { Id = id, SourceKey = "panel:synthetic", SessionId = "synthetic", Prompt = "synthetic" };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PublicationLedger : IAgentTaskLedger
    {
        public Action<AgentTaskLedgerEntry>? BeforeSave { get; init; }
        public Func<AgentTaskLedgerEntry, bool>? Block { get; set; }
        public TaskCompletionSource<AgentTaskLedgerEntry> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public ConcurrentQueue<AgentTaskLedgerEntry> Saved { get; } = new();
        public void Save(AgentTaskLedgerEntry entry)
        {
            BeforeSave?.Invoke(entry);
            if (Block?.Invoke(entry) == true)
            {
                Entered.TrySetResult(entry);
                if (!Release.Wait(Deadline)) throw new TimeoutException("publication barrier not released");
            }
            Saved.Enqueue(entry);
        }
        public IReadOnlyList<AgentTaskLedgerEntry> LoadRecent(int limit)
            => Saved.GroupBy(entry => entry.Id).Select(group => group.Last()).Take(limit).ToArray();
        public int Prune(int keep) => 0;
    }

    private sealed class PublicationExecutor(Func<CancellationToken, Task<AgentTaskExecution>> run) : IAgentTaskExecutor
    {
        public int Calls;
        public Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return run(cancellationToken);
        }
    }

    private sealed class PublicationApprovalGate(Func<DateTimeOffset> now) : IAgentTaskApprovalGate
    {
        public AgentTaskApprovalRequest? Create(AgentTaskRequest request)
            => new("approval-" + request.Id, "synthetic-tool", "synthetic", now().AddMinutes(1));
    }
}

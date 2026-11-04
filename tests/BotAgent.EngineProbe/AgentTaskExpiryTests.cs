using BotAgent.Domain.Agent;
using BotAgent.Engine.Runtime;

namespace BotAgent.EngineProbe;

internal static partial class AgentTaskPublicationTests
{
    internal static async Task CountsWithoutReadTests()
    {
        var now = Epoch;
        var ledger = new PublicationLedger();
        var executor = new PublicationExecutor(_ => Task.FromResult(new AgentTaskExecution { Ok = true }));
        await using var runtime = new AgentTaskRuntime(executor, () => now,
            ledger: ledger, approvalGate: new PublicationApprovalGate(() => now));
        for (int i = 0; i < AgentTaskRuntime.DefaultMaxActive; i++)
            Check(runtime.Submit(Request("waiting-" + i)).Accepted, "fill waiting slot");
        now = now.AddMinutes(1);
        // No Get/List before or after moving time to the exact expiry boundary.
        var counts = runtime.Counts();
        Check(counts.Active == 0 && counts.Total == 4, "Counts must retire four expired slots without Get/List");
        Check(ledger.LoadRecent(10).Count(x => x.State == AgentTaskState.Cancelled && x.ErrorCode == "approval_expired") == 4,
            "Counts sweep must persist all four expired terminal rows");
        Check(executor.Calls == 0, "expiry must never run the executor");
    }

    internal static async Task SubmitWithoutReadTests()
    {
        var now = Epoch;
        var ledger = new PublicationLedger();
        var events = new List<AgentTaskSnapshot>();
        var executor = new PublicationExecutor(_ => Task.FromResult(new AgentTaskExecution { Ok = true }));
        await using var runtime = new AgentTaskRuntime(executor, () => now,
            ledger: ledger, approvalGate: new PublicationApprovalGate(() => now));
        runtime.Changed += events.Add;
        for (int i = 0; i < AgentTaskRuntime.DefaultMaxActive; i++)
            Check(runtime.Submit(Request("waiting-" + i)).Accepted, "fill waiting slot");
        now = now.AddMinutes(1);
        // Admission itself must sweep; even Counts would mask this regression.
        var accepted = runtime.Submit(Request("replacement"));
        Check(accepted.Accepted && accepted.Task?.State == AgentTaskState.WaitingApproval,
            "Submit must admit a new task after four approvals expire");
        Check(ledger.LoadRecent(10).Count(x => x.ErrorCode == "approval_expired") == 4,
            "Submit sweep must persist expired tasks");
        Check(events.Count == 4 && events.All(x => x.State == AgentTaskState.Cancelled && x.ErrorCode == "approval_expired"),
            "Submit sweep publishes each expiry once");
        Check(runtime.Counts().Active == 1, "only replacement occupies an active slot");
        Check(executor.Calls == 0, "expiry must never run the executor");
    }
}

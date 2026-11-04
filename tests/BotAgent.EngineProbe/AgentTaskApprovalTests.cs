using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Runtime;

namespace BotAgent.EngineProbe;

/// <summary>
/// 任务级前置审批的确定性探针（#137）：
/// 待批任务<b>一次执行器都不调</b>、批准后才真正跑、拒绝/超期/取消/重启四条收口路径
/// 各自带明确原因码，以及“闸门关掉时与改造前逐字一致”这条不变量。
///
/// 口径：纯内存闸门 + 合成执行器，不启动机器人、不落盘、不连网。
/// </summary>
internal static class AgentTaskApprovalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    internal static Func<DateTimeOffset> Now => () => T0;

    internal static async Task WaitingApprovalNeverRunsTests()
    {
        var executor = new CountingExecutor();
        var gate = new StaticGate(ToolId: "bash", Ttl: TimeSpan.FromSeconds(120));
        await using var runtime = new AgentTaskRuntime(executor, Now, approvalGate: gate);

        var submit = runtime.Submit(Request("t-wait", toolScope: ["bash"]));
        Require(submit.Accepted, "待批任务同样要受理（回执写明在等批）。");
        var accepted = submit.Task ?? throw new InvalidOperationException("受理结果缺少快照。");
        Require(accepted.State == AgentTaskState.WaitingApproval,
            $"闸门命中时受理快照必须是 WaitingApproval，实际 {accepted.State}。");
        Require(accepted.ApprovalRequestId == "synthetic-approval",
            $"快照要带审批编号，实际 {accepted.ApprovalRequestId}。");
        Require(accepted.ApprovalToolId == "bash", "快照要带高风险工具名。");

        // 给执行器一点机会：如果它被调用了，这里就会看到。
        await Task.Delay(150);
        Require(executor.Calls == 0, "★★ 批准前执行器一次都不能被调用（零副作用）。");
        Require(runtime.Get("t-wait")?.State == AgentTaskState.WaitingApproval,
            "没人决策时任务必须一直停在 WaitingApproval。");

        var pending = gate.Created;
        Require(pending.Count == 1 && pending[0].ToolId == "bash", "闸门每次命中只开一张单。");
    }

    internal static async Task ApproveRunsTests()
    {
        var executor = new CountingExecutor();
        var gate = new StaticGate(ToolId: "bash", Ttl: TimeSpan.FromSeconds(120));
        await using var runtime = new AgentTaskRuntime(executor, Now, approvalGate: gate);
        runtime.Submit(Request("t-approve", toolScope: ["bash"]));
        Require(executor.Calls == 0, "待批期间不得执行。");

        var decision = runtime.DecideApproval("synthetic-approval", approve: true, T0.AddSeconds(1));
        Require(decision == AgentTaskApprovalDecision.Approved,
            $"批准应返回 Approved，实际 {decision}。");

        var snapshot = await WaitTerminalAsync(runtime, "t-approve");
        Require(snapshot.State == AgentTaskState.Succeeded,
            $"批准后任务应跑完，实际 {snapshot.State}/{snapshot.ErrorCode}。");
        Require(executor.Calls == 1, $"批准后执行器只调一次，实际 {executor.Calls}。");
    }

    internal static async Task RejectTests()
    {
        var executor = new CountingExecutor();
        var gate = new StaticGate(ToolId: "write", Ttl: TimeSpan.FromSeconds(120));
        await using var runtime = new AgentTaskRuntime(executor, Now, approvalGate: gate);
        runtime.Submit(Request("t-reject", toolScope: ["write"]));

        var decision = runtime.DecideApproval("synthetic-approval", approve: false, T0.AddSeconds(1));
        Require(decision == AgentTaskApprovalDecision.Rejected, $"拒绝应返回 Rejected，实际 {decision}。");

        var snapshot = runtime.Get("t-reject");
        Require(snapshot is not null && snapshot.State == AgentTaskState.Cancelled,
            $"拒绝后应 Cancelled，实际 {snapshot?.State}。");
        Require(snapshot!.ErrorCode == "approval_rejected",
            $"拒绝的原因码必须是 approval_rejected，实际 {snapshot.ErrorCode}。");
        Require(executor.Calls == 0, "★★ 拒绝后执行器一次都不能被调用。");
    }

    internal static Task ExpireTests()
    {
        var executor = new CountingExecutor();
        var gate = new StaticGate(ToolId: "docker", Ttl: TimeSpan.FromSeconds(30));
        var runtime = new AgentTaskRuntime(executor, Now, approvalGate: gate);
        runtime.Submit(Request("t-expire", toolScope: ["docker"]));

        // 还没到期：不扫。
        Require(runtime.SweepApprovals(T0.AddSeconds(29)) == 0, "未到期的待批单不该被收口。");
        Require(runtime.Get("t-expire")?.State == AgentTaskState.WaitingApproval, "未到期仍应等待。");

        Require(runtime.SweepApprovals(T0.AddSeconds(31)) == 1, "过期的待批单应被收口一条。");
        var snapshot = runtime.Get("t-expire") ?? throw new InvalidOperationException("过期任务必须查得到。");
        Require(snapshot.State == AgentTaskState.Cancelled,
            $"超期后应 Cancelled，实际 {snapshot.State}。");
        Require(snapshot.ErrorCode == "approval_expired",
            $"超期的原因码必须是 approval_expired，实际 {snapshot.ErrorCode}。");
        Require(executor.Calls == 0, "★★ 超期后执行器一次都不能被调用。");

        // 已经收口的单子再批 → NotPending（不是 Approved，更不能补跑）。
        Require(runtime.DecideApproval("synthetic-approval", approve: true, T0.AddSeconds(40))
                == AgentTaskApprovalDecision.NotPending, "收口后同一张单子不能再被批准。");
        Require(runtime.DecideApproval("no-such-request", approve: true, T0.AddSeconds(40))
                == AgentTaskApprovalDecision.NotFound, "不存在的审批编号必须是 NotFound。");
        Require(executor.Calls == 0, "收口之后也不能被补跑。");
        return Task.CompletedTask;
    }

    internal static Task CancelWaitingTests()
    {
        var executor = new CountingExecutor();
        var gate = new StaticGate(ToolId: "bash", Ttl: TimeSpan.FromSeconds(120));
        var runtime = new AgentTaskRuntime(executor, Now, approvalGate: gate);
        runtime.Submit(Request("t-cancel-wait", toolScope: ["bash"]));

        Require(runtime.Cancel("t-cancel-wait") == AgentTaskCancelOutcome.Requested,
            "待批任务也必须能取消（以前没有出站路径）。");
        var snapshot = runtime.Get("t-cancel-wait") ?? throw new InvalidOperationException("被取消的任务必须查得到。");
        Require(snapshot.State == AgentTaskState.Cancelled,
            $"取消待批任务应立即收口成 Cancelled，实际 {snapshot.State}。");
        Require(snapshot.ErrorCode == "approval_cancelled",
            $"取消待批的原因码必须是 approval_cancelled，实际 {snapshot.ErrorCode}。");
        Require(executor.Calls == 0, "取消后不得执行。");
        return Task.CompletedTask;
    }

    internal static Task RestartCollapsesWaitingTests()
    {
        var ledger = new AgentTaskLedgerTests.MemoryLedger();
        ledger.Seed("t-waiting", AgentTaskState.WaitingApproval);
        var executor = new CountingExecutor();
        var runtime = new AgentTaskRuntime(executor, Now, ledger: ledger, bootSeq: 9);
        try
        {
            var snapshot = runtime.Get("t-waiting");
            Require(snapshot is not null && snapshot.State == AgentTaskState.Cancelled,
                $"重启后等待审批的任务应 Cancelled，实际 {snapshot?.State}。");
            Require(snapshot!.ErrorCode == "approval_lost_on_restart",
                $"重启收口的原因码必须是 approval_lost_on_restart，实际 {snapshot.ErrorCode}。");
            Require(executor.Calls == 0, "★★ 重启绝不重做：执行器一次都不能被调用。");
            return Task.CompletedTask;
        }
        finally
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    internal static async Task GateOffTests()
    {
        var executor = new CountingExecutor();
        await using var runtime = new AgentTaskRuntime(executor, Now, approvalGate: new OffGate());
        var submit = runtime.Submit(Request("t-gate-off", toolScope: ["bash"]));
        Require(submit.Accepted && submit.Task is { } queued && queued.State != AgentTaskState.WaitingApproval,
            "闸门关掉时不该挂起（与改造前逐字一致）。");
        var snapshot = await WaitTerminalAsync(runtime, "t-gate-off");
        Require(snapshot.State == AgentTaskState.Succeeded, "闸门关掉时任务照常跑完。");
        Require(executor.Calls == 1, "闸门关掉时执行器应被调用一次。");
    }

    internal static async Task NoScopeNeverWaitsTests()
    {
        var executor = new CountingExecutor();
        var gate = new StaticGate(ToolId: "bash", Ttl: TimeSpan.FromSeconds(120));
        await using var runtime = new AgentTaskRuntime(executor, Now, approvalGate: gate);
        var submit = runtime.Submit(Request("t-no-scope"));
        Require(submit.Accepted && submit.Task is { } queued && queued.State != AgentTaskState.WaitingApproval,
            "没声明工具作用域的任务不该被挂起。");
        var snapshot = await WaitTerminalAsync(runtime, "t-no-scope");
        Require(snapshot.State == AgentTaskState.Succeeded, "没声明作用域的任务照常跑完。");
        Require(gate.Created.Count == 0, "没声明作用域时闸门不该开单。");
    }

    private static AgentTaskRequest Request(string id, string[]? toolScope = null) => new()
    {
        Id = id,
        SourceKey = "panel:probe",
        SessionId = "session-1",
        Prompt = "synthetic task prompt",
        Target = "server",
        Timeout = TimeSpan.FromSeconds(10),
        ToolScope = toolScope
    };

    private static async Task<AgentTaskSnapshot> WaitTerminalAsync(AgentTaskRuntime runtime, string id)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (runtime.Get(id) is { } task && AgentTaskTransitions.IsTerminal(task.State)) return task;
            await Task.Delay(20);
        }

        throw new InvalidOperationException($"任务 {id} 没有在 5 秒内进入终态。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>命中的闸门：只认一个工具名，开一张固定编号的单。</summary>
    private sealed class StaticGate(string ToolId, TimeSpan Ttl) : IAgentTaskApprovalGate
    {
        internal List<AgentTaskApprovalRequest> Created { get; } = [];

        public AgentTaskApprovalRequest? Create(AgentTaskRequest request)
        {
            if (request.ToolScope is not { Count: > 0 } scope) return null;
            if (!scope.Contains(ToolId, StringComparer.Ordinal)) return null;
            var approval = new AgentTaskApprovalRequest(
                "synthetic-approval", ToolId, $"任务声明了高风险工具 {ToolId}", T0.Add(Ttl));
            Created.Add(approval);
            return approval;
        }
    }

    /// <summary>审批总开关关着：与改造前逐字一致。</summary>
    private sealed class OffGate : IAgentTaskApprovalGate
    {
        public AgentTaskApprovalRequest? Create(AgentTaskRequest request) => null;
    }

    private sealed class CountingExecutor : IAgentTaskExecutor
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new AgentTaskExecution { Ok = true, Text = "synthetic ok" });
        }
    }
}

using System.Diagnostics;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Runtime;

namespace BotAgent.EngineProbe;

/// <summary>
/// 任务台账与启动对账的确定性探针（#116 / ADR-0003）：
/// 恢复策略（Queued→Cancelled、Running→Unknown、绝不重做）、每次状态变化都写台账、
/// 以及"台账写不进去也不能影响任务成败"这条边界。
///
/// 口径：纯内存假台账 + 合成执行器，不启动机器人、不碰 SQLite（DB 侧在 ProductionSpecProbe）。
/// </summary>
internal static class AgentTaskLedgerTests
{
    private static readonly Func<DateTimeOffset> FixedNow =
        () => new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    internal static Task RecoveryTests()
    {
        var ledger = new MemoryLedger();
        ledger.Seed("t-queued", AgentTaskState.Queued);
        ledger.Seed("t-running", AgentTaskState.Running);
        ledger.Seed("t-cancel-requested", AgentTaskState.CancelRequested);
        ledger.Seed("t-waiting-approval", AgentTaskState.WaitingApproval);
        ledger.Seed("t-succeeded", AgentTaskState.Succeeded, error: null);

        var executor = new CountingExecutor();
        var runtime = new AgentTaskRuntime(executor, FixedNow, ledger: ledger, bootSeq: 7);

        var queued = runtime.Get("t-queued");
        Require(queued is not null, "历史任务在恢复后必须查得到。");
        Require(queued!.State == AgentTaskState.Cancelled && queued.ErrorCode == "cancelled_before_dispatch",
            $"未派发的历史任务应恢复成 Cancelled(cancelled_before_dispatch)，实际 {queued.State}/{queued.ErrorCode}。");

        var running = runtime.Get("t-running");
        Require(running is not null && running.State == AgentTaskState.Unknown,
            $"已派发的历史任务只能恢复成 Unknown，实际 {running?.State}。");
        Require(running!.ErrorCode == "restart_state_unknown",
            $"Unknown 必须带 restart_state_unknown，实际 {running.ErrorCode}。");

        var cancelRequested = runtime.Get("t-cancel-requested");
        Require(cancelRequested is not null && cancelRequested.State == AgentTaskState.Unknown,
            "取消请求途中的历史任务同样只能恢复成 Unknown。");

        var waitingApproval = runtime.Get("t-waiting-approval");
        Require(waitingApproval is not null && waitingApproval.State == AgentTaskState.Cancelled,
            "等待审批的历史任务还没派发，应恢复成 Cancelled。");
        // #137：单子随进程消失，重启后不可能接着批 —— 原因码必须和“从没派发”区分开。
        Require(waitingApproval!.ErrorCode == "approval_lost_on_restart",
            $"等待审批的重启收口原因码必须是 approval_lost_on_restart，实际 {waitingApproval.ErrorCode}。");

        Require(runtime.Get("t-succeeded")?.State == AgentTaskState.Succeeded, "终态历史任务必须原样保留。");

        Require(executor.Calls == 0, "恢复历史任务绝不允许重做（执行器一次都不该被调用）。");

        var counts = runtime.Counts();
        Require(counts.Active == 0 && counts.Total == 5,
            $"恢复后（活跃, 总数）应为 (0, 5)，实际 ({counts.Active}, {counts.Total})。");

        Require(ledger.Saved.Any(entry => entry.Id == "t-running" && entry.State == AgentTaskState.Unknown && entry.BootSeq == 7),
            "恢复出来的 Unknown 必须立刻回写成终态，否则下次重启会再算一遍。");

        // 恢复之后仍能正常受理事新任务，而且新任务的写入用的是本次启动序号。
        var submit = runtime.Submit(Request("t-new"));
        Require(submit.Accepted, "恢复历史任务不应影响新任务提交。");
        Require(SpinWait.SpinUntil(() => runtime.Get("t-new") is { } task && AgentTaskTransitions.IsTerminal(task.State),
            TimeSpan.FromSeconds(5)), "新任务应能跑到终态。");
        Require(ledger.Saved.Any(entry => entry.Id == "t-new" && entry.State == AgentTaskState.Queued && entry.BootSeq == 7),
            "新任务入队时也要写台账。");
        Require(ledger.Saved.Any(entry => entry.Id == "t-new" && entry.State == AgentTaskState.Succeeded),
            "新任务的终态也要写台账。");

        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    internal static async Task TransitionPersistenceTests()
    {
        var ledger = new MemoryLedger();
        await using var runtime = new AgentTaskRuntime(new BlockingExecutor(), FixedNow, ledger: ledger, bootSeq: 3);
        runtime.Submit(Request("t-persist", timeout: TimeSpan.FromSeconds(5)));
        await WaitStateAsync(runtime, "t-persist", AgentTaskState.Running);
        Require(ledger.Saved.Any(entry => entry.Id == "t-persist" && entry.State == AgentTaskState.Queued),
            "受理后应写一条 Queued。");
        Require(ledger.Saved.Any(entry => entry.Id == "t-persist" && entry.State == AgentTaskState.Running),
            "开始执行后应写一条 Running。");

        runtime.Cancel("t-persist");
        Require(SpinWait.SpinUntil(() => ledger.Saved.Any(entry => entry.Id == "t-persist" && entry.State == AgentTaskState.CancelRequested),
            TimeSpan.FromSeconds(5)), "取消请求也要落台账。");
        Require(SpinWait.SpinUntil(() => ledger.Saved.Any(entry => entry.Id == "t-persist" && entry.State == AgentTaskState.Cancelled),
            TimeSpan.FromSeconds(5)), "终态 Cancelled 也要落台账。");
    }

    internal static Task LedgerFailureIsolationTests()
    {
        var ledger = new ThrowingLedger();
        var runtime = new AgentTaskRuntime(new CountingExecutor(), FixedNow, ledger: ledger, bootSeq: 1);
        var submit = runtime.Submit(Request("t-throwing"));
        Require(submit.Accepted, "台账写失败不能让提交失败。");
        Require(SpinWait.SpinUntil(() => runtime.Get("t-throwing") is { } task && AgentTaskTransitions.IsTerminal(task.State),
            TimeSpan.FromSeconds(5)), "台账写失败不能让任务卡住。");
        Require(runtime.Get("t-throwing")!.State == AgentTaskState.Succeeded,
            "台账写失败不能改写任务的成败。");
        Require(ledger.SaveAttempts > 0, "写失败也要真的尝试过（否则这条边界没被验到）。");
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    private static AgentTaskRequest Request(string id, TimeSpan? timeout = null) => new()
    {
        Id = id,
        SourceKey = "panel:probe",
        SessionId = "session-1",
        Prompt = "synthetic task prompt",
        Target = "server",
        Timeout = timeout ?? TimeSpan.FromSeconds(10)
    };

    private static Task WaitStateAsync(AgentTaskRuntime runtime, string id, AgentTaskState state)
    {
        var stopwatch = Stopwatch.StartNew();
        while (runtime.Get(id)?.State != state)
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"等待超时：任务 {id} 进入 {state}");
            Thread.Sleep(10);
        }

        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>内存台账：记录每次写入，供断言"该写的时候写了没有"。</summary>
    internal sealed class MemoryLedger : IAgentTaskLedger
    {
        private readonly object _gate = new();
        private readonly List<AgentTaskLedgerEntry> _rows = new();

        internal IReadOnlyList<AgentTaskLedgerEntry> Saved
        {
            get { lock (_gate) return _rows.ToArray(); }
        }

        internal void Seed(string id, AgentTaskState state, string? error = "seeded")
        {
            lock (_gate)
            {
                _rows.Add(new AgentTaskLedgerEntry
                {
                    Id = id,
                    State = state,
                    ErrorCode = error,
                    SourceKey = "panel:probe",
                    SessionId = "session-1",
                    Target = "server",
                    CreatedAt = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero),
                    UpdatedAt = new DateTimeOffset(2026, 11, 2, 8, 5, 0, TimeSpan.Zero),
                    BootSeq = 2
                });
            }
        }

        public void Save(AgentTaskLedgerEntry entry)
        {
            lock (_gate) _rows.Add(entry);
        }

        public IReadOnlyList<AgentTaskLedgerEntry> LoadRecent(int limit)
        {
            lock (_gate) return _rows.Take(limit).ToArray();
        }

        public int Prune(int keep) => 0;
    }

    private sealed class ThrowingLedger : IAgentTaskLedger
    {
        private int _attempts;

        internal int SaveAttempts => Volatile.Read(ref _attempts);

        public void Save(AgentTaskLedgerEntry entry)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("synthetic ledger failure");
        }

        public IReadOnlyList<AgentTaskLedgerEntry> LoadRecent(int limit)
            => throw new InvalidOperationException("synthetic ledger failure");

        public int Prune(int keep) => 0;
    }

    private sealed class CountingExecutor : IAgentTaskExecutor
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new AgentTaskExecution { Ok = true, DurationMs = 1, ToolCalls = 0 });
        }
    }

    private sealed class BlockingExecutor : IAgentTaskExecutor
    {
        public async Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AgentTaskExecution { Ok = true };
        }
    }
}

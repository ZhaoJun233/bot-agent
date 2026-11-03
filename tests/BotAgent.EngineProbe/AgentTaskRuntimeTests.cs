using System.Diagnostics;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Runtime;

namespace BotAgent.EngineProbe;

/// <summary>
/// AgentTaskRuntime 的确定性探针（#114 / ADR-0002）：
/// 转移表全矩阵、成功、取消、超时、失败、并发上限、终态保护与正文采集。
///
/// 口径：纯合成执行器，不启动机器人、不连网、不落盘；超时用真实计时器（100ms），
/// 所以每次运行只多花约 0.1~0.2 秒。
/// </summary>
internal static class AgentTaskRuntimeTests
{
    private static readonly Func<DateTimeOffset> FixedNow =
        () => new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    internal static Task TransitionTableTests()
    {
        AgentTaskState[] terminal =
        [
            AgentTaskState.Succeeded, AgentTaskState.Failed, AgentTaskState.Cancelled,
            AgentTaskState.TimedOut, AgentTaskState.Unknown
        ];
        foreach (var state in terminal)
        {
            Require(AgentTaskTransitions.IsTerminal(state), $"终态 {state} 必须被 IsTerminal 认出。");
            foreach (var target in Enum.GetValues<AgentTaskState>())
            {
                Require(!AgentTaskTransitions.CanTransition(state, target),
                    $"终态 {state} 不应允许转移到 {target}。");
            }
        }

        var expected = new Dictionary<AgentTaskState, AgentTaskState[]>
        {
            // #137：派发前的审批闸门命中也是一种从 Queued 出发的合法转移。
            [AgentTaskState.Queued] =
            [
                AgentTaskState.Running, AgentTaskState.CancelRequested, AgentTaskState.Cancelled,
                AgentTaskState.TimedOut, AgentTaskState.WaitingApproval
            ],
            [AgentTaskState.Running] =
            [
                AgentTaskState.Succeeded, AgentTaskState.Failed, AgentTaskState.Cancelled,
                AgentTaskState.TimedOut, AgentTaskState.CancelRequested, AgentTaskState.WaitingApproval,
                AgentTaskState.Unknown
            ],
            [AgentTaskState.WaitingApproval] =
                [AgentTaskState.Running, AgentTaskState.Cancelled, AgentTaskState.Failed, AgentTaskState.TimedOut],
            [AgentTaskState.CancelRequested] =
                [AgentTaskState.Cancelled, AgentTaskState.Succeeded, AgentTaskState.Failed,
                    AgentTaskState.TimedOut, AgentTaskState.Unknown]
        };
        foreach (var (from, targets) in expected)
        {
            foreach (var to in Enum.GetValues<AgentTaskState>())
            {
                Require(AgentTaskTransitions.CanTransition(from, to) == targets.Contains(to),
                    $"转移表与文档不一致：{from} → {to}。");
            }
        }

        Require(!AgentTaskTransitions.CanTransition(AgentTaskState.Queued, AgentTaskState.Queued),
            "同状态不算转移。");
        RequireThrows(() => AgentTaskTransitions.EnsureTransition(AgentTaskState.Succeeded, AgentTaskState.Running),
            "非法转移必须抛 InvalidOperationException。");
        return Task.CompletedTask;
    }

    internal static async Task ResultCaptureTests()
    {
        await using var runtime = new AgentTaskRuntime(
            new CompletingExecutor("  line1\r\nline2\u0001  ", 250, 3), FixedNow, maxResultChars: 64);

        var accepted = runtime.Submit(Request("t-capture", capture: true));
        Require(accepted.Accepted && accepted.Task is not null, "任务应被受理。");
        Require(accepted.Task!.State == AgentTaskState.Queued, "受理快照必须是 Queued。");

        var task = await WaitTerminalAsync(runtime, "t-capture");
        Require(task.State == AgentTaskState.Succeeded, "成功任务终态应为 Succeeded。");
        Require(task.DurationMs == 250 && task.ToolCalls == 3, "耗时应取执行器的报告值。");
        Require(task.ResultSummary == "line1\nline2", $"正文应清洗控制字符与 CRLF，实际：{task.ResultSummary}。");
        Require(!task.ResultTruncated && task.ResultChars == "line1\nline2".Length, "短正文不应被截断。");

        var longText = new string('x', 5000);
        await using var bounded = new AgentTaskRuntime(
            new CompletingExecutor(longText, 1, 0), FixedNow, maxResultChars: 64);
        bounded.Submit(Request("t-bounded", capture: true));
        var truncated = await WaitTerminalAsync(bounded, "t-bounded");
        Require(truncated.State == AgentTaskState.Succeeded, "长正文任务也应成功。");
        Require(truncated.ResultTruncated, "超过上限必须标记截断。");
        Require(truncated.ResultSummary?.Length == 64, "正文必须截到上限。");
        Require(truncated.ResultChars == 5000, "Chars 必须报告清洗后的原始长度。");

        await using var noCapture = new AgentTaskRuntime(
            new CompletingExecutor(longText, 1, 0), FixedNow);
        noCapture.Submit(Request("t-no-capture"));
        var quiet = await WaitTerminalAsync(noCapture, "t-no-capture");
        Require(quiet.ResultSummary is null && quiet.ResultChars == 0, "未声明采集时不得保存正文。");
    }

    internal static async Task CancelTests()
    {
        var executor = new BlockingExecutor();
        await using var runtime = new AgentTaskRuntime(executor, FixedNow);
        runtime.Submit(Request("t-cancel"));
        await WaitStateAsync(runtime, "t-cancel", AgentTaskState.Running);

        Require(runtime.Cancel("t-cancel") == AgentTaskCancelOutcome.Requested, "第一次取消应受理。");
        var cancelled = await WaitTerminalAsync(runtime, "t-cancel");
        Require(cancelled.State == AgentTaskState.Cancelled, "取消后终态应为 Cancelled。");
        Require(cancelled.ErrorCode == "cancelled", "取消的 errorCode 必须是 cancelled。");
        Require(cancelled.CancellationRequested, "快照应记录收到过取消。");
        Require(executor.CancellationObserved, "执行器必须收到取消令牌。");
        Require(runtime.Cancel("t-cancel") == AgentTaskCancelOutcome.TooLate, "终态后再取消必须是 TooLate。");
        Require(runtime.Cancel("t-missing") == AgentTaskCancelOutcome.NotFound, "不存在的任务必须 NotFound。");

        var blocker = new BlockingExecutor();
        await using var second = new AgentTaskRuntime(blocker, FixedNow);
        second.Submit(Request("t-cancel-early"));
        Require(second.Cancel("t-cancel-early") is AgentTaskCancelOutcome.Requested or AgentTaskCancelOutcome.AlreadyRequested,
            "排队/运行中的任务都应能受理取消。");
        var early = await WaitTerminalAsync(second, "t-cancel-early");
        Require(early.State is AgentTaskState.Cancelled or AgentTaskState.Succeeded or AgentTaskState.Failed,
            "取消竞态下终态必须是三选一，不能卡在 CancelRequested。");
    }

    internal static async Task TimeoutTests()
    {
        var executor = new BlockingExecutor();
        await using var runtime = new AgentTaskRuntime(executor, FixedNow);
        runtime.Submit(Request("t-timeout", timeout: TimeSpan.FromMilliseconds(100)));

        var task = await WaitTerminalAsync(runtime, "t-timeout");
        Require(task.State == AgentTaskState.TimedOut, $"超时终态应为 TimedOut，实际 {task.State}。");
        Require(task.ErrorCode == "timeout", "超时的 errorCode 必须是 timeout（与取消区分）。");
        Require(!task.CancellationRequested, "超时不是用户取消，CancellationRequested 必须为假。");
        Require(executor.CancellationObserved, "超时后执行器令牌也必须被取消。");
    }

    internal static async Task FailureTests()
    {
        await using var runtime = new AgentTaskRuntime(new FailingExecutor(), FixedNow);
        runtime.Submit(Request("t-fail"));
        var failed = await WaitTerminalAsync(runtime, "t-fail");
        Require(failed.State == AgentTaskState.Failed, "Ok=false 应映射为 Failed。");
        Require(failed.ErrorCode == "agent_failed", "未给原因码时默认 agent_failed。");

        await using var rejected = new AgentTaskRuntime(new ThrowingExecutor(), FixedNow);
        rejected.Submit(Request("t-reject"));
        var invalid = await WaitTerminalAsync(rejected, "t-reject");
        Require(invalid.State == AgentTaskState.Failed, "执行器抛异常应映射为 Failed。");
        Require(invalid.ErrorCode == "agent_rejected", "InvalidOperationException 应映射为 agent_rejected。");
    }

    internal static Task CapacityTests()
    {
        var executor = new BlockingExecutor();
        var runtime = new AgentTaskRuntime(executor, FixedNow, maxActive: 1);
        runtime.Submit(Request("t-first"));
        var second = runtime.Submit(Request("t-second"));
        Require(!second.Accepted && second.Rejection == AgentTaskRejection.CapacityExceeded,
            "超过并发上限必须拒绝。");
        var duplicate = runtime.Submit(Request("t-first"));
        Require(!duplicate.Accepted && duplicate.Rejection == AgentTaskRejection.DuplicateId,
            "重复 id 必须拒绝。");
        var counts = runtime.Counts();
        Require(counts.Active == 1 && counts.Total == 1,
            $"Counts 的顺序必须是（活跃, 总数），实际 active={counts.Active} total={counts.Total}。");
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    internal static async Task ForgetTests()
    {
        await using var runtime = new AgentTaskRuntime(new CompletingExecutor("ok", 1, 0), FixedNow);
        runtime.Submit(Request("t-forget"));
        var task = await WaitTerminalAsync(runtime, "t-forget");
        Require(task.State == AgentTaskState.Succeeded, "先让任务成功。");
        var counts = runtime.Counts();
        Require(counts.Active == 0 && counts.Total == 1,
            $"终态后活跃数必须归零，实际 active={counts.Active} total={counts.Total}。");
        Require(runtime.Forget("t-forget"), "终态记录应允许淘汰。");
        Require(runtime.Get("t-forget") is null, "淘汰后必须查不到。");

        var blocker = new BlockingExecutor();
        await using var running = new AgentTaskRuntime(blocker, FixedNow);
        running.Submit(Request("t-running"));
        await WaitStateAsync(running, "t-running", AgentTaskState.Running);
        Require(!running.Forget("t-running"), "运行中的任务绝不能被淘汰。");
    }

    private static AgentTaskRequest Request(string id, TimeSpan? timeout = null, bool capture = false) => new()
    {
        Id = id,
        SourceKey = "panel:probe",
        SessionId = "session-1",
        Prompt = "synthetic task prompt",
        Target = "server",
        Timeout = timeout ?? TimeSpan.FromSeconds(10),
        CaptureResult = capture
    };

    private static async Task<AgentTaskSnapshot> WaitTerminalAsync(AgentTaskRuntime runtime, string id)
    {
        var snapshot = await WaitForAsync(
            () => runtime.Get(id) is { } task && AgentTaskTransitions.IsTerminal(task.State) ? task : null,
            $"任务 {id} 进入终态");
        return snapshot!;
    }

    private static async Task WaitStateAsync(AgentTaskRuntime runtime, string id, AgentTaskState state)
        => await WaitForAsync(
            () => runtime.Get(id)?.State == state ? (object?)true : null,
            $"任务 {id} 进入 {state}");

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, string what) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var value = probe();
            if (value is not null) return value;
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException($"等待超时：{what}");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class CompletingExecutor(string text, long durationMs, int toolCalls) : IAgentTaskExecutor
    {
        public Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
            => Task.FromResult(new AgentTaskExecution
            {
                Ok = true,
                Text = text,
                DurationMs = durationMs,
                ToolCalls = toolCalls
            });
    }

    private sealed class BlockingExecutor : IAgentTaskExecutor
    {
        private int _calls;

        internal bool CancellationObserved { get; private set; }

        internal int Calls => Volatile.Read(ref _calls);

        public async Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            return new AgentTaskExecution { Ok = true };
        }
    }

    private sealed class FailingExecutor : IAgentTaskExecutor
    {
        public Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
            => Task.FromResult(new AgentTaskExecution { Ok = false, DurationMs = 5 });
    }

    private sealed class ThrowingExecutor : IAgentTaskExecutor
    {
        public Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
            => Task.FromException<AgentTaskExecution>(new InvalidOperationException("synthetic rejection"));
    }
}

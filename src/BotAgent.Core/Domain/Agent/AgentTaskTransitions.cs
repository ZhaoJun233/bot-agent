using System.Linq;

namespace BotAgent.Domain.Agent;

/// <summary>
/// 显式任务的状态转移表（#114）：<b>纯函数</b> —— 不读时钟、不碰 I/O、不认识通道。
///
/// 设计要点（每一条都对应一个旧实现的坑）：
///   • 终态不可再转移 —— 原来靠适配层里一句 <c>if (Terminal(state)) return;</c> 兜底，现在写成表，
///     迟到的完成结果无法覆盖取消/超时；
///   • <c>CancelRequested → Succeeded/Failed</c> 是<b>允许</b>的：取消是请求不是事实，
///     执行器可能在取消生效前已经干完，这时如实上报完成，不冒报“取消成功”；
///   • <c>Queued → TimedOut</c> 也是允许的：超时从受理开始算，包含排队时间。
/// </summary>
public static class AgentTaskTransitions
{
    private static readonly AgentTaskState[] QueuedTargets =
    [
        AgentTaskState.Running, AgentTaskState.CancelRequested,
        AgentTaskState.Cancelled, AgentTaskState.TimedOut
    ];

    private static readonly AgentTaskState[] RunningTargets =
    [
        AgentTaskState.Succeeded, AgentTaskState.Failed, AgentTaskState.Cancelled,
        AgentTaskState.TimedOut, AgentTaskState.CancelRequested, AgentTaskState.WaitingApproval
    ];

    private static readonly AgentTaskState[] WaitingApprovalTargets =
    [
        AgentTaskState.Running, AgentTaskState.Cancelled,
        AgentTaskState.Failed, AgentTaskState.TimedOut
    ];

    private static readonly AgentTaskState[] CancelRequestedTargets =
    [
        AgentTaskState.Cancelled, AgentTaskState.Succeeded,
        AgentTaskState.Failed, AgentTaskState.TimedOut
    ];

    private static readonly AgentTaskState[] NoTargets = [];

    private static readonly AgentTaskState[] Terminal =
    [
        AgentTaskState.Succeeded, AgentTaskState.Failed, AgentTaskState.Cancelled,
        AgentTaskState.TimedOut, AgentTaskState.Unknown
    ];

    /// <summary>终态：没有任何出边。</summary>
    public static bool IsTerminal(AgentTaskState state) => Terminal.Contains(state);

    /// <summary>这个状态允许去的下一批状态（终态返回空）。</summary>
    public static IReadOnlyList<AgentTaskState> Allowed(AgentTaskState from) => from switch
    {
        AgentTaskState.Queued => QueuedTargets,
        AgentTaskState.Running => RunningTargets,
        AgentTaskState.WaitingApproval => WaitingApprovalTargets,
        AgentTaskState.CancelRequested => CancelRequestedTargets,
        _ => NoTargets
    };

    /// <summary>从 <paramref name="from"/> 能不能走到 <paramref name="to"/>（同状态也不算）。</summary>
    public static bool CanTransition(AgentTaskState from, AgentTaskState to)
        => from != to && Allowed(from).Contains(to);

    /// <summary>
    /// 断言式转移：非法即抛 <see cref="InvalidOperationException"/>。
    /// 运行时用 <see cref="CanTransition"/> 做惰性判断（并发下终态可能已被别人写入）。
    /// </summary>
    public static AgentTaskState EnsureTransition(AgentTaskState from, AgentTaskState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"非法的任务状态转移：{from} → {to}。");
        }

        return to;
    }
}

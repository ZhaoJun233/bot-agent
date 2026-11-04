namespace BotAgent.Domain.Agent;

/// <summary>
/// 显式任务的状态（#114）。数值前 8 个与 ZBA 对外契约 <c>src/BotAgent.Panel/ZbaContracts.cs</c> 的
/// <c>ZbaTaskState</c> <b>一一对应</b>，只有 <see cref="TimedOut"/> = 8 是运行时独有的：
/// ZBA 契约不新增枚举值，超时在通道层投影成 <c>Failed</c> + <c>errorCode=timeout</c>
/// （理由与风险见 <c>docs/decisions/0002-agent-task-runtime.md</c> §2.2）。
///
/// 状态只由 <c>AgentTaskRuntime</c> 推进；转移规则在 <see cref="AgentTaskTransitions"/>，
/// 非法转移一律拒绝 —— 通道层不许自己改状态。
/// </summary>
public enum AgentTaskState
{
    /// <summary>已受理、尚未开始执行。</summary>
    Queued = 0,

    /// <summary>执行器已启动。</summary>
    Running = 1,

    /// <summary>等人工审批（#117 才有人写；本轮只有转移规则）。</summary>
    WaitingApproval = 2,

    /// <summary>终态：执行器如实报告成功。</summary>
    Succeeded = 3,

    /// <summary>终态：执行失败（原因在 <c>ErrorCode</c>）。</summary>
    Failed = 4,

    /// <summary>取消已受理，执行器还没停下来。</summary>
    CancelRequested = 5,

    /// <summary>终态：用户取消。</summary>
    Cancelled = 6,

    /// <summary>终态：本实例确认不了结果（#116 重启恢复用；本轮不产生）。</summary>
    Unknown = 7,

    /// <summary>终态：硬截止到点，与用户取消区分。</summary>
    TimedOut = 8
}

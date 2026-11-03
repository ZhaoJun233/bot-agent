using BotAgent.Domain.Agent;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 显式任务的执行端口（#114）：Domain 只说"谁能执行一次任务"，
/// 真正跑 agent（模型 + 工具循环）的实现在宿主（<c>Adapters/Agent/AgentTaskExecutor</c>）。
///
/// 为什么要有端口：运行时（Engine）只依赖 Core，不能直接认识 <c>AgentCommandService</c>；
/// 有了端口，取消/超时/失败三条路径可以<b>不启动机器人</b>用假执行器测。
/// </summary>
public interface IAgentTaskExecutor
{
    /// <summary>
    /// 执行一次任务。<paramref name="ct"/> 是运行时给的<b>硬截止 + 取消</b>令牌：
    /// 到点或用户取消后执行器必须尽快停下（继续跑也不会被采纳为最终结果）。
    /// </summary>
    Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct);
}

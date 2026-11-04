using BotAgent.Domain.Agent;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 任务级审批闸门（#137）：运行时在**派发前**问一句“这次要不要人批”。
///
/// 返回 null = 不需要审批（照旧直接派发）；返回一条待批信息 = 任务停在
/// <see cref="AgentTaskState.WaitingApproval" />，执行器不会被调用，直到有人批准。
/// 判定放在端口后面：运行时本身不认识 bash、不认识工具白名单，也不认识“审批总开关”。
/// </summary>
public interface IAgentTaskApprovalGate
{
    AgentTaskApprovalRequest? Create(AgentTaskRequest request);
}

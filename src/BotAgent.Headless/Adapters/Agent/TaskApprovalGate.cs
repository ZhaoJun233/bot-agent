using BotAgent.Domain.Agent;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Agent;

/// <summary>
/// 任务级前置审批闸门（#137）：任务在派发前声明的工具作用域里含高风险工具时，
/// 先停在 <see cref="AgentTaskState.WaitingApproval" />，等人通过 ZBA 决策后再放行。
///
/// 为什么是任务级而不是工具级：工具级挂起要把服务器 agent 的循环（消息栈、步数、
/// 当前调用、租约）变成可持久化状态才能跨重启恢复，成本远超一次审批的价值；任务级
/// 闸门在派发前判定，<b>批准前执行器一次都不会被调用</b>（零副作用），而且天然可持久化
/// （任务本来就有一行台账）。代价是粒度粗：声明了高危工具的任务即使最后没真的用上，
/// 也要先批一次 —— 这是刻意的 fail-closed 取舍，见 docs/architecture/approval-and-isolation.md。
///
/// 审批总开关关掉时直接返回 null，行为与改造前逐字一致：不会凭空多出待批任务。
/// </summary>
internal sealed class TaskApprovalGate(Func<DateTimeOffset> now, Func<bool> enabled) : IAgentTaskApprovalGate
{
    /// <summary>
    /// 需要先批的工具名。与 ToolPolicy 里 HighRisk 类别同一个直觉：会改文件、会跑命令、
    /// 会动容器的都算；只读工具（read 等）不拦，否则每个任务都要批一次，闸门会被绕过。
    /// </summary>
    private static readonly string[] HighRiskTools = ["bash", "write", "docker"];

    public AgentTaskApprovalRequest? Create(AgentTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!enabled()) return null;
        if (request.ToolScope is not { Count: > 0 } scope) return null;

        foreach (var tool in scope)
        {
            foreach (var risky in HighRiskTools)
            {
                if (!string.Equals(tool, risky, StringComparison.OrdinalIgnoreCase)) continue;
                return new AgentTaskApprovalRequest(
                    ApprovalFlow.NewRequestId(Random.Shared.Next),
                    risky,
                    $"任务声明了高风险工具 {risky}，需要先批准才会执行",
                    now().AddSeconds(ApprovalFlow.TtlSeconds));
            }
        }

        return null;
    }
}

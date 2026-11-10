using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Services.Agent;

namespace BotAgent.Adapters.Agent;

/// <summary>
/// 把服务器内置 agent（<see cref="AgentCommandService"/>.RunPanelAgentDirectAsync）接到
/// <see cref="IAgentTaskExecutor"/> 端口上（#114 / ADR-0002）。
///
/// 只做三件事：把运行时给的截止换算成既有参数、调用既有执行入口、把结果翻译成
/// <see cref="AgentTaskExecution"/>。<b>不判状态、不写台账、不发事件</b>。
/// 抛出的异常由运行时按"取消 / 超时 / 失败"三条路径收口（InvalidOperationException → agent_rejected）。
/// </summary>
internal sealed class AgentTaskExecutor(AgentCommandService agent) : IAgentTaskExecutor
{
    /// <summary>与既有执行入口一致：10 秒下限、900 秒上限（面板同样的范围）。</summary>
    private const int MinSeconds = 10;
    private const int MaxSeconds = 900;

    public async Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
    {
        var seconds = Math.Clamp((int)Math.Ceiling(request.Timeout.TotalSeconds), MinSeconds, MaxSeconds);
        var task = await agent.RunPanelAgentDirectAsync(
            request.SourceKey, request.SessionId, request.Prompt, seconds, request.Target, ct,
            request.ToolScope, request.PythonToolLeases).ConfigureAwait(false);
        return new AgentTaskExecution
        {
            Ok = task.Ok,
            Text = task.Text,
            DurationMs = task.DurationMs,
            ToolCalls = task.ToolCalls,
            CancelRequested = task.CancelRequested
        };
    }
}

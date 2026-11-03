namespace BotAgent.Domain.Agent;

/// <summary>
/// 执行器对一次显式任务的报告（#114）。<b>执行器不决定状态</b> —— 它只说"成没成、为什么、多久、几步"，
/// 状态由运行时按转移表推进（取消/超时优先于这里的 <see cref="Ok"/>）。
/// </summary>
public sealed record AgentTaskExecution
{
    /// <summary>执行器认为任务成功了。</summary>
    public required bool Ok { get; init; }

    /// <summary>最终答复全文（未清洗，运行时清洗并截断后才会进快照）。</summary>
    public string? Text { get; init; }

    /// <summary>失败原因码（例如 <c>agent_failed</c> / <c>agent_rejected</c>）；成功时为空。</summary>
    public string? ErrorCode { get; init; }

    /// <summary>执行耗时（毫秒）。</summary>
    public long DurationMs { get; init; }

    /// <summary>工具调用次数。</summary>
    public int ToolCalls { get; init; }

    /// <summary>执行器自己观察到"收到过取消"（例如任务对象上的取消标记）。</summary>
    public bool CancelRequested { get; init; }
}

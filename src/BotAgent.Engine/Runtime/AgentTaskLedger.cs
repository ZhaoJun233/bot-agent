using BotAgent.Domain.Agent;

namespace BotAgent.Engine.Runtime;

/// <summary>
/// 任务台账行（#116）：一份任务一行，<b>只存形状与计数</b> —— 提示词、回答正文、工具参数都不进来。
/// 谁把它写到哪里由实现决定（生产是 SQLite 的 <c>agent_tasks</c> 表，测试是内存实现）。
/// </summary>
public readonly record struct AgentTaskLedgerEntry
{
    public required string Id { get; init; }
    public required AgentTaskState State { get; init; }
    public string? ErrorCode { get; init; }
    public required string SourceKey { get; init; }
    public required string SessionId { get; init; }
    public required string Target { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public long? DurationMs { get; init; }
    public int ToolCalls { get; init; }
    public bool CancellationRequested { get; init; }
    public int ResultChars { get; init; }
    public bool ResultTruncated { get; init; }

    /// <summary>第几次宿主启动写的这一行（用于重启对账：同 boot_seq 的行说明上一进程没来得及收尾）。</summary>
    public long BootSeq { get; init; }

    /// <summary>从运行时快照投影（不接受 <c>ResultSummary</c>：正文不落库）。</summary>
    public static AgentTaskLedgerEntry From(AgentTaskSnapshot snapshot, long bootSeq)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new AgentTaskLedgerEntry
        {
            Id = snapshot.Id,
            State = snapshot.State,
            ErrorCode = snapshot.ErrorCode,
            SourceKey = snapshot.SourceKey,
            SessionId = snapshot.SessionId,
            Target = snapshot.Target,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.UpdatedAt,
            DurationMs = snapshot.DurationMs,
            ToolCalls = snapshot.ToolCalls,
            CancellationRequested = snapshot.CancellationRequested,
            ResultChars = snapshot.ResultChars,
            ResultTruncated = snapshot.ResultTruncated,
            BootSeq = bootSeq
        };
    }
}

/// <summary>
/// 任务台账的存储端口（#116）。运行时只管"该写什么"，不关心写到哪里、怎么迁移。
///
/// 三条约定：
///   • 实现必须<b>幂等</b>（同 id 重复写就是覆盖），因为状态每变一次就写一次；
///   • 实现<b>不许抛</b>给状态机（写失败只能记日志），台账不能决定任务成败；
///   • <see cref="LoadRecent" /> 只返回最近的一批，恢复策略由运行时决定（ADR-0003）。
/// </summary>
public interface IAgentTaskLedger
{
    /// <summary>写入/覆盖一行。</summary>
    void Save(AgentTaskLedgerEntry entry);

    /// <summary>按更新时间倒序取最近 <paramref name="limit" /> 行。</summary>
    IReadOnlyList<AgentTaskLedgerEntry> LoadRecent(int limit);

    /// <summary>只保留最近 <paramref name="keep" /> 行，返回删掉的条数。</summary>
    int Prune(int keep);
}

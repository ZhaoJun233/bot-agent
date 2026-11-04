using BotAgent.Domain.Agent;

namespace BotAgent.Engine.Runtime;

/// <summary>
/// 任务快照（#114）：运行时的<b>只读投影</b>，通道拿它做自己的 DTO（ZBA 的 <c>ZbaTaskSummary</c>）。
/// 不包含提示词；正文只在通道声明 <c>CaptureResult</c> 时才有，且已清洗 + 截断。
/// </summary>
public sealed record AgentTaskSnapshot
{
    public required string Id { get; init; }
    public required string SourceKey { get; init; }
    public required string SessionId { get; init; }
    public required string Target { get; init; }
    public required AgentTaskState State { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public long? DurationMs { get; init; }
    public int ToolCalls { get; init; }
    public string? ErrorCode { get; init; }
    public bool CancellationRequested { get; init; }
    public string? ResultSummary { get; init; }
    public bool ResultTruncated { get; init; }
    public int ResultChars { get; init; }

    // #137：待批信息（只有 WaitingApproval 期间有值）；通道用它回答“在等哪一张单子”。
    public string? ApprovalRequestId { get; init; }
    public string? ApprovalToolId { get; init; }
    public string? ApprovalSummary { get; init; }
    public DateTimeOffset? ApprovalExpiresAt { get; init; }
}

/// <summary>提交被拒绝的原因（通道自己翻译成 HTTP 状态码与错误码）。</summary>
public enum AgentTaskRejection
{
    /// <summary>并发任务已达上限。</summary>
    CapacityExceeded,

    /// <summary>同 id 的任务已存在。</summary>
    DuplicateId,

    /// <summary>运行时正在停机。</summary>
    Disposed
}

/// <summary>提交结果：要么受理（带 Queued 快照），要么被拒（带原因）。</summary>
public sealed record AgentTaskSubmitResult
{
    public required bool Accepted { get; init; }
    public AgentTaskRejection? Rejection { get; init; }
    public AgentTaskSnapshot? Task { get; init; }

    public static AgentTaskSubmitResult AcceptedTask(AgentTaskSnapshot task)
        => new() { Accepted = true, Task = task };

    public static AgentTaskSubmitResult Rejected(AgentTaskRejection rejection)
        => new() { Accepted = false, Rejection = rejection };
}

/// <summary>取消请求的结果：已受理 / 早就受理过 / 太晚 / 查无此任务。</summary>
public enum AgentTaskCancelOutcome
{
    /// <summary>本次受理了取消。</summary>
    Requested,

    /// <summary>之前就受理过取消。</summary>
    AlreadyRequested,

    /// <summary>任务已经到终态，取消太晚。</summary>
    TooLate,

    /// <summary>查无此任务。</summary>
    NotFound
}

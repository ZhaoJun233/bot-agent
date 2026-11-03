namespace BotAgent.Domain.Agent;

/// <summary>
/// 一次显式任务的执行请求（#114）。这是运行时交给执行器的<b>全部</b>输入 ——
/// 运行时自己只保留 id / 来源 / 会话 / 目标 / 超时 / 是否采集正文，<b>不保存提示词</b>。
/// </summary>
public sealed record AgentTaskRequest
{
    /// <summary>任务 id（由通道生成，运行时不改；重复不可提交）。</summary>
    public required string Id { get; init; }

    /// <summary>来源 key（例如 <c>panel:workspace</c>）：决定结果正文能不能被采集。</summary>
    public required string SourceKey { get; init; }

    /// <summary>执行会话 id（通道已经校验过它存在且后端是 server）。</summary>
    public required string SessionId { get; init; }

    /// <summary>任务正文（提示词）。运行时只在提交瞬间持有，不写进快照。</summary>
    public required string Prompt { get; init; }

    /// <summary>执行目标（例如 <c>server</c>）。</summary>
    public string Target { get; init; } = "server";

    /// <summary>硬截止（含排队时间）。到点由运行时置 <c>TimedOut</c> 并取消执行器令牌。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>是否采集结果正文（只有通道说可以时才存，且仍有长度上限）。</summary>
    public bool CaptureResult { get; init; }
}

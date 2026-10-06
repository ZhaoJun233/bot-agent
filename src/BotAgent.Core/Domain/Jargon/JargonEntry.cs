using System;

namespace BotAgent.Domain.Jargon;

/// <summary>黑话/俚语审核与生命周期状态（纯状态、零 IO）。</summary>
public enum JargonStatus
{
    /// <summary>待审核（AI 自动捕获提炼）</summary>
    Pending = 0,

    /// <summary>已确认（已审核放行，可注入提示词与知识上下文）</summary>
    Confirmed = 1,

    /// <summary>已拒绝（标记为普通词汇或不当用语，不再提示）</summary>
    Rejected = 2,

    /// <summary>手动录入（管理员主动添加）</summary>
    Manual = 3
}

/// <summary>群聊黑话/俚语实体（纯领域模型、零 IO）。</summary>
public sealed class JargonEntry
{
    public long Id { get; init; }

    /// <summary>生效会话范围（如 group:10001 或 global 全局通用）。</summary>
    public required string Scope { get; init; }

    /// <summary>黑话词汇/短语/流行梗。</summary>
    public required string Phrase { get; init; }

    /// <summary>含义与语境解释。</summary>
    public required string Meaning { get; set; }

    /// <summary>审核与生命周期状态。</summary>
    public JargonStatus Status { get; set; } = JargonStatus.Pending;

    /// <summary>捕获或被提及的频次。</summary>
    public int HitCount { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

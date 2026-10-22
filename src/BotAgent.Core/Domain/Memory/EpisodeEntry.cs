using System;
using System.Collections.Generic;

namespace BotAgent.Domain.Memory;

/// <summary>
/// 长期记忆事件片段（Episode Entry，纯领域模型、零 IO）。
/// 借鉴 MaiBot A-Memorix 认知架构：将长篇原始历史提炼为具备时空锚点、参与者与事实摘要的记忆切片。
/// </summary>
public sealed class EpisodeEntry
{
    public long Id { get; init; }

    /// <summary>会话作用域（如 group:10001 或 direct:user:10001）。</summary>
    public required string Scope { get; init; }

    /// <summary>事件主题/简述（例如：“关于加班吐槽与换工作讨论”）。</summary>
    public required string Title { get; init; }

    /// <summary>事件核心事实与结论摘要。</summary>
    public required string Summary { get; init; }

    /// <summary>参与该事件的实体标识列表（脱敏 ID 或虚拟代号）。</summary>
    public IReadOnlyList<string> Participants { get; init; } = Array.Empty<string>();

    /// <summary>主题标签（如“工作”, “技术”, “考研”）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>重要度评级（1~5，越高越不易衰减）。</summary>
    public int Importance { get; init; } = 1;

    /// <summary>事件发生的物理时间点。</summary>
    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

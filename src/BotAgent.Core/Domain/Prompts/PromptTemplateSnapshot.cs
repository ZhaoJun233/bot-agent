using System;

namespace BotAgent.Domain.Prompts;

/// <summary>
/// Prompt 模板版本快照实体（纯领域模型、零 IO）。
/// 借鉴 MaiBot Prompt 版本化与出厂回滚机制。
/// </summary>
public sealed class PromptTemplateSnapshot
{
    public long Id { get; init; }

    /// <summary>模板标识键（如 "system_prompt"、"persona"、"agent_prompt"）。</summary>
    public required string Key { get; init; }

    /// <summary>版本唯一 ID（如 "v1", "v2" 或时间戳版本）。</summary>
    public required string VersionId { get; init; }

    /// <summary>模板内容文本。</summary>
    public required string Content { get; init; }

    /// <summary>版本说明标签（如“出厂内置”、“增加幽默口吻”）。</summary>
    public string? Label { get; init; }

    /// <summary>当前是否处于激活使用状态。</summary>
    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

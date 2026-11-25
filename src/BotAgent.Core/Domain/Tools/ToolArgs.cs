using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace BotAgent.Domain.Tools;

/// <summary>校验结论（结构化；原因码短、可日志、可断言）。</summary>
public readonly record struct ArgsVerdict(bool Ok, string ReasonCode, string? Detail = null)
{
    public static ArgsVerdict Pass { get; } = new(true, "ok");

    public static ArgsVerdict Fail(string reasonCode, string? detail = null) => new(false, reasonCode, detail);
}

/// <summary>一个字段的契约：名字、类型、必填、长度范围（长度只对字符串有意义）。</summary>
/// <param name="Name">字段名（模型要写的那个）。</param>
/// <param name="Required">必填。</param>
/// <param name="MinLength">最短长度（0 = 不限制）。</param>
/// <param name="MaxLength">最长长度（0 = 不限制）。</param>
public sealed record ArgField(string Name, bool Required = true, int MinLength = 0, int MaxLength = 0);

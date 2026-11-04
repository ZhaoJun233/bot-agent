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

/// <summary>
/// **参数契约的执行前校验**（general-agent-platform-plan.md §5.2 第 3 步 / §9.3 第 4 格）。
///
/// 为什么要有它：模型写的参数今天只有“中文说明”约束，写错字段名/漏必填时不会有人拦，
/// 于是可能拿着半截参数去执行（读一个空 URL、搜一个空词）。这里把契约写成结构，
/// 执行前先过一次 —— **不认识的字段 / 缺必填 / 长度越界一律拒绝**（fail-closed），并回结构化原因码。
///
/// 覆盖面（**如实说明，不含糊**）：
///   · 目前只覆盖**聊天那路会当场执行的两个只读工具**（web.search / web.read）——
///     它们的执行体已经收口在 <c>InlineTurnTools</c>，校验接得上；
///   · 其余工具（音乐/语音/表情/戳，以及 `//` 那一族）的参数校验随“执行搬迁”一起做
///     （§10.2 坑 3：搬执行要逐条对照 fallback，不能顺手改语义）；
///   · **`//` 路径刻意保持宽松**（§11 第 3 条）：它有自己的面板开关与工作目录边界，
///     不在这一批收紧（收紧它就是行为变更）。
/// </summary>
public static class ToolArgs
{
    /// <summary>字段契约表（只收已接入校验的工具）。</summary>
    private static readonly Dictionary<string, ArgField[]> Contracts = new(StringComparer.Ordinal)
    {
        ["web.search"] = new[] { new ArgField("query", Required: true, MinLength: 2, MaxLength: 120) },
        ["web.read"] = new[] { new ArgField("url", Required: true, MinLength: 8, MaxLength: 2000) },
    };

    /// <summary>这个工具有没有结构化契约（没有 = 不校验，调用方按老路走）。</summary>
    public static bool HasContract(string? toolId)
        => Contracts.ContainsKey((toolId ?? string.Empty).Trim());

    /// <summary>
    /// 校验一次调用的参数。
    /// 拒绝的三种情形各有原因码：<c>unknown_field</c> / <c>missing_required</c> / <c>bad_length</c>。
    /// 没有契约的工具直接 <see cref="ArgsVerdict.Pass" />（保持宽松 —— 校验范围是逐步扩的，不是一刀切）。
    /// </summary>
    public static ArgsVerdict Validate(string? toolId, JsonObject? args)
    {
        var id = (toolId ?? string.Empty).Trim();
        if (!Contracts.TryGetValue(id, out var fields))
        {
            return ArgsVerdict.Pass;
        }

        args ??= new JsonObject();

        // ① 不认识的字段：模型爱编新字段名（写 search 写成 q、写成 keyword）——
        //    宁可拒绝让它重写，也不要“猜一个”然后拿错参数去执行。
        var known = fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in args)
        {
            if (!known.Contains(pair.Key))
            {
                return ArgsVerdict.Fail("unknown_field", pair.Key);
            }
        }

        foreach (var field in fields)
        {
            var node = args[field.Name];
            var text = node?.GetValue<string>()?.Trim() ?? string.Empty;

            if (field.Required && text.Length == 0)
            {
                return ArgsVerdict.Fail("missing_required", field.Name);
            }

            if (text.Length == 0)
            {
                continue;
            }

            if (field.MinLength > 0 && text.Length < field.MinLength)
            {
                return ArgsVerdict.Fail("bad_length", $"{field.Name}<{field.MinLength}");
            }

            if (field.MaxLength > 0 && text.Length > field.MaxLength)
            {
                return ArgsVerdict.Fail("bad_length", $"{field.Name}>{field.MaxLength}");
            }
        }

        // ② web.read 的地址形状：只收 http(s)（别让模型把 file:// 或别的协议递进来）
        if (id == "web.read")
        {
            var url = args["url"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return ArgsVerdict.Fail("bad_scheme", "url");
            }
        }

        return ArgsVerdict.Pass;
    }
}

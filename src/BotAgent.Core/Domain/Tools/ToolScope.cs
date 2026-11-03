using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Tools;

/// <summary>
/// 一次执行的**工具作用域**（#136）：声明“这次只允许这些工具”。
///
/// 三条语义，别改：
///   · **只能收窄、不能扩权**：作用域永远与配置白名单取交集，写在里面的名字不可能让某个工具突然可用；
///   · **未知名字静默忽略**：点到不存在的工具不是错误（宁可窄），但也不会凭空生效；
///   · **未声明 = 不限**：<see cref="Unrestricted" /> 表示沿用配置白名单 —— 这是所有旧调用的行为。
///
/// 它不读配置、不读时钟、不查目录：给定一串 id 就是纯函数，于是调用方（运行时 / 通道 / 探针）都能自己算。
/// </summary>
public sealed class ToolScope
{
    private readonly HashSet<string>? _ids;

    private ToolScope(HashSet<string>? ids) => _ids = ids;

    /// <summary>不限（= 沿用配置白名单）。</summary>
    public static ToolScope Unrestricted { get; } = new(null);

    /// <summary>按声明的 id 造一份作用域；null / 空 = 不限，重复与空白项自动去掉。</summary>
    public static ToolScope FromIds(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return Unrestricted;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var trimmed = (id ?? string.Empty).Trim();
            if (trimmed.Length > 0)
            {
                set.Add(trimmed);
            }
        }

        return set.Count == 0 ? Unrestricted : new ToolScope(set);
    }

    /// <summary>true = 没声明作用域（沿用配置白名单）。</summary>
    public bool IsUnrestricted => _ids is null;

    /// <summary>声明过的 id（排序后，给日志/回执用；不限时为空）。</summary>
    public IReadOnlyList<string> Ids
        => _ids is null ? Array.Empty<string>() : _ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>这次是否允许某个工具（不限时恒 true，判定仍归调用方的白名单/闸门）。</summary>
    public bool Allows(string? toolId) => _ids is null || _ids.Contains(toolId ?? string.Empty);

    /// <summary>把配置白名单收窄成这次真正可用的集合（只保留作用域里有的；不限时原样返回一份拷贝）。</summary>
    public HashSet<string> Restrict(IEnumerable<string> configured)
    {
        var source = configured ?? Array.Empty<string>();
        return new HashSet<string>(_ids is null ? source : source.Where(Allows), StringComparer.Ordinal);
    }

    public override string ToString() => _ids is null ? "unrestricted" : $"scope({_ids.Count})";
}

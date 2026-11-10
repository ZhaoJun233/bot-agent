using BotAgent.Domain.Mcp;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Tools;

namespace BotAgent.Services.Tools;

/// <summary>
/// **统一查询面**（#115）：把内建三族（聊天 10 / QQ 动作 10 / 服务器 6）与运行时发现的 MCP 工具
/// 放进同一张只读表，回答“系统到底有哪些工具、这条来自哪里”。
///
/// 三条边界，别越：
///   · **只读视图**：不参与任何判定（可见性在 <see cref="ToolVisibility" />，放行/审批在 ToolGate）；
///   · **内建计数不变**：<see cref="Builtin" /> 的条数仍是被探针钉住的 26（10/10/6），
///     MCP 是**追加**的另一个来源（<see cref="ToolOrigin.Mcp" />），不会让“发现成功”看起来像“内建多了一条”；
///   · **MCP 描述是不可信数据**：只投影成给人看的短摘要，参数契约以远端 schema 为准（执行前另行校验），
///     所以 <see cref="ToolSpec.Parameters" /> 对 MCP 固定写一句“由服务器声明”。
///
/// 它是纯投影（给定目录 + 给定的 MCP 快照），不持有 MCP 连接、不刷新快照 ——
/// 调用方（面板 / 探针）每次用当前快照现建一份，于是不存在“查询面里还留着上次发现的工具”这种事。
/// </summary>
public sealed class ToolInventory
{
    private readonly Dictionary<string, ToolSpec> _byId;

    private ToolInventory(IReadOnlyList<ToolSpec> builtin, IReadOnlyList<ToolSpec> mcp)
    {
        BuiltinSpecs = builtin;
        McpSpecs = mcp;
        _byId = new Dictionary<string, ToolSpec>(builtin.Count + mcp.Count, StringComparer.Ordinal);
        var duplicates = new List<string>();
        foreach (var spec in builtin.Concat(mcp))
        {
            if (!_byId.TryAdd(spec.Id, spec))
            {
                duplicates.Add(spec.Id);
            }
        }

        Duplicates = duplicates;
        Entries = builtin.Concat(mcp).ToArray();
    }

    /// <summary>没有 MCP 时的目录（内建三族；条数=26）。</summary>
    public static ToolInventory Builtin { get; } = Create(ToolDirectory.Builtin, Array.Empty<McpToolInfo>());

    /// <summary>按当前目录 + 当前 MCP 快照投影一份（<paramref name="mcpTools" /> 为 null = 没有 MCP）。</summary>
    public static ToolInventory Create(ToolDirectory directory, IReadOnlyList<McpToolInfo>? mcpTools)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var mcp = (mcpTools ?? Array.Empty<McpToolInfo>()).Select(Project).ToArray();
        return new ToolInventory(directory.Specs, mcp);
    }

    /// <summary>内建工具声明（顺序 = 聊天 / QQ 动作 / 服务器工具）。</summary>
    public IReadOnlyList<ToolSpec> BuiltinSpecs { get; }

    /// <summary>MCP 工具投影（顺序 = 发现结果顺序）。</summary>
    public IReadOnlyList<ToolSpec> McpSpecs { get; }

    /// <summary>全部条目（内建在前、MCP 在后）。</summary>
    public IReadOnlyList<ToolSpec> Entries { get; }

    /// <summary>重名（内建与 MCP 撞名，或 MCP 自己重复）；空 = 表里每条 id 唯一。</summary>
    public IReadOnlyList<string> Duplicates { get; }

    public int BuiltinCount => BuiltinSpecs.Count;

    public int McpCount => McpSpecs.Count;

    public int Count => Entries.Count;

    /// <summary>按 id 查（未知返回 false；不抛异常，调用方据此 Fail-Closed）。</summary>
    public bool TryGet(string? id, out ToolSpec spec) => _byId.TryGetValue(id ?? string.Empty, out spec!);

    /// <summary>
    /// 把一个 MCP 发现结果投影成目录行。**描述只留短摘要**（外部来源不可信，面板与提示词不需要全文）；
    /// 类别固定 <see cref="ToolCategory.RemoteAgent" />（任何审批都不放开，只能由显式白名单点名）。
    /// </summary>
    private static ToolSpec Project(McpToolInfo info)
    {
        var summary = (info.Description ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (summary.Length > 120) summary = summary[..120] + "…";
        if (summary.Length == 0) summary = "MCP 工具（无描述）";
        return new ToolSpec(
            info.QualifiedName,
            info.IsLocalProcess ? ToolCategory.FileOrShell : ToolCategory.RemoteAgent,
            summary,
            info.ReadOnlyHint,
            "mcp",
            "（由 MCP 服务器声明，调用前按远端 schema 校验）",
            ToolDefaultPolicy.NamedOnly,
            PromptVisible: false,
            FeatureKey: string.Empty,
            Origin: ToolOrigin.Mcp);
    }
}

using System.Linq;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Tools;
using BotAgent.Services.Agent;

namespace BotAgent.Services.Tools;

/// <summary>
/// <c>//</c> 那路的 **QQ 动作**在统一目录里的投影（general-agent-platform-plan.md §5.2 第 1 步）。
///
/// 三条口径：
///   · **不复制目录本体**：动作名、档位、参数说明、中文别名仍然只有 <see cref="QqActionCatalog" /> 一份，
///     这里只是把它**映射**成 <see cref="ToolSpec" />（改目录 = 这一层自动跟着变）；
///   · **Id 加 <c>qq.</c> 前缀**（<c>qq.like</c> / <c>qq.poke</c> / …）：与聊天那路的
///     <c>poke.send</c> / <c>like</c> 之类名字分开，免得两族混在一个命名空间里；
///   · **档位落进 Default**：安全档 <see cref="ToolDefaultPolicy.SafeTierWhenEmpty" />、
///     危险档 <see cref="ToolDefaultPolicy.NamedOnly" /> —— “留空 = 只开安全档”这条既有语义变成数据，
///     不再只写在 <see cref="QqActionCatalog.ParseAllowed" /> 的注释里。
/// </summary>
public static class QqToolSpecs
{
    /// <summary>QQ 动作的执行者（<c>Services/Agent/SessionQqActionHost.cs</c>）。</summary>
    public const string ExecutorActions = "qq.actions";

    /// <summary>危险档的例外标注（面板与审计要显式看到，而不是靠“没人发现”）。</summary>
    public const string RiskyNote =
        "危险档：只在 // 路径上、且面板点名（AgentServerQqActions）才开；不随普通聊天开放";

    /// <summary>短说明（每个动作一行；漏写不崩，由 <see cref="MissingSummaries" /> 报给探针）。</summary>
    private static readonly Dictionary<string, string> Summaries = new(StringComparer.Ordinal)
    {
        ["like"] = "在 QQ 里给某人点赞（名片赞）",
        ["poke"] = "戳一戳某人",
        ["emoji_like"] = "给某条消息贴个表情回应",
        ["recall"] = "撤回一条消息",
        ["ban"] = "禁言 / 解除禁言（仅群）",
        ["kick"] = "把某人踢出群（仅群）",
        ["card"] = "改某人的群名片（仅群）",
        ["group_name"] = "改群名（仅群）",
        ["leave"] = "让机器人退群（或解散群）",
        ["send"] = "以机器人身份直接发一条消息",
    };

    /// <summary>目录缺短说明的动作名（探针要求为空；一般是目录里新加了动作而这里没跟上）。</summary>
    public static readonly IReadOnlyList<string> MissingSummaries =
        QqActionCatalog.All.Where(a => !Summaries.ContainsKey(a.Name)).Select(a => a.Name).ToArray();

    /// <summary>10 个动作（顺序 = <see cref="QqActionCatalog.All" /> 的顺序）。</summary>
    public static readonly IReadOnlyList<ToolSpec> All =
        QqActionCatalog.All.Select(From).ToArray();

    private static ToolSpec From(QqActionSpec action) => new(
        "qq." + action.Name,
        ToolCategory.SendMessage,
        Summaries.TryGetValue(action.Name, out var summary) ? summary : action.Params,
        ReadOnly: false,
        ExecutorActions,
        // 参数说明**逐字取自目录**（含中文别名与“写 this 表示本条”这类细节）—— 单一来源。
        action.Params,
        action.Risky ? ToolDefaultPolicy.NamedOnly : ToolDefaultPolicy.SafeTierWhenEmpty,
        action.Risky ? RiskyNote : null);
}

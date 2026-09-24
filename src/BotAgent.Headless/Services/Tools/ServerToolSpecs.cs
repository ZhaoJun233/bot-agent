using System.Linq;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Tools;

namespace BotAgent.Services.Tools;

/// <summary>
/// <c>//</c> 那路的**服务器工具**在统一目录里的投影（general-agent-platform-plan.md §5.2 第 2 步）。
///
/// 这里有一个**必须写明的例外**：<c>bash</c>/<c>write</c>/<c>read</c>/<c>docker</c> 落的是
/// <see cref="ToolCategory.FileOrShell" /> —— 也就是“任何审批都不放开”的 <c>AlwaysDenied</c> 档。
/// 它们是 <c>//</c> **专用**能力，有各自的授权边界（面板开关 + 工作目录 + 命令超时 + docker 两把锁），
/// 所以**保留现状**；但目录里要显式标出这条例外（<see cref="ToolSpec.Exception" />），
/// 而不是靠“没人发现”。普通聊天那一路**永远**不会因此拿到文件/shell 能力。
/// </summary>
public static class ServerToolSpecs
{
    /// <summary>服务器 agent 的执行者（<c>Services/Agent/ServerAgentRunner.cs</c> 的 switch）。</summary>
    public const string ExecutorAgent = "server.agent";

    /// <summary>FileOrShell 例外的统一标注（措辞固定，面板直接显示）。</summary>
    public const string ShellNote =
        "仅 // 路径（面板开关 + 工作目录 + 命令超时）；不随普通聊天开放，任何审批也放不开";

    /// <summary>联网抓取那条的例外标注。</summary>
    public const string FetchNote = "仅 // 路径；不随普通聊天开放（普通聊天走 web.read，另有 SSRF 防护与预算）";

    /// <summary>6 个工具（顺序 = 今天给模型的那份清单）。</summary>
    public static readonly IReadOnlyList<ToolSpec> All = new ToolSpec[]
    {
        new("bash", ToolCategory.FileOrShell, "在服务器容器里跑一条 shell 命令",
            ReadOnly: false, ExecutorAgent, "command（一条短命令；别跑 tail -f 这类不收敛的）",
            ToolDefaultPolicy.AllOnWhenEmpty, ShellNote),
        new("read", ToolCategory.FileOrShell, "读服务器上的一个文件",
            ReadOnly: true, ExecutorAgent, "path（文件路径）",
            ToolDefaultPolicy.AllOnWhenEmpty, ShellNote),
        new("write", ToolCategory.FileOrShell, "往服务器上写一个文件",
            ReadOnly: false, ExecutorAgent, "path + content（路径与内容）",
            ToolDefaultPolicy.AllOnWhenEmpty, ShellNote),
        new("fetch", ToolCategory.WebRead, "抓一个网页的正文",
            ReadOnly: true, ExecutorAgent, "url（要抓的地址）",
            ToolDefaultPolicy.AllOnWhenEmpty, FetchNote),
        new("qq", ToolCategory.SendMessage, "真的去 QQ 里做一个小动作（点赞 / 戳一戳 / 撤回…）",
            ReadOnly: false, QqToolSpecs.ExecutorActions,
            "action（动作名，见 QqActionCatalog）+ 该动作的参数（user_id / times / msg_id …）",
            ToolDefaultPolicy.AllOnWhenEmpty,
            "仅 // 路径：动作清单由 AgentServerQqActions 决定；不随普通聊天开放"),
        new("docker", ToolCategory.FileOrShell, "透过 docker 看 / 改服务器（docker.sock ≈ root）",
            ReadOnly: false, ExecutorAgent, "command（一条 docker 子命令，不带开头的 docker）",
            ToolDefaultPolicy.AllOnWhenEmpty, ShellNote + "；另有面板开关 AgentServerDocker 这把锁"),
    };

    /// <summary>
    /// <c>//</c> 那路认得的名字（探针会拿它与 <c>ServerAgentRunner.ParseTools</c> 的数组比对：
    /// 少了 = “能执行却没登记”，多了 = “登记了却没人执行”）。
    /// </summary>
    public static readonly IReadOnlyList<string> Names = All.Select(s => s.Id).ToArray();

    /// <summary>
    /// `//` 那路的**登记表**（6 个工具）；`//` 路径可选过闸门时用它（见 ServerToolGate）。
    /// 注意：QQ **动作名**（like/poke/…）不在这张表里 —— 模型写的是工具 `qq` + 一个 action 字段，
    /// 动作清单仍由 QqActionCatalog 管（现状保留）。
    /// </summary>
    public static ToolRegistry Registry { get; } = BuildRegistry();

    private static ToolRegistry BuildRegistry()
    {
        var registry = new ToolRegistry();

        // 6 个服务器工具 + 10 个 QQ 动作，**都登记**：
        // 动作虽然由 `qq` 工具的一个 action 字段表达（判定走 QqActionCatalog 的动作白名单），
        // 但目录里它们是各自的 spec —— 登记表得覆盖到，否则“每个会产生副作用的能力都能到达闸门”
        // 这条（§9.2 闸门可达性）就会漏掉那 10 个动作（收尾审计时发现的）。
        foreach (var spec in All.Concat(QqToolSpecs.All))
        {
            registry.Register(spec);
        }

        return registry;
    }
}

using System.Linq;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Tools;

namespace BotAgent.Services.Tools;

/// <summary>
/// **统一工具目录**：把三条来路（聊天那 10 条能力、// 的 10 个 QQ 动作、// 的 6 个服务器工具）
/// 摊成一份只读清单（general-agent-platform-plan.md §5.2）。
///
/// 它是**只读的视图**，不参与任何判定：闸门（ToolGate）判的是登记表 + 策略快照，
/// 两条路各自的白名单口径**一个字都没改**（批次 A 的铁律：行为零变化）。
/// 它的用处有三处：面板的“工具目录”页（A5）、探针的完整性断言（§9.2）、批次 D 给模型的提示词清单。
///
/// 自检（<see cref="IsHealthy" />）覆盖四种容易悄悄发生的漂移：
///   ① 登记了却没执行者（<see cref="MissingExecutors" />）；
///   ② 执行者登记了却没人用（<see cref="UnusedExecutors" />）；
///   ③ 高风险/危险档却没写例外说明（<see cref="MissingExceptions" />，§5.2 第 2 步要求显式标注）；
///   ④ 重名（<see cref="DuplicateIds" /> —— 重名会在登记表里互相覆盖）。
/// </summary>
public sealed class ToolDirectory
{
    private ToolDirectory(
        IReadOnlyList<ToolSpec> chat,
        IReadOnlyList<ToolSpec> qq,
        IReadOnlyList<ToolSpec> server,
        ToolExecutorRegistry executors)
    {
        Chat = chat;
        Qq = qq;
        Server = server;
        Executors = executors;
        Specs = chat.Concat(qq).Concat(server).ToArray();

        DuplicateIds = Specs
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        MissingExecutors = Specs
            .Where(s => !executors.TryGet(s.Executor, out _))
            .Select(s => s.Id + "→" + s.Executor)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var used = Specs.Select(s => s.Executor).ToHashSet(StringComparer.Ordinal);
        UnusedExecutors = executors.All
            .Where(e => !used.Contains(e.Id))
            .Select(e => e.Id)
            .ToArray();

        MissingExceptions = Specs
            .Where(s => (ToolDescriptor.AlwaysDenied(s.Category) || s.Default == ToolDefaultPolicy.NamedOnly)
                        && string.IsNullOrWhiteSpace(s.Exception))
            .Select(s => s.Id)
            .ToArray();
    }

    /// <summary>内建目录（装配点与面板共用这一份；纯数据，构造一次即可）。</summary>
    public static ToolDirectory Builtin { get; } =
        new(ChatToolSpecs.All, QqToolSpecs.All, ServerToolSpecs.All, BuiltinToolExecutors.Create());

    public IReadOnlyList<ToolSpec> Chat { get; }

    public IReadOnlyList<ToolSpec> Qq { get; }

    public IReadOnlyList<ToolSpec> Server { get; }

    /// <summary>三族合起来（顺序：聊天 → QQ 动作 → 服务器工具）。</summary>
    public IReadOnlyList<ToolSpec> Specs { get; }

    public ToolExecutorRegistry Executors { get; }

    public IReadOnlyList<string> DuplicateIds { get; }

    public IReadOnlyList<string> MissingExecutors { get; }

    public IReadOnlyList<string> UnusedExecutors { get; }

    public IReadOnlyList<string> MissingExceptions { get; }

    public bool IsHealthy => DuplicateIds.Count == 0 && MissingExecutors.Count == 0
                             && UnusedExecutors.Count == 0 && MissingExceptions.Count == 0;
}

using BotAgent.Domain.Permissions;

namespace BotAgent.Domain.Tools;

/// <summary>
/// 配置留空时这个工具的默认状态（general-agent-platform-plan.md §10.2 坑 4 的显式化）。
///
/// 为什么要有它：两条 Agent 路径的“空值约定”**故意相反** ——
/// 聊天那路留空 = 跟随面板开关（没开就不在名单里），`//` 那路的 6 个工具留空 = **全开**。
/// 那两个口径以前分别藏在 <c>ChatCapabilitySet.FromSwitches</c> 与 <c>ServerAgentRunner.ParseTools</c> 里，
/// 目录统一之后极易把其中一个改错，所以每个工具**显式写一行**，不靠“空值约定”。
/// </summary>
public enum ToolDefaultPolicy
{
    /// <summary>聊天那路：留空 = 关（面板开关打开才在名单里；场景预设只能收紧）。</summary>
    FollowSwitch = 0,

    /// <summary>`//` 那路的 6 个工具：配置留空 = 全开（既有行为，动它等于动权限）。</summary>
    AllOnWhenEmpty = 1,

    /// <summary>QQ 动作的安全档：留空 = 开这几个（闹着玩、后果轻）。</summary>
    SafeTierWhenEmpty = 2,

    /// <summary>QQ 动作的危险档：必须在面板里点名才开（留空 = 关）。</summary>
    NamedOnly = 3,
}

/// <summary>
/// 一个工具的**声明**（纯数据、零 IO —— 落 Domain 的理由见 general-agent-platform-plan.md §3.1）。
///
/// 它回答四个问题：**叫什么**（<see cref="ToolDescriptor.Id" />）、
/// **给模型看的参数长什么样**（<see cref="Parameters" />）、
/// **属哪个权限类别**（<see cref="ToolDescriptor.Category" />，含“任何审批都不放开”的高风险档）、
/// **由谁执行**（<see cref="Executor" />，登记在 <c>Services/Tools/ToolExecutors.cs</c>）。
///
/// 它是 <see cref="ToolDescriptor" /> 的**超集**：登记表本来就按描述符存，于是同一批对象既能进闸门判定，
/// 又能给面板与提示词用 —— 不存在“两份工具清单”（DoD：工具目录份数 = 1）。
/// </summary>
/// <param name="Id">唯一标识（登记表主键；沿用两条路今天就有的名字，不另起别名）。</param>
/// <param name="Category">权限类别（<see cref="ToolDescriptor.AlwaysDenied" /> 的语义照旧）。</param>
/// <param name="Summary">给模型与面板看的一句话（**取值来源只有一个**，不许两处各写一遍）。</param>
/// <param name="ReadOnly">只读？（会写东西的必须 false —— 面板与审计靠它区分）</param>
/// <param name="Executor">执行者标识（一个执行者可以接多个工具）。</param>
/// <param name="Parameters">参数契约（人读的短说明：字段名 + 必填性；批次 D 拼提示词、批次 E 做校验）。</param>
/// <param name="Default">配置留空时的默认状态（显式写，见 <see cref="ToolDefaultPolicy" />）。</param>
/// <param name="Exception">非空 = 这条例外只在某条路径上成立（批次 A 的 FileOrShell 例外），面板要显式显示。</param>
public sealed record ToolSpec(
    string Id,
    ToolCategory Category,
    string Summary,
    bool ReadOnly,
    string Executor,
    string Parameters,
    ToolDefaultPolicy Default,
    string? Exception = null,

    /// <summary>
    /// 是否出现在**给模型看的清单**里（批次 D）。默认 true；只有“默认行为”类工具（<c>chat.reply</c>）标 false ——
    /// 它不是可选能力，而且 V3 §5.3 那条兼容红线要求“开关全关时提示词与改造前逐字一致”，
    /// 让它在清单里占一行就等于那一段永远都在。
    /// </summary>
    bool PromptVisible = true) : ToolDescriptor(Id, Category, Summary, ReadOnly);

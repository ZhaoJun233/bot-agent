using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// 服务端**策略快照**（V3 §5.3：一次处理开始时固定，热更新只影响后续处理）。
/// 所有上限都来自服务端；<see cref="ForbiddenCategories"/> 显式列死高风险档。
/// </summary>
public sealed record ToolPolicy(
    IReadOnlySet<string> AllowedTools,
    int MaxCallsPerRun = 1,
    IReadOnlySet<ToolCategory>? ApprovableCategories = null,
    int PolicyVersion = 1,
    IReadOnlySet<ToolCategory>? ApprovalRequiredCategories = null,
    IReadOnlySet<string>? ApprovalRequiredTools = null)
{
    /// <summary>最低权限策略：一个能力都不开（配置解析失败 / 策略缺失时用它，V3 §9.2）。</summary>
    public static ToolPolicy DenyAll(int policyVersion = 1)
        => new(new HashSet<string>(StringComparer.Ordinal), 0, new HashSet<ToolCategory>(), policyVersion);

    /// <summary>
    /// 这个能力是不是“必须有人批过才允许”。
    ///
    /// 两级口径，**先看工具、再看类别**：
    ///   ① <see cref="ApprovalRequiredTools" />：按**工具名**点名（演示用的固定假工具走这条）；
    ///   ② <see cref="ApprovalRequiredCategories" />：**显式声明的类别**优先——
    ///      非 null 时以它为准，**空集 = 明确“这类不需要审批”**（留空场景的兼容路径就走它）；
    ///      声明为 null 时退回**默认口径**：往当前会话发“额外消息”的能力（<see cref="ToolCategory.SendMessage" />）
    ///      默认必须有人批过才允许 —— 这是 V3 §9.1 那张表的默认值（普通群聊禁止模型直接调用额外发送）。
    ///
    /// 为什么默认取“收紧”而不是“放开”：策略是安全边界，漏写一个字段应该更安全而不是更危险；
    /// 真正需要“与改造前逐字一致”的只有**场景留空**这一条路，那条路由调用方**显式**写空集（见 ChatCapabilitySet）。
    /// </summary>
    public bool RequiresApproval(ToolDescriptor descriptor)
        => (ApprovalRequiredTools?.Contains(descriptor.Id) ?? false)
           || (ApprovalRequiredCategories is { } declared
               ? declared.Contains(descriptor.Category)
               : descriptor.Category == ToolCategory.SendMessage);

    /// <summary>审批可以放开这个类别吗（高风险档永远不行）。</summary>
    public bool ApprovalCouldGrant(ToolDescriptor descriptor)
        => descriptor.Category is not ToolCategory.FileOrShell
            and not ToolCategory.RemoteAgent
            and not ToolCategory.SettingsWrite
            and not ToolCategory.OtherConversationRead
            && (ApprovableCategories?.Contains(descriptor.Category) ?? false);

    /// <summary>
    /// 策略的**稳定指纹**（用于“配置真的变了才换策略版本”）。
    /// 不含 <see cref="PolicyVersion" /> 自己 —— 版本是由指纹变化推出来的。
    /// 集合都按序拼，所以同样的配置每次得到同一个串（可用它判断“这次保存设置到底改没改能力”）。
    /// </summary>
    public string Fingerprint()
    {
        static string Join(IEnumerable<string>? items)
            => items is null ? "-" : string.Join(",", items.OrderBy(x => x, StringComparer.Ordinal));

        static string JoinCats(IEnumerable<ToolCategory>? items)
            => items is null
                ? "-"
                : string.Join(",", items.Select(c => ((int)c).ToString()).OrderBy(x => x, StringComparer.Ordinal));

        return "allow[" + Join(AllowedTools) + "]"
            + " budget=" + MaxCallsPerRun
            + " appr[" + JoinCats(ApprovableCategories) + "]"
            + " needCat[" + JoinCats(ApprovalRequiredCategories) + "]"
            + " needTool[" + Join(ApprovalRequiredTools) + "]";
    }
}

/// <summary>
/// 审批通过后发下来的**一次性票据**（由 <see cref="ApprovalStore" /> 消费时产出）。
/// 票据绑定了工具与本会话：换工具、换会话都不算数。
/// </summary>
public readonly record struct ApprovalTicket(
    string RequestId,
    string ToolId,
    string ConversationKey,
    DateTimeOffset GrantedAt);

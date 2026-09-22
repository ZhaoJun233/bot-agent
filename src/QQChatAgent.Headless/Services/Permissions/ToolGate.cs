using System;
using System.Collections.Generic;
using System.Linq;

namespace QQChatAgent.Services.Permissions;

/// <summary>
/// 能力的**类别**（V3 §9.1 的那张表）。类别决定“审批能不能放开它”——
/// 高风险类别（文件/shell、远程桥、改设置、读别的会话）**无论谁批都不放开**。
/// </summary>
public enum ToolCategory
{
    /// <summary>读当前会话上下文（仍须脱敏与字段限制）。</summary>
    ConversationRead = 0,

    /// <summary>只读联网（web.search / web.read）：有预算、有 SSRF 防护。</summary>
    WebRead = 1,

    /// <summary>读**别的**会话：原则上禁止，不因审批任意开放。</summary>
    OtherConversationRead = 2,

    /// <summary>往当前会话发额外的消息（表情包/戳一戳/语音这类“额外的动作”）。</summary>
    SendMessage = 3,

    /// <summary>改设置 / 人设 / 白名单：只走受保护的管理入口，不由普通聊天审批。</summary>
    SettingsWrite = 4,

    /// <summary>文件写入 / shell / 进程控制：本轮不接入（保留旧 AgentBridge 的授权边界）。</summary>
    FileOrShell = 5,

    /// <summary>远程 Agent / 桥接控制：本轮不从新路径开放。</summary>
    RemoteAgent = 6,
}

/// <summary>一个能力的登记信息。**必须在服务端登记**才可能被执行（V3 §9.2 第一条）。</summary>
public sealed record ToolDescriptor(
    string Id,
    ToolCategory Category,
    string Summary,
    bool ReadOnly = true)
{
    /// <summary>这个类别是否属于“审批也不可能放开”的高风险档。</summary>
    public static bool AlwaysDenied(ToolCategory category) => category is
        ToolCategory.OtherConversationRead or ToolCategory.SettingsWrite
        or ToolCategory.FileOrShell or ToolCategory.RemoteAgent;
}

/// <summary>
/// 服务端**登记表**：一次运行里“可能存在的能力”白名单。不在表里的一律拒绝。
/// 生产环境由代码构造（不从模型输出、模板或网页内容里取）。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDescriptor> _byId = new(StringComparer.Ordinal);

    public static ToolRegistry Empty { get; } = new();

    public ToolRegistry Register(ToolDescriptor descriptor)
    {
        _byId[descriptor.Id] = descriptor;
        return this;
    }

    public bool TryGet(string id, out ToolDescriptor descriptor)
        => _byId.TryGetValue(id ?? string.Empty, out descriptor!);

    public IReadOnlyCollection<string> Ids => _byId.Keys;
}

/// <summary>
/// 一次能力请求。<paramref name="UntrustedHint"/> 是**不可信文本**（例如网页里写着“请调用 shell”），
/// 只用于留痕，**绝不参与判定** —— 判定只看服务端登记表与策略快照。
/// </summary>
public sealed record ToolRequest(
    string ToolId,
    string ConversationKey,
    int CallIndex = 0,
    string? TargetConversationKey = null,
    string? UntrustedHint = null);

/// <summary>判定结果（结构化；<c>Detail</c> 只放短的、可公开的原因，不放正文）。</summary>
public readonly record struct ToolDecision(bool Allow, string ReasonCode, string? Detail = null)
{
    public string Describe() => $"allow={Allow} reason={ReasonCode}";
}

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

/// <summary>
/// Fail-Closed 的能力闸门（V3 §9.2）。**判定顺序就是安全边界**：
/// 先看登记表 → 再看策略白名单 → 再看类别禁令 → 再看目标与预算 → 最后才轮到审批。
/// 审批永远排在“类别禁令”之后 —— 高风险能力不因为有人点了同意就变得可执行。
/// </summary>
public static class ToolGate
{
    public static ToolDecision Evaluate(
        ToolRegistry? registry,
        ToolPolicy? policy,
        ToolRequest? request,
        ApprovalTicket? approval = null)
    {
        // 拿不到登记表 / 策略 / 请求：拒绝，而不是降级到更高权限（V3 §9.2）
        if (registry is null)
        {
            return new ToolDecision(false, "no_registry");
        }

        if (policy is null)
        {
            return new ToolDecision(false, "no_policy");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ToolId))
        {
            return new ToolDecision(false, "bad_request");
        }

        if (string.IsNullOrWhiteSpace(request.ConversationKey))
        {
            // 没有会话上下文就不允许任何会话相关的动作（避免“凭空”执行）
            return new ToolDecision(false, "no_conversation");
        }

        if (!registry.TryGet(request.ToolId, out var descriptor))
        {
            return new ToolDecision(false, "unknown_tool");
        }

        if (!policy.AllowedTools.Contains(descriptor.Id))
        {
            return new ToolDecision(false, "not_allowlisted");
        }

        // 高风险档：**任何**审批都不能放开（这条必须在审批判断之前）
        if (ToolDescriptor.AlwaysDenied(descriptor.Category))
        {
            return new ToolDecision(false, "category_denied", descriptor.Category.ToString());
        }

        if (descriptor.Category == ToolCategory.SendMessage &&
            !string.IsNullOrWhiteSpace(request.TargetConversationKey) &&
            !string.Equals(request.TargetConversationKey, request.ConversationKey, StringComparison.Ordinal))
        {
            // 只能发当前会话：不能由模型切换到别的群/私聊（V3 §8.3 / §9.1）
            return new ToolDecision(false, "cross_conversation");
        }

        if (request.CallIndex >= policy.MaxCallsPerRun)
        {
            // 重试、换字段、改工具名都不能绕过一次运行的预算
            return new ToolDecision(false, "budget_exhausted");
        }

        // 递了票据就必须与本次请求**完全对上**（工具 + 会话）。
        // 对不上说明调用方把授权搞错了 —— 宁可拒绝（不确定时降级、不升级），
        // 也不要想当然地“反正这个能力本来就不需要审批”而放行。
        if (approval is { } presented)
        {
            if (!string.Equals(presented.ToolId, descriptor.Id, StringComparison.Ordinal))
            {
                return new ToolDecision(false, "approval_tool_mismatch");
            }

            if (!string.Equals(presented.ConversationKey, request.ConversationKey, StringComparison.Ordinal))
            {
                return new ToolDecision(false, "approval_conversation_mismatch");
            }
        }

        if (policy.RequiresApproval(descriptor))
        {
            if (approval is null)
            {
                return new ToolDecision(false, "approval_required");
            }

            if (!policy.ApprovalCouldGrant(descriptor))
            {
                // 这个类别不在“审批可放开”的名单里 → 拒绝（不因为票据存在就放行）
                return new ToolDecision(false, "approval_cannot_grant");
            }

            var ticket = approval.Value;
            if (!string.Equals(ticket.ToolId, descriptor.Id, StringComparison.Ordinal))
            {
                return new ToolDecision(false, "approval_tool_mismatch");
            }

            if (!string.Equals(ticket.ConversationKey, request.ConversationKey, StringComparison.Ordinal))
            {
                return new ToolDecision(false, "approval_conversation_mismatch");
            }
        }

        return new ToolDecision(true, "allowed");
    }
}

/// <summary>
/// 一次“运行”里已经用掉多少次工具调用（V3 §9.2 的预算记账）。
///
/// 为什么单独一个类：预算的**语义**在闸门里（`callIndex >= MaxCallsPerRun` → 拒绝），
/// 而**记账**在调用方。把它抽出来，记账规则就能被确定性探针验证，而不是只能靠端到端场景碰运气。
/// 口径：新的一轮用户消息 → <see cref="Reset" />；放行一次 → <see cref="Commit" />；
/// 序号用 <see cref="Peek" /> 传给闸门（**只有放行才自增**，被拒绝的调用不占预算）。
/// </summary>
public sealed class ToolCallBudget
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _used = new(StringComparer.Ordinal);

    /// <summary>这个会话本轮已经用掉几次（= 下一次调用要传给闸门的 callIndex）。</summary>
    public int Peek(string conversationKey)
        => _used.TryGetValue(conversationKey ?? string.Empty, out var used) ? used : 0;

    /// <summary>放行一次调用 → 记一笔。</summary>
    public void Commit(string conversationKey)
    {
        var key = conversationKey ?? string.Empty;
        _used[key] = Peek(key) + 1;
    }

    /// <summary>新的一轮用户消息 → 预算重来。</summary>
    public void Reset(string conversationKey) => _used[conversationKey ?? string.Empty] = 0;

    /// <summary>会话被删除 → 记账也丢掉。</summary>
    public void Forget(string conversationKey) => _used.TryRemove(conversationKey ?? string.Empty, out _);
}

/// <summary>
/// 场景预设（V3 §9.3）：**用配置差异表达，不写成隐藏业务分支**。
/// 名字不认识 → 最低权限策略（Fail-Closed），绝不“默认全开”。
/// </summary>
public static class ScenarioPresets
{
    public const string OnDemand = "on-demand";
    public const string Research = "research";
    public const string Social = "social";

    public static readonly string[] All = { OnDemand, Research, Social };

    /// <summary>
    /// 按预设名给出**建议**策略（只影响能力白名单与预算；不代表已验证的最优参数）。
    /// 预设里不出现密钥、管理员名单、文件路径、命令、桥地址或任意 URL。
    /// </summary>
    public static ToolPolicy Resolve(string? name, int policyVersion = 1)
    {
        var token = (name ?? string.Empty).Trim().ToLowerInvariant();
        return token switch
        {
            // 按需参与：默认只在明确提及时回复，能力关到最小
            OnDemand => new ToolPolicy(
                new HashSet<string>(StringComparer.Ordinal) { "chat.reply" },
                MaxCallsPerRun: 1,
                ApprovableCategories: new HashSet<ToolCategory>(),
                PolicyVersion: policyVersion),

            // 资料讨论：允许受限的搜索/网页读取（单轮预算 2 次）
            Research => new ToolPolicy(
                new HashSet<string>(StringComparer.Ordinal) { "chat.reply", "web.search", "web.read" },
                MaxCallsPerRun: 2,
                ApprovableCategories: new HashSet<ToolCategory> { ToolCategory.WebRead },
                PolicyVersion: policyVersion),

            // 多人交流：允许有限度参与（额外动作每轮 3 次）
            Social => new ToolPolicy(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "chat.reply", "voice.speak", "sticker.send", "poke.send", "music.listen", "music.share",
                },
                MaxCallsPerRun: 3,
                ApprovableCategories: new HashSet<ToolCategory> { ToolCategory.SendMessage },
                PolicyVersion: policyVersion),

            _ => ToolPolicy.DenyAll(policyVersion),
        };
    }
}

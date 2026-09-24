using System;
using System.Collections.Generic;
using System.Linq;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Tools;

namespace BotAgent.Services.Tools;

/// <summary>
/// `//` 那一路的**闸门接线**（general-agent-platform-plan.md 批次 A 第 2 步的收尾）。
///
/// 今天 `//` 那路用的是自己那套字符串白名单（`ParseTools` + `QqActionCatalog.ParseAllowed`），
/// 与聊天那路的 `ToolGate` 互不可见。这一步把它们**接到同一张表 + 同一个闸门**上：
///   · 登记表 = <see cref="ServerToolSpecs.Registry" />（6 个工具，服务端构造）；
///   · 策略 = 本次允许的工具（面板配置 + docker 两把锁的结果）；
///   · **高风险例外显式点名**：`bash`/`read`/`write`/`docker` 属于“任何审批都不放开”的档，
///     它们今天能跑靠的是**另一条既有授权边界**（面板开关 + 工作目录 + 命令超时 + docker 两把锁）——
///     这里把那条边界写成 <see cref="ToolPolicy.HighRiskExceptions" />，于是“为什么没被拦”有答案。
///
/// **它不改变任何既有判定**：接线处默认关（<c>AppSettings.AgentServerUseGate</c>），
/// 打开后过的也是“本次允许的工具”那条白名单 —— 判定结果与老白名单一致（可用探针钉住）。
/// </summary>
public static class ServerToolGate
{
    /// <summary>由“本次允许的工具集合”构造 `//` 那路的策略快照。</summary>
    public static ToolPolicy BuildPolicy(IEnumerable<string> allowedTools)
    {
        var allowed = new HashSet<string>(allowedTools ?? Array.Empty<string>(), StringComparer.Ordinal);

        // 例外只可能来自“本次允许”里、且属于 AlwaysDenied 档的那几个工具名（面板配置说了算，模型影响不到）。
        var exceptions = ServerToolSpecs.All
            .Where(s => allowed.Contains(s.Id) && ToolDescriptor.AlwaysDenied(s.Category))
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        return new ToolPolicy(
            allowed,
            // `//` 那路有自己的步数上限与 QQ 动作上限（MaxQqActionsPerTask），闸门里不重复限流
            MaxCallsPerRun: int.MaxValue,
            // 显式空集：这一路不走审批（审批是聊天那路的机制，两条路的授权边界不许合一）
            ApprovableCategories: new HashSet<ToolCategory>(),
            ApprovalRequiredCategories: new HashSet<ToolCategory>(),
            HighRiskExceptions: exceptions);
    }

    /// <summary>判一次（纯查表；调用方负责在拒绝时把结构化错误喂回模型）。</summary>
    public static ToolDecision Check(ToolPolicy? policy, string? sourceKey, string? toolId)
        => ToolGate.Evaluate(
            ServerToolSpecs.Registry,
            policy,
            // 只有“连工具名都没有”才算坏请求；**缺会话 key** 交给闸门自己报 no_conversation
            // （两种情形的原因码不一样，运维一眼能分清是“模型没写工具”还是“不知道该发到哪”）
            string.IsNullOrWhiteSpace(toolId)
                ? null
                : new ToolRequest(toolId!, sourceKey ?? string.Empty));
}

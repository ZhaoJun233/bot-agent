using System;
using System.Collections.Generic;

namespace BotAgent.Domain.Ops;

/// <summary>
/// 一轮里的节点（对应 general-agent-platform-plan.md §4 的四步 / §7.3 的六张卡片）。
/// 顺序即时间轴顺序；面板按它画卡片流。
/// </summary>
public enum TurnNodeKind
{
    /// <summary>① 参与判断（这一轮要不要接话）。</summary>
    Participation = 0,

    /// <summary>② 上下文组装（窗口 / 画像 / 气氛…）。</summary>
    Context = 1,

    /// <summary>③ 模型决策（这一轮模型说了什么形状的话）。</summary>
    Model = 2,

    /// <summary>④ 工具闸门（放行 / 拒绝 / 待审批）。</summary>
    Gate = 3,

    /// <summary>⑤ 工具执行（真的去干）。</summary>
    ToolExec = 4,

    /// <summary>⑥ 净化与发送（含批次 C 的回复审计）。</summary>
    Outbound = 5,
}

/// <summary>
/// 一个节点的记录。**只有形状**：状态码 / 耗时 / 工具名 / 计数 ——
/// **没有**正文、没有参数值、没有会话显示名（§9.2 的“审计不写正文”就是钉这个字段清单）。
/// </summary>
/// <param name="Kind">节点种类。</param>
/// <param name="Status">状态码（如 ok / denied / sent / blocked / error）。</param>
/// <param name="DurationMs">这一节点花了多少毫秒（从上一个节点算起）。</param>
/// <param name="ToolId">涉及的工具名（只有闸门与执行节点有）。</param>
/// <param name="ReasonCode">判定原因码（<c>not_allowlisted</c> / <c>credential_shape</c> …）。</param>
/// <param name="Count">计数（上下文条数 / 结果条数 / 字数）。</param>
public sealed record TurnNode(
    TurnNodeKind Kind,
    string Status,
    int DurationMs,
    string? ToolId = null,
    string? ReasonCode = null,
    int? Count = null);

/// <summary>
/// 一轮的**决策轨迹**（批次 C）：runId + 六个节点 + 结果码 + 总耗时。
/// 它回答“这一轮机器人都经过了什么”，**不回答“说了什么”** —— 正文永远只在会话气泡里。
/// </summary>
public sealed record TurnTrace(
    string RunId,
    string ConversationKey,
    DateTimeOffset StartedAt,
    string Outcome,
    int TotalMs,
    IReadOnlyList<TurnNode> Nodes);

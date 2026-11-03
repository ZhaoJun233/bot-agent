using System;

namespace BotAgent.Domain.Agent;

/// <summary>
/// 一个**待批的高风险任务**（#137）：审批发生在派发之前，批准之前执行器一次都不会被调用。
///
/// 为什么是任务级而不是工具级：// 的命令循环在 `ServerAgentRunner` 里，工具级挂起要把整段
/// 对话（messages / 步数 / 当前工具调用 / 租约）变成可持久化的东西，还要跨重启恢复 —— 那是
/// 一整套新机制。任务级的前置闸门同样满足“批准前零副作用”，而且天然可持久化（任务只有一行）。
/// 粒度是粗的：声明了 bash 的任务即使最后没用 bash 也要先批。这是刻意的 fail-closed 取舍。
/// </summary>
public sealed record AgentTaskApprovalRequest(
    string RequestId,
    string ToolId,
    string Summary,
    DateTimeOffset ExpiresAt);

/// <summary>待批任务的决策结果（通道翻译成 HTTP 状态码与错误码）。</summary>
public enum AgentTaskApprovalDecision
{
    Approved = 0,
    Rejected = 1,
    NotFound = 2,
    NotPending = 3,
    Expired = 4,
    Disposed = 5,
}

using System.Text.Json.Nodes;
using BotAgent.Domain.Agent;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 服务器 agent 的**执行会话台账**端口（由 <c>Adapters/Persistence/AgentSessionStore</c> 实现，见 §4 的
/// <c>AgentCommandService</c> 一行）。
///
/// 为什么要有它：<c>//</c> 命令那一路（建会话 / 切换 / 改名 / 重开 / 看流水）只该说“当前是哪个会话”，
/// 不该知道它落成哪个 JSON、怎么裁剪。有了端口，agent 会话那几条规则可以**不落盘**用假台账测。
/// </summary>
public interface IAgentSessionStore
{
    // ── 查询 ──
    /// <summary>某个聊天的全部执行会话（新的在前）。</summary>
    List<AgentSession> List(string sourceKey);

    /// <summary>取这个聊天在当前后端上的会话；没有就建一个。</summary>
    AgentSession EnsureCurrent(string sourceKey, string backend);

    /// <summary>找当前会话（优先指定后端）；没有 = null。</summary>
    AgentSession? FindCurrent(string sourceKey, string preferBackend);

    /// <summary>按 id / 名字 / 序号找会话。</summary>
    AgentSession? Find(string sourceKey, string idNameOrIndex);

    /// <summary>这个会话是不是该聊天当前在用的。</summary>
    bool IsCurrent(string sourceKey, AgentSession session);

    /// <summary>某个会话的执行流水。</summary>
    List<SessionRun> Runs(string sourceKey, string sessionId);

    /// <summary>某个会话的对话历史（内置后端才有）。</summary>
    List<(string Role, string Text)> History(string sourceKey, string sessionId);

    /// <summary>所有聊天的会话总览（面板与 <c>//sessions all</c> 用）。</summary>
    List<(string SourceKey, List<AgentSession> Sessions)> AllChats();

    /// <summary>读取某个设备上报的 pi 会话（最近一次上报的快照）。</summary>
    List<JsonObject> LastPiSessions(string? device = null);

    // ── 变更 ──
    /// <summary>建一个执行会话并把它设为当前。</summary>
    AgentSession Create(string sourceKey, string backend, string? name, string? device = null,
        string? piSessionId = null, bool piOwned = true);

    /// <summary>切换当前会话。</summary>
    bool Use(string sourceKey, string idNameOrIndex, out AgentSession? used);

    /// <summary>删掉一个执行会话（返回被删的那个）。</summary>
    AgentSession? Delete(string sourceKey, string idNameOrIndex);

    /// <summary>重开（清空上下文，保留名字与后端）。</summary>
    AgentSession? Reset(string sourceKey, string idNameOrIndex);

    /// <summary>改名（改过之后就不再自动综结）。</summary>
    bool Rename(string sourceKey, string idNameOrIndex, string newName);

    /// <summary>把 agent 这一轮用过的提示词（+ 输出）补进内置后端的历史里。</summary>
    void AppendTurn(string sourceKey, string sessionId, IReadOnlyList<(string Role, string Text)> messages);

    /// <summary>记一次任务开始，返回流水 id。</summary>
    string StartRun(string sourceKey, string sessionId, string prompt, string? device);

    /// <summary>记一次任务结束（成败、耗时、工具调用次数、结果摘要）。</summary>
    void FinishRun(string sourceKey, string sessionId, string runId, bool ok, long durationMs,
        int toolCalls, string result);

    /// <summary>把自动综结出来的名字写回去（手动改过的会话不动）。</summary>
    bool SetAutoTitle(string sourceKey, string sessionId, string title);

    /// <summary>给模型看的执行记录摘要（最近几次任务）。</summary>
    string SessionDigest(string sourceKey, string sessionId, int maxRuns = 3);

    /// <summary>记下某个设备这次上报的 pi 会话。</summary>
    void RememberPiSessions(string device, List<JsonObject> list);
}

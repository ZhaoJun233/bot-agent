using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Tools;
using BotAgent.Services.Ops;

namespace BotAgent.Services.Reply;

/// <summary>
/// 聊天这一路的**有限步进循环**（general-agent-platform-plan.md 批次 E）。
///
/// 形状（对齐提案的四步，但按本仓库的真实语义）：
///
///   Context Build（在外面做完，见 <c>ReplyPipeline.BuildTurnInputsAsync</c>）
///     → **Model Step**（本类：把模型调用收在一处）
///     → Harness Dispatch（在外面：闸门 + 动作执行；本类只回调 <c>runInlineTool</c>）
///     → State Commit（在外面：发送 / 记账 / 轨迹）
///
/// 四条不变量（每一批都要钉）：
///   ① **默认 1 步 = 与改造前逐字一致**：步数为 1 时这个方法就是"调一次模型"，没有第二个分支；
///   ② **上限固定 3**：步数来自设置，但在这里被钳到 <see cref="MinSteps" />..<see cref="MaxSteps" />；
///   ③ **不空转**：只有当场做完的工具**真的拿到东西**（回调返回非空文本）才再问一次；
///   ④ **工具照样过闸门**：本类不执行任何工具，`runInlineTool` 由调用方实现（闸门与预算都在那边）。
///
/// 它同时承担"给批次 C 的轨迹记节点"：每多一步记一个 <see cref="TurnNodeKind.Model" /> 节点
/// （<c>count</c> = 第几步），当场执行过工具则记一个 <see cref="TurnNodeKind.ToolExec" />。
/// </summary>
public sealed class AgentTurnLoop
{
    /// <summary>步数下限（1 = 与改造前逐字一致）。</summary>
    public const int MinSteps = 1;

    /// <summary>步数上限（再多没见过有意义的收敛，见 §5.4）。</summary>
    public const int MaxSteps = 3;

    private readonly IModelClient _brain;
    private readonly TurnTraceStore? _traces;

    public AgentTurnLoop(IModelClient brain, TurnTraceStore? traces = null)
    {
        _brain = brain;
        _traces = traces;
    }

    /// <summary>一轮循环的产物：最终那次的结果 + 用了几步 + 当场执行过几次工具。</summary>
    public readonly record struct LoopOutcome(CompletionResult Result, int Steps, int InlineTools);

    /// <summary>
    /// 跑完一轮（至少一次、最多 <paramref name="maxSteps" /> 次模型调用）。
    /// </summary>
    /// <param name="conversationKey">会话 key（轨迹用）。</param>
    /// <param name="turn">这一轮的输入与提示词素材。</param>
    /// <param name="caps">本轮固定的策略快照（工具清单按它裁剪）。</param>
    /// <param name="proactive">这一轮是不是主动开口（提示词要用）。</param>
    /// <param name="maxSteps">步数上限（设置来的；这里钳到 1..3）。</param>
    /// <param name="runInlineTool">
    /// 当场做掉模型点名的只读工具，返回**喂回给模型的那段文本**；返回 null/空 = 没有可当场做的工具 → 收工。
    /// 实现方负责过闸门与预算（本类不做判定）。
    /// </param>
    public async Task<LoopOutcome> RunAsync(
        string conversationKey,
        TurnInputs turn,
        Domain.Permissions.ChatCapabilitySet caps,
        bool proactive,
        int maxSteps,
        Func<CompletionResult, Task<string?>> runInlineTool)
    {
        var steps = Math.Clamp(maxSteps, MinSteps, MaxSteps);
        var feed = turn.SearchText;
        var inline = 0;
        var used = 0;
        CompletionResult result;

        while (true)
        {
            used++;
            result = await _brain.CompleteAsync(
                turn.Context,
                turn.Profiles.Count > 0 ? string.Join("\n\n", turn.Profiles) : null,
                stickers: turn.StickerChoices.Count > 0 ? turn.StickerChoices : null,
                pokeContext: turn.PokeContext,
                moodText: turn.MoodText,
                musicText: turn.MusicText,
                linkText: turn.LinkText,
                recallText: turn.RecallText,
                enableWebSearch: turn.Snapshot.EnableWebSearch,
                searchText: feed,
                groupRolesText: turn.GroupRoles,
                vibeHint: turn.VibeHint,
                proactive: proactive,
                enableListen: turn.Snapshot.EnableMusic,
                enableVoice: turn.Snapshot.EnableVoice,
                enableAsk: turn.Snapshot.EnableQuestions,
                enableToolRequest: turn.Snapshot.EnableApprovals,
                toolList: ToolPromptText.Render(caps));

            _traces?.Node(conversationKey, TurnNodeKind.Model, "ok", count: used);

            if (used >= steps)
            {
                break;
            }

            var note = await runInlineTool(result);
            if (string.IsNullOrWhiteSpace(note))
            {
                // 没有“当场能做完”的工具 → 立刻收工（不空转、不为了凑步数再问一次）
                break;
            }

            inline++;
            _traces?.Node(conversationKey, TurnNodeKind.ToolExec, "ok", reasonCode: "inline", count: inline);
            feed = string.IsNullOrWhiteSpace(feed) ? note : feed + "\n\n" + note;
        }

        return new LoopOutcome(result, used, inline);
    }
}

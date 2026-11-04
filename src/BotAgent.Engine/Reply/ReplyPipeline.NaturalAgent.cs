using BotAgent.Services.Evaluation;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Reply;
using BotAgent.Services.Agent;

namespace BotAgent.Services.Reply;

/// <summary>自然对话驱动 agent：资格判定与派活（从 GenerateReplyAsync 拆出，保持主流程短）。</summary>
public sealed partial class ReplyPipeline
{
    /// <summary>只有“人家在跟机器人说话”、发话人在 agent 白名单里、面板开关打开时才返回触发消息；否则 null。</summary>
    private ChatMessage? NaturalAgentTrigger(BotConversation conversation, long? triggerMessageId, bool proactive, bool enabled)
    {
        if (proactive || !enabled || triggerMessageId is not long id)
        {
            return null;
        }

        var trigger = conversation.Messages.FirstOrDefault(m => m.QqMessageId == id);
        return trigger is { SenderId: long sender }
            && (trigger.DirectToBot || !conversation.Target.IsGroup)
            && _agentCmds.CanRunNatural(conversation, sender)
            ? trigger
            : null;
    }

    /// <summary>
    /// 模型听出来“这是要我去干活”（action=agent）：交给服务器 agent，返回 true 表示回复流程到此为止。
    /// 不够资格却写了 agent → 正文本来就是 null，返回 false 按静默处理（Fail-Closed）。
    /// </summary>
    private bool TryDispatchNaturalAgent(BotConversation conversation, ChatMessage? trigger, CompletionResult result, double elapsed)
    {
        if (result.Action != ReplyAction.Agent)
        {
            return false;
        }

        if (trigger is not null && result.AgentTask is { Length: > 0 } task)
        {
            _hooks.Log($"[自然 agent] 从对话里接了一件活（{elapsed:F0}ms）");
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "agent", reasonCode: "natural_agent");
            _ = _agentCmds.RunNaturalSafeAsync(conversation, trigger, task, result.AgentAck);
            return true;
        }

        _hooks.Log($"[自然 agent] 模型想派活但本轮不够资格（{result.ReasonCode}），按静默处理");
        return false;
    }
}

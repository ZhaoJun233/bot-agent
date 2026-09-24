using BotAgent.Domain.Conversation;

namespace BotAgent.Services.Panel;

/// <summary>
/// 面板事件聚合（service）：一次回复链上"要告诉 UI 的事"全部从这里出去。
///
/// 为什么收拢：以前事件散在 BotAgentHost 的十几个位置各写一遍 <c>XxxChanged?.Invoke()</c>，
/// 订阅方抛异常会顺着调用栈污染业务（日志那条本来是吞掉的，别的不是）。
/// 现在只有这一个类持有事件，BotAgentHost 只留同名转发事件（面板订阅方式一字未改）。
///
/// 纪律：**这里的通知一律不吞异常**（除了日志推送 —— 它历史上就是吞的），
/// 因为订阅方是面板，它抛异常应该看得见，不该被悄悄吃掉。
/// </summary>
public sealed class PanelNotifier
{
    /// <summary>新消息落库（sourceKey, 消息）。</summary>
    public event Action<string, ChatMessage>? MessageAdded;

    /// <summary>会话发生变更（新建/改名/删除/未读/顺序）。</summary>
    public event Action? ConversationsChanged;

    /// <summary>连接状态或 AI 开关变化。</summary>
    public event Action? StateChanged;

    /// <summary>某个会话的「AI 正在思考」状态变化。</summary>
    public event Action<string>? ThinkingChanged;

    /// <summary>一条面向 UI 的运行日志。</summary>
    public event Action<string>? LogLine;

    /// <summary>写文件日志，并推送给 Web UI 日志面板（面板推送失败不影响主流程）。</summary>
    public void EmitLog(string message)
    {
        FileLog.Write("Agent", message);
        try
        {
            LogLine?.Invoke(message);
        }
        catch
        {
            // UI 推送失败不影响主流程
        }
    }

    /// <summary>切换「AI 正在思考」状态并通知 UI（状态没变就什么都不做）。</summary>
    public void SetThinking(BotConversation conversation, bool thinking)
    {
        if (conversation.Thinking == thinking)
        {
            return;
        }

        conversation.Thinking = thinking;
        ThinkingChanged?.Invoke(conversation.SourceKey);
    }

    public void NotifyMessageAdded(string sourceKey, ChatMessage message) => MessageAdded?.Invoke(sourceKey, message);

    public void NotifyConversationsChanged() => ConversationsChanged?.Invoke();

    public void NotifyStateChanged() => StateChanged?.Invoke();
}

using BotAgent.Services.Qq;

namespace BotAgent.Services.Agent;

/// <summary>
/// 显示层脱敏的**唯一**口径（V3 / AGENTS.md §4）：开关开着才遮，关掉原样返回。
///
/// 为什么要有这么一层：以前这段策略在 <c>BotAgentHost</c> 里手写了两遍（文本 / 会话名），
/// 抽完用例层之后又有更多地方要按同一个开关决定"遮不遮"—— 再抄一遍就等着两边跑偏。
/// 这里只放**策略**（看哪个开关、调哪个遮法），取名/取哪份名单由调用方给。
///
/// 规矩不变：**只遮显示层**；会话 key 与可编辑真名（<c>nameRaw</c>）一律原样。
/// </summary>
public static class MaskingRules
{
    /// <summary>按开关遮盖一段文本；<paramref name="knownNames" /> 是"这段文字里可能出现的名字"（用于整体替换）。</summary>
    public static string Text(bool maskSensitive, string text, IReadOnlyList<string>? knownNames = null)
        => maskSensitive ? AgentMask.Text(text, knownNames) : text;

    /// <summary>按开关决定聊天的显示名：开 =「群聊 940***75」，关 = 真名。</summary>
    public static string ChatLabel(bool maskSensitive, BotConversation conversation)
        => maskSensitive ? AgentMask.ChatLabel(conversation.SourceKey) : conversation.Name;
}

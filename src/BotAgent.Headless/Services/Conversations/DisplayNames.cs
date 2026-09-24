using BotAgent.Domain.Conversation;

namespace BotAgent.Services.Conversations;

/// <summary>
/// 从会话历史里找一个人的显示名（昵称/群名片）—— 找不到就写「成员 &lt;qq&gt;」。
/// 戳一戳的 notice 事件本身不带昵称，只能这样找；提示词里也用它。
/// ⚠ 这里给的是**真名**：脱敏只管显示层（见 <c>MaskingRules</c>）。
/// </summary>
public static class DisplayNames
{
    public static string Of(BotConversation conversation, long userId, long selfId)
    {
        if (userId <= 0)
        {
            return "某人";
        }

        if (selfId != 0 && userId == selfId)
        {
            return "你";
        }

        var named = conversation.Messages.LastOrDefault(m => m.SenderId == userId && m.SenderName is { Length: > 0 });
        return string.IsNullOrWhiteSpace(named?.SenderName) ? $"成员 {userId}" : named!.SenderName!;
    }
}

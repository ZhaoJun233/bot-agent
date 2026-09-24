using BotAgent.Domain.Conversation;

namespace BotAgent.Domain.Conversation;

/// <summary>
/// 复读/回声判定（纯函数、零 IO）：找某条消息的下标、认「这条是不是复读了更早的一条」、
/// 以及「这句话是不是和自己上一条一字不差」。
/// 批次 1「纯函数下沉」：从 BotAgentHost 原样搬来，判断条件一字未改；
/// 只有 `IsRepeatingOwnLastMessage` 的入参从 `BotConversation` 收紧成消息列表（同一份数据，少一层依赖）。
/// </summary>
public static class EchoGuard
{
    /// <summary>在会话里找某条 QQ 消息的下标（找不到返回 -1，例如已被滚动窗口裁掉）。</summary>
    public static int IndexOfMessage(IReadOnlyList<ChatMessage> messages, long? qqMessageId)
    {
        if (qqMessageId is not long id)
        {
            return -1;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].QqMessageId == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>机器人自己最后一条发言的下标（还没说过话返回 -1）。用来判断“这条触发消息是不是本轮的新诉求”。</summary>
    public static int LastSelfMessageIndex(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.Self)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 这条消息是不是“复读 / 模仿”：正文与上下文里**更早的一条**消息一字不差。
    /// 群里很常见的玩法（复读机、学机器人说话），但它是“引用挂错人”的高发场景 ——
    /// 这时候说话对象是复读的那个人，模型却容易把被复读的原文当成引用目标
    /// （线上实测：群友复读了机器人的话、机器人回“别学我说话！”，引用却挂到了别人那条上）。
    /// 只认“有实际内容的文本”：太短（“？”“6”）容易只是撞车，内容标记（[图片]/[表情:…]）是系统写的，都不算。
    /// </summary>
    public static bool IsEchoOfEarlierMessage(IReadOnlyList<ChatMessage> messages, int index)
    {
        if (index <= 0 || index >= messages.Count || messages[index].Recalled)
        {
            return false;
        }

        var text = messages[index].Text?.Trim() ?? string.Empty;
        if (!IsEchoCandidate(text))
        {
            return false;
        }

        for (var i = 0; i < index; i++)
        {
            if (messages[i].Recalled)
            {
                continue;   // 撤回的内容群里已经看不到了，谈不上“复读”
            }

            if (string.Equals(messages[i].Text?.Trim(), text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>能不能拿来做“复读”比对：一两个字的短句、以及内容标记（[图片]/[表情:…]）都不算。</summary>
    public static bool IsEchoCandidate(string text)
    {
        if (text.Length < 2)
        {
            return false;
        }

        if (text[0] is '[' or '【')
        {
            var close = text.IndexOfAny([']', '】']);
            if (close > 0 && TextRules.IsContentMarker(text[1..close]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 本次要发的这句话，是不是和会话里自己上一条发言完全相同？
    /// （用于断掉“模型把自己上一条读进上下文 → 原样再发”的死循环）
    /// </summary>
    public static bool IsRepeatingOwnLastMessage(IReadOnlyList<ChatMessage> messages, string reply)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != MessageRole.Self)
            {
                continue;
            }

            return string.Equals(messages[i].Text?.Trim() ?? string.Empty, reply.Trim(), StringComparison.Ordinal);
        }

        return false; // 自己还没说过话：不算复读
    }
}

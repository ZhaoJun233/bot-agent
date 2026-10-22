using System;
using System.Collections.Generic;
using BotAgent.Domain.Conversation;

namespace BotAgent.Domain.Atmosphere;

/// <summary>群聊活跃度级别（纯状态、零 IO）。</summary>
public enum AtmosphereActivity
{
    /// <summary>静寂（发言间隔长，群聊冷清）</summary>
    Quiet = 0,

    /// <summary>平缓（正常低频闲聊）</summary>
    Moderate = 1,

    /// <summary>活跃（多人在短时间内连续交谈）</summary>
    Active = 2,

    /// <summary>狂热/刷屏（极高频消息涌入）</summary>
    Heated = 3
}

/// <summary>会话氛围快照值对象（纯领域模型、零 IO）。</summary>
public readonly record struct AtmosphereSnapshot(
    AtmosphereActivity Activity,
    string DominantVibe,
    int ConsecutiveBotReplies,
    double SecondsSinceLastBotReply,
    int RecentMessageCount)
{
    public static AtmosphereSnapshot Default => new(
        AtmosphereActivity.Moderate,
        string.Empty,
        0,
        9999.0,
        0);
}

/// <summary>
/// 聊天氛围与动态发言意愿模型（纯函数、零 IO，对齐八荣八耻研发铁律）。
/// 解决机械发言、连续刷屏讨人嫌与气氛失焦问题。
/// </summary>
public static class ChatAtmosphere
{
    /// <summary>
    /// 计算疲劳阻尼后的有效发言欲望（0~100）。
    /// 连续多次发言会产生非线性疲劳惩罚，静默冷却后惩罚恢复。
    /// </summary>
    public static int CalculateEffectiveDesire(
        int baseDesire,
        int consecutiveBotReplies,
        double secondsSinceLastBotReply,
        AtmosphereActivity activity)
    {
        var desire = Math.Clamp(baseDesire, 0, 100);

        // 1. 疲劳阻尼（连续发言惩罚）：连续说得越多，越需要闭嘴降温
        var fatiguePenalty = consecutiveBotReplies switch
        {
            <= 0 => 0,
            1 => 5,
            2 => 20,
            3 => 45,
            _ => 75
        };

        // 2. 冷却恢复：若距上次发言已超过一段时间，疲劳阻尼逐步抵消
        if (secondsSinceLastBotReply > 180.0)
        {
            fatiguePenalty = 0;
        }
        else if (secondsSinceLastBotReply > 60.0)
        {
            fatiguePenalty /= 2;
        }

        desire -= fatiguePenalty;

        // 3. 氛围环境调节
        desire += activity switch
        {
            AtmosphereActivity.Quiet => -10, // 群聊冷清且没叫你时，降低插嘴冲动
            AtmosphereActivity.Moderate => 0,
            AtmosphereActivity.Active => 5,   // 群聊活跃时适度融入
            AtmosphereActivity.Heated => -15, // 刷屏/狂热对线时克制，防止添乱
            _ => 0
        };

        return Math.Clamp(desire, 0, 100);
    }

    /// <summary>
    /// 根据有效欲望与阈值裁定是否应当主动抑制发言（纯函数）。
    /// </summary>
    public static bool ShouldSuppressSpeech(
        int effectiveDesire,
        int threshold,
        bool isDirectMention,
        out string reason)
    {
        if (isDirectMention)
        {
            reason = "明确被呼叫/@，不予抑制";
            return false;
        }

        if (effectiveDesire <= 0)
        {
            reason = "疲劳阻尼或气氛抑制导致当前发言意愿降为零";
            return true;
        }

        if (effectiveDesire < Math.Min(threshold, 20))
        {
            reason = $"有效发言意愿({effectiveDesire})过低，保持倾听与静默";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// 从当前上下文消息窗口与参考时间戳推导氛围快照（纯函数、零 IO）。
    /// </summary>
    public static AtmosphereSnapshot Analyze(IReadOnlyList<ChatMessage>? window, DateTimeOffset now)
    {
        if (window is null || window.Count == 0)
        {
            return AtmosphereSnapshot.Default;
        }

        int consecutiveBotReplies = 0;
        int lastBotIndex = -1;
        var lastBotTime = DateTimeOffset.MinValue;

        for (int i = window.Count - 1; i >= 0; i--)
        {
            var msg = window[i];
            if (msg.Role == MessageRole.Self)
            {
                lastBotIndex = i;
                lastBotTime = msg.Timestamp;
                break;
            }
        }

        if (lastBotIndex >= 0)
        {
            for (int i = lastBotIndex; i >= 0; i--)
            {
                if (window[i].Role == MessageRole.Self)
                {
                    consecutiveBotReplies++;
                }
                else
                {
                    break;
                }
            }
        }

        double secondsSinceLastBot = (lastBotIndex >= 0 && lastBotTime > DateTimeOffset.MinValue && now >= lastBotTime)
            ? Math.Max(0.0, (now - lastBotTime).TotalSeconds)
            : 9999.0;

        int recentMsgCount = 0;
        for (int i = window.Count - 1; i >= 0; i--)
        {
            var msg = window[i];
            if (msg.Timestamp > DateTimeOffset.MinValue && now >= msg.Timestamp && (now - msg.Timestamp).TotalSeconds <= 60.0)
            {
                recentMsgCount++;
            }
        }

        var activity = recentMsgCount switch
        {
            >= 8 => AtmosphereActivity.Heated,
            >= 3 => AtmosphereActivity.Active,
            >= 1 => AtmosphereActivity.Moderate,
            _ => AtmosphereActivity.Quiet
        };

        return new AtmosphereSnapshot(
            Activity: activity,
            DominantVibe: string.Empty,
            ConsecutiveBotReplies: consecutiveBotReplies,
            SecondsSinceLastBotReply: secondsSinceLastBot,
            RecentMessageCount: recentMsgCount);
    }
}

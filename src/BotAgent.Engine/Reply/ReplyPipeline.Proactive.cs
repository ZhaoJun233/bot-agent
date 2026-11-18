using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    /// <summary>“有人在住的话题”的判据：最近 5 分钟内至少这么多条群友发言。</summary>
    private const int ProactiveBurstMessages = 3;
    /// <summary>
    /// 主动开口：没有人 @ 它、也没人在问它的时候，它自己接一句。
    ///
    /// 为什么要它：管理员要的是“陪伴感” —— 只在被叫时才出声，本质是个应答机器。
    /// 什么情况才允许主动（宁可少也不能烦人）：
    ///   • 群聊 + AI 开着 + 白名单内 + 没在冷却；
    ///   • 群里已经安静下来（≥ <see cref="AppSettings.ProactiveQuietSeconds" /> 秒没人说话）—— 不然就是抢话；
    ///   • 机器人上一条不是最最后一条（上一条是它说的，就不要再自说自话）；
    ///   • 插话概率与完整发送后的冷却由统一调度器判定；
    ///   • 而且得有个“由头”：要么它上一轮读到有人情绪低落/在求助，要么群里刚刚聊得热（≥ 3 条/5 分钟）—— 接一句话题。
    /// 不满足就什么都不做（不出声也是陪伴）。
    /// </summary>
    private void TryProactiveSpeak()
    {
        if (!_settings.EnableProactive)
        {
            return;
        }

        var now = EngineClock.Now;
        var quiet = TimeSpan.FromSeconds(Math.Max(1, _settings.ProactiveQuietSeconds));

        foreach (var conversation in _registry.Snapshot())
        {
            var source = (_source as IPlatformSourceRouter)?.CaptureForConversation(conversation.SourceKey);
            if (_source is IPlatformSourceRouter && (source is null || !source.IsCurrent)) continue;
            using var sourceScope = PlatformSourceExecution.Enter(source);
            if (_riskBackoff?.IsActive(conversation.SourceKey) == true)
            {
                continue;
            }

            if (conversation.Kind != ConversationKind.GroupChat || conversation.HasPendingReply)
            {
                continue;
            }

            if (_inFlight.ContainsKey(conversation.SourceKey))
            {
                continue;
            }

            var (isGroup, targetId) = conversation.Target;
            if (!(source is null ? _whitelist.AllowsKey(conversation.SourceKey)
                    : _whitelist.AllowsSource(source.Context, isGroup, targetId)))
            {
                continue;
            }

            var messages = conversation.Messages;
            if (messages.Count == 0)
            {
                continue;
            }

            var last = messages[^1];
            var lastAt2 = last.Timestamp;
            if (now - lastAt2 < quiet)
            {
                continue;   // 群里刚刚还在说，别抢
            }

            if (last.Role == MessageRole.Self)
            {
                continue;   // 最后一句是它自己说的 → 不再自说自话
            }

            // “由头”：情绪低落/求助那边可以主动关心；热闹话题可以接着聊
            var vibe = _vibes.Current(conversation.SourceKey);
            var caringMoment = vibe is "低落" or "求助";
            var burst = messages.Count(m => m.Role == MessageRole.Peer && now - m.Timestamp <= TimeSpan.FromMinutes(5)) >= ProactiveBurstMessages;
            if (!caringMoment && !burst)
            {
                continue;
            }

            // Same human context is one opportunity, even across repeated timer ticks.
            RequestReply(conversation, null, proactive: true);
        }
    }
}

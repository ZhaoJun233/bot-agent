using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{    /// <summary>主动开口前，群里需要安静多久（秒）。太短会显得坐不住。</summary>
    private const int ProactiveQuietDefaultSeconds = 120;
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
    ///   • 同一会话距上次主动 ≥ ProactiveCooldownSeconds；
    ///   • 而且得有个“由头”：要么它上一轮读到有人情绪低落/在求助，要么群里刚刚聊得热（≥ 3 条/5 分钟）—— 接一句话题。
    /// 不满足就什么都不做（不出声也是陪伴）。
    /// </summary>
    private void TryProactiveSpeak()
    {
        if (!_settings.EnableProactive)
        {
            return;
        }

        var cooldown = TimeSpan.FromSeconds(Math.Max(60, _settings.ProactiveCooldownSeconds));
        var now = Clock.Now;
        var quiet = TimeSpan.FromSeconds(Math.Max(1, _settings.ProactiveQuietSeconds));

        foreach (var conversation in _registry.Snapshot())
        {
            var source = (_source as ChannelRouter)?.CaptureForConversation(conversation.SourceKey);
            if (_source is ChannelRouter && (source is null || !source.IsCurrent)) continue;
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
                    : _whitelist.AllowsSource(source.Context, isGroup, targetId)) || !AllowReply(conversation))
            {
                continue;
            }

            if (_lastProactive.TryGetValue(conversation.SourceKey, out var lastAt) && now - lastAt < cooldown)
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

            _lastProactive[conversation.SourceKey] = now;
            _hooks.Log($"[主动] {conversation.Name}：安静 {(now - lastAt2).TotalMinutes:F0} 分钟" +
                    (caringMoment ? $"、上轮气氛「{vibe}」" : "、刚聊得热") + " → 自己开一句");
            EnqueueReply(conversation, null, proactive: true);
            return;   // 一次 tick 只主动一个会话（避免同时到处说话）
        }
    }
}

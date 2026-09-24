using BotAgent.Domain.Conversation;
using BotAgent.Domain.Rendering;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Panel;
using BotAgent.Services.Qq;
using BotAgent.Services.Ports;

namespace BotAgent.Services.Reply;

/// <summary>
/// 「把一段文本发出去并记账」这一件事（用例层最底下的那层发送）：
///   • <see cref="SendWithCadenceAsync" />：聊天回复用的发送（Markdown 降级 → 分句 → 带节奏 → 记账）；
///   • <see cref="SendPlainAsync" />：不走人设、不分句、不受群冷却（agent 回话、审批公告/回执用它）；
///   • <see cref="SendApprovalReplyAsync" />：把审批回执发回"消息来的那个会话"。
///
/// 为什么单拎：回复主链、agent 命令、审批三处都要发消息，各自抄一份"分句 + 记账"迟早不一致；
/// 而且它把 <see cref="OwnMessageLedger" />（"这句话是我哪条消息发的"）的写入收在一处。
/// </summary>
public sealed class PlainSender : IQqMessageSender
{
    private readonly SettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly ConversationRegistry _registry;
    private readonly PanelNotifier _ui;
    private readonly OwnMessageLedger _ownLedger;
    private readonly Action<string> _log;

    public PlainSender(
        SettingsBox box,
        IQqChatSource source,
        ConversationRegistry registry,
        PanelNotifier ui,
        OwnMessageLedger ownLedger,
        Action<string> log)
    {
        _box = box;
        _source = source;
        _registry = registry;
        _ui = ui;
        _ownLedger = ownLedger;
        _log = log;
    }

    private AppSettings _settings => _box.Current;

    /// <summary>
    /// 发送回复。开启分句时按句末标点切分并留出打字间隔（更像真人）；
    /// 只有第一句带 QQ 的"回复"引用，后续分句不带。
    /// </summary>
    public async Task<bool> SendWithCadenceAsync(bool isGroup, long targetId, string reply, long? replyTo)
    {
        // P4（V3 §10）：发送前把 Markdown 降级成 QQ 纯文本。
        var rawReply = reply;
        reply = QqPlainText.Sanitize(reply);
        if (reply.Length == 0)
        {
            _log($"清洗后没有可发内容（原文 {rawReply.Length} 字，全是 Markdown 装饰）→ 这一条不发");
            return false;
        }

        if (!_settings.SplitReplies)
        {
            var one = await _source.SendTextAsync(isGroup, targetId, reply, replyToMessageId: replyTo);
            _ownLedger.Remember(one, reply);
            return one.Ok;
        }

        var segments = TextRules.SplitSentences(reply);
        if (segments.Count <= 1)
        {
            var one = await _source.SendTextAsync(isGroup, targetId, reply, replyToMessageId: replyTo);
            _ownLedger.Remember(one, reply);
            return one.Ok;
        }

        var allOk = true;
        for (var i = 0; i < segments.Count; i++)
        {
            var sent = await _source.SendTextAsync(
                isGroup,
                targetId,
                segments[i],
                replyToMessageId: i == 0 ? replyTo : null);
            _ownLedger.Remember(sent, segments[i]);

            if (!sent.Ok)
            {
                allOk = false;
                _log($"第 {i + 1}/{segments.Count} 段发送失败，停止后续分段");
                break;
            }

            // 打字节奏：基础间隔 + 按字数估算的输入时间
            if (i < segments.Count - 1)
            {
                var delay = Math.Max(0, _settings.SegmentDelayMs);
                await Clock.Delay(delay);
            }
        }

        return allOk;
    }

    /// <summary>发一条纯文本（agent 回话 / 审批公告专用：不走人设、不分句、不受群冷却限制）。</summary>
    public async Task SendPlainAsync(BotConversation conversation, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var (isGroup, targetId) = conversation.Target;
        var index = 0;
        foreach (var segment in SplitForChat(text, Math.Clamp(_settings.AgentReplyMaxChars, 200, 3000)))
        {
            index++;
            var result = await _source.SendTextAsync(isGroup, targetId, segment);
            _log($"agent 回话 → {(isGroup ? "群" : "私聊")}{targetId}（第 {index} 段，{segment.Length} 字，{(result.Ok ? "已发出" : "发送失败")}）: {TextRules.Shorten(segment.Replace('\n', ' '), 60)}");
            _ownLedger.Remember(result, segment);

            // 记进上下文：下一轮人设路线能看到"本机 agent 刚做了什么"，不会把它当外人说的话
            var appended = new ChatMessage
            {
                Role = MessageRole.Self,
                Text = segment,
                Timestamp = Clock.Now,
                QqMessageId = result.MessageId > 0 ? result.MessageId : null
            };
            conversation.Append(appended);
            _ui.NotifyMessageAdded(conversation.SourceKey, appended);
        }

        _registry.Touch(conversation);
        _registry.Save();
    }

    /// <summary>把审批回执发回原会话（走既有发送链路；失败只记日志，不影响别的会话）。</summary>
    public async Task SendApprovalReplyAsync(QqChatMessage msg, string text)
    {
        try
        {
            var isGroup = msg.IsGroup;
            var targetId = isGroup ? msg.GroupId : msg.UserId;
            var ok = await SendWithCadenceAsync(isGroup, targetId, text, msg.MessageId);
            if (!ok)
            {
                _log("[审批] 回执没发出去（协议端拒绝或超时）");
            }
        }
        catch (Exception ex)
        {
            _log("[审批] 回执发送异常: " + ex.Message);
        }
    }

    /// <summary>把长文本切成能发出去的消息（QQ 单条太长会被吞；按行/句尽量切得好看）。</summary>
    private static IEnumerable<string> SplitForChat(string text, int maxChars)
    {
        text = text.Replace("\r\n", "\n").Trim();
        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        var rest = text;
        var index = 0;
        while (rest.Length > 0 && index < 8)     // 最多 8 条，剩下用省略号收尾
        {
            index++;
            if (rest.Length <= maxChars)
            {
                yield return rest;
                yield break;
            }

            var cut = rest.LastIndexOf('\n', maxChars - 1);
            if (cut < maxChars / 3)
            {
                cut = rest.LastIndexOf('。', maxChars - 1);
            }

            if (cut < maxChars / 3)
            {
                cut = maxChars - 1;
            }

            yield return rest[..(cut + 1)].TrimEnd();
            rest = rest[(cut + 1)..].TrimStart();
        }

        if (rest.Length > 0)
        {
            yield return $"（输出太长，后面省略了 {rest.Length} 字）";
        }
    }
}

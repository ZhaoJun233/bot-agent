using BotAgent.Engine.Runtime;
using System.Text.Json;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Rendering;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Qq;
using BotAgent.Services.Ports;
using BotAgent.Services.Resilience;
using BotAgent.Services.Platforms;

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
public sealed class PlainSender : IQqMessageSender, IConversationReplySender
{
    private readonly ISettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly ConversationRegistry _registry;
    private readonly IPanelNotifier _ui;
    private readonly OwnMessageLedger _ownLedger;
    private readonly Action<string> _log;
    private readonly TurnTraceStore _traces;
    private readonly IAuditChain? _audit;
    private readonly IProtocolRiskBackoff? _riskBackoff;
    private readonly IPlatformPolicyResolver? _platformPolicies;
    private readonly ActionNarrationFilter? _actionNarrationFilter;

    public PlainSender(
        ISettingsBox box,
        IQqChatSource source,
        ConversationRegistry registry,
        IPanelNotifier ui,
        OwnMessageLedger ownLedger,
        Action<string> log,
        TurnTraceStore traces,
        IAuditChain? audit = null,
        IProtocolRiskBackoff? riskBackoff = null,
         IPlatformPolicyResolver? platformPolicies = null,
         IModelClient? model = null)
    {
        _box = box;
        _source = source;
        _registry = registry;
        _ui = ui;
        _ownLedger = ownLedger;
        _log = log;
        _traces = traces;
        _audit = audit;
        _riskBackoff = riskBackoff;
        _platformPolicies = platformPolicies;
        _actionNarrationFilter = model is null ? null : new ActionNarrationFilter(model, box);
    }

    private AppSettings _settings => _box.Current;

    /// <summary>
    /// 发送回复。开启分句时按句末标点切分并留出打字间隔（更像真人）；
    /// 只有第一句带 QQ 的"回复"引用，后续分句不带。
    /// 返回逐段报告（issue #14）：调用方只把**真的发出去**的段落写进会话历史。
    /// </summary>
    public Task<CadenceSendReport> SendWithCadenceAsync(bool isGroup, long targetId, string reply, long? replyTo, bool directAddress = false)
        => SendWithCadenceCoreAsync(null, isGroup, targetId, reply, replyTo, directAddress, null);

    /// <summary>完整 sourceKey 仅用于记账，不改变现有策略、协议发送或 targetId 路由。</summary>
    public Task<CadenceSendReport> SendWithCadenceAsync(string sourceKey, bool isGroup, long targetId,
        string reply, long? replyTo, bool directAddress = false)
    {
        if (!ConversationIdCodec.TryParse(sourceKey, out var conversation))
            return Task.FromResult(new CadenceSendReport(Array.Empty<string>(), "invalid_conversation_scope"));
        return SendWithCadenceCoreAsync(null, isGroup, targetId, reply, replyTo, directAddress, conversation);
    }

    /// <summary>
    /// 支持显式 PlatformContext 的重载：优先使用显式上下文，未提供时回退到 targetId 号段推断。
    /// </summary>
    public Task<CadenceSendReport> SendWithCadenceAsync(
        PlatformContext? context,
        bool isGroup,
        long targetId,
        string reply,
        long? replyTo,
        bool directAddress = false)
        => SendWithCadenceCoreAsync(context, isGroup, targetId, reply, replyTo, directAddress, null);

    private async Task<CadenceSendReport> SendWithCadenceCoreAsync(PlatformContext? context,
        bool isGroup, long targetId, string reply, long? replyTo, bool directAddress, ConversationId? conversationScope)
    {
        context = PlatformSourceExecution.Current?.Context ?? context;
        var policy = context is not null && _platformPolicies is not null
            ? _platformPolicies.Resolve(context)
            : ResolveTargetPolicy(isGroup, targetId);
        var textDecision = policy?.Feature("text", globallyEnabled: true);
        if (textDecision is { Enabled: false })
        {
            _log($"文本出站被平台策略拒绝（{textDecision.ReasonCode}，target={(isGroup ? "group" : "private")}）");
            return new CadenceSendReport(Array.Empty<string>(), textDecision.ReasonCode);
        }

        // P4（V3 §10）：发送前把 Markdown 降级成 QQ 纯文本。
        // 批次 C 的回复审计：凭据形状、或（聊天这一路）本机/服务器路径形状 → **整条不发**。
        // 记的是原因码与字数，**不记正文** —— 审计本身不能变成新的隐私面。
        var audit = ReplyAuditRules.Judge(reply, allowLocalPaths: false);
        if (audit != ReplyAuditVerdict.Allow)
        {
            RecordDlpBlock(audit, "chat", reply.Length);
            _log($"[审计] 这条回复不发（{ReplyAuditRules.Code(audit)}；{reply.Length} 字）");
            return new CadenceSendReport(Array.Empty<string>(), ReplyAuditRules.Code(audit));
        }

        var rawReply = reply;
        if (_settings.FilterActionNarration && _actionNarrationFilter is not null)
        {
            reply = await _actionNarrationFilter.FilterAsync(reply).ConfigureAwait(false);
            // 改写也必须通过既有审计；新引入敏感形状时回退到已审计的原回复。
            if (ReplyAuditRules.Judge(reply, allowLocalPaths: false) != ReplyAuditVerdict.Allow)
                reply = rawReply;
        }
        reply = QqPlainText.Sanitize(reply);
        if (reply.Length == 0)
        {
            _log($"清洗后没有可发内容（原文 {rawReply.Length} 字，全是 Markdown 装饰或动作旁白）→ 这一条不发");
            return new CadenceSendReport(Array.Empty<string>(), "sanitized_empty");
        }

        var sourceKey = BotAgent.Domain.Qq.Channels.Key(BotAgent.Domain.Qq.Channels.IsAliasId(targetId) ? BotAgent.Domain.Qq.Channels.Official : BotAgent.Domain.Qq.Channels.IsLocalId(targetId) ? BotAgent.Domain.Qq.Channels.Local : BotAgent.Domain.Qq.Channels.IsFeishuId(targetId) ? BotAgent.Domain.Qq.Channels.Feishu : BotAgent.Domain.Qq.Channels.Private, isGroup, targetId);
        if (_riskBackoff?.IsActive(sourceKey) == true)
        {
            var decision = _riskBackoff.EvaluateText(sourceKey, directAddress, reply);
            if (!decision.Allowed)
            {
                _traces.Node(sourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: decision.ReasonCode);
                return new CadenceSendReport(Array.Empty<string>(), decision.ReasonCode);
            }

            var shortReply = decision.Text;
            var one = await _source.SendTextAsync(isGroup, targetId, shortReply, replyToMessageId: replyTo, directAddress: true);
            RememberOwnMessage(conversationScope, context ?? policy?.Context, isGroup, targetId, one, shortReply);
            _traces.Node(sourceKey, TurnNodeKind.Outbound, one.Ok ? "sent" : "failed", reasonCode: decision.ReasonCode, count: shortReply.Length);
            return one.Ok
                ? new CadenceSendReport(new[] { shortReply })
                : new CadenceSendReport(Array.Empty<string>(), decision.ReasonCode);
        }
        if (!_settings.SplitReplies)
        {
            var one = await _source.SendTextAsync(isGroup, targetId, reply, replyToMessageId: replyTo, directAddress: directAddress);
            RememberOwnMessage(conversationScope, context ?? policy?.Context, isGroup, targetId, one, reply);
            return one.Ok ? new CadenceSendReport(new[] { reply }) : new CadenceSendReport(Array.Empty<string>(), "send_failed");
        }

        var segments = TextRules.SplitSentences(reply);
        if (segments.Count <= 1)
        {
            var one = await _source.SendTextAsync(isGroup, targetId, reply, replyToMessageId: replyTo, directAddress: directAddress);
            RememberOwnMessage(conversationScope, context ?? policy?.Context, isGroup, targetId, one, reply);
            return one.Ok ? new CadenceSendReport(new[] { reply }) : new CadenceSendReport(Array.Empty<string>(), "send_failed");
        }

        // 分句发送：**逐段记账**。以前只回一个 bool —— 前几段成功、后面失败时，
        // 调用方只看到 false，于是"实际发出去的段落"根本没写进会话历史（issue #14）。
        var delivered = new List<string>();
        string? failure = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var sent = await _source.SendTextAsync(
                isGroup,
                targetId,
                segments[i],
                replyToMessageId: i == 0 ? replyTo : null, directAddress: directAddress);
            RememberOwnMessage(conversationScope, context ?? policy?.Context, isGroup, targetId, sent, segments[i]);

            if (!sent.Ok)
            {
                failure = "segment_failed";
                _log($"第 {i + 1}/{segments.Count} 段发送失败，停止后续分段（已发出 {delivered.Count} 段）");
                break;
            }

            delivered.Add(segments[i]);

            // 打字节奏：基础间隔 + 按字数估算的输入时间
            if (i < segments.Count - 1)
            {
                var delay = Math.Max(0, _settings.SegmentDelayMs);
                await EngineClock.Delay(delay);
            }
        }

        return new CadenceSendReport(delivered, failure);
    }

    /// <summary>发一条纯文本（agent 回话 / 审批公告专用：不走人设、不分句、不受群冷却限制）。</summary>
    public async Task SendPlainAsync(BotConversation conversation, string text)
    {
        var source = PlatformSourceExecution.Current ?? (_source as IPlatformSourceRouter)?.CaptureForConversation(conversation.SourceKey);
        if (_source is IPlatformSourceRouter && (source is null || !source.IsCurrent)) return;
        using var sourceScope = PlatformSourceExecution.Enter(source);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 批次 C 的回复审计：`//` 那一路**允许报路径**（答案本来就该带路径，且只有白名单用户看得到），
        // 但**凭据形状一律挡**（这条没有例外档）。
        var audit = ReplyAuditRules.Judge(text, allowLocalPaths: true);
        if (audit != ReplyAuditVerdict.Allow)
        {
            RecordDlpBlock(audit, "agent", text.Length);
            _log($"[审计] agent 回话不发（{ReplyAuditRules.Code(audit)}；{text.Length} 字）");
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: ReplyAuditRules.Code(audit));
            return;
        }

        var (isGroup, targetId) = conversation.Target;
        var channel = Channels.ChannelOf(conversation.SourceKey);
        var policy = PlatformSourceExecution.Current is { } lease && _platformPolicies is not null
            ? _platformPolicies.Resolve(lease.Context)
            : !string.IsNullOrEmpty(channel) && _platformPolicies is not null
            ? _platformPolicies.ResolveForChannel(channel)
            : ResolveTargetPolicy(isGroup, targetId);
        var textDecision = policy?.Feature("text", globallyEnabled: true);
        if (textDecision is { Enabled: false })
        {
            _log($"agent 文本出站被平台策略拒绝（{textDecision.ReasonCode}）");
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: textDecision.ReasonCode);
            return;
        }

        var index = 0;
        foreach (var segment in SplitForChat(text, Math.Clamp(_settings.AgentReplyMaxChars, 200, 3000)))
        {
            index++;
            var result = await _source.SendTextAsync(isGroup, targetId, segment);
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, result.Ok ? "sent" : "failed", count: segment.Length);
            _log($"agent 回话 → {(isGroup ? "群" : "私聊")}{targetId}（第 {index} 段，{segment.Length} 字，{(result.Ok ? "已发出" : "发送失败")}）: {TextRules.Shorten(segment.Replace('\n', ' '), 60)}");
            _ownLedger.Remember(conversation.SourceKey, result, segment);

            // 记进上下文：下一轮人设路线能看到"本机 agent 刚做了什么"，不会把它当外人说的话
            var appended = new ChatMessage
            {
                Role = MessageRole.Self,
                Text = segment,
                Timestamp = EngineClock.Now,
                QqMessageId = result.MessageId > 0 ? result.MessageId : null
            };
            conversation.Append(appended);
            _ui.NotifyMessageAdded(conversation.SourceKey, appended);
        }

        _registry.Touch(conversation);
        _registry.Save();
    }

    private EffectivePlatformPolicy? ResolveTargetPolicy(bool isGroup, long targetId)
    {
        if (_platformPolicies is null || targetId <= 0)
        {
            return null;
        }

        if (PlatformSourceExecution.Current is { } lease) return _platformPolicies.Resolve(lease.Context);
        var channel = Channels.IsAliasId(targetId)
            ? Channels.Official
            : Channels.IsLocalId(targetId)
                ? Channels.Local
                : Channels.IsFeishuId(targetId)
                    ? Channels.Feishu
                    : Channels.Private;
        return _platformPolicies.ResolveForChannel(channel);
    }

    private void RememberOwnMessage(ConversationId? conversationScope, PlatformContext? context,
        bool isGroup, long targetId, SendResult sent, string text)
    {
        if (conversationScope is not null)
        {
            _ownLedger.Remember(conversationScope, sent, text);
            return;
        }
        if (targetId <= 0) return;
        if (context is not null)
        {
            _ownLedger.Remember(new ConversationId(context.PlatformId, context.AccountScope,
                isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
                targetId.ToString(System.Globalization.CultureInfo.InvariantCulture)), sent, text);
            return;
        }
        // Compatibility send uses IPlatformSourceRouter's existing disjoint target ranges, not a global message id.
        var channel = Channels.IsAliasId(targetId) ? Channels.Official
            : Channels.IsLocalId(targetId) ? Channels.Local
            : Channels.IsFeishuId(targetId) ? Channels.Feishu : Channels.Private;
        _ownLedger.Remember(Channels.Key(channel, isGroup, targetId), sent, text);
    }

    private void RecordDlpBlock(ReplyAuditVerdict verdict, string route, int length)
    {
        if (_audit is null)
        {
            return;
        }

        var detail = JsonSerializer.Serialize(new
        {
            result = "blocked",
            route,
            reason = ReplyAuditRules.Code(verdict),
            length = Math.Max(0, length)
        });
        try
        {
            _audit.Append(new AuditEvent("dlp_block", "reply-pipeline", "redacted", detail, "2.1"));
        }
        catch (Exception ex)
        {
            _log($"[审计] DLP 事件写入失败（{ex.GetType().Name}）");
        }
    }
    /// <summary>把审批回执发回原会话（走既有发送链路；失败只记日志，不影响别的会话）。</summary>
    public async Task SendApprovalReplyAsync(QqChatMessage msg, string text)
    {
        try
        {
            var isGroup = msg.IsGroup;
            var targetId = isGroup ? msg.GroupId : msg.UserId;
            var channel = Channels.Declared(msg.Channel);
            var platform = !string.IsNullOrEmpty(channel) ? PlatformId.Normalize(channel) : null;
            var account = platform is PlatformId.QqPrivate or PlatformId.QqOfficial or PlatformId.Local
                ? AccountScope.Legacy
                : AccountScope.Default;
            var context = platform is not null ? new PlatformContext(platform, account) : null;
            var ok = (await SendWithCadenceAsync(context, isGroup, targetId, text, msg.MessageId, directAddress: true)).AnySent;
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

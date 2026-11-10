using BotAgent.Services.Plugins;
using BotAgent.Services.Evaluation;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Stickers;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Links;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.OneBot;
using BotAgent.Services.Panel;
using BotAgent.Services.Ports;
using BotAgent.Services.Ops;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Poke;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Services.Resilience;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;
using System.Collections.Concurrent;
using System.Text;

namespace BotAgent.Services.Reply;

/// <summary>Confirmed chat delivery only; images, pokes and partial replies never start a completion cooldown.</summary>
public sealed record ChatDeliveryReport(bool AnyChatSent, bool CompleteChatReply)
{
    public static ChatDeliveryReport FromAcknowledgements(CadenceSendReport textReport, bool textIntended,
        IReadOnlyList<string> voiceParts, bool voiceSent, IReadOnlyList<string> voiceFailed, bool extrasComplete = true)
    {
        var textSent = textReport.SentSegments.Any(part => !string.IsNullOrWhiteSpace(part));
        var anyChatSent = textSent || (voiceSent && voiceParts.Any(part => !string.IsNullOrWhiteSpace(part)));
        var completeVoice = voiceParts.Count == 0 || (voiceSent && voiceFailed.Count == 0);
        var completeText = !textIntended || (textSent && textReport.AllSent);
        return new ChatDeliveryReport(anyChatSent, anyChatSent && completeVoice && completeText && extrasComplete);
    }
}

/// <summary>
/// 一轮回复的**投递**（#106 第 6 步）：把已经想好的话说出去，并如实记账。
/// <para>
/// 这一段管三个不变量（搬出来一条都没改语义）：
/// ① **语音与文字去重** —— 语音已经把那句话说完了就不再发同一句文字（按去掉标点后是否同一句判断，
///    不靠"含几个数字"这类猜测）；模型明确 both:true 才两个都发；文字里有链接时强制补发。
/// ② **引用只挂第一条** —— 分句发送时引用挂第一段（图与戳不带引用）。
/// ③ **戳一戳要过三道门** —— 号码在当前上下文里出现过（防编造）→ 心情门 → 频率门 + 能力闸门。
/// </para>
/// <para>
/// 边界：这里**不判断该不该回**（策略层的事），也**不生成内容**（模型与决策动作的事）。
/// 它只负责"发出去"和"把发出去的事实记进上下文、参与状态机、运行日志"。
/// 失败必须可见：发不出去就记失败节点，不静默当成功。
/// </para>
/// </summary>
internal sealed partial class ReplyDelivery(
    ReplyHooks hooks,
    ConversationRegistry registry,
    IPanelNotifier ui,
    IQqMessageSender plain,
    IQqChatSource source,
    StickerService stickers,
    IVoiceUseCase voice,
    PokeUseCase poke,
    IMoodRepository mood,
    ApprovalUseCase approvals,
    ParticipationUseCase participation,
    TurnTraceStore traces,
    IProtocolRiskBackoff? riskBackoff,
    Func<string, bool> allowsFeature,
    ISettingsBox? settingsBox = null,
    IPlatformPolicyResolver? platformPolicies = null)
{
    private readonly ReplyHooks _hooks = hooks;
    private readonly ConversationRegistry _registry = registry;
    private readonly IPanelNotifier _ui = ui;
    private readonly IQqMessageSender _plain = plain;
    private readonly IQqChatSource _source = source;
    private readonly StickerService _stickers = stickers;
    private readonly IVoiceUseCase _voice = voice;
    private readonly PokeUseCase _poke = poke;
    private readonly IMoodRepository _mood = mood;
    private readonly ApprovalUseCase _approvals = approvals;
    private readonly ParticipationUseCase _participation = participation;
    private readonly TurnTraceStore _traces = traces;
    private readonly IProtocolRiskBackoff? _riskBackoff = riskBackoff;
    private readonly Func<string, bool> _allowsFeature = allowsFeature;
    private readonly ISettingsBox? _settingsBox = settingsBox;
    private readonly IPlatformPolicyResolver? _platformPolicies = platformPolicies;

    /// <summary>
    /// 语音那条路（第 4 步之一）：过技术性限制（开关 / 字数上限 / 同会话频率下限 / 能力闸门）→ 拼 /speak URL
    /// → 逐段交给协议端发。哪段没发出去就把它记下来当文字补（内容不能丢）。
    /// 「该不该发语音」**不由这里判断** —— 那是模型的事（管理员 2026-09-21 明确过）。
    /// </summary>
    public async Task<(bool VoiceSent, List<string> VoiceFailed)> TryVoiceAsync(
         BotConversation conversation,
         AppSettings snapshot,
         Domain.Permissions.ChatCapabilitySet caps,
         EffectivePlatformPolicy platformPolicy,
        List<string> voiceParts,
        string voiceText,
        bool isGroup,
        long targetId,
        CompletionResult result,
        bool directAddress)
    {
        if (_riskBackoff?.IsActive(conversation.SourceKey) == true)
        {
            return (false, new List<string>());
        }

        // ───── 语音（模型填了 speak）─────
        // 怎么发：只把 TTS 的 /speak URL 交给协议端，让 NapCat 自己去下载 → 转 silk → 上传
        // （见 IPlatformHistorySource.SendVoiceAsync）—— 机器人这边不碰音频编码。
        // 为什么得克制：合成要几秒 CPU、音频占流量、群里语音连发就是刷屏。
        // 提示词让它“偶尔用”，代码侧再加一道同会话 45 秒的闸门。
        string? voiceUrl = null;
        var voiceUrls = new List<string>();
        string? voiceSkipWhy = null;
        if (voiceParts.Count > 0)
        {
            // 2026-09-21（管理员要求“什么时候发语音让模型自己定”）：这里以前还有一道
            // “气氛沉（有人低落/在吵架）就一律不发语音”的硬拦，已删——那本来就是判断类的事，
            // 现在只把气氛（vibeHint）递给模型看，由它自己权衡。
            // 代码侧只留“技术性”限制：开关、字数上限（云端/协议端真有上限）、同会话频率下限（防刷屏）。
            var maxChars = Math.Clamp(snapshot.VoiceMaxChars, 10, 300);
            if (!platformPolicy.Feature("voice", snapshot.EnableVoice).Enabled || !_allowsFeature(PluginGate.VoiceCapability) || _voice is null)
            {
                voiceSkipWhy = "语音消息开关是关的";
            }
            else if (!_approvals.AllowCapability(conversation, "voice.speak", null, out var voiceCapWhy, pinned: caps))
            {
                voiceSkipWhy = $"能力闸门拒绝（{voiceCapWhy}）";
            }
            else if (voiceParts.FirstOrDefault(p => p.Length > maxChars) is { } longPart)
            {
                voiceSkipWhy = $"{longPart.Length} 字超过上限 {maxChars}";
            }
            else if (!AllowVoice(conversation, out var voiceReason))
            {
                voiceSkipWhy = voiceReason;
            }
            // 语速/情绪/音调**由模型按语境自己定**（管理员 2026-09-21）：它给了就用它的，
            // 没给就退回面板里那三个默认值；面板值仍受同样范围限制。
            else
            {
                // 拼 /speak URL（第 2、3 段同一套语气参数）：细节在 IVoiceUseCase，这里只管编排
                voiceUrls = _voice.BuildSpeakUrls(voiceParts, result.VoiceEmotion, result.VoiceSpeed, result.VoicePitch);
                if (voiceUrls.Count == 0)
                {
                    voiceSkipWhy = "TTS 服务地址没配置（应形如 http://tts:5000）";
                }
                else
                {
                    voiceUrl = voiceUrls[0];
                }
            }
        }

        var voiceSent = false;
        var voiceFailed = new List<string>();
        if (voiceUrls.Count > 0)
        {
            // 模型把话切成了几段 → 每段一条语音条（≤3 条）。
            // 哪段没发出去，就把那段当文字补上（内容不能丢）。
            for (var i = 0; i < voiceUrls.Count; i++)
            {
                var part = i < voiceParts.Count ? voiceParts[i] : voiceText;
                bool ok;
                try
                {
                    using var validation = ReplyCommitBoundary.EnterValidation(() =>
                        ValidateModality(conversation, snapshot, platformPolicy, "voice", PluginGate.VoiceCapability, "voice.speak"));
                    await ReplyCommitBoundary.BeforeSourceSendAsync(_source);
                    ok = await _source.SendVoiceAsync(isGroup, targetId, voiceUrls[i]);
                }
                catch (Exception) when (voiceSent && ReplyCommitBoundary.IsActive)
                {
                    voiceFailed.AddRange(voiceParts.Skip(i));
                    break;
                }
                if (!ok)
                {
                    if (part.Length > 0) voiceFailed.Add(part);
                    _hooks.Log(voiceUrls.Count > 1
                        ? $"[Voice] 第 {i + 1}/{voiceUrls.Count} 段没发出去 → 这段改发文字"
                        : "[Voice] 语音没发出去 → 改发文字（具体原因见上一行的 retcode/响应体）");
                    continue;
                }

                voiceSent = true;
                _voice?.MarkSent(conversation.SourceKey);
                // 把模型给的语气参数也记下来 —— 不然“它到底有没有按语境调情绪”没法验证
                var tone = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.VoiceEmotion)) tone.Add("情绪 " + result.VoiceEmotion);
                if (result.VoiceSpeed is double ms) tone.Add($"语速 {ms:0.##}");
                if (result.VoicePitch is int mp) tone.Add($"音调 {mp:+#;-#;0}");
                var voiceName = _voice?.ClientVoiceName ?? "默认";
                _hooks.Log($"[Voice] 已发语音{(voiceUrls.Count > 1 ? $"（第 {i + 1}/{voiceUrls.Count} 段）" : string.Empty)}"
                        + $"（{part.Length} 字，音色 {voiceName}"
                        + (tone.Count > 0 ? "，模型定的 " + string.Join('/', tone) : "，模型未指定语气（用面板默认）")
                        + $"）：{TextRules.Shorten(part, 40)}");
            }
        }
        else if (voiceSkipWhy is not null)
        {
            _hooks.Log($"[Voice] 模型想用语音说，但{voiceSkipWhy} → 改发文字");
        }

        return (voiceSent, voiceFailed);
    }

    /// <summary>
    /// 一轮回复的**发送与记账**（第 5 步）：语音/文字去重 → 写进上下文 → 实际发送（文字/图/戳）→ 喂参与状态机 → 写运行日志。
    /// 不变量：**引用只挂第一条**（文字 + 图时图不带引用）、**语音/文字去重**（同一句不重复发）、
    /// 戳一戳要过三道门（号码出现过 / 心情 / 频率 + 能力闸门）。
    /// 拆出来只为了可读性：这里的一行都没改语义 —— 位置、顺序、日志措辞都是原来那句。
    /// </summary>
    public async Task<ChatDeliveryReport> SendAsync(
         BotConversation conversation,
         IReadOnlyList<ChatMessage> context,
         long? triggerMessageId,
         Domain.Permissions.ChatCapabilitySet caps,
         EffectivePlatformPolicy platformPolicy,
        bool isGroup,
        long targetId,
        long? replyTo,
        string reply,
        string voiceText,
        List<string> voiceParts,
        List<string> voiceFailed,
        bool voiceSent,
        StickerRecord? sticker,
        long? pokeTarget,
        double elapsed,
        CompletionResult result,
        bool directAddress)
    {
        // 到底还发不发文字：
        //   • 语音发成功了、且 reply 就是那句话（或没写 reply）→ 不再重复发同一句；
        //   • 语音发成功了、但 reply 另写了内容 → 那是模型自己想补的话，照发；
        //   • 语音没发出去 → 至少把要说的话当文字发出去。
        var textReply = reply.Length > 0 ? reply : null;
        if (voiceParts.Count > 0)
        {
            if (voiceSent)
            {
                // 去重（2026-09-21 修）：以前只在**一字不差**时才吞掉文字 ✗ —— speak「好呀，那我们八点见」
                // 配 reply「好呀八点见！」就会语音+文字把同一句发两遍 ✗。现在按“去标点后是否同一句 /
                // 是否互相包含”判断。
                var said = string.Join(" ", voiceParts.Where(p => !voiceFailed.Contains(p)));
                if (textReply is not null && TextRules.SameSaid(textReply, said))
                {
                    _hooks.Log($"[Voice] 语音与文字是同一句（{TextRules.Shorten(textReply, 24)}）→ 不再重复发文字");
                    textReply = null;
                }
                else if (textReply is not null && said.Length > 0)
                {
                    // 口径（2026-09-21 第三次定）：**看不看这段文字由模型说了算** ——
                    // 它在 JSON 里加了 both:true 就照发 ✓；没加就默认“语音已经把这轮话说完了” → 不重复 ✗。
                    // 之前用“含不含 5 位数字/8 个字母”猜 ✗ ——那种规则既解释不清也会误伤（“明天 8 点见”就被吞 ✗）。
                    // 唯一保留的自动补发：**链接**（念出来完全没用，这是物理原因，不是猜）。
                    if (result.Both)
                    {
                        _hooks.Log("[Voice] 模型要求语音+文字都发（both）→ 文字照发");
                    }
                    else if (TextRules.CarriesLink(textReply))
                    {
                        _hooks.Log("[Voice] 文字里有链接（语音念不出来）→ 补发文字");
                    }
                    else
                    {
                        _hooks.Log($"[Voice] 语音说了这轮的话{(TextRules.SameSaid(textReply, said) ? "（就是同一句）" : string.Empty)}"
                                + $" → 文字不再重复发（被吞掉 {textReply.Length} 字；想同时发文字需 both:true）");
                        textReply = null;
                    }
                }
            }
            else
            {
                textReply ??= string.Join(" ", voiceParts);
            }

            // 个别段没发出去的：那几段当文字补上
            if (voiceFailed.Count > 0)
            {
                var fallback = string.Join(" ", voiceFailed.Where(p => p.Length > 0));
                if (fallback.Length > 0)
                {
                    textReply = textReply is null ? fallback : textReply + "\n" + fallback;
                }
            }
        }

        if (_riskBackoff?.IsActive(conversation.SourceKey) == true)
        {
            var mentioned = triggerMessageId is long mentionId &&
                conversation.Messages.FirstOrDefault(m => m.QqMessageId == mentionId)?.MentionedBot == true;
            var decision = _riskBackoff.EvaluateText(conversation.SourceKey, mentioned, textReply ?? string.Empty);
            if (!decision.Allowed || decision.Text.Length == 0)
            {
                _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: "protocol_backoff");
                return new ChatDeliveryReport(voiceSent, false);
            }
            textReply = decision.Text;
            sticker = null;
            pokeTarget = null;
        }

        var sent = voiceSent;
        var sendDirect = directAddress && triggerMessageId is long mentionTrigger &&
            conversation.Messages.FirstOrDefault(m => m.QqMessageId == mentionTrigger)?.MentionedBot == true;
        // 分句发送的**逐段**结果（issue #14）：前几段成功、后面失败时，
        // 只有真正发出去的段落才写进会话历史 —— 不能因为"整条返回 false"就把已发的当成没发。
        var effectiveReplyTo = platformPolicy.Feature("quote", true).Enabled ? replyTo : null;
        var sendReport = await SendTextAfterVoiceAsync(conversation, isGroup, targetId, textReply, effectiveReplyTo,
            _riskBackoff?.IsActive(conversation.SourceKey) == true ? sendDirect : directAddress, voiceSent);
        var sentText = sendReport.AnySent;
        if (sentText)
        {
            sent = true;
        }
        _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound,
            textReply is null ? "silent" : sendReport.AllSent ? "sent" : sentText ? "partial" : "blocked",
            reasonCode: sendReport.FailureReasonCode, count: textReply?.Length);

        // 引用只给第一条消息，避免“文字 + 图”两条都带引用。
        var (sentImage, pokeSent) = await SendExtrasAsync(conversation, context, caps, platformPolicy,
            isGroup, targetId, textReply is not null ? null : replyTo, sticker, pokeTarget, sent);
        sent |= sentImage;

        if (sent || pokeSent)
        {
            // 文字那一段：全部发出 → 记原文（与改造前逐字一致）；部分成功 → 只记**实际发出**的段落。
            var recordedTextBody = sentText
                ? sendReport.AllSent ? textReply! : sendReport.Text
                : null;
            var recordedText = voiceSent
                ? recordedTextBody is not null ? $"{recordedTextBody}（同时用语音说：{voiceText}）" : $"[语音] {voiceText}"
                : recordedTextBody is not null ? recordedTextBody
                : sentImage ? "[表情包]"
                : "[戳一戳]";
            var appended = new ChatMessage
            {
                Role = MessageRole.Self,
                Text = recordedText,
                Timestamp = EngineClock.Now
            };
            conversation.Append(appended);
            _registry.Touch(conversation);
            _ui.NotifyMessageAdded(conversation.SourceKey, appended);
            _registry.Save();
        }

        // 引用目标写进日志（handoff-4 §23.3 B：“真验证需要把每次带引用的回复 + 上下文存下来人工看几十条”）。
        // 只写“带引用”的话，事后根本看不出引到了谁头上 —— 复盘只能靠猜。
        // 2026-09-16 加：连**触发那条**也写上 —— “正文回答 A、引用挂到 B”这类错位，
        // 只有把两边摆在一起才看得出来（管理员反馈“引用错误”时就是靠这个定位的）。
        var quoteNote = string.Empty;
        if (triggerMessageId is long loggedTriggerId)
        {
            var trig = context.FirstOrDefault(m => m.QqMessageId == loggedTriggerId);
            if (trig is not null)
            {
                quoteNote += "，触发→" + (trig.SenderName ?? "?") + "「" + TextRules.Shorten(trig.Text ?? string.Empty, 14) + "」";
            }
        }

        if (replyTo is long loggedQuoteId)
        {
            var quoted = context.FirstOrDefault(m => m.QqMessageId == loggedQuoteId);
            quoteNote = quoted is null
                ? "，带引用"
                : "，带引用→" + (quoted.SenderName ?? "?") + "「" + TextRules.Shorten(quoted.Text ?? string.Empty, 18) + "」";
        }

        // P1（只观测）：把这轮的终态喂给参与状态机 —— 发出去了=Replied，没发出去=Silent。
        // 注意：**返回值故意不用**，本轮不改变任何发送/拦截行为。
        _participation.Observe(
            conversation.SourceKey,
            sent
                ? Services.Participation.ParticipationEvent.Replied
                : Services.Participation.ParticipationEvent.Silent);

        // Unsent memory-derived output must not survive in logs or the panel event stream.
        if (BotAgent.Services.Memory.MemoryUseScope.HasMemory)
        {
            _hooks.Log($"[memory] outcome={(sent ? "replied" : "failed")} elapsedMs={elapsed:F0} sentSegments={sendReport.SentSegments.Count} textLength={reply.Length}");
            return ChatDeliveryReport.FromAcknowledgements(sendReport, textReply is not null, voiceParts, voiceSent, voiceFailed,
                extrasComplete: (sticker is null || sentImage) && (pokeTarget is null || pokeSent));
        }

        // P2（结构化运行记录）：同一条终态也用统一字段写一行，便于核对「决策 → 实际发送」是否一致。
        _hooks.Log($"[决策] outcome={(sent ? "replied" : "failed")} action={result.Action}"
                + $" reason={result.ReasonCode ?? "unknown"}: {conversation.Name}");

        _hooks.Log(
            $"{(sent ? "已回复" : "回复失败")} {conversation.Name}（{elapsed:F0}ms 生成" +
            $"{(result.Suitability is int sc ? $"，自评 {sc}" : string.Empty)}" +
            (reply.Length > 0 ? $"，{reply.Length} 字" : string.Empty) +
            (sentText && !sendReport.AllSent ? $"，部分发出（{sendReport.SentSegments.Count}/{TextRules.SplitSentences(reply).Count} 段）" : string.Empty) +
            (voiceSent ? $"，语音 {voiceText.Length} 字" : string.Empty) +
            (sticker is not null ? $"，表情包 #{sticker.Id}（{StickerText.Describe(sticker)}）" : string.Empty) +
            (pokeSent ? $"，戳了 {pokeTarget}" : string.Empty) +
            $"{quoteNote}）" +
            (reply.Length > 0 ? $": {reply}" : string.Empty));

        return ChatDeliveryReport.FromAcknowledgements(sendReport, textReply is not null, voiceParts, voiceSent, voiceFailed,
            extrasComplete: (sticker is null || sentImage) && (pokeTarget is null || pokeSent));
    }

    /// <summary>戳一戳发送与前置闸门裁决：仅在目标出现于当前上下文且心情/能力闸门放行时发出。</summary>
    public async Task<bool> TryPokeAsync(
         BotConversation conversation,
         IReadOnlyList<ChatMessage> context,
         Domain.Permissions.ChatCapabilitySet caps,
         EffectivePlatformPolicy platformPolicy,
        bool isGroup,
        long targetId,
        long pokeUserId)
    {
        var pokeFeature = platformPolicy.Feature("poke", globallyEnabled: true);
        if (!pokeFeature.Enabled)
        {
            _hooks.Log($"这次不戳（平台策略拒绝：{pokeFeature.ReasonCode}）");
            return false;
        }

        var known = context.Any(m => m.SenderId == pokeUserId) ||
                    _poke.IsRecentPoker(conversation.SourceKey, pokeUserId);
        if (!known)
        {
            _hooks.Log($"模型想戳 {pokeUserId}，但这个人没在本次上下文里出现过 → 忽略（防编造号码）");
            return false;
        }
        if (!_mood.WillPokeBack(EngineClock.Now, out var moodWhy))
        {
            _hooks.Log($"这次不戳 {pokeUserId}（{moodWhy}）");
            return false;
        }
        if (!_poke.AllowPokeBack(conversation.SourceKey, pokeUserId, out var pokeWhy))
        {
            _hooks.Log($"这次不戳 {pokeUserId}（{pokeWhy}）");
            return false;
        }
        if (!_approvals.AllowCapability(conversation, "poke.send", null, out var pokeCapWhy, pinned: caps))
        {
            _hooks.Log($"这次不戳 {pokeUserId}（能力闸门拒绝：{pokeCapWhy}）");
            return false;
        }
        using var validation = ReplyCommitBoundary.EnterValidation(() =>
            ValidateModality(conversation, null, platformPolicy, "poke", PluginGate.PokeCapability, "poke.send"));
        await ReplyCommitBoundary.BeforeSourceSendAsync(_source);
        var sent = await _source.SendPokeAsync(isGroup, targetId, pokeUserId);
        if (sent) _poke.NotePokedBack(conversation.SourceKey, pokeUserId, EngineClock.Now);
        else _hooks.Log($"戳 {pokeUserId} 失败（协议端可能不支持戳一戳）");
        return sent;
    }

    /// <summary>
    /// 语音频率门。为什么要它：
    ///   • 语音在群里是“稀罕事”，几秒内连发就是刷屏（和表情包同一个道理）；
    ///   • 合成+转码是串行的，连发会排队卡住后面的消息。
    /// 只挡“几秒内连发”这种物理性的问题；频率是否得体由模型自己判断（提示词给它积极性与建议节奏）。
    /// </summary>
    private bool AllowVoice(BotConversation conversation, out string reason)
        => _voice.Allow(conversation.SourceKey, out reason);
    /// <summary>表情包频率门（实现见 StickerService.Allow）：同一会话冷却内、同一张 10 分钟内都不发。</summary>
    public bool AllowSticker(BotConversation conversation, string stickerId, out string reason)
        => _stickers.Allow(conversation.SourceKey, stickerId, out reason);
    /// <summary>发一张表情包（实现见 StickerService.SendAsync：读盘 → base64 → image 段，发出去才记账）。</summary>
    private Task<bool> SendStickerAsync(bool isGroup, long targetId, StickerRecord sticker, long? replyTo, string sourceKey)
        => _stickers.SendAsync(isGroup, targetId, sticker, replyTo, sourceKey);
}

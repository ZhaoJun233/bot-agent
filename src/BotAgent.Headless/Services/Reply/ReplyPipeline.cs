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

/// <summary>
/// 回复主链（用例层）：**入站 → 排队 → 串行取上下文 → 调模型 → 按决策执行动作 → 发送与记账**。
/// 以前这一整条链路都长在 <c>BotAgentHost</c> 上（那个类同时还是面板 facade、健康巡检、配置热更新的宿主）。
///
/// 四条不变量（重构时别弄丢；每条都有对应的 harness 场景在兜底）：
///   ① **每会话 FIFO**：同一会话严格按触发顺序处理（否则回复会错位、引用会认错人）；不同会话并发。
///   ② **静默短路在发送之前**：模型选择沉默就一个字节都不发（连“我正在想”都不给群体看）。
///   ③ **引用只挂第一条**：分句/文字+图时，QQ 的“回复”引用只给第一条消息。
///   ④ **语音/文字去重**：同一轮里语音已经说过的内容不再用文字重复一遍。
///
/// 宿主能力（日志、自己的 QQ 号、处置状态）从 <see cref="ReplyHooks" /> 注入 —— 方向是「用例 → 宿主」。
/// </summary>
public sealed partial class ReplyPipeline
{
    private readonly SettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly IModelClient _brain;
    private readonly IProfileRepository _profiles;
    private readonly ConversationRegistry _registry;
    private readonly PanelNotifier _ui;
    private readonly WhitelistGate _whitelist;
    private readonly ApprovalUseCase _approvals;
    private readonly ParticipationUseCase _participation;
    private readonly PokeUseCase _poke;
    private readonly VibeTracker _vibes;
    private readonly MemberRoleUseCase _roles;
    private readonly OwnMessageLedger _ownLedger;
    private readonly AgentCommandService _agentCmds;
    /// <summary>发消息（分句/节奏/记账）：聊天、agent 回话、审批回执都走它。</summary>
    private readonly IQqMessageSender _plain;

    private readonly StickerService _stickers;
    private readonly VoiceUseCase _voice;
    private readonly MusicUseCase _music;
    private readonly ResearchUseCase _research;
    private readonly LinkPreviewer? _links;
    private readonly IMoodRepository _mood;
    private readonly ReplyHooks _hooks;
    /// <summary>决策轨迹（批次 C）：一轮一条，只有形状 —— 见 <see cref="TurnTraceStore" /> 的注释。</summary>
    private readonly TurnTraceStore _traces;
    private readonly ProtocolRiskBackoff? _riskBackoff;
    private readonly ITenantQuotaLedger? _quotas;
    private readonly PlatformPolicyResolver _platformPolicies;
    /// <summary>有限步进循环（批次 E）：默认 1 步 = 与改造前逐字一致。</summary>
    private readonly AgentTurnLoop _turnLoop;
    /// <summary>当场做掉只读工具（批次 E）：判定与冷却与“留给下一轮”那条路完全一致。</summary>
    private readonly InlineTurnTools _inlineTools;

    public ReplyPipeline(
        SettingsBox box,
        IQqChatSource source,
        IModelClient brain,
        IProfileRepository profiles,
        ConversationRegistry registry,
        PanelNotifier ui,
        WhitelistGate whitelist,
        ApprovalUseCase approvals,
        ParticipationUseCase participation,
        PokeUseCase poke,
        VibeTracker vibes,
        MemberRoleUseCase roles,
        OwnMessageLedger ownLedger,
        AgentCommandService agentCmds,
        IQqMessageSender plain,
        StickerService stickers,
        VoiceUseCase voice,
        MusicUseCase music,
        ResearchUseCase research,
        LinkPreviewer? links,
        IMoodRepository mood,
        ReplyHooks hooks,
        TurnTraceStore traces,
        ProtocolRiskBackoff? riskBackoff = null,
        ITenantQuotaLedger? quotas = null,
        PlatformPolicyResolver? platformPolicies = null)
    {
        _box = box;
        _source = source;
        _brain = brain;
        _profiles = profiles;
        _registry = registry;
        _ui = ui;
        _whitelist = whitelist;
        _approvals = approvals;
        _participation = participation;
        _poke = poke;
        _vibes = vibes;
        _roles = roles;
        _ownLedger = ownLedger;
        _agentCmds = agentCmds;
        _plain = plain;
        _stickers = stickers;
        _voice = voice;
        _music = music;
        _research = research;
        _links = links;
        _mood = mood;
        _hooks = hooks;
        _traces = traces;
        _riskBackoff = riskBackoff;
        _quotas = quotas;
        _platformPolicies = platformPolicies ?? new PlatformPolicyResolver(box);
        _turnLoop = new AgentTurnLoop(brain, traces);
        _inlineTools = new InlineTurnTools(research, approvals, participation, hooks.Log);

        _replyGate = new ResizableReplyGate(
            Math.Clamp(box.Current.MaxConcurrentReplies, 1, 16));
    }

    private AppSettings _settings => _box.Current;

    /// <summary>最近一次主动请求时间（静默兜底判断用；跨线程原子访问）。</summary>
    private DateTime LastActiveRequestTime
    {
        get => new(Volatile.Read(ref _lastActiveRequestTicks));
        set => Volatile.Write(ref _lastActiveRequestTicks, value.Ticks);
    }

    /// <summary>切换「AI 正在思考」并把状态推给面板（实现在 <see cref="PanelNotifier.SetThinking" />）。</summary>
    private void SetThinking(BotConversation conversation, bool thinking) => _ui.SetThinking(conversation, thinking);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _historyRequested = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _replyCooldown = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TenantReplyQueue> _pendingReplies = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BotConversation> _pendingConversations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlight = new();
    private readonly ResizableReplyGate _replyGate;
    private int _replyWorkerRunning;
    private long _lastActiveRequestTicks = DateTime.MinValue.Ticks; // 最近一次主动请求时间（原子读写）

    /// <summary>每个会话最近一次“自己主动开口”的时间（用于主动发言的冷却）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastProactive = new();
    private long _lastGenerationMs;
    /// <summary>日志节流：同一来源的“忽略”类日志最多每分钟一条（否则忙群里会刷爆）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _noisyLogAt = new();
    /// <summary>
    /// 把群友消息**开头 / 结尾**的括号旁白转成标注（`〔旁白：笑〕`），
    /// 返回（是否整条都是旁白、标注后的文本）。
    ///
    /// 口径来自群里真实消息（管理员反馈后我拉了 700 多条带括号的群消息看过）：
    ///   • “（雨哗啦啦）”            整条都是旁白 → `〔旁白：雨哗啦啦〕`（只接收，不触发回复）；
    ///   • “行（端在桌上）”          尾巴上的旁白 → `行〔旁白：端在桌上〕`；
    ///   • “（放在地上）来吧，猫猫，”  开头的旁白   → `〔旁白：放在地上〕来吧，猫猫，`；
    ///   • 中间位置的括号**不碰**（“（2026）年的计划”里的括号是正文，群样本里也没这种旁白）。
    ///
    /// 管理员 2026-09-14 追加的口径（§25）：**不要单纯忽略，也要接收，但要特别注明** ——
    /// 旁白不再被丢掉，而是标成 `〔旁白：…〕` 一起进聊天记录与模型上下文：模型能拿它理解现场
    /// （“端到桌上”“抱着猫”这类动作本来就是语境），但一眼就知道那不是“他说的话”。
    ///
    /// 三个例外——机器人自己的内容标记（[图片] / [表情:斜眼笑] / [动画表情:…] / [语音]…）
    /// 是“对方发了啥”的记录，不是旁白：一律保留（把它们一起标注就等于把群友发表情/图片的记录抹了，踩过）。
    /// 私聊 / 带图 / @ 机器人的消息完全不动（宁可多回也不装死）。
    /// </summary>
    private static (bool AsideOnly, string Text) AnnotateBracketAsides(QqChatMessage msg)
    {
        var text = msg.Text?.Trim() ?? string.Empty;
        if (!msg.IsGroup || msg.MentionedSelf || msg.ImageUrls is { Count: > 0 } || text.Length == 0)
        {
            return (false, text);
        }

        // 开头的旁白（可能连着几段）→ 依次标在正文前面
        var leading = new StringBuilder();
        var rest = text;
        while (TextRules.TryTakeLeadingBracket(rest, out var afterLead, out var inner))
        {
            leading.Append(MessageMarkers.AsAside(inner));
            rest = afterLead;
        }

        // 结尾的旁白（可能连着几段）→ 先倒着收，再按原顺序接到正文后面
        var trailing = new List<string>();
        while (TextRules.TryTakeTrailingBracket(rest, out var afterTail, out var inner))
        {
            trailing.Add(MessageMarkers.AsAside(inner));
            rest = afterTail;
        }

        if (leading.Length == 0 && trailing.Count == 0)
        {
            return (false, text);   // 本来就没有旁白（如单一个“？”）→ 当普通消息
        }

        rest = rest.Trim();
        if (rest.Length == 0 || TextRules.IsOnlyDecoration(rest))
        {
            // 剥完只剩标点/表情（如“（真的）？”）→ 整条就是旁白，标好的旁白就是全文
            trailing.Reverse();
            return (true, leading + string.Concat(trailing));
        }

        trailing.Reverse();
        return (false, leading + rest + string.Concat(trailing));
    }
    /// <summary>
    /// 认出“这条消息引用回复的是哪一条”，返回（那个人叫什么、原话、上下文里到底找到没有、是不是机器人自己说的）。
    /// 查找顺序（先免费的后花钱的）：
    ///   ① 会话上下文里按 id 找 —— 绝大多数引用都是刚发过的消息，这里命中；
    ///   ② 机器人自己发出去的消息表（从发送响应里拿到 id，见 RememberOwnMessage；现在会落盘，重启不清）；
    ///   ③ 协议端在 reply 段里自带的摘要文本（有就用；段里还带了被引用者的 QQ 时，谁说的也当场就知道）。
    /// 都找不到时不编内容，只标一句“更早的一条”（随后由 EnrichQuotedFromProtocolAsync 事后补齐原文）。
    ///
    /// <para>2026-09-19 修“引用机器人发的消息被吞”：IsSelf 以前只看内存表，部署重启（每次部署都会重启）
    /// 后表是空的 —— 群里“引用机器人上一句再说话”就不算直接对它说，于是整条被静默丢掉。</para>
    /// </summary>
    private (string Name, string Text, bool Hit, bool IsSelf) ResolveQuotedMessage(BotConversation conversation, QqChatMessage msg)
    {
        // 落盘的那份要在**查询之前**就绪：它是懒加载的，而重启后第一件事往往就是“有人引用了上一句”，
        // 那时候机器人还没发过任何消息（只在发送时加载的话，这里就会永远查不到 —— 2026-09-19 差点踩到）。
        _ownLedger.EnsureLoaded();
        var botUin = _settings.NormalizedUin;
        var quotedFromProtocol = msg.ReplyToSenderId is long quotedSender
                                 && !string.IsNullOrWhiteSpace(botUin)
                                 && quotedSender.ToString() == botUin;

        if (msg.ReplyToMessageId is long quotedId)
        {
            var messages = conversation.Messages;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].QqMessageId != quotedId)
                {
                    continue;
                }

                var quoted = messages[i];
                var self = quoted.Role == MessageRole.Self;
                // 自己说的那一条：对模型来说“你”才是有意义的称呼
                var name = self
                    ? "你"
                    : (string.IsNullOrWhiteSpace(quoted.SenderName) ? "某人" : quoted.SenderName!);
                return (name, quoted.Text ?? string.Empty, true, self);
            }

            if (_ownLedger.TryGet(conversation.SourceKey, quotedId, out var mine))
            {
                return ("你", mine.Text, true, true);
            }
        }

        if (msg.ReplyToPreviewText is { Length: > 0 } preview)
        {
            return quotedFromProtocol ? ("你", preview, true, true) : ("某人", preview, true, false);
        }

        return (quotedFromProtocol ? "你" : string.Empty, string.Empty, false, quotedFromProtocol);
    }
    /// <summary>
    /// 把“引用回复”拼成给模型看的标注：`[回复 老王「你昨天说的那个 bug」]`。
    /// 为什么要把原话也带上：群聊里一句“我也是”全靠引用的那条才能懂 —— 上下文里虽然也有那条，
    /// 但位置可能隔着好几十条，模型不一定会自己去对（而且对不上就会编）。
    /// 引的是机器人自己那句就用“你”（模型才分得清是跟它说话）。
    /// </summary>
    private static string BuildReplyAnnotation(string name, string quotedText)
    {
        name = MessageMarkers.EscapeExternalControlTags(name);
        if (string.IsNullOrEmpty(name) && string.IsNullOrWhiteSpace(quotedText))
        {
            return "[回复一条更早的消息（我这边已经看不到原文了）]";
        }
        var text = (quotedText ?? string.Empty).Replace('\n', ' ').Trim();
        // 引用的那条自己可能也是回复（“[回复 你「…」] xxx”）：嵌套引号只会变成噪音，剥一层
        if (text.StartsWith("[回复", StringComparison.Ordinal))
        {
            var close = text.IndexOf(']');
            if (close > 0)
            {
                text = text[(close + 1)..].Trim();
            }
        }

        text = MessageMarkers.EscapeExternalControlTags(text);
        if (text.Length == 0)
        {
            return $"[回复 {name}（引用的内容我这边取不到）]";
        }
        return $"[回复 {name}「{TextRules.Shorten(text, 40)}」]";
    }
    public void HandleInbound(QqChatMessage msg)
    {
        var pre = HandleInboundControl(msg with { Text = MessageMarkers.EscapeExternalControlTags(msg.Text) });
        if (pre is null)
        {
            return;   // 被总开关 / 白名单 / 自回显 / 审批命令挡下（或已消费）
        }

        var asideOnly = pre.AsideOnly;
        msg = pre.Message;

        var conversation = _registry.GetOrCreate(msg);
        if (conversation is null)
        {
            _hooks.Log($"入站消息关联会话失败，放弃后续处理（channel={msg.Channel}）");
            return;
        }

        // 本机 Agent 命令（// 开头）：**不进人设路线** —— 它不是一个“插个嘴”，是一个真任务；
        // 也不该被适合度阈值/群冷却/复读守卫那些限流卡住（它们都是为“聊天”设计的）。handoff-4 §31
        if (!asideOnly && _agentCmds.TryParseCommand(msg.Text, out var agentPayload))
        {
                    _ = _agentCmds.RunCommandSafeAsync(conversation, msg, agentPayload);
            return;
        }

        // 引用回复：把“在回哪条”标进正文。
        // 以前 reply 段被直接丢掉 → 模型只看到一句“我也是，哈哈”，不知道在回什么，
        // 也认不出“他在回机器人自己上一句”（管理员反馈：识别不了引用回复消息 —— handoff-4 §27）。
        // 2026-09-19：引自己那句的判定不再只看内存表（重启就清空）—— 见 ResolveQuotedMessage 的 IsSelf。
        var quotedIsSelf = false;
        var quotedResolved = true;
        if (msg.ReplyToMessageId is not null || msg.ReplyToPreviewText is { Length: > 0 })
        {
            var (quotedName, quotedText, hit, isSelf) = ResolveQuotedMessage(conversation, msg);
            quotedIsSelf = isSelf;
            quotedResolved = hit;
            var annot = BuildReplyAnnotation(quotedName, quotedText);
            if (annot.Length > 0 && !msg.Text.StartsWith(annot, StringComparison.Ordinal))
            {
                // 正文可能是一个空格（只点“回复”不写字）：先 Trim，不然会拼出个尾巴空格
                var body = msg.Text.Trim();
                msg = msg with { Text = body.Length > 0 ? annot + " " + body : annot };
            }

            _hooks.Log($"引用回复：{msg.SenderName} 引用了" +
                    (hit ? $" {quotedName} 的「{TextRules.Shorten(quotedText, 24)}」" : " 一条我这边已看不到的消息（先标“更早的一条”，同时去协议端补原文）"));
        }

        var appended = new ChatMessage
        {
            Role = MessageRole.Peer,
            SenderName = msg.IsGroup ? msg.SenderName : null,
            SenderId = msg.UserId,
            Text = msg.Text,
            Timestamp = msg.Time,
            QqMessageId = msg.MessageId,
            ImageUrls = msg.ImageUrls,
            // 被点名 = @ 了机器人自己，或引用了机器人发的那条（引了自己的话也是“在跟你说话”）
            DirectToBot = msg.MentionedSelf || quotedIsSelf,
            // 协议风控退避只认可明确 @；普通引用仍可参与正常对话，但不能绕过退避。
            MentionedBot = msg.MentionedSelf,
        };
        conversation.Append(appended);

        // P1 观测锚点 + 闸门（V3 §7.3）：把「这条消息是什么性质」喂给状态机，
        // 并（仅在面板打开闸门时）用它的结论决定**这一轮要不要参与**。
        // 纯旁白不喂 —— 旁白不是对谁说的话（与 HasPendingReply 同一口径），也就没判过。
        // 默认关时 gate.Proceed 恒为 true、原因码恒为 gating_off → 与改造前逐字一致（V3 §5.3）。
        var gate = Services.Participation.ParticipationGate.Decide(
            _settings.EnableParticipationGating,
            asideOnly
                ? null
                : _participation.Observe(
                    conversation.SourceKey,
                    Services.Participation.ParticipationEvents.ClassifyInbound(msg.IsGroup, appended.DirectToBot)));

        // 被闸门拦下就**不欠这次回复** —— 否则空闲兜底过一会儿又会补一次，等于没拦。
        conversation.HasPendingReply = !asideOnly && gate.Proceed;
        _registry.Touch(conversation);
        _ui.NotifyMessageAdded(conversation.SourceKey, appended);
        _registry.Save();

        // 提问路径：有人应了一声就把那条待答问题标记为已答（一次性）。
        // **放在闸门之前**：「有人答了」是一个**事实**，与「机器人要不要回话」是两件事 ——
        // 闸门只该决定后者；否则一条已经有人答过的问题会一直挂在台账上（等它自然过期）。
        // 旁白除外：旁白不是“回答”，不能拿来消费提问。
        if (_settings.EnableQuestions && !asideOnly)
        {
            _approvals.MarkQuestionAnswered(conversation, msg);
        }

        // 引用的原文本地一条都对不上（重启前的旧消息 / 早被清出上下文）→ 后台去协议端按 id 查一次，
        // 查到就把真实原文补写进这条消息。为什么是“事后补”而不是发之前查：网关是在接收循环里
        // 同步调我们的（GetAwaiter().GetResult()），在这里等协议端回包会死锁到超时。
        //
        // ⚠ 排队必须放在闸门 return **之前**：否则“只引用了机器人旧消息、又没 @”的轮次会被闸门
        // 按无关消息拦掉，补查永远没机会证明它其实是在跟机器人说话（V3 §7.3 / §8.3）。
        if (!quotedResolved && msg.ReplyToMessageId is long unresolvedId)
        {
            EnrichQuotedFromProtocolAsync(conversation, appended, unresolvedId, msg.IsGroup, retriggerIfSelf: !gate.Proceed);
        }

        if (!gate.Proceed)
        {
            _hooks.Log($"[参与] 闸门拦下这一轮（{gate.ReasonCode}，state={gate.State}）: {conversation.Name}");
            return;
        }
        // 人物档案（帮助模型认识群友/好友）
        var botUin = _settings.NormalizedUin;
        var isSelfSender = !string.IsNullOrWhiteSpace(botUin) && msg.UserId.ToString() == botUin;
        if (!isSelfSender)
        {
            _profiles.Append(
                msg.UserId.ToString(),
                msg.SenderName,
                msg.Text,
                msg.Time,
                msg.IsGroup ? conversation.Name : null,
                msg.IsGroup ? msg.GroupId : 0,
                appended.Seq);

            // 群成员身份（群主/管理员/群头衔）：先记下消息事件里带的 role（零成本），
            // 缺头衔或太久没更新时再后台去问协议端（ get_group_member_info 才能拿到自定义头衔）。
            if (msg.IsGroup)
            {
                _roles.Remember(msg);
            }
        }

        _hooks.Log(
            $"收到 {(msg.IsGroup ? $"群{msg.GroupId}" : "私聊")} {msg.SenderName}({msg.UserId}): " +
            $"{(msg.Text.Length > 80 ? msg.Text[..80] + "…" : msg.Text)}" +
            (msg.ImageUrls is { Count: > 0 } ? $" [+{msg.ImageUrls.Count}图]" : string.Empty));

        // 媒体类入站（表情包入库 / 音乐分享 / 链接预览）+ 首次群消息补历史：都不阻塞接收线程
        var musicShares = HandleInboundMedia(conversation, msg);

        if (!asideOnly && musicShares is not { Count: > 0 })
        {
            // 新的“人”发言 = 一次新的运行窗口：单次运行的工具体预算从 0 起算（V3 §9.2）。
            // 机器人自己发的消息不算（否则它每说一句就把自己这一轮的预算洗白了）。
            if (!isSelfSender)
            {
                _approvals.ResetBudget(conversation.SourceKey);
            }

            // 被限流挡下也不丢：RequestReply 会记一笔，这一轮说完补一次评估（见该方法注释）
            RequestReply(conversation, msg.MessageId > 0 ? msg.MessageId : null, directInWindow: appended.DirectToBot);
        }
    }
    /// <summary>
    /// 入站最前面那几道闸：通道总开关 → 白名单 → 自回显 → 审批命令（同意/拒绝 编号）→ 括号旁白标注。
    /// 返回 null = 这条消息到此为止（已忽略或已消费）；否则返回标注后的消息与是不是纯旁白。
    /// 「只观测」的参与事件在这几道闸里照旧要喂（拦下也是 Silent，不改任何判定）。
    /// </summary>
    private InboundHead? HandleInboundControl(QqChatMessage msg)
    {
        var channel = msg.Channel;
        var platformPolicy = _platformPolicies.ResolveForChannel(channel);
        if (!platformPolicy.Registered || !platformPolicy.Connected)
        {
            LogThrottled("unknown-channel:" + channel, $"忽略未注册或未连接的平台通道: {channel}");
            return null;
        }

        var channelEnabled = platformPolicy.Enabled && platformPolicy.ChatEnabled;
        if (!channelEnabled)
        {
            var reason = platformPolicy.Enabled ? "聊天已静音；chat_disabled" : "平台已停用；platform_disabled";
            LogThrottled("chanoff:" + Channels.ChannelOf(msg.Channel), $"忽略（{Channels.Display(msg.Channel)}{reason}）");
            // P1（只观测）：这是“收到了但被服务端拦下” → 对状态机是 Silent 事件（**不改判定**）
            _participation.Observe(
                Channels.Key(msg.Channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId),
                Services.Participation.ParticipationEvent.Silent);
            return null;
        }

        if (!_whitelist.AllowsMessage(msg))
        {
            // 忙群里这类日志会把日志文件和面板刷爆 → 同一来源每分钟最多一条
            var label = msg.IsGroup ? "群 " + msg.GroupId : "私聊 " + msg.UserId;
            // 把“用哪份名单、那份的状态”一并印出来：
            // 实际踩过——“官方通道永远不回”但日志只有一句“不在白名单”，看不出是名单选错了还是填了真实号。
            var tag = Channels.Tag(msg.Channel);
            var gateNow = _whitelist.Describe();
            var listState = Channels.IsOfficial(msg.Channel)
                ? (msg.IsGroup ? $"官方群名单{gateNow.OfficialGroups}" : $"官方私聊名单{gateNow.OfficialPrivates}")
                : Channels.IsLocal(msg.Channel)
                    ? $"本地名单{gateNow.Local}"
                    : (msg.IsGroup ? "私域群名单" : "私域私聊名单");
            LogThrottled("ignore:" + label, $"忽略（不在白名单）: [{tag}] {label}（{listState}）");
            // P1（只观测）：不在白名单 = 不参与这个话题 → Silent（同样不改判定）
            _participation.Observe(
                Channels.Key(msg.Channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId),
                Services.Participation.ParticipationEvent.Silent);
            return null;
        }

        // 机器人自己发的消息不入库（协议端可能回显）
        if (_hooks.SelfId() != 0 && msg.UserId == _hooks.SelfId())
        {
            return null;
        }

        // P3 审批（V3 §9.4，**默认关**）：群主/管理员（或面板点名的人）在**原会话**里回复
        // 「同意 编号」/「拒绝 编号」才算数 —— 身份、会话、有效期、一次性、策略版本全在服务端核。
        // 注意：解析不出“动词 + 编号”这种形状的消息**不拦截**，照旧走普通聊天链路。
        if (_settings.EnableApprovals && _approvals.HandleCommand(msg))
        {
            return null;
        }

        // 括号旁白：**标注**（`〔旁白：…〕`）而不是忽略 —— 管理员口径（2026-09-14）：
        // “不要单纯忽略，也要接收，但需要特别注明”。
        // 纯旁白（“（笑）”“（放在地上）来吧”里的括号段）会进聊天记录与上下文，但**不单独触发回复**
        // （旁白不是对谁说的话，不然“（笑）”就会把机器人拽出来接话 —— §20 的原始问题）；
        // 正文 + 旁白混着的照常触发（正文才是那句话）。
        var asideOnly = false;
        if (_settings.IgnoreBracketMessages)
        {
            var (isAsideOnly, annotated) = AnnotateBracketAsides(msg);
            asideOnly = isAsideOnly;

            if (annotated.Length > 0 && annotated != msg.Text)
            {
                msg = msg with { Text = annotated };   // 落库与上下文都用标注后的文本
            }

            if (asideOnly)
            {
                // 同一个人一分钟最多记一条：不然旁白刷屏时日志也跟着刷
                LogThrottled("bracket:" + msg.UserId,
                    $"旁白（只接收、不触发回复）: {msg.SenderName}({msg.UserId}): {TextRules.Shorten(msg.Text, 30)}");
            }
        }

        return new InboundHead(asideOnly, msg);
    }

    /// <summary>
    /// 媒体类入站（第 2 步的一部分）：群友发的图自动收进表情包库（后台下载）、音乐分享去查歌词与波形、
    /// 链接去取标题摘要、首个群消息补一次历史上下文。返回识别到的音乐分享（真的有歌时这一轮**先不回复**）。
    /// 一律 fire-and-forget：网关是在接收循环里同步调我们的，这里绝不能等。
    /// </summary>
    private IReadOnlyList<MusicShare>? HandleInboundMedia(BotConversation conversation, QqChatMessage msg)
    {
        var policy = _platformPolicies.ResolveForChannel(msg.Channel);
        var stickersEnabled = policy.Feature("stickers", _settings.EnableStickers).Enabled;
        var musicEnabled = policy.Feature("music", _settings.EnableMusic).Enabled;
        var linksEnabled = policy.Feature("linkpreview", _settings.EnableLinkPreview).Enabled;

        // 表情包：群友发的图自动收进库（后台下载，不阻塞接收线程）
        if (stickersEnabled && _settings.StickerLibraryMax > 0 && msg.IsGroup && msg.ImageUrls is { Count: > 0 })
        {
            var urls = msg.ImageUrls.ToList();
            var uid = msg.UserId.ToString();
            var group = msg.GroupId;
            var mid = msg.MessageId;
            _ = Task.Run(() => _stickers.CollectAsync(urls, uid, group, mid));
        }

        // 听音乐：识别到分享就后台去查歌词 + 下一份低码率音频分析波形。
        // 有歌的时候**先不回复** —— 等分析结果回来再让模型开口，否则它只能对着一个歌名瞎聊。
        var musicShares = musicEnabled ? msg.MusicShares : null;
        if (musicShares is { Count: > 0 })
        {
            _ = Task.Run(() => HandleMusicAsync(conversation, msg.SenderName, musicShares.ToList()));
        }

        // 链接：群里发的 URL（包括分享卡片里那个）真去打开看一眼，取回标题/摘要。
        // 有音乐分享时跳过 —— 音乐那条路自己会处理链接，不必看两遍。
        var linkUrls = musicShares is { Count: > 0 } || _links is null || !linksEnabled
            ? []
            : LinkExtractor.Extract(msg.Text, Math.Clamp(_settings.LinkPreviewMax, 0, 5));
        if (linkUrls.Count > 0)
        {
            var key = conversation.SourceKey;
            var urls = linkUrls.ToList();
            var pending = Task.Run(async () =>
            {
                try
                {
                    var note = await _links!.DescribeAsync(urls, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        _linkNotes[key] = note!;
                        _hooks.Log($"[Link] 已看过 {urls.Count} 个链接：{string.Join("、", urls.Select(u => u.Length > 48 ? u[..48] + "…" : u))}");
                    }
                }
                catch (Exception ex)
                {
                    _hooks.Log($"[Link] 预览失败: {ex.Message}");
                }
                finally
                {
                    _linkTasks.TryRemove(key, out _);
                }
            });
            _linkTasks[key] = pending;
        }

        // 首个群消息时补历史上下文（没有会话列表可点，只能在这里补）
        if (msg.IsGroup)
        {
            EnsureGroupContext(conversation, msg.GroupId);
        }

        return musicShares;
    }

    /// <summary>
    /// 后台真去搜一次，把结果留给下一轮（并在允许时叫醒模型）。
    /// 为什么要冷却：搜索是一次真实的模型调用 + 几秒等待；群里连问几个问题就排队了。
    /// </summary>
    private void QueueWebSearchAsync(BotConversation conversation, string query)
    {
        var key = conversation.SourceKey;
        if (!_research.TryBeginSearch(key, _settings.WebSearchCooldownSeconds, out var searchWhy))
        {
            _hooks.Log($"[Search] 这次不搜（{searchWhy}）：{query}");
            return;
        }

        _hooks.Log($"[Search] 模型想搜「{query}」");
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _research.SearchAsync(key, query);
                if (result is not null && !result.HasContent)
                {
                    _hooks.Log($"[Search] 没搜到「{query}」：{result.Error}");
                    // P1 观测：模型点名的工具没拿到东西 → 记一次失败（只降级不升级）
                    _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
                }

                // 搜到了就给它一次开口机会（没搜到也给 —— 让它能如实说“没查到”）
                RequestReply(conversation, null);
            }
            catch (Exception ex)
            {
                _hooks.Log($"[Search] 搜「{query}」失败: {ex.Message}");
                _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
            }
        });
    }
    /// <summary>后台读一个网页的正文（模型填 read 时），留给下一轮。</summary>
    private void QueuePageReadAsync(BotConversation conversation, string url)
    {
        var key = conversation.SourceKey;
        _hooks.Log($"[Search] 模型想读页面 {TextRules.Shorten(url, 80)}");
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await _research.ReadPageAsync(key, url);
                if (text is null)
                {
                    // P1 观测：读页面失败（工具失败 → 只降级）
                    _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
                }

                RequestReply(conversation, null);
            }
            catch (Exception ex)
            {
                _hooks.Log($"[Search] 读页面失败: {ex.Message}");
                _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
            }
        });
    }
    /// <summary>
    /// 听音乐：拿歌词 + 低码率音频做波形分析，把实测到的事实留给下一轮回复。
    /// 分析完成后单独触发一次发言机会 —— 这样模型是“听完再说”，而不是先瞎猜一遍再补课。
    /// </summary>
    private async Task HandleMusicAsync(BotConversation conversation, string sender, List<MusicShare> shares)
    {
        if (!_music.IsReady)
        {
            return;
        }

        var heard = false;
        foreach (var share in shares)
        {
            try
            {
                var note = await _music.DescribeShareAsync(share, sender);
                if (string.IsNullOrWhiteSpace(note))
                {
                    continue;
                }

                // 一次发好几首时合并，别让后一首盖掉前一首
                _music.AddNote(conversation.SourceKey, note!);
                heard = true;
                _hooks.Log($"[Music] 已听过：{share.Describe()}");
            }
            catch (Exception ex)
            {
                _hooks.Log($"[Music] 处理失败（{share.Describe()}）: {ex.Message}");
            }
        }

        if (heard)
        {
            RequestReply(conversation, null); // 没有触发消息 → 不引用（沿用 replyTo 那套规则）
        }
    }
    /// <summary>每个会话正在跑的链接预览（回复前短暂等一下：快站点能当轮就用上）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _linkTasks = new();
    /// <summary>每个会话最近一次“链接里写了啥”的描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _linkNotes = new();
    /// <summary>每个会话最近一次“有人撤回消息”的时间（冷却：连着撤几条时不要每条都评论）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastRecallAt = new();
    /// <summary>每个会话最近一次撤回事件的描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _recallNotes = new();
    /// <summary>同会话两次“评论撤回”的最小间隔（秒）。</summary>
    private const int RecallCommentCooldownSeconds = 90;
    /// <summary>
    /// 有人撤回了一条消息。
    ///
    /// 为什么不能不管：撤回后群友已经看不到那条了，但机器人手里还有 ——
    /// 不管的话它下一轮会去接一句“群里已经不存在的消息”，或者把撤回的内容
    /// 当成公共信息接着聊（对方会觉得“我明明擦掉了”）。
    /// 做三件事：
    ///   ① 在上下文里把那条标成 <c>[已撤回] 原内容</c>（内容保留，但一眼能看出被收回去了）；
    ///   ② 给模型一次开口的机会（“撤回了啥”是人类最常见的反应），带冷却；
    ///   ③ 提示词里明确：“可以记得，但不要引用/复述/当众开玩笑”。
    /// </summary>
    public void OnMessageRecalled(QqRecallEvent recall)
    {
        try
        {
            // 撤回事件不带昵称，先用它给的身份把会话找到（没有就新建，与戳一戳同一套）
            var conversation = _registry.GetOrCreate(new QqChatMessage(
                0, recall.IsGroup, recall.UserId, recall.GroupId, string.Empty, string.Empty, recall.Time, false));
            if (conversation is null)
            {
                return;
            }

            var key = conversation.SourceKey;
            var target = conversation.Messages.FirstOrDefault(m => m.QqMessageId == recall.MessageId);
            if (target is null)
            {
                // 常见于：那条消息已经被滚动窗口/归档挤掉了 —— 没什么要改的，也不值得评论
                _hooks.Log($"[Recall] {conversation.Name}：有一条消息被撤回（id={recall.MessageId}），但它不在当前上下文里");
                return;
            }

            if (target.Recalled)
            {
                return; // 重复事件：已经标过了，也不再评论
            }

            target.Recalled = true;
            _registry.Save();

            var sender = target.SenderName ?? ResolveDisplayName(conversation, recall.UserId);            var byOther = recall.OperatorId > 0 && recall.OperatorId != recall.UserId
                ? $"（由 {ResolveDisplayName(conversation, recall.OperatorId)} 撤回）"
                : string.Empty;
            _hooks.Log($"[Recall] {conversation.Name}：{sender} 撤回了一条消息{byOther} —— 原内容（已标进上下文）：{TextRules.Shorten(target.Text, 40)}");

            var now = Clock.Now;
            if (_lastRecallAt.TryGetValue(key, out var last) &&
                now - last < TimeSpan.FromSeconds(RecallCommentCooldownSeconds))
            {
                _hooks.Log($"[Recall] 这次不评论（同会话 {RecallCommentCooldownSeconds}s 内已经评论过一次）");
                return;
            }

            _lastRecallAt[key] = now;

            // 手误更正：撤回后同一个人又发了新消息（实测：把“固定bpc”改成“固定npc”）——
            // 这种时候去点评“撤回了啥”很尴尬（群里实测被怼过）。人类的做法是当没看见。
            var corrected = conversation.Messages.Any(m =>
                m.Role == MessageRole.Peer &&
                m.SenderId == target.SenderId &&
                m.Seq > target.Seq &&
                !m.Recalled);
            if (corrected)
            {
                _hooks.Log("[Recall] 看起来是手误更正（同一个人随后又发了消息）→ 不给模型开口机会，只标记");
                _registry.Save();
                return;
            }

            _recallNotes[key] = $"（刚有人撤回了一条消息：{sender}。上下文里那条已标成 [已撤回]。）";
            _registry.Touch(conversation);

            RequestReply(conversation, null);
        }
        catch (Exception ex)
        {
            _hooks.Log("[Recall] 处理撤回事件出错: " + ex.Message);
        }
    }
    /// <summary>从历史消息里找一个人的显示名（昵称/群名片）；找不到就写“成员 <qq>”。</summary>
    public string ResolveDisplayName(BotConversation conversation, long userId)
        => DisplayNames.Of(conversation, userId, _hooks.SelfId());
    /// <summary>会话首次出现时：异步补全真实群名 + 拉取近期历史消息。</summary>
    private void EnsureGroupContext(BotConversation conversation, long groupId)
    {
        if (!_historyRequested.TryAdd(conversation.SourceKey, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var name = await _source.GetGroupNameAsync(groupId);
                if (!string.IsNullOrWhiteSpace(name) && name != conversation.Name)
                {
                    conversation.Name = name!;
                    _registry.Save();
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("History", "获取群名失败: " + ex.Message);
            }

            if (conversation.HistoryLoaded || _source is not OneBotGateway gateway || !gateway.IsConnected)
            {
                return;
            }

            // 会话已经满了：插入到顶部的旧消息会立刻被裁掉，白调一次协议端接口
            if (conversation.MessageCount >= _settings.MaxMessagesPerConversation)
            {
                conversation.HistoryLoaded = true;
                FileLog.Write("History", $"群 {groupId} 本地已有 {conversation.MessageCount} 条（达上限），跳过历史补录");
                return;
            }

            try
            {
                var history = await gateway.GetGroupMsgHistoryAsync(groupId, 20);
                if (history.Count == 0)
                {
                    return;
                }

                var restored = new List<ChatMessage>(history.Count);

                // 协议端返回的是**最新在前**，而 MergeHistoryAtTop 要求正序（旧→新）。
                // 不反转的话，补录消息在会话里的先后会颠倒，我的序号分配也会与时间相反。
                for (var i = history.Count - 1; i >= 0; i--)
                {
                    var m = history[i];
                    if (string.IsNullOrWhiteSpace(m.Text))
                    {
                        continue;
                    }

                    restored.Add(new ChatMessage
                    {
                        Role = MessageRole.Peer,
                        SenderName = m.IsGroup ? m.SenderName : null,
                        SenderId = m.UserId,
                        Text = MessageMarkers.EscapeExternalControlTags(m.Text),
                        Timestamp = m.Time,
                        QqMessageId = m.MessageId,
                        ImageUrls = m.ImageUrls
                    });
                }

                var inserted = conversation.MergeHistoryAtTop(restored);
                conversation.HistoryLoaded = true;
                if (inserted > 0)
                {
                    // 历史也写入人物档案
                    var botUin = _settings.NormalizedUin;
                    foreach (var m in restored)
                    {
                        if (m.SenderId is not long uid ||
                            (!string.IsNullOrWhiteSpace(botUin) && uid.ToString() == botUin))
                        {
                            continue;
                        }

                        _profiles.Append(uid.ToString(), m.SenderName ?? string.Empty, m.Text, m.Timestamp, conversation.Name, groupId, m.Seq);
                    }

                    _registry.Save();
                    FileLog.Write("History", $"群 {groupId} 补入 {inserted} 条历史消息");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("History", "拉取群历史失败: " + ex.Message);
            }
        });
    }
    /// <summary>节流日志：同一 key 在窗口内只输出一次（避免忙群里刷爆日志与面板）。</summary>
    public void LogThrottled(string key, string message, int windowSeconds = 60)
    {
        var now = Clock.TickCount;

        if (_noisyLogAt.TryGetValue(key, out var last) && now - last < windowSeconds * 1000L)
        {
            return;
        }

        _noisyLogAt[key] = now;

        // 白名单外的来源可能很多：键数量做兵底
        if (_noisyLogAt.Count > 2000)
        {
            _noisyLogAt.Clear();
        }

        _hooks.Log(message);
    }
    /// <summary>限流：私聊/群聊各自冷却，防止连发刷屏。</summary>
    private bool AllowReply(BotConversation conversation)
    {
        var cooldown = conversation.Kind == ConversationKind.GroupChat
            ? TimeSpan.FromSeconds(Math.Max(0, _settings.GroupCooldownSeconds))
            : TimeSpan.FromSeconds(Math.Max(0, _settings.PrivateCooldownSeconds));

        if (cooldown == TimeSpan.Zero)
        {
            return true;
        }

        var key = conversation.SourceKey;
        var now = Clock.Now;
        if (_replyCooldown.TryGetValue(key, out var last) && now - last < cooldown)
        {
            _hooks.Log($"冷却中（{(cooldown - (now - last)).TotalSeconds:F1}s 后放行）: {conversation.Name}");
            return false;
        }

        _replyCooldown[key] = now;
        return true;
    }
    /// <summary>限流期间被挡下的触发：等这一轮生成结束再补一次评估（见 RunReplyAsync 的 finally）。</summary>
    /// <param name="TriggerId">要拿来当触发的那条消息 id（0 = 事件没有消息 id，例如戳一戳）。</param>
    /// <param name="Direct">这一窗口里有没有“直接跟机器人说话”的（@ 你 / 引用你的话）——那种必须被回答。</param>
    private readonly record struct DeferredTrigger(long TriggerId, bool Direct);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeferredTrigger> _deferredTriggers = new();
    /// <summary>
    /// “要不要现在回” + “不能回就记下来”——**统一入口**。
    /// 以前各处写的是 <c>if (AllowReply(...)) EnqueueReply(...)</c>：被冷却拦下的触发就永久丢了，
    /// 群里连珠炮时表现为“怎么都不理我”（管理员 2026-09-16 反馈）。
    /// 现在被拦下会记一笔，本轮生成结束后补一次评估；@ 你 / 引用你的话那种“直接跟你说话”的，
    /// 在补评估时会带上 Direct 标记（阈值不再把它压成沉默）。
    /// </summary>
    public void RequestReply(
        BotConversation conversation,
        long? triggerMessageId,
        bool proactive = false,
        bool catchUp = false,
        bool directInWindow = false)
    {
        if (!_settings.AiModeEnabled)
        {
            return;
        }

        if (catchUp || AllowReply(conversation))
        {
            if (catchUp)
            {
                // 补评估是“这一轮的尾巴”，也要刷新冷却时间，否则下一句又立刻放行、连珠炮变刷屏
                _replyCooldown[conversation.SourceKey] = Clock.Now;
            }

            var mustPreserve = catchUp || directInWindow;
            EnqueueReply(conversation, triggerMessageId, proactive, mustPreserve);
            return;
        }

        // 被挡下：记一笔待补（窗口里出现过“直接跟你说话”的，优先拿它当补评估的触发）
        var key = conversation.SourceKey;
        _deferredTriggers.AddOrUpdate(
            key,
            _ => new DeferredTrigger(triggerMessageId ?? 0, directInWindow),
            (_, old) =>
            {
                var direct = old.Direct || directInWindow;
                // 有直接跟你说话的 → 用那条当触发（那是最该回答的一条）；否则用最新的
                var id = directInWindow || old.TriggerId == 0 ? (triggerMessageId ?? old.TriggerId) : old.TriggerId;
                return new DeferredTrigger(id, direct);
            });
        _hooks.Log($"等这一轮说完再评估（被限流挡下的新消息已记下）: {conversation.Name}");
    }
    /// <summary>把一条待回复请求排入该会话的有界 FIFO 链，并唤醒调度器。</summary>
    private void EnqueueReply(
        BotConversation conversation,
        long? triggerMessageId,
        bool proactive = false,
        bool mustPreserve = false)
    {
        if (triggerMessageId is not null)
        {
            LastActiveRequestTime = Clock.LocalDateTime; // 主动请求刷新活跃时间
        }

        _pendingConversations[conversation.SourceKey] = conversation;
        var queue = _pendingReplies.GetOrAdd(conversation.SourceKey, _ => new TenantReplyQueue());
        _ = queue.Enqueue(new PendingReply(triggerMessageId, proactive, mustPreserve), TenantReplyQueue.DefaultCapacity);
        _ = DrainReplyQueueAsync();
    }

    /// <summary>
    /// 调度器：从各会话的 FIFO 链头取请求交给并发 worker。
    ///   • 同一会话同时只能有一个在途请求（保证回复顺序与引用正确）
    ///   • 不同会话并行执行（一个慢请求不再阻塞其它群）
    ///   • 全局并发上限由 _replyGate 控制
    /// </summary>
    private async Task DrainReplyQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _replyWorkerRunning, 1, 0) == 1)
        {
            return; // 已有调度器在跑
        }

        try
        {
            while (true)
            {
                var fired = false;

                foreach (var (key, queue) in _pendingReplies)
                {
                    if (_inFlight.ContainsKey(key))
                    {
                        continue; // 该会话已在跑 → 保持顺序，等它完成
                    }

                    if (queue.IsEmpty || !queue.TryDequeue(out var pending))
                    {
                        continue;
                    }

                    if (!_inFlight.TryAdd(key, 0))
                    {
                        // 并发抢占失败：把触发消息放回队首位置（重新入队到尾部也可，
                        // 因为同一会话此时必定无其它待处理项）
                        queue.Enqueue(pending, TenantReplyQueue.DefaultCapacity);
                        continue;
                    }

                    if (!_pendingConversations.TryGetValue(key, out var conversation))
                    {
                        _inFlight.TryRemove(key, out _);
                        continue;
                    }

                    fired = true;
                    _ = RunReplyAsync(conversation, key, pending.TriggerMessageId, pending.Proactive);
                }

                if (fired)
                {
                    continue; // 可能还有别的会话可跑
                }

                // 本轮一无所获：要么全在途（等释放），要么真的没活了
                var waiting = _pendingReplies.Any(kv => !kv.Value.IsEmpty && _inFlight.ContainsKey(kv.Key));
                if (!waiting)
                {
                    CleanupPending();
                    break;
                }

                await Clock.Delay(150);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _replyWorkerRunning, 0);
            if (_pendingReplies.Any(kv => !kv.Value.IsEmpty))
            {
                _ = DrainReplyQueueAsync();
            }
        }
    }
    /// <summary>清掉已排空且不在途的会话条目，避免字典无限增长。</summary>
    private void CleanupPending()
    {
        foreach (var key in _pendingReplies.Keys.ToList())
        {
            if (_inFlight.ContainsKey(key))
            {
                continue;
            }

            if (_pendingReplies.TryGetValue(key, out var queue) && queue.IsEmpty)
            {
                _pendingReplies.TryRemove(key, out _);
                _pendingConversations.TryRemove(key, out _);
            }
        }
    }
    /// <summary>执行一次回复（受全局并发闸门限制）。</summary>
    private async Task RunReplyAsync(BotConversation conversation, string sourceKey, long? triggerMessageId, bool proactive = false)
    {
        _traces.Begin(sourceKey);

        // Reserve includes queued waiters in the retired generation's lifetime.
        var gate = _replyGate.Reserve();
        try
        {
            await gate.WaitAsync();
            conversation.HasPendingReply = false;
            await GenerateReplyAsync(conversation, triggerMessageId, proactive);
        }
        catch (Exception ex)
        {
            _hooks.Log("回复流程异常: " + ex.Message);
        }
        finally
        {
            gate.Dispose();

            _inFlight.TryRemove(sourceKey, out _);
            LastActiveRequestTime = Clock.LocalDateTime;
            _traces.Complete(sourceKey, "done");

            // 限流期间被挡下的消息**不能就这么算了**（管理员反馈“连续多人对话不回应”的根因）：
            // 以前 AllowReply 一返回 false，那条触发就彻底没人评估了 —— 群里连着说话时，
            // 只要第一轮生成完是沉默（或慢），后面那几条就要等到 60 秒静默兜底才有下一次机会。
            // 现在：这一轮说完，若中间又有消息被挡下，就**补一次评估**（每轮生成只补一次，不会打转）。
            if (_deferredTriggers.TryRemove(sourceKey, out var deferred))
            {
                var why = deferred.TriggerId > 0 ? $"最近一条 #{deferred.TriggerId}" : "（无消息 id 的事件）";
                _hooks.Log($"补一次评估（上一轮生成期间又有新消息，{why}）: {conversation.Name}");
                RequestReply(conversation, deferred.TriggerId > 0 ? deferred.TriggerId : null,
                    catchUp: true, directInWindow: deferred.Direct);
            }

            _ = DrainReplyQueueAsync(); // 唤醒调度器处理该会话的后续项
        }
    }
    /// <summary>静默兜底：超过 IdleFallbackSeconds 没有主动请求时，为待处理会话补一次请求。</summary>
    public void IdleFallbackTick()
    {
        if (_hooks.IsDisposed() || !_settings.AiModeEnabled)
        {
            return;
        }

        // 审批台账周期清理（V3 §9.4：台账要有界，10 分钟节流也在用例里）
        _approvals.PruneIfDue(Clock.Now);

        if (Clock.LocalDateTime - LastActiveRequestTime < TimeSpan.FromSeconds(_settings.IdleFallbackSeconds))
        {
            return;
        }

        foreach (var conversation in _registry.Snapshot())
        {
            if (!conversation.HasPendingReply)
            {
                continue;
            }

            conversation.HasPendingReply = false;
            _hooks.Log($"静默兜底触发: {conversation.Name}");
            EnqueueReply(conversation, null);
        }

        TryProactiveSpeak();
    }
    private async Task GenerateReplyAsync(BotConversation conversation, long? triggerMessageId, bool proactive = false)
    {
        if (_riskBackoff?.IsActive(conversation.SourceKey) == true &&
            (triggerMessageId is not long mentionId ||
             conversation.Messages.FirstOrDefault(m => m.QqMessageId == mentionId)?.MentionedBot != true))
        {
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: "protocol_backoff");
            return;
        }

        if (_quotas?.IsEnergySaving(conversation.SourceKey) == true &&
            (proactive || (triggerMessageId is not long directMentionId ||
             conversation.Messages.FirstOrDefault(m => m.QqMessageId == directMentionId)?.MentionedBot != true)))
        {
            _hooks.Log($"[配额] 租户已达到当日 Token 配额上限，进入节能静默: {conversation.Name}");
            _traces.Node(conversation.SourceKey, TurnNodeKind.Participation, "silent", reasonCode: "quota_energy_saving");
            return;
        }

        // 配置快照（V3 §5.3）：这一轮从开始到结束一律读它 —— 面板热更新只影响后续处理，
        // 不会让"请求已经在路上"的这一轮中途换开关（能力闸门另有 caps 快照）。
        // 频率门（语音/表情/戳的冷却）仍读实时值：那是限速，不是授权。
        var snapshot = _settings.Snapshot();
        var caps = _approvals.Capabilities;
        var platformPolicy = _platformPolicies.ResolveForChannel(Channels.ChannelOf(conversation.SourceKey));
        if (!platformPolicy.Registered || !platformPolicy.Connected || !platformPolicy.Enabled || !platformPolicy.ChatEnabled)
        {
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: "platform_chat_disabled");
            return;
        }

        var turn = await BuildTurnInputsAsync(conversation, snapshot, caps, triggerMessageId, platformPolicy, proactive);
        if (turn is null)
        {
            return;
        }
        _traces.Node(conversation.SourceKey, TurnNodeKind.Context, "ok", count: turn.Context.Count);

        CompletionResult result;

        try
        {
            // 批次 E：模型步骤交给有限步进循环（默认 1 步 = 只调一次，与改造前逐字一致）。
            var outcome = await _turnLoop.RunAsync(
                conversation.SourceKey, turn, caps, proactive, snapshot.MaxAgentSteps,
                result2 => _inlineTools.RunAsync(conversation, turn.Snapshot, caps, result2));
            result = outcome.Result;
            _quotas?.RecordUsage(conversation.SourceKey, result.PromptTokens, result.CompletionTokens);
        }
        catch (Exception ex)
        {
            SetThinking(conversation, false);
            // 连异常类型和第一帧调用堆栈一起记：只记 Message 时，“空引用”这种根本看不出在哪（踩过）
            _hooks.Log($"模型请求失败: {ex.GetType().Name} {ex.Message}" +
                    (ex.StackTrace is { Length: > 0 } stack
                        ? "　@ " + stack.Split('\n').FirstOrDefault(l => l.Contains("BotAgentHost"))?.Trim()
                        : string.Empty));
            // P1 观测：模型这一路出事了（超时/连不上/上游 5xx）→ ModelTimeout（**只降级不升级**）。
            // 注意与“模型选择沉默”分开：那是它主动不说话，这是它没能说上话。
            _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ModelTimeout);
            _research.KeepNote(conversation.SourceKey, turn.SearchText, "模型请求失败");
            return;
        }

        var elapsed = (Clock.LocalDateTime - turn.Started).TotalMilliseconds;
        Volatile.Write(ref _lastGenerationMs, (long)elapsed);
        SetThinking(conversation, false);

        // 发言适合度门槛：以前只写在提示词里、代码不执行；现在真正生效。
        // 模型未按 JSON 输出（Suitability == null）时按普通文本回复处理，不拦截。
        // 例外：**这一轮带着刚查到的资料**时不受门槛限制 —— 模型自评“现在插嘴合适吗”时
        // 往往给低分（它只是回来汇报查到的东西，不是要插话），结果就是“查了半天啥也不说”。
        // 查都查了，就得让它说出来；真不想说（空回复）时下面会把资料留给下一轮。
        // 情绪介入（这一步才是“人性化陪伴”的关键）：先看它读到的气氛，再决定门槛 ——
        //   • 吵架/对线：不插嘴（门槛抬到 60，即“非说不可”才说）
        //   • 低落/求助/孤独：更愿意轻声接一句（门槛降 10），但禁掉表情包/语音/戳（人家难过时发图很尬）
        //   • 生气/吐槽：照常，但也不发表情包（容易像在嘲笑）
        var vibe = result.Vibe ?? "中性";
        var baseThreshold = Math.Clamp(turn.Snapshot.SuitabilityThreshold, 0, 100);

        // 这一轮是不是“人家在跟你说话”：触发那条 @ 了你，或引用了你发的那句话。
        // 两个用途：① 自评再低也接（被点名不应该沉默）；② 引用优先挂给点名的人。
        var directAddress = triggerMessageId is long directTriggerId &&
                            conversation.Messages.FirstOrDefault(m => m.QqMessageId == directTriggerId)?.DirectToBot == true;
        var threshold = VibeRules.VibeAdjustedThreshold(vibe, baseThreshold, out var vibeReason);
        var soberMood = vibe is "低落" or "求助" or "吵架" or "生气" or "吐槽";

        if (vibe != "中性" && result.VibeNote is { Length: > 0 })
        {
            _hooks.Log($"[Vibe] {conversation.Name}：读到「{vibe}」——{result.VibeNote}" +
                    (vibeReason.Length > 0 ? $"（{vibeReason}）" : string.Empty));
        }

        // 不管这轮说不说话，读到的气氛都记下来：沉默也是一种回应，下一轮要接得上
        _vibes.Remember(conversation.SourceKey, vibe, result.VibeNote);

        if (result.Suitability is int score && score < threshold)
        {
            // 被点名（@ 你 / 引用你的话）就该答，门槛不适用于这种轮次：
            // 管理员反馈“直接跟我说话它也不理”—— 自评低说明模型“不想插嘴”，但人家就是在问它，
            // 沉默在群里看着就是坏了（而且连珠炮场景下会连着好几条都不理）。
            // ⚠ 但“有人在对线”那一档是刻意设的规矩（不站队/不评理/不添柴）：哪怕 @ 你评理也不接。
            if (directAddress && vibe != "吵架")
            {
                _hooks.Log($"自评 {score} < 阈值 {threshold}，但这条是直接跟机器人说话（@ 你/引用了你的话）→ 照样接");
            }
            else if (turn.SearchText is null)
            {
                  _hooks.Log($"适合度不足 → 沉默（评分 {score} < 阈值 {threshold}" +
                          (vibe != "中性" ? $"，气氛 {vibe}" : string.Empty) + $"，{elapsed:F0}ms）: {conversation.Name}");
                  // P1 观测：这一轮**也是它选择不说**（只是理由是自评太低）→ 同样喂 Silent。
                  // 为什么不能只在那条“空回复”分支喂：现实里绝大多数沉默都是走这一支（自评 < 阈值），
                  // 漏掉它，状态机就永远看不到“连续静默”，V3 §7.3 的「静默 → 退场」也就形同虚设。
                  _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.Silent);
                  return;
            }
            else
            {
                _hooks.Log($"适合度不足（{score} < {threshold}）但本轮带着刚查到的资料 → 照样说");
            }
        }

        var sticker = await QueueTurnActionsAsync(conversation, turn.Snapshot, turn.Caps, turn.PlatformPolicy, result, directAddress);

        var reply = (result.Reply ?? string.Empty).Trim();

        // 模型想“用语音说这句”（speak）。真正的发送在下边（要等 isGroup/targetId），
        // 这里先把文本取出来：它得参与“沉默判定”与消息落库，否则“只发语音不说话”会被当成空回复。
        var voiceText = (result.Speak ?? string.Empty).Trim();

        // 断句归模型（管理员 2026-09-21）：它可以在 speak 里用 `|` 或换行把话切成几段，每段 = 一条语音条（最多 3 条）。
        // 为什么不让程序切：中文的停顿是语气的一部分（“你是不是傻，我可没这么说” vs “你是不是傻 | 我可没这么说”
        // 听着是两句话 ✗），这种事模型比正则懂。代码只留技术性限制（条数/字数）。
        var voiceParts = voiceText
            .Split(new[] { '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)   // 模型有时把同一句写两遍（A | A）→ 只发一条
            .Take(3)
            .ToList();
        if (voiceParts.Count == 0 && voiceText.Length > 0)
        {
            voiceParts.Add(voiceText);
        }

        // 模型可以顺手写一句“我现在的心情”——存下来，下一轮提示词里带上（空/太长会被忽略）
        if (_mood.SetText(result.Mood, Clock.Now))
        {
            _hooks.Log($"心情变成：{_mood.Describe(Clock.Now)}");
        }

        // 模型可以“只戳不说话”：这样也算有动作，不算沉默
        var pokeTarget = result.PokeTargetId;

        if (await TryHandleTurnShortcutsAsync(
                conversation, turn.Snapshot, turn.Caps, result, reply, sticker, pokeTarget, voiceText, triggerMessageId, turn.SearchText, elapsed))
        {
            return;   // 这一轮已经到此为止（开了待批单 / 问了问题 / 就是不说）
        }

        var (targetStopped, replyTo) = ResolveReplyTarget(conversation, result, turn, triggerMessageId, reply, directAddress, elapsed);
        if (targetStopped)
        {
            return;
        }

        var (isGroup, targetId) = conversation.Target;

        var (voiceSent, voiceFailed) = await TrySendVoiceAsync(
            conversation, turn.Snapshot, turn.Caps, turn.PlatformPolicy, voiceParts, voiceText, isGroup, targetId, result, directAddress);

        await SendTurnAsync(
            conversation, turn.Context, triggerMessageId, turn.Caps, turn.PlatformPolicy, isGroup, targetId, replyTo,
            reply, voiceText, voiceParts, voiceFailed, voiceSent, sticker, pokeTarget, elapsed, result, directAddress);
    }

    /// <summary>
    /// 第 3 步里「回谁」那一半：复读守卫（与上一条自己的话完全相同就沉默）加上引用目标裁决。
    /// 判定本身在纯函数 <see cref="ReplyTargetRules" /> 里（线上修过三次的规则，要能脱离端到端场景测）；
    /// 这里只负责把事实凑齐、把改了主意的那几种情况说给人听。
    /// 返回 Stopped=true 表示这一轮不说话了。
    /// </summary>
    private (bool Stopped, long? ReplyTo) ResolveReplyTarget(
        BotConversation conversation,
        CompletionResult result,
        TurnInputs turn,
        long? triggerMessageId,
        string reply,
        bool directAddress,
        double elapsed)
    {
        // 复读守卫：模型偶尔会把上下文里自己上一条发言一字不差地再说一遍 ——
        // 群里实测过单字“悼”连发 6 条：第一条件来自上游截断，之后模型读到自己那条“悼”，
        // 就把它当成“可以接的梗”反复发。一模一样的话紧接着再来一遍，对群里就是刷屏。
        if (reply.Length > 0 && EchoGuard.IsRepeatingOwnLastMessage(conversation.Messages, reply))
        {
            _hooks.Log($"检测到复读（与上一条自己的发言完全相同）→ 沉默（{elapsed:F0}ms）: {TextRules.Shorten(reply, 40)}");
            _research.KeepNote(conversation.SourceKey, turn.SearchText, "复读守卫");
            return (true, null);
        }

        // 回复引用目标怎么定（两件事一起决定，别只看排队时记录的那个 id）：
        //   ① **模型自己指认的最准**：提示词里最近几条别人的消息都带了 (#id)，
        //      它在 JSON 里用 replyTo 说明“我在回哪条”。这是唯一能从根上对上号的办法 ——
        //      启发式只能猜“最新那条”或“排队时的触发”，都猜不准（线上两度因此看起来回错人）。
        //      采信条件：它必须在本次上下文里（防模型报个不存在的编号）。
        //   ② 没指认时：只有“触发消息还是最新诉求”才拿它当引用目标；如果触发已经过去
        //      （后面有人插话，比如模型慢了几秒），就**不引用** —— 以前这里会抽“上下文里最新那条
        //      别人发的消息”当目标，于是“正文回答 A、引用挂到 B”，群里看到的就是“回复错人”。
        //   ③ 紧挨着回就不引用：目标后面没有别的新消息时，引用是多余的（保留原有手感）。
        var messages = conversation.Messages;
        var lastSelfIndex = EchoGuard.LastSelfMessageIndex(messages);
        var triggerIndex = EchoGuard.IndexOfMessage(messages, triggerMessageId);

        // 「本轮触发是在复读 / 模仿」时（有人原样重复了别人的话，包括学机器人说话），
        // 说话对象是**复读的那条**，不是被复读的原文。线上实测（handoff-4 §23）：群友 c 复读了
        // 机器人那句，机器人回“别学我说话！”，模型却把引用指到了上一条别人的消息上 ——
        // 它把“素材”当成了“对象”（§22 修的是启发式，治不了这一类）。
        // 提示词里已经把 replyTo 的语义写死，这里再兜一道：这种轮次里模型的指认只要不在触发那条上就不引用。
        var triggerWasEcho = triggerIndex >= 0 && EchoGuard.IsEchoOfEarlierMessage(messages, triggerIndex);

        // 引用目标的判定抽在 Services/Decision/ReplyTarget.cs（纯函数，能被确定性测；口径见那里的注释）。
        // 这里只负责把**事实**凑出来：模型指认了谁、那条能不能用、触发是谁、点名了没、是不是复读轮。
        // 抽出来的原因：这条规则线上修过三次，而“群里看到它回错人”这类问题必须能脱离端到端场景被反复验证。
        var replyTargetFacts = new Domain.Conversation.ReplyTargetFacts(
            Chosen: result.ReplyToMessageId,
            // 采信条件：在本次上下文里、未撤回、且是**别人发的**（自己引自己没意义）
            ChosenUsable: result.ReplyToMessageId is long chosenProbe &&
                turn.Context.Any(m => m.QqMessageId == chosenProbe && !m.Recalled && m.Role == MessageRole.Peer),
            ChosenIndex: EchoGuard.IndexOfMessage(messages, result.ReplyToMessageId),
            MessageCount: messages.Count,
            TriggerMessageId: triggerMessageId,
            TriggerIndex: triggerIndex,
            // 触发那条被撤回了就不能再引用（与 ChosenUsable 的 !Recalled 同口径，V3 §8.3）
            TriggerUsable: triggerIndex >= 0 && !messages[triggerIndex].Recalled,
            LastSelfIndex: lastSelfIndex,
            DirectAddress: directAddress,
            TriggerWasEcho: triggerWasEcho);

        var replyTargetDecision = Domain.Conversation.ReplyTargetRules.Resolve(replyTargetFacts);
        var replyTo = replyTargetDecision.Target;

        // 只把“改了主意”的那几种情况说给人听（与改造前的日志口径一致）
        switch (replyTargetDecision.ReasonCode)
        {
            case "direct_address_priority":
                _hooks.Log($"模型想引 #{result.ReplyToMessageId}，但这一轮是 #{triggerMessageId} 在跟机器人说话 → 改引触发那条（点名优先）");
                break;
            case "echo_ignored":
                _hooks.Log($"不引用（本轮触发是复读/模仿，模型却指认了 #{result.ReplyToMessageId}）—— 宁可不引，也不把引用挂到被复读的原文上");
                break;
            case "trigger_stale":
                _hooks.Log("不引用（触发消息已经过去了、后面有人插话）—— 宁可不引，也不把正文挂到别人头上");
                break;
            case "trigger_recalled":
                _hooks.Log("不引用（触发那条已经被撤回了，群里看不到它）");
                break;
        }

        return (false, replyTo);
    }

    /// <summary>
    /// 第 1 步：**取上下文**（以及提示词要用的素材）—— 会话窗口、参与者档案、表情包候选、群成员身份、
    /// 上一轮气氛、心情、刚听过的歌、刚撤回的消息、刚查到的资料、链接预览。
    /// 只读不写：这里不下任何判断（判断在第 3 步），也不发任何东西。
    /// </summary>
    private async Task<TurnInputs?> BuildTurnInputsAsync(
        BotConversation conversation,
        AppSettings snapshot,
        Domain.Permissions.ChatCapabilitySet caps,
        long? triggerMessageId,
        EffectivePlatformPolicy platformPolicy,
         bool proactive)
    {
        var context = conversation.TakeLast(snapshot.MaxContextMessages);
        if (context.Count == 0)
        {
            return null;   // 上下文空 = 没什么可回的（与改造前一致：直接结束这一轮）
        }

        // 当前上下文最早一条：档案只取比它更早的（按单调序号精确判定，避免时间戳秒级精度误判）
        var contextOldestSeq = context[0].Seq;
        var contextOldestUnix = context[0].Timestamp.ToUnixTimeSeconds();
        var (isGroupScope, scopeGroupId) = conversation.Target;

        // 收集最近 40 条上下文里出现过的发送者 QQ → 读取各自在**本会话**的人物档案（角色卡片）
        var profiles = new List<string>();
        var profileChars = 0;
        var recentSenders = context
            .TakeLast(40)
            .Where(m => m.Role == MessageRole.Peer && m.SenderId.HasValue)
            .Select(m => m.SenderId!.Value)
            .Distinct()
            .Take(Math.Max(0, snapshot.ProfileLookupCount))
            .ToList();

        foreach (var uid in recentSenders)
        {
            var summary = _profiles.GetProfileSummary(
                uid.ToString(),
                isGroupScope ? scopeGroupId : 0,
                snapshot.ProfileSummaryLines,
                contextOldestSeq,
                contextOldestUnix);

            if (string.IsNullOrWhiteSpace(summary))
            {
                continue; // 本会话没有更早的历史 → 不占提示词预算
            }

            if (profileChars + summary.Length > snapshot.MaxProfileChars)
            {
                break; // 超出预算：后面的（发言更早的）丢弃
            }

            profiles.Add(summary);
            profileChars += summary.Length;
        }

        var started = Clock.LocalDateTime;
        SetThinking(conversation, true);

        // 表情包候选：拿最近的对话文字当检索词，从库里挑几张给模型选。
        // 先挑后发 —— 库可能有上千张，全塞进提示词既贵又不准。
        // 气氛“沉”（有人低落/在吵架）时不给候选：给了它就容易挑一张发出去，与气氛不搭。
        var stickerChoices = new List<StickerChoice>();
        if (platformPolicy.Feature("stickers", snapshot.EnableStickers).Enabled && snapshot.StickerLibraryMax > 0 && snapshot.StickerCandidates > 0 &&
            !_vibes.IsSober(conversation.SourceKey))
        {
            var query = string.Join(" ", context.TakeLast(8).Select(m => m.Text));
            stickerChoices = _stickers
                .Store
                .PickCandidates(query, snapshot.StickerCandidates, -1, conversation.SourceKey)
                .Select(s => new StickerChoice(s.Id, StickerText.Describe(s)))
                .ToList();
        }

        // 群成员身份（群主 / 管理员 / 群头衔）：只在“有值得说的人”时才给，不占 token。
        // 上限 14 人：再多人就只是个名字列表，既没用又费 token。
        // 先等一下“刚触发的身份查询”：查询就十几毫秒，等它一下，免得第一次说话那轮看不到头衔。
        if (isGroupScope)
        {
            await _roles.WaitForPendingAsync(scopeGroupId, TimeSpan.FromMilliseconds(700));
        }

        var roleCount = 0;
        var groupRoles = isGroupScope ? _roles.DescribeForPrompt(scopeGroupId, 14, out roleCount) : null;

        // 上一次读到的气氛（有 TTL）：给模型当底色，让它接得上
        var vibeHint = _vibes.Hint(conversation.SourceKey);
        var previousVibe = _vibes.Current(conversation.SourceKey);

        _hooks.Log($"请求模型…（{conversation.Name}，上下文 {context.Count} 条，档案 {profiles.Count} 份/{profileChars} 字" +
                (roleCount > 0 ? $"，身份 {roleCount} 人" : string.Empty) +
                (previousVibe.Length > 0 ? $"，上轮气氛 {previousVibe}" : string.Empty) +
                (proactive ? "，主动开口" : string.Empty) +
                (stickerChoices.Count > 0 ? $"，表情包候选 {stickerChoices.Count} 张" : string.Empty) + "）");

        // 最近被戳过（10 分钟内）才给模型“可以戳回去”的指令，平时不浪费 token
        var pokeContext = platformPolicy.Feature("poke", snapshot.EnablePoke).Enabled &&
            _poke.RecentlyPoked(conversation.SourceKey, TimeSpan.FromMinutes(10), Clock.Now);

        // 心情（被戳次数客观 + 模型主观写的）：影响还戳不戳回去、话多话少。
        // 只在“刚被戳过”或“模型写过心情”时给，平常不浪费 token。
        var moodNow = Clock.Now;
        var moodText = pokeContext || _mood.CurrentText(moodNow) is not null ? _mood.Describe(moodNow) : null;

        // 刚“听过”的歌：把歌词与波形实测交给模型，用完就清（避免以后每轮都背上它）
        var musicText = _music.TakeNote(conversation.SourceKey);

        // 刚有人撤回了消息：把这件事告诉模型（但不告诉它撤回了什么）——用完就清
        var recallText = _recallNotes.TryRemove(conversation.SourceKey, out var pendingRecall) ? pendingRecall : null;

        // 刚上网查到的资料 / 读到的页面正文：交给模型，用完就清
        var searchText = _research.TakeNote(conversation.SourceKey);

        // 链接预览：给快站点 2.5 秒的机会当轮用上；太慢就先不等（完成后留给下一轮）
        if (_linkTasks.TryGetValue(conversation.SourceKey, out var linkTask))
        {
            await Task.WhenAny(linkTask, Clock.Delay(TimeSpan.FromMilliseconds(2500)));
        }

        var linkText = _linkNotes.TryRemove(conversation.SourceKey, out var pendingLink) ? pendingLink : null;

        return new TurnInputs(
            Started: started, Snapshot: snapshot, Caps: caps, PlatformPolicy: platformPolicy, Context: context, Profiles: profiles,
            ProfileChars: profileChars, StickerChoices: stickerChoices, RoleCount: roleCount,
            GroupRoles: groupRoles, VibeHint: vibeHint, PreviousVibe: previousVibe, PokeContext: pokeContext,
            MoodText: moodText, MusicText: musicText, RecallText: recallText, SearchText: searchText,
            LinkText: linkText);
    }

    /// <summary>
    /// 第 4 步里**不需要真的发东西**的那几条出口：开待批单（action=tool）、开待答问题（action=ask）、
    /// 以及“这一轮就是不说话”（空回复 / 上游空响应 / 自评低已经在上一步拦过）。
    /// 返回 true = 本轮到此为止。三条出口都**只借助既有链路**：服务端不执行任何工具、不授予任何权限。
    /// </summary>
    private async Task<bool> TryHandleTurnShortcutsAsync(
        BotConversation conversation,
        AppSettings snapshot,
        Domain.Permissions.ChatCapabilitySet caps,
        CompletionResult result,
        string reply,
        StickerRecord? sticker,
        long? pokeTarget,
        string voiceText,
        long? triggerMessageId,
        string? searchText,
        double elapsed)
    {
        // P3 审批触发点（V3 §9.4，**默认关**）：模型说“我想调工具”时，服务端**绝不上手执行**，
        // 只可能开一张待批单并公告到群里（模型输出不构成授权）。开不出单 → 照旧安全静默。
        if (snapshot.EnableApprovals
            && result.Action == Domain.Reply.ReplyAction.Tool
            && reply.Length == 0 && sticker is null && pokeTarget is null && voiceText.Length == 0
            && await _approvals.OpenApprovalForTool(conversation, result.ToolId, triggerMessageId, caps))
        {
            _research.KeepNote(conversation.SourceKey, searchText, "等待审批");
            return true;
        }

        // P2 提问路径（V3 §8.1 的 action=ask，**默认关**）：模型想问就问，但**提问不是权限** ——
        // 服务端只是把问题包一层自己的文案（一次性编号 + 有效期）发到当前会话；
        // 谁答一声都算答完，回答也只是一条普通消息，不触发任何动作。
        if (snapshot.EnableQuestions
            && result.Action == Domain.Reply.ReplyAction.Ask
            && reply.Length == 0 && sticker is null && pokeTarget is null && voiceText.Length == 0
            && await _approvals.OpenQuestionForAsk(conversation, result.QuestionText, triggerMessageId, caps))
        {
            _research.KeepNote(conversation.SourceKey, searchText, "等待回答");
            return true;
        }

        if (reply.Length == 0 && sticker is null && pokeTarget is null && voiceText.Length == 0)
        {
            // “上游把回复吞了”（200 但没 choices，已重试一次）与“模型自己决定不说话”不是一回事：
            // 以前两种都写成“模型选择沉默”，主人根本看不出是网关出事了（22:09 那条就是这样）。
            var why = result.UpstreamEmpty
                ? "上游空响应"
                : result.Suitability is int s2 ? $"自评 {s2}" : "空回复";
            _hooks.Log(result.UpstreamEmpty
                ? $"本轮没拿到模型输出（上游连续两次空响应，{elapsed:F0}ms，已重试）: {conversation.Name}"
                : $"模型选择沉默（{why}，{elapsed:F0}ms）: {conversation.Name}");
            // P2（结构化运行记录，V3 §12.2）：静默也要能一眼看出是哪一类 ——
            // 「模型自己决定不说」和「控制字段非法/空回复被降级」在排查时是两回事。
            // 只写结构化字段（不写正文、不写提示词）。
            _hooks.Log($"[决策] outcome=suppressed action={result.Action} reason={result.ReasonCode ?? "unknown"}"
                    + (result.Malformed ? " malformed=true（按安全默认降级）" : string.Empty)
                    + $": {conversation.Name}");
            // P1 观测：这一轮它**自己决定不说**（或被安全降级成不说）→ Silent。
            // V3 §7.3 那一列写着「连续静默 → active/exiting 退场」，所以这条不喂进去，状态机就永远
            // 看不到“它刚选择了沉默”这个事实 —— 之前的 Silent 只在“服务端拦下”（总开关关/不在白名单）。
            _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.Silent);
            _research.KeepNote(conversation.SourceKey, searchText, why);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 引用原文的兜底：本地查不到时去协议端问一次（OneBot get_msg），查到就把真实原文补写进那条消息。
    /// fire-and-forget：不阻塞收消息（那是在接收循环上跑的），也不影响这一轮已经开始的生成。
    /// </summary>
    /// <param name="retriggerIfSelf">
    /// 这条消息是被参与闸门拦下的（当时判成“无关消息”）：如果补查证明它引用的正是**机器人自己**说过的话，
    /// 那就不是在聊闲天 —— 标记为直接对话并按闸门重判一次，必要时补一条回复请求（只补一次）。
    /// </param>
    private void EnrichQuotedFromProtocolAsync(
        BotConversation conversation, ChatMessage appended, long quotedId, bool isGroup, bool retriggerIfSelf = false)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var (text, senderId) = await _source.GetMessageInfoAsync(quotedId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    _hooks.Log($"引用原文兜底：协议端取不到 #{quotedId} 的原文（保持“更早的消息”）");
                    return;
                }

                var botUin = _settings.NormalizedUin;
                var isSelf = !string.IsNullOrWhiteSpace(botUin) && senderId > 0 && senderId.ToString() == botUin;
                bool rewritten;
                lock (_quoteEnrichLock)
                {
                    // 期间可能已经被补过（或被裁剪），只认“还是那句更早的消息”的情况。
                    // 注意文字要与 BuildReplyAnnotation 的兜底原文完全一致：以前写成“更早的一条”，
                    // 顺序反了永远不匹配 —— 补写静默失效（2026-09-19 踩过）。
                    if (appended.Text is null || !appended.Text.Contains("已经看不到原文"))
                    {
                        return;
                    }

                    var body = appended.Text;
                    var close = body.IndexOf(']');
                    if (close >= 0 && body.StartsWith("[回复", StringComparison.Ordinal))
                    {
                        body = body[(close + 1)..].Trim();
                    }

                    var annot = BuildReplyAnnotation(isSelf ? "你" : "某人", text);
                    rewritten = conversation.RewriteText(appended, body.Length > 0 ? annot + " " + body : annot);
                }

                if (!rewritten)
                {
                    _hooks.Log($"引用原文兜底：取到了 #{quotedId} 的原文，但那条已经被裁出窗口（跳过）");
                    return;                      // 那条已经被裁出窗口了，没什么可补的
                }

                _ui.NotifyMessageAdded(conversation.SourceKey, appended);
                _registry.Save();
                _hooks.Log($"引用原文兜底：从协议端取到 #{quotedId} 的原文（{(isSelf ? "我发的" : "别人发的")}「{TextRules.Shorten(text, 24)}」）");

                if (!isSelf || !retriggerIfSelf)
                {
                    return;
                }

                // 原来这一轮已经被闸门按“无关消息”拦掉了；现在证据表明它在跟机器人说话 → 重判一次。
                // 仍受全局回复冷却与参与策略约束（闸门只收不放，不会因为引用了旧消息就绕开限流）。
                appended.DirectToBot = true;
                var recheck = Services.Participation.ParticipationGate.Decide(
                    _settings.EnableParticipationGating,
                    _participation.Observe(
                        conversation.SourceKey,
                        Services.Participation.ParticipationEvents.ClassifyInbound(isGroup, directToBot: true)));
                if (!recheck.Proceed)
                {
                    _hooks.Log($"[参与] 引用补查确认是在跟机器人说话，但闸门仍拦下（{recheck.ReasonCode}）: {conversation.Name}");
                    return;
                }

                _hooks.Log($"[参与] 引用补查确认是在跟机器人说话 → 补一次参与评估（{recheck.ReasonCode}）: {conversation.Name}");
                RequestReply(conversation, appended.QqMessageId, directInWindow: true);
            }
            catch (Exception ex)
            {
                _hooks.Log("引用原文兜底失败（不影响聊天）：" + ex.Message);
            }
        });
    }
    private readonly object _quoteEnrichLock = new();
}

/// <summary>入站最前面那几道闸的产物：标注后的消息 + 是不是纯旁白（null = 这条消息到此为止）。</summary>
public sealed record InboundHead(bool AsideOnly, QqChatMessage Message);

/// <summary>媒体类入站的产物：这一轮识别到的音乐分享（有歌时先不回复，等分析结果）。</summary>

/// <summary>
/// 一轮回复的"输入"（第 1 步的产物）：上下文 + 提示词素材。只读、不可变，后面几步都拿它取数。
/// </summary>
public sealed record TurnInputs(
    DateTime Started,
    AppSettings Snapshot,
    Domain.Permissions.ChatCapabilitySet Caps,
     EffectivePlatformPolicy PlatformPolicy,
    IReadOnlyList<ChatMessage> Context,
    List<string> Profiles,
    int ProfileChars,
    List<StickerChoice> StickerChoices,
    int RoleCount,
    string? GroupRoles,
    string? VibeHint,
    string PreviousVibe,
    bool PokeContext,
    string? MoodText,
    string? MusicText,
    string? RecallText,
    string? SearchText,
    string? LinkText);

/// <summary>
/// 回复主链要用到的宿主能力（由 BotAgentHost 提供实现）。
/// 全是回调而不是接口实现，是为了让宿主那边**一个方法都不新增**：接线只写在构造函数的参数里。
/// </summary>
/// <param name="Log">普通运行日志（写文件 + 推面板）。</param>
/// <param name="SelfId">登录的 QQ 号（0 = 未知）。</param>
/// <param name="IsDisposed">宿主是否已经在收摊（兜底轮询要看它，收摊中不再补请求）。</param>
public readonly record struct ReplyHooks(
    Action<string> Log,
    Func<long> SelfId,
    Func<bool> IsDisposed);

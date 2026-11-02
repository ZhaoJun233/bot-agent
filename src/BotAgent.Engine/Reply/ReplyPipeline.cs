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
    private readonly ISettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly IModelClient _brain;
    private readonly IProfileRepository _profiles;
    private readonly ConversationRegistry _registry;
    private readonly IPanelNotifier _ui;
    private readonly WhitelistGate _whitelist;
    private readonly ApprovalUseCase _approvals;
    private readonly ParticipationUseCase _participation;
    private readonly PokeUseCase _poke;
    private readonly VibeTracker _vibes;
    private readonly MemberRoleUseCase _roles;
    private readonly OwnMessageLedger _ownLedger;
    /// <summary>投递：语音/文字去重、引用挂载、分句发送与记账（图/戳一并在这里发）。</summary>
    private readonly ReplyDelivery _delivery;

    /// <summary>决策动作：当场结算（表情包/戳目标）与后台登记（查资料/读页面/听歌/分享）。</summary>
    private readonly ReplyTurnActions _actions;

    /// <summary>入站最前面的几道闸（平台/通道/白名单/自回显/去重/审批/旁白）。</summary>
    private readonly ReplyInboundGates _inboundGates;
    /// <summary>媒体类入站（表情包/音乐/链接/首群补历史）的后台登记与产出。</summary>
    private readonly ReplyInboundMedia _inboundMedia;
    /// <summary>轮次上下文构建（这一轮模型能看见什么）。</summary>
    private readonly ReplyTurnContext _turnContext;
    /// <summary>引用识别与回复目标裁决（本轮在回谁 / 引的是哪条）。</summary>
    private readonly ReplyQuoteContext _quotes;
    private readonly IAgentCommandService _agentCmds;
    /// <summary>发消息（分句/节奏/记账）：聊天、agent 回话、审批回执都走它。</summary>
    private readonly IQqMessageSender _plain;

    private readonly StickerService _stickers;
    private readonly IVoiceUseCase _voice;
    private readonly IMusicUseCase _music;
    private readonly IResearchUseCase _research;
    private readonly ILinkPreviewer? _links;
    private readonly IMoodRepository _mood;
    private readonly ReplyHooks _hooks;
    /// <summary>决策轨迹（批次 C）：一轮一条，只有形状 —— 见 <see cref="TurnTraceStore" /> 的注释。</summary>
    private readonly TurnTraceStore _traces;
    private readonly IProtocolRiskBackoff? _riskBackoff;
    private readonly ITokenUsageRecorder? _usage;
    private readonly PluginReplyPolicies? _replyPolicies;
    private readonly IPlatformPolicyResolver _platformPolicies;
    private readonly IWebhookDedupStore? _inboundDedup;
    private readonly PluginGate? _plugins;
    private readonly BackgroundEvaluatorQueue? _background;
    /// <summary>有限步进循环（批次 E）：默认 1 步 = 与改造前逐字一致。</summary>
    private readonly AgentTurnLoop _turnLoop;
    /// <summary>当场做掉只读工具（批次 E）：判定与冷却与“留给下一轮”那条路完全一致。</summary>
    private readonly InlineTurnTools _inlineTools;
    private readonly ReplyToolAvailability _replyTools;

    public ReplyPipeline(
        ISettingsBox box,
        IQqChatSource source,
        IModelClient brain,
        IProfileRepository profiles,
        ConversationRegistry registry,
        IPanelNotifier ui,
        WhitelistGate whitelist,
        ApprovalUseCase approvals,
        ParticipationUseCase participation,
        PokeUseCase poke,
        VibeTracker vibes,
        MemberRoleUseCase roles,
        OwnMessageLedger ownLedger,
        IAgentCommandService agentCmds,
        IQqMessageSender plain,
        StickerService stickers,
        IVoiceUseCase voice,
        IMusicUseCase music,
        IResearchUseCase research,
        ILinkPreviewer? links,
        IMoodRepository mood,
        ReplyHooks hooks,
        TurnTraceStore traces,
        IProtocolRiskBackoff? riskBackoff = null,
        ITokenUsageRecorder? usage = null,
        IPlatformPolicyResolver? platformPolicies = null,
        IWebhookDedupStore? inboundDedup = null,
        PluginGate? plugins = null,
        BackgroundEvaluatorQueue? background = null,
        PluginReplyPolicies? replyPolicies = null)
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
        _usage = usage;
        _replyPolicies = replyPolicies;
        _platformPolicies = platformPolicies ?? PlatformPolicyResolver.ForSettings(box);
        _inboundDedup = inboundDedup;
        _plugins = plugins;
        _background = background;
        _quotes = new ReplyQuoteContext(ownLedger, () => box.Current.NormalizedUin, research, hooks.Log);
        _inboundGates = new ReplyInboundGates(
            box, source, _platformPolicies, whitelist, approvals, participation, inboundDedup, hooks,
            (key, message) => LogThrottled(key, message));
        _inboundMedia = new ReplyInboundMedia(
            box, hooks, _platformPolicies, AllowsFeature, stickers, music, links, QueueBackground,
            conversation => RequestReply(conversation, null), EnsureGroupContext);
        _turnContext = new ReplyTurnContext(
            profiles, vibes, stickers, roles, poke, mood, music, research, _inboundMedia, hooks,
            AllowsFeature, (conversation, thinking) => SetThinking(conversation, thinking));
        _replyTools = new ReplyToolAvailability(plugins, _platformPolicies, hooks.IsDisposed, () => research.IsReady);
        _delivery = new ReplyDelivery(
            hooks, registry, ui, plain, source, stickers, voice, poke, mood, approvals, participation, traces, riskBackoff, AllowsFeature);
        _actions = new ReplyTurnActions(
            box, hooks, _replyTools, research, music, stickers, vibes, approvals, participation, _platformPolicies,
            _delivery, riskBackoff, AllowsFeature, QueueBackground, conversation => RequestReply(conversation, null));
        _turnLoop = new AgentTurnLoop(brain, traces, _replyTools);
        _inlineTools = new InlineTurnTools(research, approvals, participation, hooks.Log, plugins, _platformPolicies, hooks.IsDisposed);

        _replyGate = new ResizableReplyGate(
            Math.Clamp(box.Current.MaxConcurrentReplies, 1, 16));
    }

    private AppSettings _settings => _box.Current;

    /// <summary>Plugin gate: disabled preset plugin blocks new capability work; no gate = legacy behavior.</summary>
    private bool AllowsFeature(string capability) => _plugins?.Allows(capability) ?? true;

    private bool AllowsTool(string toolId) => _plugins?.AllowsTool(toolId) ?? true;

    /// <summary>Observability for wiring tests: is this preset capability currently enabled?</summary>
    public bool IsCapabilityEnabled(string capability) => AllowsFeature(capability);

    /// <summary>Observability for wiring tests: is this tool currently allowed by the plugin gate?</summary>
    public bool IsToolEnabled(string toolId) => AllowsTool(toolId);

    /// <summary>
    /// Queue non-reply work on the bounded evaluator. No queue = refuse to start unbounded
    /// background work (production wires one in CompositionRoot; tests that do not need
    /// background paths simply never reach here).
    ///
    /// 队列 worker 的执行上下文与入队方不同（<see cref="PlatformSourceExecution" /> 是 AsyncLocal）：
    /// 入队时显式捕获来源租约、在作业里恢复。少了这一步，发送会走通道路由的默认源回退，
    /// 等于绕过“来源已退休”的拒绝（协议端实例换代后仍往旧源/默认源发）。
    /// </summary>
    private bool QueueBackground(string jobId, string dedupKey, Func<CancellationToken, Task> work)
    {
        if (_background is null)
        {
            _hooks.Log("[Queue] 后台队列未接线，任务未执行: " + jobId);
            return false;
        }

        var source = PlatformSourceExecution.Current;
        if (_hooks.IsDisposed() || source is { IsCurrent: false }) return false;
        if (_source is IPlatformSourceRouter router
            && (source is null || !ReferenceEquals(source.Owner, router)))
        {
            _hooks.Log("[Queue] 缺少可信来源租约或来源不匹配，任务未执行: " + jobId);
            return false;
        }

        if (!_background.TryEnqueue(jobId, dedupKey, async ct =>
            {
                using var sourceScope = PlatformSourceExecution.Enter(source);
                if (_hooks.IsDisposed() || source is { IsCurrent: false } || ct.IsCancellationRequested) return;
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, source?.Lifetime ?? default);
                await work(lifetime.Token);
            }))
        {
            _hooks.Log("[Queue] 后台任务被拒绝或合并: " + jobId);
            return false;
        }

        return true;
    }

    /// <summary>最近一次主动请求时间（静默兜底判断用；跨线程原子访问）。</summary>
    private DateTime LastActiveRequestTime
    {
        get => new(Volatile.Read(ref _lastActiveRequestTicks));
        set => Volatile.Write(ref _lastActiveRequestTicks, value.Ticks);
    }

    /// <summary>切换「AI 正在思考」并把状态推给面板（实现在 <see cref="IPanelNotifier.SetThinking" />）。</summary>
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
    public void HandleInbound(QqChatMessage msg)
    {
        var pre = _inboundGates.Inspect(msg with { Text = MessageMarkers.EscapeExternalControlTags(msg.Text) });
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
        // 2026-09-19：引自己那句的判定不再只看内存表（重启就清空）—— 见 ReplyQuoteContext.Resolve 的 IsSelf。
        var quotedIsSelf = false;
        var quotedResolved = true;
        if (msg.ReplyToMessageId is not null || msg.ReplyToPreviewText is { Length: > 0 })
        {
            var (quotedName, quotedText, hit, isSelf) = _quotes.Resolve(conversation, msg);
            quotedIsSelf = isSelf;
            quotedResolved = hit;
            var annot = ReplyAnnotations.BuildReplyAnnotation(quotedName, quotedText);
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
            if (msg.IsGroup && AllowsFeature(PluginGate.ProfilesCapability))
            {
                _roles.Remember(msg);
            }
        }

        _hooks.Log(
            $"收到 {(msg.IsGroup ? $"群{msg.GroupId}" : "私聊")} {msg.SenderName}({msg.UserId}): " +
            $"{(msg.Text.Length > 80 ? msg.Text[..80] + "…" : msg.Text)}" +
            (msg.ImageUrls is { Count: > 0 } ? $" [+{msg.ImageUrls.Count}图]" : string.Empty));

        // 媒体类入站（表情包入库 / 音乐分享 / 链接预览）+ 首次群消息补历史：都不阻塞接收线程
        var musicShares = _inboundMedia.Register(conversation, msg);

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

    /// <summary>每个会话最近一次“有人撤回消息”的时间（冷却：连着撤几条时不要每条都评论）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastRecallAt = new();
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

            var now = EngineClock.Now;
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

            _turnContext.RememberRecall(key, $"（刚有人撤回了一条消息：{sender}。上下文里那条已标成 [已撤回]。）");
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
                _hooks.Log("[History] 获取群名失败: " + ex.Message);
            }

            if (conversation.HistoryLoaded || _source is not IPlatformHistorySource gateway || !gateway.IsConnected)
            {
                return;
            }

            // 会话已经满了：插入到顶部的旧消息会立刻被裁掉，白调一次协议端接口
            if (conversation.MessageCount >= _settings.MaxMessagesPerConversation)
            {
                conversation.HistoryLoaded = true;
                _hooks.Log($"[History] 群 {groupId} 本地已有 {conversation.MessageCount} 条（达上限），跳过历史补录");
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
                    _hooks.Log($"[History] 群 {groupId} 补入 {inserted} 条历史消息");
                }
            }
            catch (Exception ex)
            {
                _hooks.Log("[History] 拉取群历史失败: " + ex.Message);
            }
        });
    }
    /// <summary>节流日志：同一 key 在窗口内只输出一次（避免忙群里刷爆日志与面板）。</summary>
    public void LogThrottled(string key, string message, int windowSeconds = 60)
    {
        var now = EngineClock.TickCount;

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
        var now = EngineClock.Now;
        if (_replyCooldown.TryGetValue(key, out var last) && now - last < cooldown)
        {
            _hooks.Log($"冷却中（{(cooldown - (now - last)).TotalSeconds:F1}s 后放行）: {conversation.Name}");
            return false;
        }

        _replyCooldown[key] = now;
        return true;
    }
    /// <summary>静默兜底：超过 IdleFallbackSeconds 没有主动请求时，为待处理会话补一次请求。</summary>
    public void IdleFallbackTick()
    {
        if (_hooks.IsDisposed() || !_settings.AiModeEnabled)
        {
            return;
        }

        // 审批台账周期清理（V3 §9.4：台账要有界，10 分钟节流也在用例里）
        _approvals.PruneIfDue(EngineClock.Now);

        if (EngineClock.LocalDateTime - LastActiveRequestTime < TimeSpan.FromSeconds(_settings.IdleFallbackSeconds))
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
            if (_source is IPlatformSourceRouter) FlushDeferred(conversation);
            else EnqueueReply(conversation, null);
        }

        TryProactiveSpeak();
    }
    private async Task GenerateReplyAsync(BotConversation conversation, long? triggerMessageId, bool proactive = false, CancellationToken ct = default)
    {
        if (_riskBackoff?.IsActive(conversation.SourceKey) == true &&
            (triggerMessageId is not long mentionId ||
             conversation.Messages.FirstOrDefault(m => m.QqMessageId == mentionId)?.MentionedBot != true))
        {
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: "protocol_backoff");
            return;
        }

        var policyContext = new BotAgent.Domain.Plugins.ReplyPolicyContext(conversation.SourceKey, proactive,
            triggerMessageId is long directMentionId &&
            conversation.Messages.FirstOrDefault(m => m.QqMessageId == directMentionId)?.MentionedBot == true);
        if (_replyPolicies?.BlockReason(policyContext) is { } blockReason)
        {
            _traces.Node(conversation.SourceKey, TurnNodeKind.Participation, "silent", reasonCode: blockReason);
            return;
        }

        // 配置快照（V3 §5.3）：这一轮从开始到结束一律读它 —— 面板热更新只影响后续处理，
        // 不会让"请求已经在路上"的这一轮中途换开关（能力闸门另有 caps 快照）。
        // 频率门（语音/表情/戳的冷却）仍读实时值：那是限速，不是授权。
        var snapshot = _settings.Snapshot();
        var caps = _approvals.Capabilities;
        var platformPolicy = PlatformSourceExecution.Current is { } source
            ? _platformPolicies.Resolve(source.Context)
            : _platformPolicies.ResolveForChannel(Channels.ChannelOf(conversation.SourceKey));
        if (!platformPolicy.Registered || !platformPolicy.Connected || !platformPolicy.Enabled || !platformPolicy.ChatEnabled)
        {
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: "platform_chat_disabled");
            return;
        }

        var turn = await _turnContext.BuildAsync(conversation, snapshot, caps, platformPolicy, proactive);
        if (turn is null)
        {
            return;
        }
        _traces.Node(conversation.SourceKey, TurnNodeKind.Context, "ok", count: turn.Context.Count);

        CompletionResult result;

        // 自然对话驱动 agent（默认关）：够资格才在提示词里出现 action=agent 这条路（见 ReplyPipeline.NaturalAgent.cs）。
        var naturalTrigger = NaturalAgentTrigger(conversation, triggerMessageId, proactive, snapshot.EnableNaturalAgent);
        var naturalAgent = naturalTrigger is not null;

        try
        {
            // 批次 E：模型步骤交给有限步进循环（默认 1 步 = 只调一次，与改造前逐字一致）。
            var outcome = await _turnLoop.RunAsync(
                conversation.SourceKey, turn, caps, proactive, snapshot.MaxAgentSteps,
                result2 => _inlineTools.RunAsync(conversation, turn.Snapshot, caps, result2, turn.PlatformPolicy, ct),
                naturalAgent ? NaturalAgentPrompt.Hint : null, ct,
                step => _usage?.RecordUsage(conversation.SourceKey, step.PromptTokens, step.CompletionTokens));
            result = outcome.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || !_replyTools.IsCurrent)
        {
            SetThinking(conversation, false);
            return; // Source retirement/shutdown is not a provider failure.
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

        var elapsed = (EngineClock.LocalDateTime - turn.Started).TotalMilliseconds;
        Volatile.Write(ref _lastGenerationMs, (long)elapsed);
        SetThinking(conversation, false);

        if (TryDispatchNaturalAgent(conversation, naturalTrigger, result, elapsed))
        {
            return;
        }

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
        if (AllowsFeature(PluginGate.VibesCapability))
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

        var sticker = await _actions.QueueTurnActions(conversation, turn.Snapshot, turn.Caps, turn.PlatformPolicy, result, directAddress);

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
        if (_mood.SetText(result.Mood, EngineClock.Now))
        {
            _hooks.Log($"心情变成：{_mood.Describe(EngineClock.Now)}");
        }

        // 模型可以“只戳不说话”：这样也算有动作，不算沉默
        var pokeTarget = result.PokeTargetId;

        if (await TryHandleTurnShortcutsAsync(
                conversation, turn.Snapshot, turn.Caps, result, reply, sticker, pokeTarget, voiceText, triggerMessageId, turn.SearchText, elapsed))
        {
            return;   // 这一轮已经到此为止（开了待批单 / 问了问题 / 就是不说）
        }

        var (targetStopped, replyTo) = _quotes.ResolveTarget(conversation, result, turn, triggerMessageId, reply, directAddress, elapsed);
        if (targetStopped)
        {
            return;
        }

        var (isGroup, targetId) = conversation.Target;

        var (voiceSent, voiceFailed) = await _delivery.TryVoiceAsync(
            conversation, turn.Snapshot, turn.Caps, turn.PlatformPolicy, voiceParts, voiceText, isGroup, targetId, result, directAddress);

        await _delivery.SendAsync(
            conversation, turn.Context, triggerMessageId, turn.Caps, turn.PlatformPolicy, isGroup, targetId, replyTo,
            reply, voiceText, voiceParts, voiceFailed, voiceSent, sticker, pokeTarget, elapsed, result, directAddress);
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
    /// 走有界后台队列：每条未解析引用都可能触发一次协议端查询，确认引用的是机器人时还会补一次回复请求，
    /// 所以必须有容量、超时、取消与停机收口，不能再是 fire-and-forget。
    /// </summary>
    /// <param name="retriggerIfSelf">
    /// 这条消息是被参与闸门拦下的（当时判成“无关消息”）：如果补查证明它引用的正是**机器人自己**说过的话，
    /// 那就不是在聊闲天 —— 标记为直接对话并按闸门重判一次，必要时补一条回复请求（只补一次）。
    /// </param>
    private void EnrichQuotedFromProtocolAsync(
        BotConversation conversation, ChatMessage appended, long quotedId, bool isGroup, bool retriggerIfSelf = false)
    {
        var jobId = "quote-enrich:" + conversation.SourceKey + ":" + appended.QqMessageId + ":" + quotedId;
        QueueBackground(jobId, jobId, async ct =>
        {
            try
            {
                var (text, senderId) = await _source.GetMessageInfoAsync(quotedId, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

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

                    var annot = ReplyAnnotations.BuildReplyAnnotation(isSelf ? "你" : "某人", text);
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
            catch (OperationCanceledException)
            {
                // 停机/超时取消：不是失败，也不该补写引用。
            }
            catch (Exception ex)
            {
                _hooks.Log("引用原文兜底失败（不影响聊天）：" + ex.Message);
            }
        });
    }
    private readonly object _quoteEnrichLock = new();
}

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

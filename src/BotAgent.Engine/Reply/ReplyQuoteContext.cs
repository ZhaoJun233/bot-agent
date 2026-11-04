using BotAgent.Domain.Conversation;
using BotAgent.Domain.Reply;
using BotAgent.Services.Conversations;
using BotAgent.Services.Net;
using BotAgent.Services.OneBot;

namespace BotAgent.Services.Reply;

/// <summary>
/// 「这一轮在回谁、引的是哪条」：引用原文的识别（历史 / 自己发过的台账 / 协议端摘要）与引用目标裁决。
///
/// 为什么单独成类：这两件事都是“把事实凑齐再交给纯函数判定”，跟模型的输出处理、发送、记账无关；
/// 引用规则线上修过三次（回错人、引自己被吞、复读轮指错对象），拆出来之后改规则不用翻主链。
/// 纯判定部分仍在 <see cref="EchoGuard" /> / <see cref="ReplyTargetRules" />（Domain，能被确定性测试）。
/// </summary>
internal sealed class ReplyQuoteContext
{
    private readonly OwnMessageLedger _ownLedger;
    private readonly Func<string> _botUin;
    private readonly IResearchUseCase _research;
    private readonly Action<string> _log;

    public ReplyQuoteContext(OwnMessageLedger ownLedger, Func<string> botUin, IResearchUseCase research, Action<string> log)
    {
        _ownLedger = ownLedger;
        _botUin = botUin;
        _research = research;
        _log = log;
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
    public (string Name, string Text, bool Hit, bool IsSelf) Resolve(BotConversation conversation, QqChatMessage msg)
    {
        // 落盘的那份要在**查询之前**就绪：它是懒加载的，而重启后第一件事往往就是“有人引用了上一句”，
        // 那时候机器人还没发过任何消息（只在发送时加载的话，这里就会永远查不到 —— 2026-09-19 差点踩到）。
        _ownLedger.EnsureLoaded();
        var botUin = _botUin();
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
    /// 第 3 步里「回谁」那一半：复读守卫（与上一条自己的话完全相同就沉默）加上引用目标裁决。
    /// 判定本身在纯函数 <see cref="ReplyTargetRules" /> 里（线上修过三次的规则，要能脱离端到端场景测）；
    /// 这里只负责把事实凑齐、把改了主意的那几种情况说给人听。
    /// 返回 Stopped=true 表示这一轮不说话了。
    /// </summary>
    public (bool Stopped, long? ReplyTo) ResolveTarget(
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
            _log($"检测到复读（与上一条自己的发言完全相同）→ 沉默（{elapsed:F0}ms）: {TextRules.Shorten(reply, 40)}");
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
                _log($"模型想引 #{result.ReplyToMessageId}，但这一轮是 #{triggerMessageId} 在跟机器人说话 → 改引触发那条（点名优先）");
                break;
            case "echo_ignored":
                _log($"不引用（本轮触发是复读/模仿，模型却指认了 #{result.ReplyToMessageId}）—— 宁可不引，也不把引用挂到被复读的原文上");
                break;
            case "trigger_stale":
                _log("不引用（触发消息已经过去了、后面有人插话）—— 宁可不引，也不把正文挂到别人头上");
                break;
            case "trigger_recalled":
                _log("不引用（触发那条已经被撤回了，群里看不到它）");
                break;
        }

        return (false, replyTo);
    }
}

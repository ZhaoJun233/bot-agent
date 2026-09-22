namespace QQChatAgent.Services.Decision;

/// <summary>
/// 决定「这条回复要挂到哪条消息上」时用到的**事实**（全部由调用方从会话里取，这里不碰会话）。
///
/// 为什么把事实单独一个结构：这条规则线上修过三次（模型指认错人 / 触发已过期还引用 /
/// 复读轮引用到原文上 / 只发图时不引用），每次都是“群里看到它回错人”。
/// 抽出来之后，规则能被**确定性**地跑（见 tests/QQChatAgent.PipelineEval 的引用目标正确率），
/// 而不是只能靠集成测试里那几条端到端场景碰运气。
/// </summary>
/// <param name="Chosen">模型在 replyTo 里指认的消息编号（null = 它没指认）。</param>
/// <param name="ChosenUsable">它指认的那条**可用**吗：在本次上下文里、未撤回、且是别人发的。</param>
/// <param name="ChosenIndex">指认那条在当前会话窗口里的下标（-1 = 不在窗口里）。</param>
/// <param name="MessageCount">当前会话窗口的消息条数。</param>
/// <param name="TriggerMessageId">本轮是谁把机器人叫起来的（null = 不是消息触发的，比如被戳/主动发言）。</param>
/// <param name="TriggerIndex">触发那条在窗口里的下标（-1 = 不在窗口里）。</param>
/// <param name="TriggerUsable">触发那条还**可用**吗：在窗口里且**未被撤回**（撤回的那条群里已经看不到）。</param>
/// <param name="LastSelfIndex">窗口里机器人自己最后一条的下标（-1 = 它还没说过话）。</param>
/// <param name="DirectAddress">本轮触发是在**跟机器人说话**（@ 了它 / 引用了它的话）。</param>
/// <param name="TriggerWasEcho">本轮触发是在**复读/模仿**（含学机器人说话）。</param>
public readonly record struct ReplyTargetFacts(
    long? Chosen = null,
    bool ChosenUsable = false,
    int ChosenIndex = -1,
    int MessageCount = 0,
    long? TriggerMessageId = null,
    int TriggerIndex = -1,
    bool TriggerUsable = false,
    int LastSelfIndex = -1,
    bool DirectAddress = false,
    bool TriggerWasEcho = false);

/// <summary>规则给出的结论：要引用哪条（null = 不引用），以及为什么。</summary>
/// <param name="Target">最终要引用的消息编号；null = 这一轮不引用。</param>
/// <param name="ReasonCode">结构化原因码（短、无正文，可直接写进运行记录）。</param>
public readonly record struct ReplyTargetResult(long? Target, string ReasonCode)
{
    /// <summary>这一轮到底引没引。</summary>
    public bool Quoted => Target is not null;
}

/// <summary>
/// 「引用挂到哪条」的服务端规则（V3 §8.3：模型可以**建议**目标，但不能自己发明或篡改）。
///
/// 两条硬口径（都是线上真事换来的）：
///   1. **宁可不引，也不挂错人** —— 目标不可用/已过期/已被裁掉/就是最新那条，一律不引用；
///   2. **点名优先** —— 人家正在跟机器人说话时，模型指认别人也不作数，改引触发那条
///      （不然群里看到的就是“你正跟它说话，它去回另一个人”）。
/// </summary>
public static class ReplyTargetRules
{
    /// <summary>按事实推出引用目标。纯函数：同样的事实永远给同样的结论。</summary>
    public static ReplyTargetResult Resolve(ReplyTargetFacts facts)
    {
        var triggerIsCurrent = facts.TriggerIndex >= 0 && facts.TriggerIndex > facts.LastSelfIndex;

        // 触发那条已经被撤回了：不能再把它当引用目标 —— 群里其他人根本看不到那条，
        // 引用上去就是“回复了一条不存在的话”（与模型指认那条的 !Recalled 校验口径一致，V3 §8.3）。
        var triggerQuotable = facts.TriggerUsable;

        long? candidate;
        string reason;

        if (facts.Chosen is long chosen && facts.ChosenUsable && facts.ChosenIndex >= 0)
        {
            if (facts.DirectAddress && facts.TriggerMessageId is long directTrigger && chosen != directTrigger
                && triggerQuotable)
            {
                // 人家在跟你说话，模型却指了别人 → 先回该回的人
                candidate = directTrigger;
                reason = "direct_address_priority";
            }
            else if (facts.TriggerWasEcho && chosen != facts.TriggerMessageId)
            {
                // 复读轮：它把“素材”当成了“对象” → 宁可不引
                candidate = null;
                reason = "echo_ignored";
            }
            else
            {
                candidate = chosen;
                reason = "model_chosen";
            }
        }
        else if (facts.TriggerMessageId is long trigger)
        {
            if (triggerIsCurrent && triggerQuotable)
            {
                candidate = trigger;
                reason = "trigger_current";
            }
            else if (triggerIsCurrent && !triggerQuotable)
            {
                // 触发还在窗口里、就是最新那条，但它已经被撤回 → 不引用
                candidate = null;
                reason = "trigger_recalled";
            }
            else
            {
                // 触发已经过去了（后面有人插话）→ 不引用，免得把正文挂到插话的人头上
                candidate = null;
                reason = "trigger_stale";
            }
        }
        else
        {
            // 不是消息触发的（被戳 / 主动发言）：没有可引用的目标
            candidate = null;
            reason = "no_message_trigger";
        }

        if (candidate is long target)
        {
            var index = target == facts.Chosen ? facts.ChosenIndex
                : target == facts.TriggerMessageId ? facts.TriggerIndex
                : -1;

            if (index < 0)
            {
                // 目标已被滚动窗口裁掉 → 不引用
                return new ReplyTargetResult(null, "target_out_of_window");
            }

            if (index >= facts.MessageCount - 1)
            {
                // 目标后面没有别的新消息（它就是最新那条）→ 引用是多余的（保留原有手感）
                return new ReplyTargetResult(null, "target_is_latest");
            }

            return new ReplyTargetResult(target, reason);
        }

        return new ReplyTargetResult(null, reason);
    }
}

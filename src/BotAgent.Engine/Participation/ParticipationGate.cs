namespace BotAgent.Services.Participation;

/// <summary>闸门的结论（结构化）：放不放行、为什么、当时是什么状态。</summary>
public readonly record struct ParticipationGateResult(
    bool Proceed,
    string ReasonCode,
    ParticipationState State)
{
    /// <summary>只给日志/面板看的单行摘要（不含正文）。</summary>
    public string Describe() => $"proceed={Proceed} state={State} reason={ReasonCode}";
}

/// <summary>
/// 参与闸门（V3 §7.4「参与后能够在冷却或话题结束时退出」的落地开关）。
///
/// **默认关**，这是硬要求：V3 §5.3 规定“未启用新功能的旧会话保持原行为”，
/// 所以关着的时候这个类只回答一句话 —— 放行，且原因码是 <c>gating_off</c>（一眼能看出没拦）。
///
/// 打开之后的口径**只收不放**：状态机说“不参与”就不叫模型；它说“参与”也只是**回到原来的判定链**，
/// 后面还有既有的自评阈值、白名单、冷却那些门。也就是说闸门只能让机器人**少说话**，不可能多说话。
///
/// 为什么单独一个纯类：这条判定将来要拿去线上观测（“打开之后到底少说了多少”），
/// 必须能被确定性测（见 tests/BotAgent.ParticipationProbe），而不是埋在 BotAgentHost 的大流程里。
/// </summary>
public static class ParticipationGate
{
    /// <summary>
    /// 这一轮要不要继续走（叫模型 / 发送）。
    /// </summary>
    /// <param name="gatingEnabled">面板上的开关；**默认 false**。</param>
    /// <param name="decision">
    /// 状态机对这一轮入站事件的结论；<c>null</c> = 这一轮没判过（例如纯旁白不喂事件）→ 放行。
    /// </param>
    public static ParticipationGateResult Decide(bool gatingEnabled, ParticipationDecision? decision)
    {
        if (decision is not { } d)
        {
            // 没判过就不拦 —— “没判”不是“判过且不同意”，不能拿它当拒绝的理由
            return new ParticipationGateResult(true, "not_evaluated", ParticipationState.Observing);
        }

        if (!gatingEnabled)
        {
            return new ParticipationGateResult(true, "gating_off", d.State);
        }

        return new ParticipationGateResult(d.Allow, d.ReasonCode, d.State);
    }
}
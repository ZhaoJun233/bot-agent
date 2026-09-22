namespace QQChatAgent.Services.Participation;

/// <summary>
/// 「收到一条消息」该给状态机喂哪个**事件**（V3 §7.3 左边那一列）。
///
/// 单独抽成纯函数的原因：这张映射表是**观测质量的全部**——映射错了，状态机再对也没用
/// （日志里会看到“明明是别人在闲聊，却被当成在跟机器人说话”）。
/// 抽出来之后它能被确定性测（见 tests/QQChatAgent.ParticipationProbe），
/// 而不是只能靠端到端场景碰运气。
///
/// 口径（三条，与提示词里“什么算在跟我说话”保持一致）：
///   · **群里被 @ 或引用了机器人** → <see cref="ParticipationEvent.Mentioned" />（最强触发）；
///   · **群里的其它消息** → <see cref="ParticipationEvent.IrrelevantMessage" />（交叉发言/旁白）；
///   · **私聊** → <see cref="ParticipationEvent.RepliedToBot" />（一对一里每条都是在跟它说话）。
/// </summary>
public static class ParticipationEvents
{
    /// <summary>一条入站消息（不是戳一戳/撤回这类通知）对应的事件。</summary>
    /// <param name="isGroup">是不是群消息。</param>
    /// <param name="directToBot">是不是在跟机器人说话（@ 了它，或引用了它发的那条）。</param>
    public static ParticipationEvent ClassifyInbound(bool isGroup, bool directToBot)
        => isGroup
            ? (directToBot ? ParticipationEvent.Mentioned : ParticipationEvent.IrrelevantMessage)
            : ParticipationEvent.RepliedToBot;
}
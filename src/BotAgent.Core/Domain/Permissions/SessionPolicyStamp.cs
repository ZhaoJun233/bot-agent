using System;
using BotAgent.Domain.Qq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// 一条会话的**权限元数据**（general-agent-platform-plan.md 批次 B）：这条会话是**用哪个策略建立**的
/// —— 策略版本 + 策略指纹 + 通道 + 建立时间。
///
/// 为什么要有它：审批单早就绑定了策略版本（ApprovalTicket / ApprovalStore），但“**这条会话**属于哪个策略”
/// 一直没有显式记录 —— 策略换了之后，老会话到底是继续用老权限，还是当场重建映射，谁都答不上来
/// （DSH 的做法是后者：权限变化即重建会话）。这一批先把**事实**记下来（只记录、不改判定）；
/// “重建会丢掉什么会话级派生状态”要等真的有那份状态时再落地（批次 F 的多通道）。
/// </summary>
/// <param name="ConversationKey">会话 key（原样，不带脱敏 —— 它是内部标识，脱敏只在显示层做）。</param>
/// <param name="Channel">通道（private / official；由 key 前缀推出来）。</param>
/// <param name="PolicyVersion">建立这条会话时的策略版本。</param>
/// <param name="PolicyFingerprint">策略指纹（内容级：登记表 + 白名单 + 审批口径）。</param>
/// <param name="StampedAt">记这条戳的时间（由调用方给，Domain 不读时钟）。</param>
public sealed record SessionPolicyStamp(
    string ConversationKey,
    string Channel,
    int PolicyVersion,
    string PolicyFingerprint,
    DateTimeOffset StampedAt)
{
    /// <summary>按当前策略快照造一条戳（通道从 key 前缀推，不额外传参 —— 少一个会说谎的入参）。</summary>
    public static SessionPolicyStamp For(string conversationKey, ChatCapabilitySet policy, DateTimeOffset now)
        => new(
            conversationKey,
            Channels.ChannelOf(conversationKey),
            policy.Policy.PolicyVersion,
            policy.PolicyFingerprint,
            now);

    /// <summary>
    /// 与当前策略对不上 → 这条会话的权限元数据是旧的（该重建）。
    ///
    /// 口径刻意只看**指纹**（不看 <see cref="PolicyVersion" />）：版本是“内容变了才前进”的计数器，
    /// 而指纹是内容本身 —— “改了又改回来”会让版本往前走但权限其实一样，那时不该判定成旧会话。
    /// 版本仍然记着（审计要看“这条会话建立时的版本号”），只是不参与这个判断。
    /// </summary>
    public bool IsStale(string currentFingerprint)
        => !string.Equals(PolicyFingerprint, currentFingerprint, StringComparison.Ordinal);
}

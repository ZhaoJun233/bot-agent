namespace BotAgent.Domain.Profiles;

/// <summary>
/// 画像摘要的候选项（纯数据）：告诉用例“谁、在哪个范围、攒了多少条新发言、现有摘要是什么”，
/// 由用例去问模型要一句新摘要，再经 <c>IProfileRepository.ApplySummary</c> 写回。
///
/// 为什么在 Domain：它**是端口签名的一部分**（<c>IProfileRepository.FindSummarizable</c> 的返回类型），
/// 而端口不能引用适配层的类型（§3.2 的 <c>db → service</c>）—— 所以随端口一起下沉。
/// </summary>
public readonly record struct SummaryCandidate(
    string Uid,
    string Name,
    string Scope,
    long GroupId,
    IReadOnlyList<string> NewMessages,
    string ExistingSummary,
    long ThroughSeq,
    int FoldedCount);

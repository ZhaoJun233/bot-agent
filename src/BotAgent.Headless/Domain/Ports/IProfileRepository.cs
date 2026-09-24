using BotAgent.Domain.Profiles;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 人物画像的存取端口（由 <c>Adapters/Persistence/MemberProfileStore</c> 实现，见 §6.4）。
///
/// 为什么要有它：用例层（回复链记发言、画像巡检取候选 / 写回摘要）只该说“记一句”“把摘要写回去”，
/// 不该知道表长什么样、SQL 怎么写。有了端口，画像那几条规则可以**不连库**用假仓储测。
/// 形状与语义与实现一一对应；**不含**面板那几条只读查询（那是适配层自己的事）。
/// </summary>
public interface IProfileRepository
{
    /// <summary>挑出“又攒够了新发言”的人（巡检用；最多 <paramref name="maxCandidates" /> 个）。</summary>
    List<SummaryCandidate> FindSummarizable(int minNewMessages, int maxCandidates);

    /// <summary>把一条新摘要写回（并把已折叠的发言记为已归档）。</summary>
    void ApplySummary(string uid, string scope, string text, long throughSeq, int foldedCount);

    /// <summary>记下某人的一句发言（<paramref name="groupId" /> = 0 表示私聊）。</summary>
    void Append(string uid, string name, string text, DateTimeOffset time, string? groupName, long groupId = 0, long seq = 0);

    /// <summary>取出画像摘要文本（给提示词用）。</summary>
    string GetProfileSummary(
        string uid,
        long scopeGroupId = 0,
        int limit = 12,
        long beforeSeq = long.MaxValue,
        long beforeUnix = long.MaxValue,
        bool allScopes = false);
}

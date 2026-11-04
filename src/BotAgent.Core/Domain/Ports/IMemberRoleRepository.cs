namespace BotAgent.Domain.Ports;

/// <summary>
/// 群成员身份（群主 / 管理员 / 群头衔）的存取端口（由 <c>Adapters/Persistence/MemberRoleStore</c> 实现，见 §6.4）。
///
/// 为什么要有它：用例层只该说“记一下这个人是群主”“他是不是该重新问一次头衔”“把群里的身份写成一句话”，
/// 不该知道表长什么样、缓存怎么判新鲜。有了端口，身份规则可以**不连库**用假仓储测。
/// 形状与语义与实现一一对应；**不含**面板那几条只读查询（那是适配层自己的事）。
/// </summary>
public interface IMemberRoleRepository
{
    /// <summary>记下某人在某个群里的身份 / 头衔（<paramref name="titleChecked" /> = 头衔是不是已经问过协议端）。</summary>
    void Remember(string uid, long groupId, string? role, string? title, string? name, bool titleChecked = false);

    /// <summary>这个人的头衔该不该重新去问一次（问过一段时间内不再问）。</summary>
    bool NeedsRefresh(string uid, long groupId);

    /// <summary>把群里有身份的人拼成一句给提示词用的话；<paramref name="people" /> 回传人数。</summary>
    string? DescribeForPrompt(long groupId, int limit, out int people);
}

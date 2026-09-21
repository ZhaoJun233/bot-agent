using System.Text;

namespace QQChatAgent.Services.Qq;

/// <summary>
/// 通道（渠道）常量，以及「会话 key ⇄ 通道」的换算。
///
/// 为什么要有这个概念：机器人现在同时接两路上行 ——
///   • <see cref="Private"/>：**私域**，自建协议端（NapCat / OneBot，号主自己的 QQ 号）；
///   • <see cref="Official"/>：**官方商用**，QQ 开放平台（appid + 官方网关，用户是 openid）。
/// 两边的 QQ 号/群号体系完全不同（一边是数字 QQ 号，一边是 openid 字符串），
/// 上下文、人设、白名单、长期记忆必须**严格隔离** —— 否则官方那边一个 openid 撞上群号，
/// 就会把两个场景的对话串到一起。
///
/// 隔离靠的是**会话 key 前缀**，而且有个刻意的取舍：
/// <b>私域不加前缀</b>（仍旧是 <c>group:123</c> / <c>private:456</c>）——
/// 老库里的会话、面板按钮、<c>//</c> 命令参数全都以这个格式为准，加前缀会把它们全废掉。
/// 官方通道才带 <c>official:</c> 前缀。<see cref="ChannelOf"/> 认前缀，其余地方不必知道细节。
/// </summary>
public static class Channels
{
    /// <summary>私域通道：自建协议端（NapCat / OneBot）。</summary>
    public const string Private = "private";

    /// <summary>官方商用通道：QQ 开放平台（官方机器人）。</summary>
    public const string Official = "official";

    /// <summary>官方通道的会话 key 前缀。</summary>
    public const string OfficialPrefix = Official + ":";

    /// <summary>
    /// 官方通道「别名号」的起点：官方平台的会话标识是 openid 字符串（<c>C4A1…</c>），
    /// 而整条链路（白名单、会话 key、面板按钮）都按数字号走 —— 所以官方网关会把 openid
    /// **确定性地映射**到这个起点往上的数字号段（见 OfficialIdMap）。
    /// 真实 QQ 号/群号最多 10 位（当前 < 5e9），8e15 起步的号段永远撞不上 ——
    /// 于是“这个号属于哪个通道”成了一个秒判的规则，而不是需要查表的猜测。
    /// </summary>
    public const long AliasBase = 8_000_000_000_000_000L;

    /// <summary>是不是官方通道发的别名号。</summary>
    public static bool IsAliasId(long id) => id >= AliasBase;

    public static string ChannelOf(string? sourceKey)
        => sourceKey is not null && sourceKey.StartsWith(OfficialPrefix, StringComparison.OrdinalIgnoreCase)
            ? Official
            : Private;

    public static bool IsOfficial(string? channel)
        => string.Equals(channel, Official, StringComparison.OrdinalIgnoreCase);

    /// <summary>把（通道, 是否群, 目标号）拼成会话 key。私域通道不带前缀（见类注释的取舍）。</summary>
    public static string Key(string? channel, bool isGroup, long id)
        => IsOfficial(channel)
            ? $"{OfficialPrefix}{(isGroup ? "group" : "private")}:{id}"
            : $"{(isGroup ? "group" : "private")}:{id}";

    /// <summary>从会话 key 上得到（是否群, 目标号），前缀被吃掉。解析不出来时返回 (false, 0)。</summary>
    public static (bool IsGroup, long Id) Parse(string? sourceKey)
    {
        var parts = Strip(sourceKey).Split(':');
        return parts.Length == 2 && long.TryParse(parts[1], out var id) && id > 0
            ? (parts[0] == "group", id)
            : (false, 0);
    }

    /// <summary>去掉通道前缀（只剩 <c>group:123</c> / <c>private:456</c>）。</summary>
    public static string Strip(string? sourceKey)
    {
        if (string.IsNullOrEmpty(sourceKey))
        {
            return string.Empty;
        }

        return sourceKey.StartsWith(OfficialPrefix, StringComparison.OrdinalIgnoreCase)
            ? sourceKey[OfficialPrefix.Length..]
            : sourceKey;
    }

    /// <summary>面板/日志里给人看的名字。</summary>
    public static string Display(string? channel) => IsOfficial(channel) ? "官方商用" : "私域";

    /// <summary>短标签，用于会话列表里的小徽标。</summary>
    public static string Tag(string? channel) => IsOfficial(channel) ? "官方" : "私域";

    /// <summary>
    /// 会话 key 的排序/展示用副本：把 <c>group:123</c> 变成 <c>群 123</c>（脱敏在更外层做）。
    /// </summary>
    public static string Describe(string? sourceKey)
    {
        var (isGroup, id) = Parse(sourceKey);
        var body = id > 0 ? (isGroup ? "群 " + id : "私聊 " + id) : "(未知会话)";
        return IsOfficial(ChannelOf(sourceKey)) ? "官方 " + body : body;
    }

    /// <summary>调试用：一串 key 里的通道分布（只报数量，不带内容）。</summary>
    public static string Summary(IEnumerable<string> sourceKeys)
    {
        var official = 0;
        var priv = 0;
        foreach (var key in sourceKeys)
        {
            if (IsOfficial(ChannelOf(key)))
            {
                official++;
            }
            else
            {
                priv++;
            }
        }

        var sb = new StringBuilder();
        sb.Append("私域 ").Append(priv).Append(" 个会话");
        if (official > 0)
        {
            sb.Append("，官方 ").Append(official).Append(" 个会话");
        }

        return sb.ToString();
    }
}

using System.Text;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;

namespace BotAgent.Domain.Qq;

/// <summary>
/// 通道常量与遗留会话 key 换算收口。
/// 结构化编码现已由 <see cref="ConversationIdCodec"/> 集中管理，本类维护前缀与历史号段互斥规则。
///
/// 隔离靠的是**会话 key 前缀**与专属号段互斥（QQ 私域无前缀 / 官方 official: 8e15 起 / 本地 local: 7e15 起 / 飞书 feishu: 6e15 起）。
/// </summary>
public static class Channels
{
    /// <summary>私域通道：自建协议端（NapCat / OneBot）。</summary>
    public const string Private = "private";

    /// <summary>官方通道：QQ 开放平台（官方机器人）。</summary>
    public const string Official = "official";

    /// <summary>
    /// **本地通道**（批次 F）：从面板那张令牌门进来的本地 HTTP 入口（`POST /api/local/message`），
    /// 用来验证“接入层可换、核心与治理不用改”。默认**关**（见 <c>AppSettings.LocalChannelIds</c>：空 = 整个通道都不建）。
    /// </summary>
    public const string Local = "local";

    /// <summary>飞书通道（多平台演进阶段 4）：飞书应用机器人，默认关。</summary>
    public const string Feishu = "feishu";

    /// <summary>官方通道的会话 key 前缀。</summary>
    public const string OfficialPrefix = Official + ":";

    /// <summary>本地通道的会话 key 前缀。</summary>
    public const string LocalPrefix = Local + ":";

    /// <summary>飞书通道的会话 key 前缀。</summary>
    public const string FeishuPrefix = Feishu + ":";

    /// <summary>飞书通道内部别名号起点：6e15 起，与本地 7e15、官方 8e15 及真实 QQ 号互斥。</summary>
    public const long FeishuBase = 6_000_000_000_000_000L;

    /// <summary>是不是飞书通道发的内部别名号。</summary>
    public static bool IsFeishuId(long id) => id >= FeishuBase && id < LocalBase;

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

    /// <summary>
    /// 本地通道（批次 F）的**目标号段**起点：7e15 起。真实 QQ 号/群号当前 &lt; 5e9、官方别名 8e15 起，
    /// 三段互不相撞 —— 于是"这个号属于哪条通道"仍然是秒判的规则，而不是查表猜。
    ///
    /// 为什么必须有这一段：**路由器按数字路由出站**（<c>ChannelRouter.Resolve(isGroup, id)</c>），
    /// 两条通道拿到同一个 (isGroup, id) 就会串台。官方那条早有别名号段，本地这条照同一个办法解决。
    /// </summary>
    public const long LocalBase = 7_000_000_000_000_000L;

    /// <summary>本地 id 的**上限**（配置里写的那部分）：再往上 + LocalBase 就撞进官方别名号段了。</summary>
    public const long LocalIdMax = 999_999_999_999L;

    /// <summary>把配置里那个**短 id** 换成内部目标号（本地通道专用号段）。超范围返回 0（调用方拒掉）。</summary>
    public static long LocalTarget(long configuredId)
        => configuredId is > 0 and <= LocalIdMax ? LocalBase + configuredId : 0;

    /// <summary>是不是本地通道号段里的内部目标号。</summary>
    public static bool IsLocalId(long id) => id >= LocalBase && id < AliasBase;

    public static string ChannelOf(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return string.Empty;
        }

        if (sourceKey.StartsWith(OfficialPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Official;
        }

        if (sourceKey.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Local;
        }

        if (sourceKey.StartsWith(FeishuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Feishu;
        }

        if (sourceKey.StartsWith("v=1;", StringComparison.OrdinalIgnoreCase) && ConversationIdCodec.TryParse(sourceKey, out var cid))
        {
            var p = cid.PlatformId;
            return p switch
            {
                PlatformId.QqOfficial => Official,
                PlatformId.Local => Local,
                PlatformId.Feishu => Feishu,
                PlatformId.QqPrivate => Private,
                _ => string.Empty,
            };
        }

        return sourceKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase)
               || sourceKey.StartsWith("private:", StringComparison.OrdinalIgnoreCase)
            ? Private
            : string.Empty;
    }

    public static bool IsOfficial(string? channel)
        => string.Equals(channel, Official, StringComparison.OrdinalIgnoreCase);

    public static bool IsFeishu(string? channel)
        => string.Equals(channel, Feishu, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 上行自报的通道名 → 内部通道常量；未知值返回空，由入口拒绝。
    ///
    /// 空标签为兼容旧 OneBot 消息仍归私域；显式未知通道不可回退。
    /// </summary>
    public static string Declared(string? channel)
        => IsOfficial(channel) || string.Equals(channel, PlatformId.QqOfficial, StringComparison.OrdinalIgnoreCase) ? Official
            : IsLocal(channel) || string.Equals(channel, PlatformId.Local, StringComparison.OrdinalIgnoreCase) ? Local
            : IsFeishu(channel) || string.Equals(channel, PlatformId.Feishu, StringComparison.OrdinalIgnoreCase) ? Feishu
            : string.IsNullOrWhiteSpace(channel)
                || string.Equals(channel, Private, StringComparison.OrdinalIgnoreCase)
                || string.Equals(channel, PlatformId.QqPrivate, StringComparison.OrdinalIgnoreCase)
                ? Private : string.Empty;

    /// <summary>是不是本地通道（批次 F）。</summary>
    public static bool IsLocal(string? channel)
        => string.Equals(channel, Local, StringComparison.OrdinalIgnoreCase);

    /// <summary>把（通道, 是否群, 目标号）拼成会话 key。私域通道不带前缀（见类注释的取舍）。</summary>
    public static string Key(string? channel, bool isGroup, long id)
        => IsOfficial(channel)
            ? $"{OfficialPrefix}{(isGroup ? "group" : "private")}:{id}"
            : IsLocal(channel)
                ? $"{LocalPrefix}{(isGroup ? "group" : "private")}:{id}"
                : IsFeishu(channel)
                    ? $"{FeishuPrefix}{(isGroup ? "group" : "private")}:{id}"
                    : $"{(isGroup ? "group" : "private")}:{id}";

    /// <summary>从会话 key 上得到（是否群, 目标号），前缀被吃掉。解析不出来时返回 (false, 0)。</summary>
    public static (bool IsGroup, long Id) Parse(string? sourceKey)
    {
        var parts = Strip(sourceKey).Split(':');
        return parts.Length == 2
            && (string.Equals(parts[0], "group", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[0], "private", StringComparison.OrdinalIgnoreCase))
            && long.TryParse(parts[1], out var id) && id > 0
            ? (string.Equals(parts[0], "group", StringComparison.OrdinalIgnoreCase), id)
            : (false, 0);
    }

    /// <summary>去掉通道前缀（只剩 <c>group:123</c> / <c>private:456</c>）。</summary>
    public static string Strip(string? sourceKey)
    {
        if (string.IsNullOrEmpty(sourceKey))
        {
            return string.Empty;
        }

        if (sourceKey.StartsWith(OfficialPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return sourceKey[OfficialPrefix.Length..];
        }

        if (sourceKey.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return sourceKey[LocalPrefix.Length..];
        }

        return sourceKey.StartsWith(FeishuPrefix, StringComparison.OrdinalIgnoreCase)
            ? sourceKey[FeishuPrefix.Length..]
            : sourceKey;
    }

    /// <summary>面板/日志里给人看的名字。</summary>
    public static string Display(string? channel)
        => IsOfficial(channel) ? "官方" : IsLocal(channel) ? "本地" : IsFeishu(channel) ? "飞书" : "私域";

    /// <summary>短标签，用于会话列表里的小徽标。</summary>
    public static string Tag(string? channel)
        => IsOfficial(channel) ? "官方" : IsLocal(channel) ? "本地" : IsFeishu(channel) ? "飞书" : "私域";

    /// <summary>
    /// 会话 key 的排序/展示用副本：把 <c>group:123</c> 变成 <c>群 123</c>（脱敏在更外层做）。
    /// </summary>
    public static string Describe(string? sourceKey)
    {
        if (!string.IsNullOrEmpty(sourceKey)
            && sourceKey.StartsWith("v=1;", StringComparison.OrdinalIgnoreCase)
            && ConversationIdCodec.TryParse(sourceKey, out var cid))
        {
            var body = (cid.Kind == ConversationKind.GroupChat ? "群 " : "私聊 ") + cid.NativeTargetId;
            var tag = Tag(cid.PlatformId);
            return string.IsNullOrEmpty(tag) || tag == "私域" ? body : tag + " " + body;
        }

        var (isGroup, id) = Parse(sourceKey);
        var descBody = id > 0 ? (isGroup ? "群 " + id : "私聊 " + id) : "(未知会话)";
        var channel = ChannelOf(sourceKey);
        return IsOfficial(channel) || IsLocal(channel) || IsFeishu(channel) ? Tag(channel) + " " + descBody : descBody;
    }

    /// <summary>调试用：一串 key 里的通道分布（只报数量，不带内容）。</summary>
    public static string Summary(IEnumerable<string> sourceKeys)
    {
        var official = 0;
        var local = 0;
        var priv = 0;
        foreach (var key in sourceKeys)
        {
            if (IsOfficial(ChannelOf(key)))
            {
                official++;
            }
            else if (IsLocal(ChannelOf(key)))
            {
                local++;
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

        if (local > 0)
        {
            sb.Append("，本地 ").Append(local).Append(" 个会话");
        }

        return sb.ToString();
    }
}

using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;

namespace BotAgent.Services.Qq;

/// <summary>
/// 白名单闸门：这个群 / 这个人 / 这个会话 key 到底能不能收（V3 §4）。
///
/// 三份名单**各用各的**，混用会出“看不懂的拦”：
///   • 私域群 / 私聊：<c>WhitelistGroups</c> / <c>WhitelistPrivates</c>（留空回落到老的 <c>MessageWhitelist</c>）；
///   • 官方通道：<c>OfficialWhitelistGroups</c> / <c>OfficialWhitelistPrivates</c>，存的是**别名号**（8e15 起），
///     两份都留空 = **全部接受**（官方平台自身有准入与额度）—— 不能沿用“空 = 全拦”。
///
/// 2026-09-21 真实事故（搬过来时特意把注释一起带上）：收消息那道闸原来只看私域那份名单，
/// 于是官方通道无论怎么配都被拦，日志还只写“忽略（不在白名单）”。
/// 现在**会话 key 是唯一入口**（<see cref="AllowsKey" />，按前缀分派到正确的名单），别的都从它派生。
/// </summary>
public sealed class WhitelistGate
{
    private readonly SettingsBox _box;

    private HashSet<long> _groups = new();
    private bool _allGroups;
    private HashSet<long> _privates = new();
    private bool _allPrivates;

    private HashSet<long> _officialGroups = new();
    private bool _officialAllGroups = true;
    private HashSet<long> _officialPrivates = new();
    private bool _officialAllPrivates = true;

    /// <summary>哪一边在用旧的共用名单（面板上要如实显示，不然号主会以为新框填了没生效）。</summary>
    private bool _groupsFromLegacy;
    private bool _privatesFromLegacy;

    public WhitelistGate(SettingsBox box)
    {
        _box = box;
        Rebuild();
    }

    private AppSettings _settings => _box.Current;

    /// <summary>群那边是不是在用旧的共用名单（<c>MessageWhitelist</c>）。</summary>
    public bool GroupsFromLegacy => _groupsFromLegacy;

    /// <summary>私聊那边是不是在用旧的共用名单。</summary>
    public bool PrivatesFromLegacy => _privatesFromLegacy;

    /// <summary>重建三份名单（启动时、以及每次设置热更新后）。</summary>
    public void Rebuild()
    {
        var legacy = _settings.MessageWhitelist;

        _groupsFromLegacy = string.IsNullOrWhiteSpace(_settings.WhitelistGroups);
        _privatesFromLegacy = string.IsNullOrWhiteSpace(_settings.WhitelistPrivates);

        (_groups, _allGroups) = WhitelistPolicy.ParseWhitelist(
            _groupsFromLegacy ? legacy : _settings.WhitelistGroups);
        (_privates, _allPrivates) = WhitelistPolicy.ParseWhitelist(
            _privatesFromLegacy ? legacy : _settings.WhitelistPrivates);

        // 官方通道：单独的名单；两份都留空 = 全部接受（官方平台自身有准入与额度）——
        // 不能沿用 ParseWhitelist 的“空 = 全拦”，否则没配名单时官方通道会直接死掉。
        var officialGroups = _settings.OfficialWhitelistGroups;
        var officialPrivates = _settings.OfficialWhitelistPrivates;
        (_officialGroups, _officialAllGroups) = string.IsNullOrWhiteSpace(officialGroups)
            ? (new HashSet<long>(), true)
            : WhitelistPolicy.ParseWhitelist(officialGroups);
        (_officialPrivates, _officialAllPrivates) = string.IsNullOrWhiteSpace(officialPrivates)
            ? (new HashSet<long>(), true)
            : WhitelistPolicy.ParseWhitelist(officialPrivates);
    }

    /// <summary>
    /// 收消息那道闸：它必须**按通道**选名单（<c>msg.Channel</c> 直接传进来，别包一层 ChannelOf ——
    /// 那是“从 key 前缀反推通道”的，传 "official" 进去会被判成私域）。
    /// </summary>
    public bool AllowsMessage(QqChatMessage msg)
        => AllowsKey(Channels.Key(
            msg.Channel,
            msg.IsGroup,
            msg.IsGroup ? msg.GroupId : msg.UserId));

    /// <summary>
    /// 群/私聊是否在白名单里（戳一戳事件没有 QqChatMessage，只能单拎一个判据）。
    /// 两边**各用各的名单**（号主 2026-09-18：“私聊白名单和群聊白名单两个框分开”）：以前只比数字，
    /// 所以把一个 QQ 号填进去，连“同号的群”也一起放行了。
    /// </summary>
    public bool AllowsSource(bool isGroup, long id)
        => isGroup
            ? _allGroups || _groups.Contains(id)
            : _allPrivates || _privates.Contains(id);

    /// <summary>
    /// 会话 key 能不能收（白名单）—— **唯一入口**：通道分开算，官方通道用另一份名单（别名号）。
    /// </summary>
    public bool AllowsKey(string sourceKey)
    {
        var (isGroup, id) = Channels.Parse(sourceKey);
        if (id <= 0)
        {
            return false;
        }

        return Channels.IsOfficial(Channels.ChannelOf(sourceKey))
            ? isGroup
                ? _officialAllGroups || _officialGroups.Contains(id)
                : _officialAllPrivates || _officialPrivates.Contains(id)
            : AllowsSource(isGroup, id);
    }

    /// <summary>闸门现在的样子（启动日志与“被忽略”日志都用它，措辞与以前逐字一致）。</summary>
    public sealed record GateState(
        string Groups,
        string Privates,
        string OfficialGroups,
        string OfficialPrivates,
        bool GroupsFromLegacy,
        bool PrivatesFromLegacy,
        bool BothPrivateListsEmpty);

    /// <summary>取一份可打印/可判定闸门状态的快照。</summary>
    public GateState Describe()
    {
        var groups = WhitelistPolicy.WhitelistSummary(_allGroups, _groups);
        var privates = WhitelistPolicy.WhitelistSummary(_allPrivates, _privates);
        return new GateState(
            groups,
            privates,
            WhitelistPolicy.WhitelistSummary(_officialAllGroups, _officialGroups),
            WhitelistPolicy.WhitelistSummary(_officialAllPrivates, _officialPrivates),
            _groupsFromLegacy,
            _privatesFromLegacy,
            groups is "(空，忽略全部)" && privates is "(空，忽略全部)");
    }

    /// <summary>
    /// 官方通道的“某人被**显式**列进官方白名单·私聊”—— <c>//</c> agent 命令额外认这一种写法
    /// （那边发送者是别名号，<c>AgentAllowedUsers</c> 里填的私域真号永远匹配不上）。
    /// 空名单不算“全部允许”：执行命令的权限必须落到具体某个人身上。
    /// </summary>
    public bool IsOfficialPrivateExplicit(long userId)
        => _officialPrivates.Count > 0 && _officialPrivates.Contains(userId);
}

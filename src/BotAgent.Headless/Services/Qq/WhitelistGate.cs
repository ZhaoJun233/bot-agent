using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Platforms;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Domain.Configuration;
using System.Collections.Immutable;

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
    private readonly PlatformPolicyResolver _platformPolicies;
    private readonly Func<bool, long, bool>? _feishuTargetAllowed;

    private HashSet<long> _groups = new();
    private bool _allGroups;
    private HashSet<long> _privates = new();
    private bool _allPrivates;

    private HashSet<long> _officialGroups = new();
    private bool _officialAllGroups = true;
    private HashSet<long> _officialPrivates = new();
    private bool _officialAllPrivates = true;

    /// <summary>
    /// 本地通道（批次 F）：**空 = 一个人都不收**（fail-closed）。
    /// 与官方那条“空 = 全收”故意不同 —— 官方平台自己有准入与额度，本地通道是自建入口，没名单就不该开。
    /// </summary>
    private HashSet<long> _local = new();

    /// <summary>哪一边在用旧的共用名单（面板上要如实显示，不然管理员会以为新框填了没生效）。</summary>
    private bool _groupsFromLegacy;
    private bool _privatesFromLegacy;

    public WhitelistGate(SettingsBox box, PlatformPolicyResolver platformPolicies)
        : this(box, platformPolicies, null) { }

    public WhitelistGate(SettingsBox box, PlatformPolicyResolver platformPolicies,
        Func<bool, long, bool>? feishuTargetAllowed)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _platformPolicies = platformPolicies ?? throw new ArgumentNullException(nameof(platformPolicies));
        _feishuTargetAllowed = feishuTargetAllowed;
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
        var qqPolicy = FindPolicy(PlatformId.QqPrivate, AccountScope.Legacy);
        var offPolicy = FindPolicy(PlatformId.QqOfficial, AccountScope.Legacy);
        var locPolicy = FindPolicy(PlatformId.Local, AccountScope.Legacy);

        var groupSource = !string.IsNullOrWhiteSpace(qqPolicy?.GroupWhitelist) ? qqPolicy.GroupWhitelist : _settings.WhitelistGroups;
        var privateSource = !string.IsNullOrWhiteSpace(qqPolicy?.PrivateWhitelist) ? qqPolicy.PrivateWhitelist : _settings.WhitelistPrivates;

        _groupsFromLegacy = string.IsNullOrWhiteSpace(groupSource);
        _privatesFromLegacy = string.IsNullOrWhiteSpace(privateSource);

        (_groups, _allGroups) = WhitelistPolicy.ParseWhitelist(
            _groupsFromLegacy ? legacy : groupSource);
        (_privates, _allPrivates) = WhitelistPolicy.ParseWhitelist(
            _privatesFromLegacy ? legacy : privateSource);

        // 官方通道：单独的名单；两份都留空 = 全部接受（官方平台自身有准入与额度）——
        // 不能沿用 ParseWhitelist 的“空 = 全拦”，否则没配名单时官方通道会直接死掉。
        var officialGroups = !string.IsNullOrWhiteSpace(offPolicy?.GroupWhitelist) ? offPolicy.GroupWhitelist : _settings.OfficialWhitelistGroups;
        var officialPrivates = !string.IsNullOrWhiteSpace(offPolicy?.PrivateWhitelist) ? offPolicy.PrivateWhitelist : _settings.OfficialWhitelistPrivates;
        (_officialGroups, _officialAllGroups) = string.IsNullOrWhiteSpace(officialGroups)
            ? (new HashSet<long>(), true)
            : WhitelistPolicy.ParseWhitelist(officialGroups);
        (_officialPrivates, _officialAllPrivates) = string.IsNullOrWhiteSpace(officialPrivates)
            ? (new HashSet<long>(), true)
            : WhitelistPolicy.ParseWhitelist(officialPrivates);

        // 本地通道：只有点名才算数（空 = 全拦）。配置里写的是**短 id**（1、2、1001…），
        // 这里换算成内部目标号（+ LocalBase）—— 超出范围的直接丢掉（fail-closed，不让它撞进官方号段）。
        var hasLocPolicy = !string.IsNullOrWhiteSpace(locPolicy?.GroupWhitelist) || !string.IsNullOrWhiteSpace(locPolicy?.PrivateWhitelist);
        var localSource = hasLocPolicy
            ? $"{locPolicy?.GroupWhitelist},{locPolicy?.PrivateWhitelist}"
            : _settings.LocalChannelIds;
        var (localIds, _) = WhitelistPolicy.ParseWhitelist(localSource);
        _local = localIds.Select(Channels.LocalTarget).Where(id => id > 0).ToHashSet();
    }

    private PlatformPolicySettings? FindPolicy(string platform, string account)
        => (_settings.PlatformPolicies ?? new List<PlatformPolicySettings>()).FirstOrDefault(p =>
            p is not null
            && string.Equals(PlatformId.Normalize(p.PlatformId), platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(string.IsNullOrWhiteSpace(p.AccountScope) ? AccountScope.Default : p.AccountScope,
                account, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 收消息那道闸：它必须**按通道**选名单（<c>msg.Channel</c> 直接传进来，别包一层 ChannelOf ——
    /// 那是“从 key 前缀反推通道”的，传 "official" 进去会被判成私域）。
    /// </summary>
    public bool AllowsMessage(QqChatMessage msg)
        => AllowsKey(Channels.Key(
            msg.Channel,
            msg.IsGroup,
            msg.IsGroup ? msg.GroupId : msg.UserId));

    /// <summary>A caller must supply its authenticated source context; no instance is inferred from a key.</summary>
    public bool AllowsMessage(PlatformContext context, QqChatMessage msg)
        => AllowsSource(context, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);

    public bool AllowsSource(PlatformContext context, bool isGroup, long id)
    {
        var settings = _settings;
        if (!settings.RuntimePlatformPolicies.HasValue)
            return context == new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy) && AllowsSource(isGroup, id);
        var policy = _platformPolicies.Resolve(context);
        if (!policy.Registered || !policy.Connected || !policy.Enabled || !policy.ChatEnabled) return false;
        if (PlatformId.Normalize(context.PlatformId) == PlatformId.Feishu)
        {
            var current = PlatformSourceExecution.Current;
            if (current is not null)
                return current.IsCurrent && current.Context == context
                    && current.Source is IFeishuTargetMap native
                    && native.AllowsMappedTarget(isGroup, id);
            return _feishuTargetAllowed?.Invoke(isGroup, id) == true;
        }
        var rules = PlatformSwitchSettings.ResolvePolicyRules(settings, context, Actions(settings));
        return Matches(isGroup ? rules.GroupWhitelist : rules.PrivateWhitelist, id, context.PlatformId == PlatformId.Local);
    }

    private static ImmutableArray<ActionId> Actions(AppSettings settings)
        => BotAgent.Services.Agent.QqActionCatalog.ParseAllowed(settings.AgentServerQqActions)
            .Select(value => new ActionId(value)).ToImmutableArray();

    private static bool Matches(ListRule rule, long id, bool local)
    {
        if (id <= 0) return false;
        if (rule.Kind == ListRuleKind.AllowAll) return true;
        if (!rule.TryGetExplicitValue(out var text)) return false;
        var (ids, all) = WhitelistPolicy.ParseWhitelist(text);
        return local ? ids.Select(Channels.LocalTarget).Contains(id) : all || ids.Contains(id);
    }

    /// <summary>
    /// 群/私聊是否在白名单里（戳一戳事件没有 QqChatMessage，只能单拎一个判据）。
    /// 两边**各用各的名单**（管理员 2026-09-18：“私聊白名单和群聊白名单两个框分开”）：以前只比数字，
    /// 所以把一个 QQ 号填进去，连“同号的群”也一起放行了。
    /// </summary>
    public bool AllowsSource(bool isGroup, long id)
        => _settings.RuntimePlatformPolicies.HasValue
            ? AllowsCanonical(PlatformId.QqPrivate, AccountScope.Legacy, isGroup, id)
            : isGroup
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

        var channel = Channels.ChannelOf(sourceKey);
        if (!_platformPolicies.IsChatEnabled(channel))
        {
            return false;
        }

        if (_settings.RuntimePlatformPolicies.HasValue)
        {
            var platform = Channels.IsLocal(channel) ? PlatformId.Local : Channels.IsFeishu(channel) ? PlatformId.Feishu
                : Channels.IsOfficial(channel) ? PlatformId.QqOfficial : PlatformId.QqPrivate;
            return AllowsCanonical(platform, platform == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy, isGroup, id);
        }

        if (Channels.IsLocal(channel))
        {
            // 本地通道：只有点名的 id 收（空 = 全拦，fail-closed）
            return _local.Contains(id);
        }

        if (Channels.IsFeishu(channel))
        {
            // 飞书通道：入站网关已核验签名、去重及自身白名单
            return true;
        }

        return Channels.IsOfficial(channel)
            ? isGroup
                ? _officialAllGroups || _officialGroups.Contains(id)
                : _officialAllPrivates || _officialPrivates.Contains(id)
            : AllowsSource(isGroup, id);
    }

    private bool AllowsCanonical(string platform, string account, bool isGroup, long id)
    {
        var settings = _settings;
        var declared = settings.DeclaredPlatformDrivers.Where(key => key.PlatformId == platform && key.AccountScope == account).ToArray();
        if (declared.Length != 1 || settings.RuntimePlatformPolicies!.Value.Any(entry =>
            entry.Target is PolicyTarget.Instance target && target.Key.PlatformId == platform && target.Key.AccountScope == account))
            return false;
        var loaded = _platformPolicies.ResolveForChannel(platform);
        if (!loaded.Registered || !loaded.Connected || !loaded.Enabled || !loaded.ChatEnabled) return false;
        var rules = PlatformSwitchSettings.ResolvePolicyRules(settings, new PolicyTarget.AccountDefault(platform, account), Actions(settings));
        if (rules.Enabled != ToggleRule.Enabled || rules.ChatEnabled != ToggleRule.Enabled) return false;
        if (platform == PlatformId.Feishu)
        {
            // This is the unique canonical compatibility lane, not an inferred authenticated instance envelope.
            try { return _feishuTargetAllowed?.Invoke(isGroup, id) == true; }
            catch (Exception) { return false; }
        }
        return Matches(isGroup ? rules.GroupWhitelist : rules.PrivateWhitelist, id, platform == PlatformId.Local);
    }

    /// <summary>闸门现在的样子（启动日志与“被忽略”日志都用它，措辞与以前逐字一致）。</summary>
    public sealed record GateState(
        string Groups,
        string Privates,
        string OfficialGroups,
        string OfficialPrivates,
        string Local,
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
            // 本地通道（批次 F）：没有“全收”这一档（空 = 全拦），汇总只报数量
            _local.Count == 0 ? "无（全拦）" : _local.Count + " 个",
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
    {
        var settings = _settings;
        if (!settings.RuntimePlatformPolicies.HasValue) return _officialPrivates.Count > 0 && _officialPrivates.Contains(userId);
        var rules = PlatformSwitchSettings.ResolvePolicyRules(settings,
            new PolicyTarget.AccountDefault(PlatformId.QqOfficial, AccountScope.Legacy), Actions(settings));
        return rules.PrivateWhitelist.TryGetExplicitValue(out var text)
            && WhitelistPolicy.ParseWhitelist(text).Ids.Contains(userId)
            && AllowsCanonical(PlatformId.QqOfficial, AccountScope.Legacy, false, userId);
    }
}

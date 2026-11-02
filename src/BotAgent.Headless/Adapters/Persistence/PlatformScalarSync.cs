using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Platforms;
using BotAgent.Services;
using BotAgent.Services.Platforms;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 把面板上提交的平台标量（总开关、聊天开关、白名单文本）写回对应平台的默认策略条目。
///
/// 为什么必须有这一步：策略条目一旦存在，<see cref="PlatformSwitchSettings.Overlay"/> 就按条目判定，
/// 标量只在不存在的字段上兜底。旧面板时代标量是唯一入口，条目由 ImportLegacyPolicies 一次性导入，
/// 于是出现「面板改了白名单却不生效」——面板能改的东西不参与判定，参与判定的东西改不了。
///
/// 两条纪律：
/// ① 只认**这一次确实提交了**的字段（<see cref="FieldEdit{T}"/> 为 Set）：两种面板都只发送用户改过的字段
///    （旧面板逐字段 <c>LegacyField</c>、新面板 <c>buildPatch</c> 只带 dirty 项），所以「提交了」等于「用户改了」；
/// ② 名单规则的形状由 <see cref="PlatformSwitchSettings.ScalarListRules"/> 单一来源给出，
///    这里不重复实现「空名单是拦是放」这类语义。
/// </summary>
internal static class PlatformScalarSync
{
    private const int GroupBit = 4;
    private const int PrivateBit = 8;

    public static void Apply(
        Dictionary<PolicyTarget, PlatformPolicyEntry> entries,
        ImmutableDictionary<PolicyTarget, int>.Builder modes,
        AppSettings candidate,
        PlatformChanges scalars)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(scalars);

        SyncToggle(PlatformId.QqOfficial, AccountScope.Legacy, scalars.OfficialEnabled, scalars.OfficialChatEnabled);
        SyncToggle(PlatformId.QqPrivate, AccountScope.Legacy, default, scalars.PrivateChatEnabled);
        SyncToggle(PlatformId.Feishu, AccountScope.Default, scalars.FeishuEnabled, scalars.FeishuEnabled);
        if (scalars.LocalChannelIds.TryGetValue(out var ids))
        {
            var enabled = FieldEdit<bool>.Set(!string.IsNullOrWhiteSpace(ids));
            SyncToggle(PlatformId.Local, AccountScope.Legacy, enabled, enabled);
        }

        if (scalars.WhitelistGroups.Kind != EditKind.Keep
            || scalars.WhitelistPrivates.Kind != EditKind.Keep
            || scalars.MessageWhitelist.Kind != EditKind.Keep)
            SyncList(PlatformId.QqPrivate, AccountScope.Legacy);
        if (scalars.OfficialWhitelistGroups.Kind != EditKind.Keep
            || scalars.OfficialWhitelistPrivates.Kind != EditKind.Keep)
            SyncList(PlatformId.QqOfficial, AccountScope.Legacy);
        if (scalars.FeishuWhitelist.Kind != EditKind.Keep
            || scalars.FeishuWhitelistPrivates.Kind != EditKind.Keep)
            SyncList(PlatformId.Feishu, AccountScope.Default);
        if (scalars.LocalChannelIds.Kind != EditKind.Keep)
            SyncList(PlatformId.Local, AccountScope.Legacy);

        PlatformPolicyEntry EntryFor(PolicyTarget target)
            => entries.TryGetValue(target, out var entry)
                ? entry
                : new(target, new(ToggleRule.Inherit, ToggleRule.Inherit, ListRule.Inherit(), ListRule.Inherit(),
                    new(ActionRuleKind.Inherit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty));

        void SyncToggle(string platform, string account, FieldEdit<bool> enabled, FieldEdit<bool> chat)
        {
            var target = new PolicyTarget.AccountDefault(platform, account);
            if (enabled.Kind == EditKind.Keep && chat.Kind == EditKind.Keep) return;
            var entry = EntryFor(target);
            var rules = entry.Configuration;
            var bits = modes.GetValueOrDefault(target);
            if (enabled.TryGetValue(out var e))
            {
                rules = rules with { Enabled = e ? ToggleRule.Enabled : ToggleRule.Disabled };
                bits |= 1;
            }
            if (chat.TryGetValue(out var c))
            {
                rules = rules with { ChatEnabled = c ? ToggleRule.Enabled : ToggleRule.Disabled };
                bits |= 2;
            }
            entries[target] = entry with { Configuration = rules };
            modes[target] = bits;
        }

        void SyncList(string platform, string account)
        {
            var target = new PolicyTarget.AccountDefault(platform, account);
            var entry = EntryFor(target);
            var (group, direct) = PlatformSwitchSettings.ScalarListRules(platform, candidate);
            entries[target] = entry with
            {
                Configuration = entry.Configuration with { GroupWhitelist = group, PrivateWhitelist = direct },
            };
            modes[target] = modes.GetValueOrDefault(target) | GroupBit | PrivateBit;
        }
    }
}

using System.Threading;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Platforms;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void PlatformPolicyActionTests()
    {
        Section("多平台策略 · QQ 动作执行边界");

        var settings = new AppSettings
        {
            FeishuEnabled = true,
            PlatformPolicies =
            [
                new PlatformPolicySettings
                {
                    PlatformId = PlatformId.Feishu,
                    AccountScope = AccountScope.Default,
                    Enabled = true,
                    ChatEnabled = true,
                    AllowedActions = [],
                },
            ],
        };
        var registry = new SyntheticPlatformRegistry(new PlatformStatusSnapshot(
            PlatformId.Feishu, AccountScope.Default, "synthetic feishu", "synthetic", true, true,
            PlatformCapabilities.FeishuTextOnly));
        var resolver = new PlatformPolicyResolver(new Services.SettingsBox(settings), registry);
        var gateway = new FakeQqActions();
        var host = new SessionQqActionHost(gateway, isGroup: true, targetId: 10001,
            senderId: 20002, messageId: 30003, selfId: 10001, resolver, "feishu");
        var spec = QqActionCatalog.Find("like")!;
        var result = host.ExecuteAsync(spec, new System.Text.Json.Nodes.JsonObject
        {
            ["user_id"] = "sender",
        }, CancellationToken.None).GetAwaiter().GetResult();

        Check("★ 飞书会话拒绝 QQ 动作且不调用 OneBot 网关",
            result.Contains("仅支持私域 OneBot", StringComparison.Ordinal)
            && gateway.LikeCalls == 0, result);

        var policy = resolver.ResolveForChannel("feishu");
        Check("★ 显式空动作名单保持 fail-closed",
            policy.ActionAllowlistConfigured && !policy.CanUseAction("like"),
            $"configured={policy.ActionAllowlistConfigured}");
        var unknown = resolver.ResolveForChannel("synthetic.unknown");
        Check("★ 未注册平台拒绝文本与动作",
            !unknown.Registered && !unknown.Enabled && !unknown.CanUseAction("like"));
        var otherAccount = resolver.Resolve(new PlatformContext(PlatformId.Feishu, "synthetic-other"));
        Check("★ 不同账号不能继承已登记账号的连接状态",
            !otherAccount.Registered && !otherAccount.Connected);
        Check("★ 非法会话种类不被解析成私域目标",
            BotAgent.Domain.Qq.Channels.Parse("unknown:10001") == (false, 0L)
            && BotAgent.Domain.Qq.Channels.Parse("feishu:unknown:10001") == (false, 0L));

        // GitHub Issue #29: ConversationRegistry.GetOrCreate 对未知平台通道安全返回 null 而不抛出未捕获异常
        var conversationRegistry = new BotAgent.Services.Conversations.ConversationRegistry(
            new FakeConversationRepository(),
            new SettingsBox(settings),
            new BotAgent.Services.Local.LocalChannelSource(),
            _ => true,
            _ => { });
        var unknownChannelMsg = new BotAgent.Services.OneBot.QqChatMessage(
            0, true, 20002, 10001,
            "synthetic", "hello", DateTimeOffset.UtcNow, false,
            Channel: "unknown.platform");
        var created = conversationRegistry.GetOrCreate(unknownChannelMsg);
        Check("ConversationRegistry.GetOrCreate 对未知渠道安全降级返回 null 且不抛未捕获异常",
            created is null, created is null ? "ok" : "not null");

        // ── 测试各平台实例策略中的白名单覆盖机制 ──
        var policySettings = new AppSettings
        {
            MessageWhitelist = "10001",
            OfficialEnabled = true,
            OfficialWhitelistGroups = "8000000000000001",
            LocalChannelIds = "1",
            PlatformPolicies =
            [
                new PlatformPolicySettings
                {
                    PlatformId = PlatformId.QqPrivate,
                    AccountScope = AccountScope.Legacy,
                    GroupWhitelist = "10002",
                    PrivateWhitelist = "20003",
                },
                new PlatformPolicySettings
                {
                    PlatformId = PlatformId.QqOfficial,
                    AccountScope = AccountScope.Legacy,
                    GroupWhitelist = "8000000000000002",
                    PrivateWhitelist = "8000000000000003",
                },
                new PlatformPolicySettings
                {
                    PlatformId = PlatformId.Local,
                    AccountScope = AccountScope.Legacy,
                    GroupWhitelist = "2",
                    PrivateWhitelist = "3",
                },
            ],
        };
        var policyBox = new SettingsBox(policySettings);
        var policyResolver = new PlatformPolicyResolver(policyBox);
        var policyGate = new BotAgent.Services.Qq.WhitelistGate(policyBox, policyResolver);

        Check("★ QQ私域实例策略白名单优先覆盖全局设置（群 10002 放行，10001 拦截）",
            policyGate.AllowsSource(isGroup: true, id: 10002) && !policyGate.AllowsSource(isGroup: true, id: 10001));
        Check("★ QQ官方实例策略白名单优先覆盖（群 8000000000000002 放行，8000000000000001 拦截）",
            policyGate.AllowsKey("official:group:8000000000000002") && !policyGate.AllowsKey("official:group:8000000000000001"));
        Check("★ 本地通道实例策略白名单优先覆盖（本地 id=2 与 id=3 放行，id=1 拦截）",
            policyGate.AllowsKey(BotAgent.Domain.Qq.Channels.Key(BotAgent.Domain.Qq.Channels.Local, true, BotAgent.Domain.Qq.Channels.LocalTarget(2)))
            && policyGate.AllowsKey(BotAgent.Domain.Qq.Channels.Key(BotAgent.Domain.Qq.Channels.Local, false, BotAgent.Domain.Qq.Channels.LocalTarget(3)))
            && !policyGate.AllowsKey(BotAgent.Domain.Qq.Channels.Key(BotAgent.Domain.Qq.Channels.Local, true, BotAgent.Domain.Qq.Channels.LocalTarget(1))));

        // 校验全局通道关闭时不被实例策略 Enabled=true 穿透（Fail-Closed 原则）
        var disabledOfficialSettings = new AppSettings
        {
            OfficialEnabled = false,
            PlatformPolicies =
            [
                new PlatformPolicySettings
                {
                    PlatformId = PlatformId.QqOfficial,
                    AccountScope = AccountScope.Legacy,
                    Enabled = true,
                    ChatEnabled = true,
                },
            ],
        };
        var disabledOfficialResolver = new PlatformPolicyResolver(new SettingsBox(disabledOfficialSettings));
        var disabledOfficialPolicy = disabledOfficialResolver.ResolveForChannel("official");
        Check("★ 全局通道关闭时实例策略 Enabled=true 不会穿透越权（保持 disabled）",
            !disabledOfficialPolicy.Enabled && !disabledOfficialPolicy.ChatEnabled);

        // Exercise the real resolver, without loading persisted or production data.
        var unified = new AppSettings { OfficialEnabled = false, OfficialChatEnabled = false };
        var officialPolicy = new PlatformPolicySettings
        {
            PlatformId = PlatformId.QqOfficial, AccountScope = AccountScope.Legacy,
            Enabled = true, ChatEnabled = true, GroupWhitelist = "synthetic-group",
        };
        unified.PlatformPolicies.Add(officialPolicy);
        PlatformSwitchSettings.Normalize(unified);
        Check("switch migration preserves the old disabled intersection",
            !officialPolicy.Enabled!.Value && !officialPolicy.ChatEnabled!.Value
            && unified.PlatformSwitchSchemaVersion == 1);
        officialPolicy.Enabled = true;
        officialPolicy.ChatEnabled = true;
        var unifiedBox = new SettingsBox(unified);
        var unifiedResolver = new PlatformPolicyResolver(unifiedBox);
        Check("canonical policy switches override stale legacy switches",
            unifiedResolver.IsChatEnabled("official"));
        PlatformSwitchSettings.Normalize(unified);
        Check("migration is idempotent and mirrors the canonical switches",
            unified.OfficialEnabled && unified.OfficialChatEnabled
            && officialPolicy.GroupWhitelist == "synthetic-group");
        unifiedBox.Apply(s => s.PlatformPolicies[0].ChatEnabled = false);
        Check("chat mute takes effect immediately without disabling the platform",
            unifiedResolver.ResolveForChannel("official").Enabled
            && !unifiedResolver.IsChatEnabled("official"));
        unifiedBox.Apply(s => { s.PlatformPolicies[0].Enabled = false; s.PlatformPolicies[0].ChatEnabled = true; });
        Check("platform off preserves the chat switch preference",
            !unifiedResolver.IsChatEnabled("official")
            && PlatformSwitchSettings.Read(unifiedBox.Current, PlatformId.QqOfficial, AccountScope.Legacy).ChatEnabled);
        unifiedBox.Apply(s => s.PlatformPolicies[0].Enabled = true);
        Check("platform on restores the configured chat switch",
            unifiedResolver.IsChatEnabled("official"));

        var scoped = new AppSettings { OfficialEnabled = false, PlatformSwitchSchemaVersion = 1 };
        scoped.PlatformPolicies.Add(new PlatformPolicySettings
        {
            PlatformId = PlatformId.QqOfficial, AccountScope = "synthetic-other", Enabled = true, ChatEnabled = true,
        });
        PlatformSwitchSettings.Normalize(scoped);
        Check("another account cannot overwrite standard adapter enablement", !scoped.OfficialEnabled);
        Check("standard account does not inherit another account's switches",
            !new PlatformPolicyResolver(new SettingsBox(scoped)).IsChatEnabled("official"));
        scoped.PlatformPolicies.Add(new PlatformPolicySettings
        {
            PlatformId = "synthetic.unknown", AccountScope = AccountScope.Default, Enabled = true, ChatEnabled = true,
        });
        Check("unknown platform stays fail closed with explicit enabled policy",
            !new PlatformPolicyResolver(new SettingsBox(scoped)).IsChatEnabled("synthetic.unknown"));

        var localSettings = new AppSettings { PlatformSwitchSchemaVersion = 1 };
        localSettings.PlatformPolicies.Add(new PlatformPolicySettings
        {
            PlatformId = PlatformId.Local, AccountScope = AccountScope.Legacy, Enabled = true, ChatEnabled = true,
        });
        Check("local enablement is separate from whitelist readiness",
            new PlatformPolicyResolver(new SettingsBox(localSettings)).ResolveForChannel("local").Enabled);
        var noPolicies = new AppSettings { OfficialEnabled = true, OfficialChatEnabled = true };
        PlatformSwitchSettings.Normalize(noPolicies);
        Check("legacy-only configurations retain fallback without new action policies",
            noPolicies.PlatformPolicies.Count == 0
            && new PlatformPolicyResolver(new SettingsBox(noPolicies)).IsChatEnabled("official"));

        var legacyRosters = new AppSettings
        {
            WhitelistGroups = "10001",
            WhitelistPrivates = "10002",
            OfficialWhitelistGroups = "synthetic-group",
            OfficialWhitelistPrivates = "synthetic-user",
            FeishuWhitelist = "synthetic-feishu",
            LocalChannelIds = "101",
        };
        var projectedRosters = PlatformSwitchSettings.EditablePolicies(legacyRosters);
        Check("synthesized standard rows inherit legacy whitelists to prevent roundtrip clearing",
            projectedRosters.Single(p => p.PlatformId == PlatformId.QqPrivate).GroupWhitelist == "10001"
            && projectedRosters.Single(p => p.PlatformId == PlatformId.QqPrivate).PrivateWhitelist == "10002"
            && projectedRosters.Single(p => p.PlatformId == PlatformId.QqOfficial).GroupWhitelist == "synthetic-group"
            && projectedRosters.Single(p => p.PlatformId == PlatformId.QqOfficial).PrivateWhitelist == "synthetic-user"
            && projectedRosters.Single(p => p.PlatformId == PlatformId.Feishu).GroupWhitelist == "synthetic-feishu"
            && projectedRosters.Single(p => p.PlatformId == PlatformId.Local).GroupWhitelist == "101");

        var projected = PlatformSwitchSettings.EditablePolicies(noPolicies);
        noPolicies.PlatformPolicies = projected;
        Check("switch-only projection roundtrip preserves an absent action allowlist",
            !new PlatformPolicyResolver(new SettingsBox(noPolicies)).ResolveForChannel("private").ActionAllowlistConfigured);
        var explicitActions = new AppSettings { PlatformSwitchSchemaVersion = 1 };
        explicitActions.PlatformPolicies.Add(new PlatformPolicySettings
        {
            PlatformId = PlatformId.QqPrivate, AccountScope = AccountScope.Legacy, AllowedActions = new(),
        });
        explicitActions.PlatformPolicies = PlatformSwitchSettings.EditablePolicies(explicitActions);
        Check("explicit empty action allowlist remains configured and fail closed",
            new PlatformPolicyResolver(new SettingsBox(explicitActions)).ResolveForChannel("private").ActionAllowlistConfigured);
        projected.Single(p => p.PlatformId == PlatformId.QqPrivate).AllowedActions.Add("poke");
        Check("nonempty explicit actions are always configured even with inherited switch metadata",
            new PlatformPolicyResolver(new SettingsBox(noPolicies)).ResolveForChannel("private").ActionAllowlistConfigured);
        var matrixPreserved = true;
        foreach (var platform in new[] { PlatformId.QqPrivate, PlatformId.QqOfficial, PlatformId.Feishu, PlatformId.Local })
        foreach (var legacyOn in new[] { false, true })
        foreach (bool? overrideOn in new bool?[] { null, false, true })
        foreach (bool? overrideChat in new bool?[] { null, false, true })
        {
            var matrix = new AppSettings
            {
                OfficialEnabled = legacyOn, OfficialChatEnabled = legacyOn, PrivateChatEnabled = legacyOn,
                FeishuEnabled = legacyOn, LocalChannelIds = legacyOn ? "101" : "",
            };
            matrix.PlatformPolicies.Add(new PlatformPolicySettings
            {
                PlatformId = platform, AccountScope = platform == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy,
                Enabled = overrideOn, ChatEnabled = overrideChat,
            });
            var matrixResolver = new PlatformPolicyResolver(new SettingsBox(matrix));
            var beforeMigration = matrixResolver.ResolveForChannel(platform);
            PlatformSwitchSettings.Normalize(matrix);
            var afterMigration = matrixResolver.ResolveForChannel(platform);
            matrixPreserved &= beforeMigration.Enabled == afterMigration.Enabled
                && beforeMigration.ChatEnabled == afterMigration.ChatEnabled;
        }
        Check("all 72 legacy/platform/nullable switch combinations preserve effective states", matrixPreserved);
        var caseVariant = new AppSettings { PlatformSwitchSchemaVersion = 1, OfficialEnabled = true };
        caseVariant.PlatformPolicies.Add(new PlatformPolicySettings
        {
            PlatformId = "QQ.OFFICIAL", AccountScope = "LEGACY", Enabled = false, ChatEnabled = true,
        });
        var editable = PlatformSwitchSettings.EditablePolicies(caseVariant);
        Check("editable projection canonicalizes standard keys without duplicate account rows",
            editable.Count == 4 && editable.Count(p => p.PlatformId == PlatformId.QqOfficial && p.AccountScope == AccountScope.Legacy) == 1
            && editable.Single(p => p.PlatformId == PlatformId.QqOfficial).Enabled == false);
        Check("editable projection never mutates original policies",
            caseVariant.PlatformPolicies.Count == 1 && caseVariant.PlatformPolicies[0].AccountScope == "LEGACY");
        var inherited = new AppSettings { PlatformSwitchSchemaVersion = 1, FeishuEnabled = false, LocalChannelIds = "" };
        foreach (var platform in new[] { PlatformId.Feishu, PlatformId.Local })
            inherited.PlatformPolicies.Add(new PlatformPolicySettings
            {
                PlatformId = platform, AccountScope = "synthetic-other", Enabled = true, ChatEnabled = null,
            });
        var inheritedRows = PlatformSwitchSettings.EditablePolicies(inherited).Where(p => p.AccountScope == "synthetic-other").ToArray();
        Check("nonstandard accounts project resolved null fallback without unmuting",
            inheritedRows.Length == 2 && inheritedRows.All(p => p.Enabled == true && p.ChatEnabled == false)
            && inherited.PlatformPolicies.All(p => p.ChatEnabled is null));
    }

    private sealed class SyntheticPlatformRegistry(PlatformStatusSnapshot snapshot) : IPlatformRegistry
    {
        public IReadOnlyList<IPlatformAdapter> Adapters => Array.Empty<IPlatformAdapter>();
        public IPlatformAdapter? GetAdapter(string platformId, string accountScope = AccountScope.Default) => null;
        public IPlatformMessenger? GetMessenger(string platformId, string accountScope = AccountScope.Default) => null;
        public IReadOnlyList<PlatformStatusSnapshot> GetSnapshots() => [snapshot];
    }
}

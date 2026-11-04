extern alias StorageModule;

using BotAgent.Domain.Configuration;
using System.Collections.Immutable;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;
using ConfigurationModule = StorageModule::BotAgent.Storage;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Panel;
using BotAgent.Adapters.Panel.Zba;
using BotAgent.Adapters.Model;
using BotAgent.Adapters.Platforms;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Model;
using BotAgent.Services.Platforms;
using BotAgent.Services.Resilience;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.Official;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Qq;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;
using BotAgent.Services.Links;
using BotAgent.Services.Local;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.Reply;
using BotAgent.Services.Conversations;
using BotAgent.Services.Poke;
using BotAgent.Services.Permissions;
using BotAgent.Services.Participation;
using BotAgent.Services.Panel;
using BotAgent.Services.Settings;
using BotAgent.Services.Plugins;
using BotAgent.Services.Evaluation;
using BotAgent.Services.Mcp;
namespace BotAgent.Host;

internal static partial class CompositionRoot
{
    /// <summary>
    /// 数据侧的四件共享状态：会话台账（面板首屏与命令都读它）、自己发的消息台账、
    /// 心情的保留时长（现取配置）与限流日志。构造顺序与副作用（订阅 Changed / 载入心情）保持原样。
    /// </summary>
    private static (ConversationRegistry Registry, OwnMessageLedger OwnLedger, MoodStore Mood, ThrottledLog Throttled)
        BuildConversationState(ConversationStore store, SettingsBox settingsBox, IQqChatSource source,
            WhitelistGate whitelist, PanelNotifier ui)
    {
        var registry = new ConversationRegistry(store, settingsBox, source, whitelist.AllowsKey, ui.EmitLog);
        registry.Changed += ui.NotifyConversationsChanged;
        var ownLedger = new OwnMessageLedger(new OwnMessageStore(), ui.EmitLog);
        // 心情的保留时长跟着配置走：MoodStore 现取（以前要记得"改完设置顺手同步一次"，忘了就用着旧上限）
        var mood = new MoodStore(() => settingsBox.Current.MoodTtlSeconds);
        mood.Load(AppPaths.RuntimeRoot);

        var throttled = new ThrottledLog(ui.EmitLog);
        return (registry, ownLedger, mood, throttled);
    }

    private static bool DriverCredentialConfigured(DriverKey key, AppSettings current)
    {
        if (key.PlatformId == PlatformId.Local) return false; // No credential is required; readiness is independent.
        if (key.PlatformId == PlatformId.QqPrivate) return !string.IsNullOrWhiteSpace(current.OneBotToken); // Optional token.
        if (key.PlatformId == PlatformId.QqOfficial) return !string.IsNullOrWhiteSpace(current.OfficialAppSecret);
        if (key.PlatformId != PlatformId.Feishu) return false;
        using var connection = AppDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var metadata = ConfigurationSecretMetadata(connection, current, ImmutableArray<ConfigurationModule.SecretMutation>.Empty);
        var configured = metadata.Any(value => value.Slot == WritableSecretSlot.FeishuAppSecret && value.Configured)
            && metadata.Any(value => value.Slot is WritableSecretSlot.FeishuEncryptKey or WritableSecretSlot.FeishuVerificationToken && value.Configured);
        transaction.Commit();
        return configured;
    }

    private static ImmutableArray<BotAgent.Domain.Management.ManagedDescriptor> ManagedProviderDescriptors(
        IReadOnlyList<PersistedModelProvider> captured, AppSettings current)
    {
        using var connection = AppDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var items = ModelProviderStore.LoadAllInTransaction(connection).Select(provider =>
        {
            var runtime = captured.SingleOrDefault(item => item.Id == provider.Id);
            var binding = ModelProviderStore.ClassifyCredentialBinding(provider.SecretKeyRef);
            var configured = binding != CredentialBindingState.Unclassified
                && (provider.Protocol == ModelProtocol.Ollama
                    || binding != CredentialBindingState.Unconfigured
                        && !string.IsNullOrWhiteSpace(ResolveProviderSecretInTransaction(provider.SecretKeyRef, current, connection)));
            var restart = runtime is null || runtime.SecretKeyRef != provider.SecretKeyRef
                || runtime.IsEnabled != provider.IsEnabled || runtime.Priority != provider.Priority
                || runtime.Protocol != provider.Protocol || runtime.Options != provider.Options
                || provider.Id != "primary" && (runtime.BaseUrl != provider.BaseUrl || runtime.ModelName != provider.ModelName);
            return new BotAgent.Domain.Management.ManagedDescriptor(
                new BotAgent.Domain.Management.ManagedObjectKey.Provider(new(provider.Id)), "model-provider", provider.IsEnabled,
                runtime is null ? BotAgent.Domain.Management.ManagedRuntimeState.NotLoaded : BotAgent.Domain.Management.ManagedRuntimeState.Unknown,
                ImmutableArray.Create(new BotAgent.Domain.Management.ActionCapability(BotAgent.Domain.Management.ManagedAction.TestConnection,
                    configured, "provider_credential_required")),
                configured, restart ? "provider_restart_required" : null, "");
        }).ToImmutableArray();
        transaction.Commit();
        return items;
    }

    private static bool DriverIdentityMatches(AppSettings startup, AppSettings current, string platform)
        => PlatformId.Normalize(platform) switch
        {
            PlatformId.QqPrivate => startup.NormalizedUin == current.NormalizedUin
                && startup.OneBotProtocol == current.OneBotProtocol && startup.OneBotAddress == current.OneBotAddress
                && startup.OneBotToken == current.OneBotToken,
            PlatformId.QqOfficial => !string.IsNullOrWhiteSpace(startup.OfficialAppId)
                && !string.IsNullOrWhiteSpace(startup.OfficialAppSecret)
                && startup.OfficialAppId == current.OfficialAppId && startup.OfficialAppSecret == current.OfficialAppSecret
                && startup.OfficialSandbox == current.OfficialSandbox
                && startup.OfficialApiBase == current.OfficialApiBase && startup.OfficialTokenUrl == current.OfficialTokenUrl,
            PlatformId.Feishu => !string.IsNullOrWhiteSpace(startup.FeishuAppId)
                && startup.FeishuAppId == current.FeishuAppId && startup.FeishuAppSecret == current.FeishuAppSecret
                && startup.FeishuApiBase == current.FeishuApiBase
                && startup.FeishuEncryptKey == current.FeishuEncryptKey && startup.FeishuVerificationToken == current.FeishuVerificationToken,
            PlatformId.Local => true,
            _ => false
        };

    private static (OneBotGateway Gateway, IQqChatSource Source, OfficialBotGateway? Official, LocalChannelSource? Local, FeishuBotGateway? Feishu, IPlatformRegistry Registry, PlatformDriverOperations Operations) BuildChannelLayer(
        AppSettings settings, SettingsBox settingsBox, ProtocolRiskBackoff riskBackoff, IWebhookDedupStore inboundDedup)
    {
        var gateway = new OneBotGateway(settingsBox, riskBackoff)
        {
            SelfIdHint = settings.UinOrZero
        };

        var sources = new List<IQqChatSource> { gateway };
        OfficialBotGateway? official = null;
        LocalChannelSource? local = null;
        FeishuBotGateway? feishu = null;
        var officialEnabled = PlatformSwitchSettings.Read(settings, PlatformId.QqOfficial, AccountScope.Legacy).Enabled;
        var feishuEnabled = PlatformSwitchSettings.Read(settings, PlatformId.Feishu, AccountScope.Default).Enabled;
        if (officialEnabled
            && !string.IsNullOrWhiteSpace(settings.OfficialAppId)
            && !string.IsNullOrWhiteSpace(settings.OfficialAppSecret))
        {
            official = new OfficialBotGateway(settingsBox,
                new HttpFetcher(TimeSpan.FromSeconds(30), msg => FileLog.Write("Net", msg), "official"),
                new OfficialIdMap(Path.Combine(AppPaths.DataDir, "official-ids.json")),
                msg => FileLog.Write("Official", msg));
            sources.Add(official);
            FileLog.Write("Channel", $"官方通道已启用（appid={settings.OfficialAppId}，" +
                                   $"{(settings.OfficialSandbox ? "沙箱" : "正式")}环境，与私域通道隔离）");
            if (settings.OfficialSandbox)
            {
                FileLog.Write("Channel", "⚠ 官方通道跑在**沙箱环境**：只能收到开放平台「沙箱配置」里那些沙箱群/沙箱单聊的事件；"
                                        + "正式群里 @ 机器人不会被推送，日志里也不会有任何行。要在正式群用，取消面板「用沙箱环境」并重启。");
            }
        }
        else if (officialEnabled)
        {
            FileLog.Write("Channel", "官方通道开关是开的，但 appid/secret 没配齐 → 这次只跑私域通道");
        }

        // 本地通道（批次 F）：**名单非空才建**（默认关）。
        var localEnabled = settings.RuntimePlatformPolicies.HasValue
            ? PlatformSwitchSettings.ResolvePolicyRules(settings, new PolicyTarget.AccountDefault(PlatformId.Local, AccountScope.Legacy),
                QqActionCatalog.ParseAllowed(settings.AgentServerQqActions).Select(value => new ActionId(value)).ToImmutableArray()).Enabled == ToggleRule.Enabled
            : PlatformSwitchSettings.Read(settings, PlatformId.Local, AccountScope.Legacy).Enabled
                && !string.IsNullOrWhiteSpace(settings.LocalChannelIds);
        if (localEnabled)
        {
            local = new LocalChannelSource(msg => FileLog.Write("Local", msg));
            sources.Add(local);

            FileLog.Write("Channel", "本地通道已启用（名单非空；入口：面板 POST /api/local/message，与另两条上行隔离）");
        }

        // 飞书通道（阶段 4）：默认关，开启且配置了 appid 才构造，禁用的平台零网络零凭据。
        if (feishuEnabled && !string.IsNullOrWhiteSpace(settings.FeishuAppId))
        {
            feishu = new FeishuBotGateway(
                settingsBox,
                new HttpFetcher(TimeSpan.FromSeconds(30), msg => FileLog.Write("Net", msg), "feishu"),
                msg => FileLog.Write("Feishu", msg),
                ids: new FeishuIdMap(Path.Combine(AppPaths.DataDir, "feishu-ids-v2.json")),
                dedupStore: inboundDedup);
            sources.Add(feishu);

            FileLog.Write("Channel", $"飞书通道已启用（appid={settings.FeishuAppId}，入口：POST /api/webhooks/feishu）");
        }
        else if (feishuEnabled)
        {
            FileLog.Write("Channel", "飞书通道开关是开的，但 appid 没配齐 → 这次不启用飞书通道");
        }

        var adapters = new List<IPlatformAdapter>();
        var messengers = new List<IPlatformMessenger>();
        var gwAdapter = new QqChatSourcePlatformAdapter(
            gateway,
            new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy),
            PlatformCapabilities.QqOneBot,
            "QQ私域",
            "私域");
        adapters.Add(gwAdapter);
        messengers.Add(gwAdapter);

        if (official is not null)
        {
            var offAdapter = new QqChatSourcePlatformAdapter(
                official,
                new PlatformContext(PlatformId.QqOfficial, AccountScope.Legacy),
                PlatformCapabilities.QqOfficial,
                "QQ官方",
                "官方");
            adapters.Add(offAdapter);
            messengers.Add(offAdapter);
        }

        if (local is not null)
        {
            var locAdapter = new QqChatSourcePlatformAdapter(
                local,
                new PlatformContext(PlatformId.Local, AccountScope.Legacy),
                PlatformCapabilities.Local,
                "本地通道",
                "本地");
            adapters.Add(locAdapter);
            messengers.Add(locAdapter);
        }

        if (feishu is not null)
        {
            adapters.Add(feishu);
            messengers.Add(feishu);
        }

        var platformRegistry = new PlatformRegistry(adapters, messengers);
        var registrations = new List<PlatformDriverRegistration>
        {
            new(gwAdapter, gateway.StartAsync, gateway.StopAsync, initiallyRunning: false)
        };
        foreach (var adapter in adapters)
        {
            if (ReferenceEquals(adapter, gwAdapter)) continue;
            if (adapter is QqChatSourcePlatformAdapter legacy && ReferenceEquals(legacy.Source, official))
                registrations.Add(new(adapter, official!.StartAsync, official.StopAsync, initiallyRunning: false));
            else if (adapter is QqChatSourcePlatformAdapter localAdapter && ReferenceEquals(localAdapter.Source, local))
                registrations.Add(new(adapter, local!.StartAsync, local.StopAsync, initiallyRunning: false));
            else if (ReferenceEquals(adapter, feishu))
                registrations.Add(new(adapter, feishu!.StartAsync, feishu.StopAsync, initiallyRunning: false));
        }
        var driverOperations = new PlatformDriverOperations(registrations);
        var source = new ChannelRouter(sources, platformRegistry, driverOperations, msg => FileLog.Write("Channel", msg),
            adapter => DriverIdentityMatches(settings, settingsBox.Current, adapter.Context.PlatformId));
        return (gateway, source, official, local, feishu, platformRegistry, driverOperations);
    }

    /// <summary>
    /// ② 装配阶段二：上行通道层与平台策略（#110 第 2 步拆出）。
    ///
    /// 通道层包含协议端、可选的官方通道/本地通道/飞书通道与平台注册表（见 <see cref="BuildChannelLayer"/>）；
    /// 平台策略解析器只依赖 <see cref="Assembly.Box"/> 与注册表，所以紧跟其后建。
    /// </summary>
    private static void AssembleChannels(Assembly a)
    {
        // ① 上行通道层（协议端 + 可选的官方通道 + 本地通道 + 飞书通道 + 平台注册表）：见 BuildChannelLayer
        var (gateway, source, official, local, feishu, platformRegistry, driverOperations) =
            BuildChannelLayer(a.Settings, a.Box, a.RiskBackoff, a.InboundDedup);
        a.Gateway = gateway;
        a.Source = source;
        a.Official = official;
        a.Local = local;
        a.Feishu = feishu;
        a.PlatformRegistry = platformRegistry;
        a.DriverOperations = driverOperations;
        a.PlatformPolicies = new PlatformPolicyResolver(a.Box, platformRegistry, options =>
            QqActionCatalog.ParseAllowed(((AppSettings)options).AgentServerQqActions)
                .Select(value => new ActionId(value)).ToImmutableArray());
    }
}

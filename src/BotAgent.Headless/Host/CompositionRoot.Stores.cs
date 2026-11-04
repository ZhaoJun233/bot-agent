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
    private static PokeUseCase BuildPokeUseCase(
        SettingsBox settingsBox,
        BotIdentity identity,
        WhitelistGate whitelist,
        ConversationRegistry registry,
        PanelNotifier ui,
        ThrottledLog throttled,
        MoodStore mood) =>
        new(settingsBox, new PokeHooks(
            SelfId: () => identity.SelfId,
            IsSourceAllowed: (isGroup, targetId) => PlatformSourceExecution.Current is { } source
                ? source.IsCurrent && whitelist.AllowsSource(source.Context, isGroup, targetId)
                : whitelist.AllowsSource(isGroup, targetId),
            GetOrCreateConversation: registry.GetOrCreate,
            RecordInbound: (conversation, message) =>
            {
                conversation.Append(message);
                registry.Touch(conversation);
                ui.NotifyMessageAdded(conversation.SourceKey, message);
                registry.Save();
            },
            LogThrottled: (key, message) => throttled.Write(key, message),
            Log: ui.EmitLog,
            RecordPokeMood: now => mood.RecordPoke(now)));

    private static ApprovalUseCase BuildApprovalUseCase(
        SettingsBox settingsBox,
        PanelNotifier ui,
        ConversationRegistry registry,
        PlainSender plain,
        SessionPolicyLedger sessionPolicies,
        TurnTraceStore traces,
        AuditLogStore audit) =>
        new(settingsBox, new ApprovalHooks(
            Log: ui.EmitLog,
            Mask: (text, key) => MaskingRules.Text(settingsBox.Current.AgentMaskSensitive, text,
                key is null ? null : registry.KnownNames(key)),
            SendPlainAsync: plain.SendPlainAsync,
            SendApprovalReplyAsync: plain.SendApprovalReplyAsync,
            // 批次 I（面板审批）：面板没有入站消息可回，回执按**会话 key**发回原会话
            // （群里的人要看到“谁批了”）；会话已删就什么也不做（失败关闭）。
            SendToKeyAsync: (key, text) =>
            {
                var target = registry.Find(key);
                return target is null ? Task.CompletedTask : plain.SendPlainAsync(target, text);
            }),
            sessionPolicies,
            traces,
            audit);

    /// <summary>
    /// ④ 装配阶段四：用例层的共享状态与各域用例（#110 第 2 步拆出）。
    ///
    /// 共享状态（登录号 / 收摊标记 / 白名单闸门 / 会话台账）必须先建：后面的发送、用例、
    /// 工具都要问它们；白名单闸门拿到的是解析器 + 飞书通道的目标映射，顺序不能倒。
    /// </summary>
    private static void AssembleStores(Assembly a)
    {
        // ---------------- 用例层：谁依赖谁，只在这里看得到 ----------------
        // 共享状态先建：登录号 / 收摊标记 / 事件聚合 / 白名单闸门（其余组件都要问它们）
        a.Identity = new BotIdentity();
        a.Lifetime = new BotLifetime();
        a.Whitelist = new WhitelistGate(a.Box, a.PlatformPolicies,
            a.Feishu is null ? null : a.Feishu.AllowsMappedTarget);

        if (a.Source is ChannelRouter router)
            router.BindOutboundAuthorization((context, isGroup, targetId, feature) =>
            {
                var policy = a.PlatformPolicies.Resolve(context);
                if (feature == "customfaces") return policy.Registered && policy.Connected && policy.Enabled && policy.Capabilities.SupportsStickers;
                return policy.Registered && policy.Connected && policy.Enabled && policy.ChatEnabled
                    && a.Whitelist.AllowsSource(context, isGroup, targetId) && policy.Feature(feature, globallyEnabled: true).Enabled;
            });

        // 数据侧：会话台账 / 自己发的消息 / 心情 / 限流日志，见 BuildConversationState
        var (registry, ownLedger, mood, throttled) =
            BuildConversationState(a.Store, a.Box, a.Source, a.Whitelist, a.Ui);
        a.Registry = registry;
        a.OwnLedger = ownLedger;
        a.Mood = mood;
        a.Throttled = throttled;

        // 发送（分句/节奏/记账）：聊天、agent 回话、审批回执共用
        // 决策轨迹（批次 C）：一轮一条、只有形状；回复链 / 发送层 / 能力闸门三处往上记节点。
        a.Traces = new TurnTraceStore(() => BotAgent.Adapters.Time.Clock.Now, new TraceArchiveStore());
        a.Audit = new AuditLogStore();
        a.Mcp = McpConfiguration.Build(a.Secrets, a.Audit, msg => FileLog.Write("Mcp", msg));
        a.Plain = new PlainSender(a.Box, a.Source, registry, a.Ui, ownLedger, a.Ui.EmitLog, a.Traces, a.Audit,
            a.RiskBackoff, a.PlatformPolicies, a.Brain);

        // 各域用例
        a.Vibes = new VibeTracker();
        a.Roles = new MemberRoleUseCase(a.Source, new MemberRoleStore(a.Ui.EmitLog), a.Ui.EmitLog);
        a.Participation = new ParticipationUseCase(a.Box, a.Ui.EmitLog);
        a.Poke = BuildPokeUseCase(a.Box, a.Identity, a.Whitelist, registry, a.Ui, throttled, mood);

        // 会话级权限元数据（批次 B）：内存台账，只记录不改判定 —— 面板的“工具/权限”那页读它。
        a.SessionPolicies = new SessionPolicyLedger();
        a.Approvals = BuildApprovalUseCase(a.Box, a.Ui, registry, a.Plain, a.SessionPolicies, a.Traces, a.Audit);
    }
}

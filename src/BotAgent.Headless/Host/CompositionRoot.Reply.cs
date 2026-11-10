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
    /// 会话被删 → 把它在各台账里的痕迹一起清掉（回复冷却 / 待回复队列 / 参与 / 审批），
    /// 并按老样子记一条日志 + 再通知一次面板。订阅必须在 reply 与 settingsHotReload 都建好之后；
    /// 顺序与以前 BotAgentHost.DeleteConversation 逐字一致（注册表内部那次 ConversationsChanged 仍然先发）。
    /// </summary>
    private static void BindConversationDeletion(ConversationRegistry registry, ReplyPipeline reply,
        SettingsHotReload settingsHotReload, PanelNotifier ui)
    {
        registry.Deleted += conversation =>
        {
            reply.ForgetSession(conversation.SourceKey);
            settingsHotReload.ForgetSessionState(conversation.SourceKey);
            ui.EmitLog($"已删除会话 {conversation.Name}");
            ui.NotifyConversationsChanged();
        };
    }

    /// <summary>
    /// ⑥ 装配阶段六：回复主链、调度、配置热更新扇出与宿主（#110 第 2 步拆出）。
    ///
    /// 回复链要用到前面所有用例；建好之后才把"戳一戳请求一轮回复"这条边接上。
    /// 热更新扇出排在回复链与调度之后（它要重建两者的注意力），宿主排在最后（它持有全部组件）。
    /// </summary>
    private static void AssembleReply(Assembly a)
    {
        // 回复主链（要用到上面所有用例）→ 建好之后把"戳一戳请求一轮回复"这条边接上
        a.Reply = new ReplyPipeline(a.Box, a.Source, a.Brain, a.Profiles, a.Registry, a.Ui, a.Whitelist, a.Approvals,
            a.Participation, a.Poke, a.Vibes, a.Roles, a.OwnLedger, a.AgentCmds, a.Plain, a.Stickers, a.Voice, a.Music,
            a.Research, a.Links, a.Mood,
            new ReplyHooks(
                Log: a.Ui.EmitLog,
                SelfId: () => a.Identity.SelfId,
                IsDisposed: () => a.Lifetime.IsDisposed),
            a.Traces, a.RiskBackoff,
            usage: a.Quotas,
            platformPolicies: a.PlatformPolicies,
            inboundDedup: a.InboundDedup,
            plugins: a.PluginGate,
            background: a.Background, replyPolicies: new PluginReplyPolicies(a.Plugins),
            unifiedMemory: a.UnifiedMemory, memoryCommand: a.MemoryCommands.TryHandle, memoryParticipant: MemoryParticipant);
        a.Poke.RequestReply = conversation => a.Reply.RequestReply(conversation, null);

        // 后台巡检（静默兜底 / 画像巡检 / 表情包巡检 / 账号在线探测）
        a.Scheduler = new BotScheduler(a.Box, a.Source, a.Profiles, a.Brain, a.Reply, a.Stickers, a.Ui,
            new BotSchedulerHooks(Log: a.Ui.EmitLog, IsDisposed: () => a.Lifetime.IsDisposed),
            a.PluginGate.Allows, unifiedMemory: a.UnifiedMemory);

        // 配置热更新的扇出（面板保存设置 / 一键部署记住产物地址）：顺序在它内部，见 SettingsHotReload
        a.HotReload = new SettingsHotReload(a.Box, a.SettingsStore, a.Approvals, a.Participation, a.Whitelist,
            a.AgentCmds, a.Brain, a.Reply, a.Scheduler, a.Registry, a.Ui);

        // 会话被删 → 把它在各台账里的痕迹一起清掉，见 BindConversationDeletion
        BindConversationDeletion(a.Registry, a.Reply, a.HotReload, a.Ui);

        // 图片地址过期（QQ 的 rkey 有时效）时的重签通道：下载器 → 协议端 get_msg
        // （**只在这里接一次** —— 它与配置无关，不再是"每次保存设置都重接一遍"）
        a.Brain.RefreshImageUrls = (messageId, ct) => a.Source.RefreshImageUrlsAsync(messageId, ct);

        a.Agent = new BotAgentHost(a.Source, a.Brain, a.Ui, a.Registry, a.Reply, a.Scheduler, a.Poke, a.Stickers,
            a.Identity, a.Lifetime);

        // 启动自述（"AI=开/关、两份名单、模型、表情包库…"）：它读的就是上面这些件
        a.BootReport = new BootReport(a.Box, a.Whitelist, a.Stickers);
    }
}

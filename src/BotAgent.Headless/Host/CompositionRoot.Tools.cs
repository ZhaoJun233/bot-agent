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
{    /// <summary>
    /// ⑤ 装配阶段五：插件门禁、agent 命令与后台评估队列（#110 第 2 步拆出）。
    ///
    /// 顺序是硬的：插件门禁先于 agent 命令与回复链（显式 QQ 动作要吃掉同一份门禁），
    /// agent 命令先于回复链，后台评估队列先于面板（面板的"评估"页要读它的水位）。
    /// </summary>
    private static void AssembleTools(Assembly a)
    {
        // Wave3：插件注册与能力门禁先于 agent 命令与回复链建立；显式 QQ 动作也要吃同一份门禁。
        a.Plugins = a.PluginRuntime.Registry;
        a.PluginGate = new PluginGate(a.Plugins);
        a.Poke.PluginEnabled = () => a.PluginGate.Allows(PluginGate.PokeCapability);
        a.Stickers.PluginEnabled = () => a.PluginGate.Allows(PluginGate.StickersCapability);
        // agent 命令（//）：会话台账 + 内置/外部两路后端
        a.Sessions = new AgentSessionStore(
            Path.Combine(AppPaths.DataDir, "agent-sessions.json"), msg => FileLog.Write("Agent", msg));
        a.ServerAgent = new ServerAgentRunner(a.Box, a.Brain,
            new HttpFetcher(TimeSpan.FromSeconds(60), msg => FileLog.Write("Net", msg), "server-agent"),
            msg => FileLog.Write("ServerAgent", msg), a.Mcp.UseCase);
        a.AgentCmds = new AgentCommandService(a.Box, a.Source, a.Brain, a.Registry, a.Ui, a.Sessions, a.ServerAgent,
            new AgentImageStore(),
            new AgentHooks(
                Log: a.Ui.EmitLog,
                SelfId: () => a.Identity.SelfId,
                IsOfficialUserAllowed: a.Whitelist.IsOfficialPrivateExplicit,
                SendPlainAsync: a.Plain.SendPlainAsync),
             actionGateway: a.Gateway,
             platformPolicies: a.PlatformPolicies,
             pluginAllows: a.PluginGate.Allows);

        a.Background = new BackgroundEvaluatorQueue(new BackgroundEvaluatorOptions
        {
            Capacity = 64,
            MaxConcurrency = 2,
            MaxAttempts = 1,
            PerJobTimeout = TimeSpan.FromSeconds(30),
            DrainTimeout = TimeSpan.FromSeconds(3),
        });
    }
}

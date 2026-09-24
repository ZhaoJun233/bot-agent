using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Panel;
using BotAgent.Adapters.Model;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Ports;
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

namespace BotAgent.Host;

/// <summary>
/// 整个进程的**唯一装配点**：谁依赖谁、谁先谁后，只有这一处说了算。
///
/// 为什么要把这段从 <c>Program.Main</c> 里搬出来：
///   • 顺序是**语义**（"先建库再读配置""先起官方再起私域"），散在启动脚本里没人守得住；
///   • 以前还有第二个装配点 —— <c>BotAgentHost</c> 构造函数自己 <c>new</c> 了十几种具体实现，
///     想知道"谁真正拥有音乐服务"得读两个文件；
///   • 测试要替身时没有插口：现在只改这里就能换实现（见 architecture-optimization.md §6.1）。
/// Program 只剩：参数、启停、优雅退出。
/// </summary>
internal sealed record AppGraph(
    AppSettings Settings,
    SettingsBox SettingsBox,
    IQqChatSource Source,
    OneBotGateway Gateway,
    OfficialBotGateway? Official,
    OpenAiClient Brain,
    AgentBridgeServer AgentBridge,
    BotAgentHost Agent,
    BootReport BootReport,
    LoginQrService LoginQr,
    HealthReportService HealthReports,
    WebUiServer Web);

internal static class CompositionRoot
{
    /// <summary>
    /// 阶段一：数据层 → 配置 → 日志。
    /// ⚠ 顺序很重要：<see cref="BotConfig.Load" /> 会从 settings 表读配置，之前必须先把库准备好。
    /// </summary>
    public static AppSettings BootstrapSettings()
    {
        AppDatabase.Initialize();
        LegacyJsonImporter.ImportIfNeeded();

        var settings = BotConfig.Load();
        FileLog.Verbose = settings.VerboseLog;
        FileLog.WriteToFile = Environment.GetEnvironmentVariable("QQCHAT_LOG_FILE") != "0";

        // 面板日志页的首屏历史：从日志文件尾部回填一段（刷新页面 / 重启进程之后也不是空的）。
        // 回填要放在往文件里写第一行之前，否则会把"本次启动"的那几行再攒一遍（虽只是重复，没必要）。
        FileLog.PreloadRecent();
        return settings;
    }

    /// <summary>
    /// 阶段二：按依赖顺序把服务接起来。
    /// 顺序：协议端（必要时两条 → 聚合器）→ 模型客户端 → 仓储 → host 用例 → 面板。
    /// </summary>
    public static AppGraph Build(AppSettings settings)
    {
        // 配置的唯一发布点：所有组件都从它读"当前生效的那一份"。
        // 面板热更新换的是它背后的实例（不是就地改共享对象），这样在途的那一轮不会读到半新半旧的配置
        // （review-findings #4）。见 SettingsBox。
        var settingsBox = new SettingsBox(settings);

        // 密钥库：无状态（每次都直接读写库里那几条），全进程共用一个实例只是为了少造对象。
        var secrets = new SecretsStore();
        // 配置库：同样无状态；热更新与面板共用它。
        var settingsStore = new SettingsStore();

        // ① 上行通道层（协议端 + 可选的官方通道 + 聚合器）：见 BuildChannelLayer
        var (gateway, source, official, local) = BuildChannelLayer(settings, settingsBox);

        // ② 模型与媒体层（模型客户端 / 表情包 / 桥 / 语音 / 音乐与链接 / 联网研究）：见 BuildModelAndMediaLayer
        var (brain, store, profiles, stickers, agentBridge, voice, music, links, research) =
            BuildModelAndMediaLayer(settings, settingsBox, source, secrets);

        // ---------------- 用例层：谁依赖谁，只在这里看得到 ----------------
        // 共享状态先建：登录号 / 收摊标记 / 事件聚合 / 白名单闸门（其余组件都要问它们）
        var identity = new BotIdentity();
        var lifetime = new BotLifetime();
        var ui = new PanelNotifier();
        var whitelist = new WhitelistGate(settingsBox);

        // 数据侧
        var registry = new ConversationRegistry(store, settingsBox, source, whitelist.AllowsKey, ui.EmitLog);
        registry.Changed += ui.NotifyConversationsChanged;
        var ownLedger = new OwnMessageLedger(new OwnMessageStore(), ui.EmitLog);
        // 心情的保留时长跟着配置走：MoodStore 现取（以前要记得"改完设置顺手同步一次"，忘了就用着旧上限）
        var mood = new MoodStore(() => settingsBox.Current.MoodTtlSeconds);
        mood.Load(AppPaths.RuntimeRoot);

        var throttled = new ThrottledLog(ui.EmitLog);

        // 发送（分句/节奏/记账）：聊天、agent 回话、审批回执共用
        // 决策轨迹（批次 C）：一轮一条、只有形状；回复链 / 发送层 / 能力闸门三处往上记节点。
        var traces = new TurnTraceStore();
        var plain = new PlainSender(settingsBox, source, registry, ui, ownLedger, ui.EmitLog, traces);

        // 各域用例
        var vibes = new VibeTracker();
        var roles = new MemberRoleUseCase(source, new MemberRoleStore(ui.EmitLog), ui.EmitLog);
        var participation = new ParticipationUseCase(settingsBox, ui.EmitLog);
        var poke = new PokeUseCase(settingsBox, new PokeHooks(
            SelfId: () => identity.SelfId,
            IsSourceAllowed: whitelist.AllowsSource,
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
        // 会话级权限元数据（批次 B）：内存台账，只记录不改判定 —— 面板的“工具/权限”那页读它。
        var sessionPolicies = new SessionPolicyLedger();
        var approvals = new ApprovalUseCase(settingsBox, new ApprovalHooks(
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
            traces);

        // agent 命令（//）：会话台账 + 内置/外部两路后端
        var sessions = new AgentSessionStore(
            Path.Combine(AppPaths.DataDir, "agent-sessions.json"), msg => FileLog.Write("Agent", msg));
        var serverAgent = new ServerAgentRunner(settingsBox, brain,
            new HttpFetcher(TimeSpan.FromSeconds(60), msg => FileLog.Write("Net", msg), "server-agent"),
            msg => FileLog.Write("ServerAgent", msg));
        var agentCmds = new AgentCommandService(settingsBox, source, brain, registry, ui, sessions, serverAgent, agentBridge,
            new AgentImageStore(),
            new AgentHooks(
                Log: ui.EmitLog,
                SelfId: () => identity.SelfId,
                IsOfficialUserAllowed: whitelist.IsOfficialPrivateExplicit,
                SendPlainAsync: plain.SendPlainAsync));

        // 回复主链（要用到上面所有用例）→ 建好之后把"戳一戳请求一轮回复"这条边接上
        var reply = new ReplyPipeline(settingsBox, source, brain, profiles, registry, ui, whitelist, approvals,
            participation, poke, vibes, roles, ownLedger, agentCmds, plain, stickers, voice, music, research, links, mood,
            new ReplyHooks(
                Log: ui.EmitLog,
                SelfId: () => identity.SelfId,
                IsDisposed: () => lifetime.IsDisposed),
            traces);
        poke.RequestReply = conversation => reply.RequestReply(conversation, null);

        // 后台巡检（静默兜底 / 画像巡检 / 表情包巡检 / 账号在线探测）
        var scheduler = new BotScheduler(settingsBox, source, profiles, brain, reply, stickers, ui,
            new BotSchedulerHooks(Log: ui.EmitLog, IsDisposed: () => lifetime.IsDisposed));

        // 配置热更新的扇出（面板保存设置 / 一键部署记住产物地址）：顺序在它内部，见 SettingsHotReload
        var settingsHotReload = new SettingsHotReload(settingsBox, settingsStore, approvals, participation, whitelist, agentCmds,
            brain, reply, scheduler, registry, ui);

        // 会话被删 → 把它在各台账里的痕迹一起清掉（回复冷却 / 待回复队列 / 参与 / 审批），
        // 并按老样子记一条日志 + 再通知一次面板。订阅点在这里是因为它同时要 reply 与 settingsHotReload；
        // 顺序与以前 BotAgentHost.DeleteConversation 逐字一致（注册表内部那次 ConversationsChanged 仍然先发）。
        registry.Deleted += conversation =>
        {
            reply.ForgetSession(conversation.SourceKey);
            settingsHotReload.ForgetSessionState(conversation.SourceKey);
            ui.EmitLog($"已删除会话 {conversation.Name}");
            ui.NotifyConversationsChanged();
        };

        // 图片地址过期（QQ 的 rkey 有时效）时的重签通道：下载器 → 协议端 get_msg
        // （**只在这里接一次** —— 它与配置无关，不再是"每次保存设置都重接一遍"）
        brain.RefreshImageUrls = (messageId, ct) => source.RefreshImageUrlsAsync(messageId, ct);

        var agent = new BotAgentHost(source, brain, ui, registry, reply, scheduler, poke, stickers, identity, lifetime);

        // 启动自述（"AI=开/关、两份名单、模型、表情包库…"）：它读的就是上面这些件
        var bootReport = new BootReport(settingsBox, whitelist, stickers);

        // 面板内的扫码登录：把 NapCat 的登录二维码搬进机器人面板
        // （用户打开面板看不到二维码，是远程部署卡住最久的原因）
        var loginQr = new LoginQrService(settings.NapCatWebUiUrl, settings.NapCatWebUiToken,
            new HttpFetcher(TimeSpan.FromSeconds(8), msg => FileLog.Write("Net", msg), "login-qr"));

        // 服务器健康日报（每天定时私聊一条状态）：整条链路只用机器人自己 + 协议端，
        // **不经过外部设备 agent**（那台电脑可能根本没开）—— 号主 2026-09-18 明确要求。
        // 宿主事实（cgroup 内存上限 / 负载）：健康日报与面板仪表盘共用同一份只读端口
        var hostFacts = new HostMetrics();
        var healthReports = new HealthReportService(settingsBox, registry, reply, identity, scheduler, voice, gateway,
            new HttpFetcher(TimeSpan.FromSeconds(6), msg => FileLog.Write("Net", msg), "health"),
            hostFacts);

        // 面板：一键重启 = 退出进程。为什么不自接 docker.sock 重启容器：那等于把 root 交给面板；
        // 容器本身就是 `restart: unless-stopped`，退出去 Docker 会毫秒级把它拉起来。
        // 面板自己的两条出网（模型列表探测 20s / 自建网易云登录 15s）
        var panelHttp = new HttpFetcher(TimeSpan.FromSeconds(20), msg => FileLog.Write("Net", msg), "panel-models");
        var neteaseHttp = new HttpFetcher(TimeSpan.FromSeconds(15), msg => FileLog.Write("Net", msg), "panel-netease");

        var web = new WebUiServer(settings.HealthPort, settingsBox, gateway, source, agent, loginQr, panelHttp, neteaseHttp,
            settingsHotReload, ui, stickers, mood, voice, music, research, registry, profiles, secrets, settingsStore, identity, scheduler,
            reply, participation, agentCmds, agentBridge, healthReports,
            sessionPolicies: sessionPolicies,
            traces: traces,
            hostFacts: hostFacts,
            approvals: approvals,
            localChannel: local,
            onRestart: () =>
            {
                FileLog.Write("Host", "一键重启：即将退出，让 Docker 把容器重新拉起来…");
                foreach (var svc in new IDisposable?[] { official })
                {
                    try
                    {
                        svc?.Dispose();
                    }
                    catch (Exception)
                    {
                        // 退出路径，收尾失败不阻塞
                    }
                }

                Environment.Exit(0);
            });

        return new AppGraph(settings, settingsBox, source, gateway, official, brain, agentBridge, agent, bootReport, loginQr, healthReports, web);
    }

    /// <summary>
    /// 上行通道层：私域协议端 →（配齐了 appid/secret 且开关打开时）官方通道 → 聚合器。
    /// 两条路交给**同一个** Agent，隔离靠会话 key 的通道前缀（见 Channels.Key）。顺序与日志措辞逐字搬来。
    /// </summary>
    private static (OneBotGateway Gateway, IQqChatSource Source, OfficialBotGateway? Official, LocalChannelSource? Local) BuildChannelLayer(
        AppSettings settings, SettingsBox settingsBox)
    {
        var gateway = new OneBotGateway(settingsBox)
        {
            SelfIdHint = settings.UinOrZero
        };

        // 上行通道聚合：默认只有私域（自建 NapCat）。配了官方平台 appid/secret 并打开开关时，
        // 官方那条也接进来 —— 两条路交给**同一个** BotAgentHost，隔离靠会话 key 的通道前缀
        // （见 Channels.Key），而不是靠两套 Agent（那样白名单/上下文/面板都得写两遍，迟早不一致）。
        IQqChatSource source = gateway;
        OfficialBotGateway? official = null;
        LocalChannelSource? local = null;
        if (settings.OfficialEnabled
            && !string.IsNullOrWhiteSpace(settings.OfficialAppId)
            && !string.IsNullOrWhiteSpace(settings.OfficialAppSecret))
        {
            official = new OfficialBotGateway(settingsBox,
                new HttpFetcher(TimeSpan.FromSeconds(30), msg => FileLog.Write("Net", msg), "official"),
                new OfficialIdMap(Path.Combine(AppPaths.DataDir, "official-ids.json")),
                msg => FileLog.Write("Official", msg));
            source = new ChannelRouter(new IQqChatSource[] { gateway, official }, msg => FileLog.Write("Channel", msg));
            FileLog.Write("Channel", $"官方通道已启用（appid={settings.OfficialAppId}，" +
                                   $"{(settings.OfficialSandbox ? "沙箱" : "正式")}环境，与私域通道隔离）");
            if (settings.OfficialSandbox)
            {
                // 沙箱环境**只**推「沙箱群 / 沙箱单聊」的事件 —— 正式群里 @ 它一条都不会到，
                // 而且日志里什么都不会出现（2026-09-21 号主卡在这里："艾特了日志根本不显示"）。
                // 所以这句话要说到最响：它解释的正是"看起来啥都没发生"。
                FileLog.Write("Channel", "⚠ 官方通道跑在**沙箱环境**：只能收到开放平台「沙箱配置」里那些沙箱群/沙箱单聊的事件；"
                                        + "正式群里 @ 机器人不会被推送，日志里也不会有任何行。要在正式群用，取消面板「用沙箱环境」并重启。");
            }
        }
        else if (settings.OfficialEnabled)
        {
            FileLog.Write("Channel", "官方通道开关是开的，但 appid/secret 没配齐 → 这次只跑私域通道");
        }

        // 本地通道（批次 F）：**名单非空才建**（默认关）。
        // 它走的是同一张工具表 + 同一套治理，只是入站来自面板那张令牌门后的 POST /api/local/message。
        if (!string.IsNullOrWhiteSpace(settings.LocalChannelIds))
        {
            local = new LocalChannelSource(msg => FileLog.Write("Local", msg));
            if (source is ChannelRouter router)
            {
                // 已经有两条上行：把本地这条也接进同一个路由器（隔离靠会话 key 前缀 local:）
                source = new ChannelRouter(
                    router.Sources.Concat(new IQqChatSource[] { local }), msg => FileLog.Write("Channel", msg));
            }
            else
            {
                source = new ChannelRouter(new IQqChatSource[] { gateway, local }, msg => FileLog.Write("Channel", msg));
            }

            FileLog.Write("Channel", "本地通道已启用（名单非空；入口：面板 POST /api/local/message，与另两条上行隔离）");
        }

        return (gateway, source, official, local);
    }

    /// <summary>
    /// 模型与媒体层：模型客户端（三个出网分别 60s / 聊天超时 / 8s）、两个仓储、表情包运营、本机 agent 桥、
    /// 语音、音乐与链接预览、联网研究。都是"拿到配置就能造出来"的件，与用例之间的接线无关（见 Build）。
    /// </summary>
    private static (OpenAiClient Brain, ConversationStore Store, MemberProfileStore Profiles, StickerService Stickers,
        AgentBridgeServer AgentBridge, VoiceUseCase Voice, MusicUseCase Music, LinkPreviewer Links, ResearchUseCase Research)
        BuildModelAndMediaLayer(AppSettings settings, SettingsBox settingsBox, IQqChatSource source, ISecretsRepository secrets)
    {
        // 模型那条路的出网：辅助调用 60 秒、聊天按 QQCHAT_MODEL_TIMEOUT_SECONDS（默认 120）、图片下载 8 秒
        var modelAuxHttp = new HttpFetcher(TimeSpan.FromSeconds(60), msg => FileLog.Write("Net", msg), "model-aux");
        var modelChatHttp = new HttpFetcher(OpenAiClient.ModelTimeout(), msg => FileLog.Write("Net", msg), "model-chat");
        var imageHttp = new HttpFetcher(TimeSpan.FromSeconds(8), msg => FileLog.Write("Net", msg), "image");

        // 图片下载器与传输层（模型那条路的两个 IO 件）：都在这里造，客户端只拿端口。
        var imageDownloader = new ImageDownloader(imageHttp);
        var transport = new ModelTransport(
            settingsBox, modelChatHttp, modelAuxHttp, imageDownloader, OpenAiClient.ModelTimeout());

        var brain = new OpenAiClient(settingsBox, modelAuxHttp, modelChatHttp, imageDownloader, transport)
        {
            BotIdentity = string.IsNullOrWhiteSpace(settings.NormalizedUin) ? null : settings.NormalizedUin,
            BotPersona = settings.BotPersona,
            AiDesire = settings.AiDesire,
            SuitabilityThreshold = settings.SuitabilityThreshold,
            MaxContextMessages = settings.MaxContextMessages // 漏了这行会让上下文窗口在面板修改后不同步
        };

        var store = new ConversationStore();
        var profiles = new MemberProfileStore();

        // 表情包运营（收图/说明/自巡检/频率门）：状态与定时器都在它自己身上，装配在这里、只装配一次
        var stickers = new StickerService(new StickerStore(), brain, source, settingsBox, msg => FileLog.Write("Sticker", msg));
        // 表情包库建好即就位：巡检定时器（BotScheduler.RebuildIfNeeded → EnsureTimer）与面板首屏
        // 都要在**任何一轮回复之前**读到它。载入属启动顺序，所以放在装配点（§6.1）。
        stickers.Load(AppPaths.RuntimeRoot);

        // 本机 Agent 桥（// 命令）：机器人**监听**一个 WS 端点，号主本机那个 pi-bridge 主动连进来。
        // 为什么不让机器人直接连本机：号主的电脑在 NAT 后面（没公网入口，也不应该开一个）。
        var agentBridge = new AgentBridgeServer(settingsBox, msg => FileLog.Write("Agent", msg));
        // 语音（TTS）：客户端、频率门、面板试听都在这个用例里（HttpClient 也只在这里造一个）
        var voiceHttp = new HttpFetcher(TimeSpan.FromSeconds(30), msg => FileLog.Write("Net", msg), "voice");
        var voice = new VoiceUseCase(
            new VoiceService(voiceHttp, () => settingsBox.Current, secrets, msg => FileLog.Write("Voice", msg)),
            settingsBox,
            msg => FileLog.Write("Voice", msg));

        // 音乐（听歌/分享）与链接预览：各自的 HttpClient 在这里造，依赖顺着构造函数往下传。
        // 音乐数据放 data/music/：台账常驻，音频默认分析完就丢（只在 MusicKeepAudio 打开时才留在 audio/）。
        var mediaHttp = new HttpFetcher(TimeSpan.FromSeconds(45), msg => FileLog.Write("Net", msg), "media");
        var musicStore = new MusicStore(() => settingsBox.Current.MusicLibraryMax, msg => FileLog.Write("Music", msg));
        var netease = new NeteaseMusicClient(
            mediaHttp,
            () => settingsBox.Current.NeteaseCookie,
            () => settingsBox.Current.NeteaseBaseUrl,
            msg => FileLog.Write("Music", msg));
        var audioResolver = new MusicAudioResolver(
            mediaHttp,
            () => settingsBox.Current.MusicSources.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            () => Math.Clamp(settingsBox.Current.MusicBitrate, 32, 320),
            () => Math.Clamp(settingsBox.Current.MusicMaxDownloadMb, 1, 64) * 1024 * 1024,
            msg => FileLog.Write("Music", msg));
        var music = new MusicUseCase(
            new MusicService(
                musicStore, netease, audioResolver, brain, () => settingsBox.Current,
                Path.Combine(AppPaths.DataDir, "music", "audio"), new AudioCache(), msg => FileLog.Write("Music", msg)),
            source,
            msg => FileLog.Write("Music", msg));
        var links = new LinkPreviewer(mediaHttp, () => settingsBox.Current, msg => FileLog.Write("Links", msg));

        // 联网研究（搜索 / 读页面）：它自己的 HttpClient 超时给宽松点（检索要等上游模型回话）
        var researchHttp = new HttpFetcher(TimeSpan.FromSeconds(60), msg => FileLog.Write("Net", msg), "search");
        var research = new ResearchUseCase(
            new WebSearchService(researchHttp, () => settingsBox.Current, msg => FileLog.Write("Search", msg)),
            msg => FileLog.Write("Search", msg));


        return (brain, store, profiles, stickers, agentBridge, voice, music, links, research);
    }
}

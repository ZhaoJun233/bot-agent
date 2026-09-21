using QQChatAgent.Models;
using QQChatAgent.Services.Agent;
using QQChatAgent.Services.OneBot;
using QQChatAgent.Services.Qq;
using System.Text;
using System.Text.Json.Nodes;

using QQChatAgent.Services.Stickers;
using QQChatAgent.Services.Music;
using QQChatAgent.Services.Links;
using QQChatAgent.Services.Voice;
using QQChatAgent.Services.Net;

namespace QQChatAgent.Services;

/// <summary>
/// 机器人主控（headless 版 Agent 循环）。
/// 这是桌面版 <c>ChatPageViewModel</c> 里 Agent 逻辑的无 UI 移植：
/// 白名单过滤 → 落会话 → 写人物档案 → 冷却判断 → 串行请求模型 → 分句发送 → 持久化。
///
/// 与桌面版的差异：
///   • 无 DispatcherQueue/UI 线程，全部在后台线程；并发由锁与串行 worker 保证。
///   • AI 模式由配置控制（容器里没有"AI 开/关"按钮）。
///   • 群历史在首次收到该群消息时拉取（而非"用户点开会话时"）。
///   • 回复支持按句切分、带节奏发送（README 声称的行为，桌面版实际未实现）。
/// </summary>
public sealed class BotAgent : IDisposable
{
    private readonly AppSettings _settings;
    private readonly IQqChatSource _source;

    /// <summary>协议端（QQ 动作要用它）—— 只有 OneBot 这条路有。</summary>
    private readonly OneBotGateway? _gateway;
    private readonly OpenAiClient _brain;
    private readonly ConversationStore _store;
    private readonly MemberProfileStore _profiles;

    /// <summary>群成员身份（群主/管理员/群头衔）。提示词里要用，所以要落库、要能补齐。</summary>
    private readonly MemberRoleStore _memberRoles;

    private readonly object _conversationsGate = new();
    private readonly List<BotConversation> _conversations = new();

    // ───── 表情包（全局共用一个库，不分会话）─────
    private readonly StickerStore _stickers = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _stickerDescribeQueue = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _stickerDescribeQueued = new();
    private Timer? _stickerTimer;
    private int _stickerBusy;
    private long _lastStickerCurateAt;
    private long _stickerDescribeDone;

    /// <summary>表情包库（面板用）。</summary>
    public StickerStore Stickers => _stickers;

    /// <summary>面板用：当前心情（读/写）。写空串 = 交回代码按被戳次数自动描述。</summary>
    public string MoodText => _mood.CurrentText(DateTimeOffset.Now) ?? string.Empty;

    public string MoodSummary => _mood.Describe(DateTimeOffset.Now);

    public void SetMood(string? text)
    {
        var now = DateTimeOffset.Now;
        if (string.IsNullOrWhiteSpace(text))
        {
            _mood.Reset(now);
            EmitLog("心情已交回自动描述（按被戳次数）");
            return;
        }

        if (_mood.SetText(text, now))
        {
            EmitLog($"心情被手动改成：{_mood.Describe(now)}");
        }
    }

    /// <summary>正在等待生成说明的张数。</summary>
    public int StickerPendingDescribe => _stickerDescribeQueue.Count;

    /// <summary>已生成说明的张数（含历史累计）。</summary>
    public long StickerDescribeDone => Interlocked.Read(ref _stickerDescribeDone);

    private HashSet<long> _whitelistGroups = new();
    private bool _whitelistAllGroups;
    private HashSet<long> _whitelistPrivates = new();
    private bool _whitelistAllPrivates;

    // 官方商用通道自己的一份名单（里的是别名号，见 Channels.AliasBase）。
    // 两份名单**不共用**：QQ 那份写的是真实群号，官方那份写的是别名，混在一起只会出现
    // “官方通道永远被拦”这种看不懂的结果。官方两份都留空 = 全部接受（官方平台自身有准入）。
    private HashSet<long> _officialWhitelistGroups = new();
    private bool _officialWhitelistAllGroups = true;
    private HashSet<long> _officialWhitelistPrivates = new();
    private bool _officialWhitelistAllPrivates = true;

    /// <summary>哪一边在用旧的共用名单（面板上要如实显示，不然号主会以为新框填了没生效）。</summary>
    private bool _whitelistGroupsFromLegacy;
    private bool _whitelistPrivatesFromLegacy;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _historyRequested = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _replyCooldown = new();

    // 待回复队列：**每个会话一条 FIFO 链**。
    // 要点：同一会话的请求必须严格按触发顺序执行（否则回复会错位、引用判定会错），
    //       而不同会话之间可以并发，互不阻塞。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<PendingReply>> _pendingReplies = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BotConversation> _pendingConversations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlight = new();
    private SemaphoreSlim _replyGate;
    private int _replyWorkerRunning;
    private volatile int _replyGatePermits;
    private long _lastActiveRequestTicks = DateTime.MinValue.Ticks; // 最近一次主动请求时间（原子读写）

    /// <summary>
    /// 每个会话最近一次“读到的气氛”：模型自己写的 vibe + 一句人话，下一轮当底色用（有 TTL）。
    /// 为什么存内存而不是落库：气氛是分钟级的东西，重启后重读一遍上下文就行，不值得占库。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Vibe, string Note, DateTimeOffset At)> _vibes = new();

    /// <summary>气氛的保留时长：超过就当“上一轮”已经过去，不再影响提示词。</summary>
    private static readonly TimeSpan VibeTtl = TimeSpan.FromMinutes(45);

    /// <summary>每个会话最近一次“自己主动开口”的时间（用于主动发言的冷却）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastProactive = new();

    /// <summary>当前在途的模型请求数（可观测）。</summary>
    public int InFlightReplies => _inFlight.Count;

    /// <summary>
    /// QQ 账号是否真的在线（null = 未知）。
    /// 与“协议端连接状态”是两件事：连接可以一直是连着的，而账号已经掉线收不到任何消息。
    /// </summary>
    public bool? AccountOnline => Volatile.Read(ref _accountOnline) is var v && v >= 0 ? v == 1 : null;

    /// <summary>最近一次模型生成的耗时（毫秒；0 = 还没生成过）。面板与健康日报用。</summary>
    public long LastGenerationMilliseconds => Volatile.Read(ref _lastGenerationMs);

    /// <summary>当前排队的待回复请求数（可观测）。</summary>
    public int QueuedReplies => _pendingReplies.Values.Sum(q => q.Count);

    // ══════════ 长期记忆：画像摘要 ══════════

    /// <summary>已完成的画像摘要次数（可观测）。</summary>
    public long SummaryDoneCount => Interlocked.Read(ref _summaryDoneCount);

    /// <summary>画像摘要失败次数（可观测）。</summary>
    public long SummaryFailCount => Interlocked.Read(ref _summaryFailCount);

    /// <summary>
    /// 画像巡检：把各会话里新积累的发言用模型压缩成人物画像。
    /// 好处：以后注入的是“一段画像”而不是“几十条原文”，token 降一个数量级且信息密度更高。
    /// 只在后台跑，且独占 1 个并发位，不与回复抢资源。
    /// </summary>
    private async Task RunProfileSummarizationAsync()
    {
        if (!_settings.EnableProfileSummary || _disposed == 1)
        {
            return;
        }

        if (Interlocked.Exchange(ref _summaryRunning, 1) == 1)
        {
            return; // 上一轮还没跑完
        }

        try
        {
            var candidates = _profiles.FindSummarizable(_settings.ProfileSummaryThreshold, maxCandidates: 20);

            foreach (var c in candidates)
            {
                if (_disposed == 1)
                {
                    break;
                }

                await _summaryGate.WaitAsync();
                try
                {
                    var text = await _brain.SummarizePersonaAsync(
                        string.IsNullOrWhiteSpace(c.Name) ? $"QQ{c.Uid}" : c.Name,
                        c.ExistingSummary,
                        c.NewMessages,
                        _settings.ProfileSummaryMaxChars);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Interlocked.Increment(ref _summaryFailCount);
                        continue;
                    }

                    _profiles.ApplySummary(c.Uid, c.Scope, text, c.ThroughSeq, c.FoldedCount);
                    Interlocked.Increment(ref _summaryDoneCount);
                    EmitLog($"画像已生成：{c.Name}({c.Uid}) @ {c.Scope}（{c.NewMessages.Count} 条 → {text.Length} 字）");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _summaryFailCount);
                    FileLog.Write("Profiles", $"画像摘要失败 {c.Uid}@{c.Scope}: {ex.Message}");
                }
                finally
                {
                    _summaryGate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write("Profiles", "画像巡检异常: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _summaryRunning, 0);
        }
    }

    /// <summary>最近一次主动请求时间（静默兜底判断用；跨线程原子访问）。</summary>
    private DateTime LastActiveRequestTime
    {
        get => new(Volatile.Read(ref _lastActiveRequestTicks));
        set => Volatile.Write(ref _lastActiveRequestTicks, value.Ticks);
    }

    private Timer? _idleTimer;
    private Timer? _summaryTimer;
    private int _summaryRunning;

    // QQ 账号在线探测：-1 = 未知，0 = 离线，1 = 在线
    private Timer? _healthTimer;
    private int _healthRunning;
    private int _accountOnline = -1;

    // 最近一次生成的耗时（毫秒）：健康日报要报“模型现在还快不快”。0 = 还没生成过。
    private long _lastGenerationMs;

    // 定时器当前依据的配置值：用于判断“是否真的需要重建”（避免每次保存都重置计时）
    private int _timerIdleSeconds = -1;
    private bool _timerSummaryEnabled;
    private int _timerSummarySeconds = -1;
    private bool _timerStickersEnabled;
    private readonly SemaphoreSlim _summaryGate = new(1, 1);
    private long _summaryDoneCount;
    private long _summaryFailCount;
    private long _selfId;
    private int _disposed;

    public BotAgent(
        AppSettings settings,
        IQqChatSource source,
        OpenAiClient brain,
        ConversationStore store,
        MemberProfileStore profiles,
        AgentBridgeServer? agentBridge = null,
        AgentSessionStore? agentSessions = null)
    {
        _settings = settings;
        _source = source;
        _gateway = source as OneBotGateway;   // QQ 动作（点赞/戳一戳/禁言…）只能走 OneBot 协议端
        _brain = brain;
        _store = store;
        _profiles = profiles;
        _memberRoles = new MemberRoleStore(EmitLog);

        // agent 会话（新建/切换/删除/历史）：存 <dataDir>/agent-sessions.json
        _agentSessions = agentSessions ?? new AgentSessionStore(
            Path.Combine(Environment.GetEnvironmentVariable("QQCHAT_DATA_DIR")?.Trim() is { Length: > 0 } dir
                ? dir
                : "/data", "agent-sessions.json"),
            msg => FileLog.Write("Agent", msg));
        // 本机 Agent 桥（可选）：订阅它的进度/结果事件，把话说到对应群里
        _agentBridge = agentBridge;
        if (_agentBridge is not null)
        {
            _agentBridge.Progress += OnAgentProgress;
            _agentBridge.Finished += OnAgentFinished;
        }

        // 服务器内置 agent（同一个白名单、同一套回群逻辑）。总是建好，开关只管用不用（见字段注释）
        _serverAgent = new ServerAgentRunner(settings, brain, msg => FileLog.Write("ServerAgent", msg));
        _serverAgent.Progress += OnAgentProgress;
        RebuildAgentUsers();

        // 图片地址过期（QQ 的 rkey 有时效）时的重签通道：下载器 → 协议端 get_msg
        _brain.RefreshImageUrls = (messageId, ct) => _source.RefreshImageUrlsAsync(messageId, ct);

        RebuildWhitelist();
        _replyGate = new SemaphoreSlim(Math.Clamp(settings.MaxConcurrentReplies, 1, 16));
        _replyGatePermits = Math.Clamp(settings.MaxConcurrentReplies, 1, 16);
    }

    /// <summary>本机 Agent 桥（没启用时为 null）。</summary>
    private readonly AgentBridgeServer? _agentBridge;

    /// <summary>服务器内置 agent（容器里的工具循环）。**总是建好**，用不用由 _settings.EnableServerAgent 在调用时决定
    /// （以前只在启动时建一次，面板里把开关打开也不生效 —— 号主 2026-09-17 撞到的就是它）。</summary>
    private readonly ServerAgentRunner _serverAgent;

    /// <summary>待回复队列元素：触发消息 id（null = 没有触发）+ 这是不是“自己主动开口”。</summary>
    private readonly record struct PendingReply(long? TriggerMessageId, bool Proactive);
    /// <summary>日志节流：同一来源的“忽略”类日志最多每分钟一条（否则忙群里会刷爆）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _noisyLogAt = new();

    // ══════════ Web UI 事件与操控接口 ══════════

    /// <summary>新消息落库（sourceKey, 消息）。</summary>
    public event Action<string, ChatMessage>? MessageAdded;

    /// <summary>会话发生变更（新建/改名/未读变化）。</summary>
    public event Action? ConversationsChanged;

    /// <summary>连接状态或 AI 开关变化。</summary>
    public event Action? StateChanged;

    /// <summary>某个会话的「AI 正在思考」状态变化。</summary>
    public event Action<string>? ThinkingChanged;

    /// <summary>一条面向 UI 的运行日志。</summary>
    public event Action<string>? LogLine;

    /// <summary>AI 自动回复开关（可运行时切换，对应桌面版的「AI 开/关」按钮）。</summary>
    public bool AiModeEnabled
    {
        get => _settings.AiModeEnabled;
        set
        {
            if (_settings.AiModeEnabled == value)
            {
                return;
            }

            _settings.AiModeEnabled = value;
            EmitLog(value ? "AI 模式已开启" : "AI 模式已关闭");
            StateChanged?.Invoke();
        }
    }

    /// <summary>当前生效的配置（Web UI 展示与修改用）。</summary>
    public AppSettings Settings => _settings;

    /// <summary>
    /// 运行时改配置：白名单/人设/欲望/阈值/限流等立即生效，并落盘 settings.json。
    /// 模型地址与 OneBot 地址不在运行时生效（属于容器环境变量职责），由 UI 明确标注。
    /// </summary>
    /// <summary>把“心情保留多久”从设置同步给 MoodStore（0 = 不过期）。</summary>
    private void ApplyMoodTtl() => _mood.Ttl = TimeSpan.FromSeconds(Math.Max(0, _settings.MoodTtlSeconds));

    /// <summary>
    /// 组装“听音乐”服务。数据放 data/music/：台账（listened.json）常驻，音频默认分析完就丢
    /// （只在 MusicKeepAudio 打开时才留在 data/music/audio/）。
    /// </summary>
    private void BuildMusicService()
    {
        var dataDir = Path.Combine(AppPaths.DataDir, "music");
        var store = new MusicStore(() => _settings.MusicLibraryMax, EmitLog);
        var netease = new NeteaseMusicClient(_musicHttp, () => _settings.NeteaseCookie, () => _settings.NeteaseBaseUrl, EmitLog);
        var audio = new MusicAudioResolver(
            _musicHttp,
            () => _settings.MusicSources.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            () => Math.Clamp(_settings.MusicBitrate, 32, 320),
            () => Math.Clamp(_settings.MusicMaxDownloadMb, 1, 64) * 1024 * 1024,
            EmitLog);
        _music = new MusicService(store, netease, audio, _brain, () => _settings, Path.Combine(dataDir, "audio"), EmitLog);
        _links = new LinkPreviewer(_musicHttp, () => _settings, EmitLog);
    }

    /// <summary>
    /// 把模型配置同步给大脑（面板里改 Base URL / 模型名 / 密钥 后调用）。
    /// 双方平时就是同一个 AppSettings 实例，这一步是幂等的；
    /// 之所以显式留着：万一以后谁改成传副本，也不至于“面板改了但请求还走旧地址”。
    /// </summary>
    public void SyncModelSettings() => _brain.UpdateSettings(_settings);

    public void ApplyRuntimeSettings(Action<AppSettings> mutate)
    {
        mutate(_settings);
        ApplyMoodTtl();

        // 白名单重新解析并清理已有会话
        RebuildWhitelist();
        PruneNonWhitelisted();

        // Agent 用户白名单（改设置后立即生效；空 = 谁都不能用）
        RebuildAgentUsers();

        // 同步到模型客户端
        _brain.BotIdentity = string.IsNullOrWhiteSpace(_settings.NormalizedUin) ? null : _settings.NormalizedUin;
        _brain.BotPersona = _settings.BotPersona;
        _brain.AiDesire = _settings.AiDesire;
        _brain.SuitabilityThreshold = _settings.SuitabilityThreshold;
        _brain.MaxContextMessages = _settings.MaxContextMessages;
        // 图片地址过期（QQ 的 rkey 有时效）时让协议端重新签发（见 ImageDownloader / IQqChatSource）
        _brain.RefreshImageUrls = (messageId, ct) => _source.RefreshImageUrlsAsync(messageId, ct);

        // 全局并发数变更时重建闸门
        var permits = Math.Clamp(_settings.MaxConcurrentReplies, 1, 16);
        if (permits != _replyGatePermits)
        {
            _replyGatePermits = permits;

            // 整体替换，而不是 Dispose 旧的：
            // 此刻可能已有 worker 正 await 在旧闸门上，Dispose 会让它抛 ObjectDisposedException，
            // 而该异常会跳过 _inFlight 的清理 —— 结果是该会话**永久不再回复**。
            // 旧对象只有几十字节，且配置变更由人触发（低频），延迟回收即可。
            var previous = _replyGate;
            _replyGate = new SemaphoreSlim(permits);
            RetireGate(previous);
            EmitLog($"模型并发上限已改为 {permits}");
        }

        // 消息窗口变更时同步到已有会话，并立即裁一次（否则要等下一条消息才看得到效果）
        foreach (var c in Conversations)
        {
            c.MaxMessages = _settings.MaxMessagesPerConversation;
            c.TrimToMax();
        }

        // 定时器类配置（静默兜底 / 画像巡检）：只在真的改了时才重建，否则不生效
        RebuildTimersIfNeeded();

        SettingsStore.Save(_settings);
        EmitLog("配置已更新");
        ConversationsChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 按当前配置重建定时器。
    ///
    /// 为什么必须有这个：定时器以前只在 <see cref="Start"/> 里创建一次，
    /// 于是面板里改「启用画像摘要 / 巡检间隔 / 静默兜底」都不生效（要重启才变），
    /// 用起来就像“设置保存不了”。
    ///
    /// 另外：只在值真的变了时才重建 —— 否则每保存一次设置就把计时归零，
    /// 间隔 120s 而用户频繁保存的话，巡检永远不会触发。
    /// </summary>
    private void RebuildTimersIfNeeded()
    {
        var idleSeconds = Math.Max(0, _settings.IdleFallbackSeconds);
        var summaryEnabled = _settings.EnableProfileSummary;
        var summarySeconds = Math.Max(0, _settings.ProfileSummaryIntervalSeconds);
        var stickersEnabled = _settings.EnableStickers && _settings.StickerLibraryMax > 0;

        if (idleSeconds == _timerIdleSeconds &&
            summaryEnabled == _timerSummaryEnabled &&
            summarySeconds == _timerSummarySeconds &&
            stickersEnabled == _timerStickersEnabled)
        {
            return;
        }

        _timerIdleSeconds = idleSeconds;
        _timerSummaryEnabled = summaryEnabled;
        _timerSummarySeconds = summarySeconds;
        _timerStickersEnabled = stickersEnabled;

        // ---- 静默兜底 ----
        _idleTimer?.Dispose();
        _idleTimer = null;
        if (idleSeconds > 0 && _disposed == 0)
        {
            _idleTimer = new Timer(
                _ => IdleFallbackTick(),
                null,
                TimeSpan.FromSeconds(idleSeconds),
                TimeSpan.FromSeconds(idleSeconds));
        }

        // ---- 长期记忆：画像巡检 ----
        _summaryTimer?.Dispose();
        _summaryTimer = null;
        if (summaryEnabled && summarySeconds > 0 && _disposed == 0)
        {
            _summaryTimer = new Timer(
                _ => _ = RunProfileSummarizationAsync(),
                null,
                // 首次很快跑一轮（刚打开就能看到效果），之后按配置间隔
                TimeSpan.FromSeconds(Math.Min(5, summarySeconds)),
                TimeSpan.FromSeconds(summarySeconds));
        }

        // ---- 表情包：补说明 + 自巡检 ----
        _stickerTimer?.Dispose();
        _stickerTimer = null;
        if (stickersEnabled && _disposed == 0)
        {
            // 10 秒一小步：刚收的图很快就能补上说明（不然检索不到它）
            _stickerTimer = new Timer(_ => _ = StickerTickAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            // 启动后稍等一会儿先补一轮：重启后积压的未识别图 + 审核功能上线前入库的旧图
            foreach (var item in _stickers.Snapshot().Where(s => (!s.Described || s.IsSticker is null) && s.DescribeAttempts < 2).Take(30))
            {
                QueueStickerDescribe(item.Id);
            }
        }

        EmitLog($"定时器已按新配置重建（静默兜底 {idleSeconds}s，画像巡检 {(summaryEnabled && summarySeconds > 0 ? summarySeconds + "s" : "关")}，" +
                $"表情包巡检 {(stickersEnabled && _settings.StickerCurateIntervalSeconds > 0 ? _settings.StickerCurateIntervalSeconds + "s" : "关")}）");
    }

    /// <summary>
    /// 把群友发的图收进表情包库（同一张内容只存一份），并排队让模型生成说明/关键词。
    /// 完全不阻塞接收线程（下载、写盘、模型调用都在这里）。
    /// </summary>
    private async Task CollectStickersAsync(List<string> urls, string fromUid, long fromGroup, long messageId = 0)
    {
        try
        {
            var added = 0;
            foreach (var url in urls.Take(3))
            {
                if (_settings.StickerLibraryMax <= 0)
                {
                    break;
                }

                var downloaded = await _brain.DownloadImageAsync(url, CancellationToken.None, messageId > 0 ? messageId : null);
                if (downloaded is not { } image)
                {
                    continue;
                }

                var record = _stickers.Add(image.Data, image.Ext, fromUid, fromGroup);
                if (record is null)
                {
                    continue; // 重复图
                }

                added++;
                QueueStickerDescribe(record.Id);
            }

            if (added == 0)
            {
                return;
            }

            var evicted = _stickers.EnforceLimit(_settings.StickerLibraryMax);
            EmitLog($"表情包库 +{added} 张（现有 {_stickers.Count}/{_settings.StickerLibraryMax}）" +
                    (evicted.Count > 0 ? $"，超限淘汰 {evicted.Count} 张" : string.Empty));
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"收集表情包失败：{ex.Message}");
        }
    }

    private void QueueStickerDescribe(string id)
    {
        if (_stickerDescribeQueued.TryAdd(id, 0))
        {
            _stickerDescribeQueue.Enqueue(id);
        }
    }

    /// <summary>表情包定时器：先补说明，再看要不要自巡检。</summary>
    private async Task StickerTickAsync()
    {
        if (_disposed != 0 || Interlocked.Exchange(ref _stickerBusy, 1) == 1)
        {
            return;
        }

        try
        {
            await DrainStickerDescribeAsync();

            var interval = Math.Max(0, _settings.StickerCurateIntervalSeconds);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (interval > 0 && now - Volatile.Read(ref _lastStickerCurateAt) >= interval)
            {
                Volatile.Write(ref _lastStickerCurateAt, now);
                await CurateStickersAsync(force: false);
            }
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包巡检异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _stickerBusy, 0);
        }
    }

    /// <summary>给没说明的图补上“一句话说明 + 关键词”（没有它就没法按语境检索）。</summary>
    private async Task DrainStickerDescribeAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            if (!_stickerDescribeQueue.TryDequeue(out var id))
            {
                return;
            }

            _stickerDescribeQueued.TryRemove(id, out _);

            var item = _stickers.Find(id);
            if (item is null || item.DescribeAttempts >= 2)
            {
                continue;
            }

            // 已描述过但还没审核过的（审核功能上线前入库的旧图）也要补审一次
            if (item.Described && item.IsSticker is not null)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(item.AbsolutePath);
            }
            catch
            {
                _stickers.MarkDescribeFailed(id);
                continue;
            }

            var mime = item.Ext switch
            {
                "jpg" => "image/jpeg",
                "gif" => "image/gif",
                "webp" => "image/webp",
                _ => "image/png"
            };

            var (desc, tags, isSticker) = await _brain.DescribeStickerAsync(bytes, mime);
            if (desc is null && tags is null && isSticker is null)
            {
                _stickers.MarkDescribeFailed(id);
                continue;
            }

            // 审核不通过（聊天截图 / 广告 / 纯文字图…）→ 立即丢掉。
            // 线上实测把群友发的**聊天截图**当表情包收了、还准备发出去 —— 这一步就是闸门。
            if (isSticker == false)
            {
                _stickers.Remove(id, "审核：不像表情包（截图/广告/纯文字图）");
                EmitLog($"表情包审核不通过，已丢弃 #{id}：{desc ?? "(无描述)"}");
                continue;
            }

            _stickers.SetDescription(id, desc, tags, isSticker);
            Interlocked.Increment(ref _stickerDescribeDone);
            EmitLog($"表情包入库：#{id} {StickerStore.Describe(_stickers.Find(id) ?? item)}");
        }
    }

    /// <summary>
    /// 让机器人自己巡检表情包库，决定删哪些（用户要求：它应自己删/加）。
    /// 保护规则：24 小时内用过的代码侧直接拦下；一次最多删库里 1/5（至少能给模型 2 个名额）。
    /// </summary>
    public async Task<string> CurateStickersAsync(bool force)
    {
        var all = _stickers.Snapshot();
        if (all.Count == 0)
        {
            return "库里还没有表情包";
        }

        if (!force && all.Count < 6)
        {
            return $"只有 {all.Count} 张，先不删";
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var table = string.Join("\n", all.Take(200).Select(s =>
            $"{s.Id} | {StickerStore.Describe(s)} | 用过 {s.Uses} 次 | {(now - s.AddedAt) / 3600} 小时前收藏" +
            (s.LastUsedAt > 0 && now - s.LastUsedAt < 86400 ? " | 24h内用过" : string.Empty)));

        var maxDelete = Math.Max(2, all.Count / 5);
        var (wanted, reason) = await _brain.CurateStickersAsync(table, maxDelete);
        if (wanted.Count == 0)
        {
            EmitLog($"表情包巡检：不删（{all.Count} 张）" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"——{reason}"));
            return "本次没有需要删除的";
        }

        var deleted = 0;
        foreach (var id in wanted)
        {
            var item = _stickers.Find(id);
            if (item is null)
            {
                continue;
            }

            // 代码兜底：刚用过的别删（模型有时看不见标注）
            if (item.LastUsedAt > 0 && now - item.LastUsedAt < 86400)
            {
                continue;
            }

            if (_stickers.Remove(id, "自巡检"))
            {
                deleted++;
            }
        }

        var summary = $"表情包巡检：删除 {deleted} 张（{reason ?? "模型未给理由"}）";
        EmitLog(summary + $"，现有 {_stickers.Count}/{_settings.StickerLibraryMax} 张");

        // 巡检完顺手把容量压回上限（例如用户把上限改小了）
        _stickers.EnforceLimit(_settings.StickerLibraryMax);
        return summary;
    }

    /// <summary>
    /// 从登录账号在 QQ 里的“收藏表情”导入一批（机器人自己“添加”表情包的来源）。
    /// 拉图/存图失败都只是跳过，不影响其它功能。
    /// </summary>
    public async Task<string> ImportStickersFromAlbumAsync(int limit = 30)
    {
        if (_settings.StickerLibraryMax <= 0)
        {
            return "表情包库上限为 0，先在设置里放开";
        }

        var urls = await _source.FetchCustomFacesAsync(Math.Clamp(limit, 1, 200));
        if (urls.Count == 0)
        {
            return "协议端没有返回收藏表情（可能未登录，或协议端不支持 fetch_custom_face）";
        }

        var added = 0;
        foreach (var url in urls)
        {
            var downloaded = await _brain.DownloadImageAsync(url, CancellationToken.None);
            if (downloaded is not { } image)
            {
                continue;
            }

            var record = _stickers.Add(image.Data, image.Ext, null, 0);
            if (record is null)
            {
                continue;
            }

            added++;
            QueueStickerDescribe(record.Id);
        }

        var evicted = _stickers.EnforceLimit(_settings.StickerLibraryMax);
        var summary = $"从 QQ 收藏表情导入 {added} 张（收到 {urls.Count} 个地址，现有 {_stickers.Count}/{_settings.StickerLibraryMax}）" +
                      (evicted.Count > 0 ? $"，超限淘汰 {evicted.Count} 张" : string.Empty);
        EmitLog(summary);
        return summary;
    }

    /// <summary>以机器人身份向会话发送一条消息（对应桌面版的输入框）。</summary>
    public async Task<bool> SendAsBotAsync(string sourceKey, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var conversation = Find(sourceKey);
        if (conversation is null)
        {
            return false;
        }

        var (isGroup, targetId) = conversation.Target;
        if (targetId == 0 || !_source.IsConnected)
        {
            return false;
        }

        var sent = await _source.SendTextAsync(isGroup, targetId, text);
        if (sent.Ok)
        {
            conversation.Append(new ChatMessage { Role = MessageRole.Self, Text = text, QqMessageId = sent.MessageId > 0 ? sent.MessageId : null });
            Touch(conversation);
            MessageAdded?.Invoke(conversation.SourceKey, conversation.Messages[^1]);
            Save();
        }

        return sent.Ok;
    }

    /// <summary>删除会话（连同磁盘记录）。</summary>
    public bool DeleteConversation(string sourceKey)
    {
        BotConversation? conversation;
        lock (_conversationsGate)
        {
            conversation = _conversations.FirstOrDefault(c => c.SourceKey == sourceKey);
            if (conversation is null)
            {
                return false;
            }

            _conversations.Remove(conversation);
        }

        _replyCooldown.TryRemove(sourceKey, out _);
        _historyRequested.TryRemove(sourceKey, out _);
        DropPending(sourceKey); // 清待回复队列：否则在途回复还会发到 QQ，本地记录却已成孤儿
        // 库里的会话与消息要**显式删**：平时的保存只 upsert，不删任何东西
        _store.DeleteConversation(sourceKey);
        Save();
        EmitLog($"已删除会话 {conversation.Name}");
        ConversationsChanged?.Invoke();
        return true;
    }

    /// <summary>标记会话已读。</summary>
    public void MarkRead(string sourceKey)
    {
        var conversation = Find(sourceKey);
        if (conversation?.MarkRead() == true)
        {
            ConversationsChanged?.Invoke();
        }
    }

    /// <summary>按 sourceKey 查找会话。</summary>
    public BotConversation? Find(string sourceKey)
    {
        lock (_conversationsGate)
        {
            return _conversations.FirstOrDefault(c => c.SourceKey == sourceKey);
        }
    }

    /// <summary>人物档案摘要（Web UI 查看成员用）。
    /// allScopes：面板需要看全部会话的画像（含群聊），否则只能看到私聊记录。
    /// 该视图**不用于模型注入**，模型注入始终按会话隔离。</summary>
    public string GetProfileSummary(string uid) => _profiles.GetProfileSummary(uid, limit: 30, allScopes: true);

    /// <summary>已跟踪的会话快照（按最后活跃时间倒序）。</summary>
    public IReadOnlyList<BotConversation> Conversations
    {
        get
        {
            lock (_conversationsGate)
            {
                return _conversations.ToArray();
            }
        }
    }

    /// <summary>
    /// 上行通道台账（私域 / 官方各自启用没启用、连上没有）。
    /// 面板分块与 <c>//status</c> 从这里问；单通道部署时返回 null，调用方就当“只有私域”。
    /// </summary>
    public IChannelRegistry? ChannelRegistry => _source as IChannelRegistry;

    private int _savePending;
    private long _saveVersion;

    /// <summary>连接的 QQ 号（协议端上报优先，其次配置）。0 = 未知。</summary>
    public long SelfId => _selfId;

    /// <summary>启动：订阅消息源、恢复磁盘会话、启动静默兜底定时器。</summary>
    public void Start()
    {
        _source.MessageReceived += OnMessageReceived;
        _source.ConnectionChanged += OnConnectionChanged;
        _source.Poked += OnPoked;
        _source.MessageRecalled += OnMessageRecalled;

        RestoreConversations();
        _stickers.Load(AppPaths.RuntimeRoot);
        _mood.Load(AppPaths.RuntimeRoot);
        ApplyMoodTtl();
        BuildMusicService();
        _voice = new VoiceService(_voiceHttp, () => _settings, EmitLog);
        _search = new WebSearchService(_netHttp, () => _settings, EmitLog);

        RebuildTimersIfNeeded(); // 静默兜底 + 画像巡检 + 表情包巡检（运行时改配置走同一段逻辑）

        // QQ 账号在线探测：30 秒一轮（连接建立后会立即先探一次）
        _healthTimer = new Timer(_ => _ = CheckAccountOnlineAsync(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30));

        // 白名单严格模式提示：空名单 = 全部忽略，容器里很容易踩
        if (WhitelistSummary(_whitelistAllGroups, _whitelistGroups) is "(空，忽略全部)" &&
            WhitelistSummary(_whitelistAllPrivates, _whitelistPrivates) is "(空，忽略全部)")
        {
            FileLog.Warn("Agent",
                "群聊与私聊白名单都是空的 → 严格模式下将忽略所有消息。若要接收，请设 " +
                "QQCHAT_WHITELIST_GROUPS / QQCHAT_WHITELIST_PRIVATES（或旧的 QQCHAT_WHITELIST），" +
                "填具体群号/QQ 号，或写 '*'。");
        }

        FileLog.Write("Agent",
            $"已启动。AI={( _settings.AiModeEnabled ? "开" : "关")}, " +
            $"群聊白名单={WhitelistSummary(_whitelistAllGroups, _whitelistGroups)}{(_whitelistGroupsFromLegacy ? "（用旧的共用名单）" : "")}, " +
            $"私聊白名单={WhitelistSummary(_whitelistAllPrivates, _whitelistPrivates)}{(_whitelistPrivatesFromLegacy ? "（用旧的共用名单）" : "")}, " +
            // 官方那条的名单状态也得印：否则“官方通道被拦”时完全看不出到底是名单空了、还是填了不对的号
            $"官方白名单=群{WhitelistSummary(_officialWhitelistAllGroups, _officialWhitelistGroups)}/私聊{WhitelistSummary(_officialWhitelistAllPrivates, _officialWhitelistPrivates)}, " +
            $"模型={_settings.ReplyModel}" +
            (_settings.FastReply && _settings.ReplyModel != _settings.Model
                ? $"（快速档；主模型 {_settings.Model}）"
                : string.Empty) +
            $", 人设={(string.IsNullOrWhiteSpace(_settings.BotPersona) ? "无" : "已配置")}, " +
            $"表情包={(_settings.EnableStickers ? $"开（{_stickers.Count}/{_settings.StickerLibraryMax} 张，已描述 {_stickers.DescribedCount}）" : "关")}");
    }

    /// <summary>白名单显示文案（日志与面板共用口径）。</summary>
    private static string WhitelistSummary(bool all, HashSet<long> ids)
        => all ? "* (全部)" : ids.Count == 0 ? "(空，忽略全部)" : string.Join(",", ids);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _source.MessageReceived -= OnMessageReceived;
        _source.ConnectionChanged -= OnConnectionChanged;
        _source.Poked -= OnPoked;
        _source.MessageRecalled -= OnMessageRecalled;
        _idleTimer?.Dispose();
        _summaryTimer?.Dispose();
        _stickerTimer?.Dispose();
        _healthTimer?.Dispose();
        _summaryGate.Dispose();
    }

    // ---------- 连接与自我识别 ----------

    private void OnConnectionChanged(bool connected)
    {
        EmitLog(connected ? "QQ 已连接（协议端在线）" : "QQ 连接断开，等待重连…");

        if (!connected)
        {
            // 连接都没了，账号在线状态回到未知，等重连后再探
            Interlocked.Exchange(ref _accountOnline, -1);
        }

        StateChanged?.Invoke();
        if (connected)
        {
            _ = RefreshSelfIdAsync();
        }
    }

    /// <summary>连接后向协议端确认登录号，用于 @ 识别与身份注入。</summary>
    private async Task RefreshSelfIdAsync()
    {
        try
        {
            if (_source is not OneBotGateway gateway)
            {
                return;
            }

            // 给协议端一点握手时间（NapCat 刚连上时 get_login_info 可能还没就绪）
            await Task.Delay(1500);
            var id = await gateway.GetSelfIdAsync();
            if (id is > 0)
            {
                _selfId = id.Value;
                gateway.SelfIdHint = id.Value;
                _brain.BotIdentity = id.Value.ToString();
                EmitLog($"登录账号确认：QQ {id.Value}");
            }

            // 紧接一次账号在线探测：连接刚建立时就能发现“连上了但账号已掉线”
            await CheckAccountOnlineAsync();
        }
        catch (Exception ex)
        {
            EmitLog("获取登录账号失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 记下一个群成员的身份（消息事件自带 role；自带头衔的协议端就连头衔一起收下），
    /// 需要补头衔时再后台去问协议端 —— 不能每条消息都问，那会把协议端打爆。
    /// </summary>
    private void RememberMemberRole(QqChatMessage msg)
    {
        var uid = msg.UserId.ToString();
        // 事件里带 role（一定有）、可能带 title（看协议端）；titleChecked 只在真拿到 title 时才算 true
        _memberRoles.Remember(uid, msg.GroupId, msg.SenderRole, msg.SenderTitle, msg.SenderName,
            titleChecked: !string.IsNullOrWhiteSpace(msg.SenderTitle));

        // 消息事件里没有头衔（OneBot 只保证有 role）—— 缺的话去问一次；问过就把
        // updated_unix 推后，下次 3 天内不再问（见 MemberRoleStore.FreshFor）。
        if (!string.IsNullOrWhiteSpace(msg.SenderTitle))
        {
            return;
        }

        if (!_memberRoles.NeedsRefresh(uid, msg.GroupId))
        {
            return;
        }

        // 同一人同一群只排一次（群里连发会疯狂触发）
        if (!_pendingRoleLookup.TryAdd((msg.GroupId, msg.UserId), Task.CompletedTask))
        {
            return;
        }

        var groupId = msg.GroupId;
        var userId = msg.UserId;
        var name = msg.SenderName;
        var lookup = Task.Run(async () =>
        {
            try
            {
                var info = await _source.GetGroupMemberInfoAsync(groupId, userId, CancellationToken.None);
                if (info is not null)
                {
                    _memberRoles.Remember(uid, groupId, info.Role, info.Title, info.DisplayName, titleChecked: true);
                    if (!string.IsNullOrWhiteSpace(info.Title) || info.Role is "owner" or "admin")
                    {
                        EmitLog($"[Role] 群 {groupId} 成员 {info.DisplayName}({userId})：" +
                                $"{info.Role switch { "owner" => "群主", "admin" => "管理员", _ => "成员" }}" +
                                (string.IsNullOrWhiteSpace(info.Title) ? string.Empty : $"，头衔「{info.Title}」"));
                    }
                }
                else
                {
                    // 协议端不支持/没这个人：至少把“问过了”记下来，否则下次发言又要问一遍
                    _memberRoles.Remember(uid, groupId, msg.SenderRole, null, name, titleChecked: true);
                }
            }
            catch (Exception ex)
            {
                EmitLog($"[Role] 查群成员身份失败（不影响聊天）：{ex.Message}");
            }
            finally
            {
                _pendingRoleLookup.TryRemove((groupId, userId), out _);
            }
        });

        // 把真任务放进去（TryAdd 时先占位，避免同一人连发时排队问多次）
        _pendingRoleLookup[(groupId, userId)] = lookup;
    }

    /// <summary>
    /// 正在等人去问协议端身份的 (群, 人) → 那个查询任务。
    /// 存 Task 而不是个标记：回复前会等它们一下（见 <see cref="WaitForPendingRoleLookupsAsync"/>）——
    /// 查询通常十几毫秒，等一下就能让“第一次说话那轮”也带着头衔，不必等下一句。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long Group, long UserId), Task> _pendingRoleLookup = new();

    /// <summary>等本群待补身份查完（最多等 timeout，超时就算了，别拖慢回复）。</summary>
    private async Task WaitForPendingRoleLookupsAsync(long groupId, TimeSpan timeout)
    {
        var pending = _pendingRoleLookup
            .Where(kv => kv.Key.Group == groupId && !kv.Value.IsCompleted)
            .Select(kv => kv.Value)
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeout));
    }

    /// <summary>
    /// 按“读到的气氛”调门槛。返回本轮真正生效的阈值，reason 给日志用。
    /// 为什么在代码里再调一道（而不是只写在提示词里）：模型自评本来就依赖它的判断，
    /// 但它说“群里在吵架”时还想插一句的情况真出现过 —— 这时候代码得拦一下。
    /// </summary>
    private static int VibeAdjustedThreshold(string vibe, int baseThreshold, out string reason)
    {
        switch (vibe)
        {
            case "吵架":
                reason = "气氛在对线，不插嘴（除非非说不可）";
                return Math.Max(baseThreshold, 60);
            case "低落":
            case "求助":
                reason = "有人情绪不好/在求助，轻轻接一句比沉默好";
                return Math.Max(0, baseThreshold - 10);
            case "生气":
                reason = "有人在气头上，说话得稳一点";
                return baseThreshold;
            default:
                reason = string.Empty;
                return baseThreshold;
        }
    }

    /// <summary>记下这一轮读到的气氛（下一轮当底色用；没有 vibe 就不记）。</summary>
    private void RememberVibe(string sourceKey, string? vibe, string? note)
    {
        if (string.IsNullOrWhiteSpace(vibe) || vibe == "中性")
        {
            _vibes.TryRemove(sourceKey, out _);
            return;
        }

        _vibes[sourceKey] = (vibe, (note ?? string.Empty).Trim(), DateTimeOffset.Now);
    }

    /// <summary>当前还记得的气氛（过期/没记返回空串）。</summary>
    private string CurrentVibe(string sourceKey)
        => _vibes.TryGetValue(sourceKey, out var v) && DateTimeOffset.Now - v.At <= VibeTtl ? v.Vibe : string.Empty;

    /// <summary>给模型的“上一轮感觉”（带一句人话），没有就返回 null。</summary>
    private string? CurrentVibeHint(string sourceKey)
    {
        if (!_vibes.TryGetValue(sourceKey, out var v) || DateTimeOffset.Now - v.At > VibeTtl)
        {
            return null;
        }

        return v.Note.Length > 0 ? $"{v.Vibe}：{v.Note}" : v.Vibe;
    }

    /// <summary>
    /// 气氛“沉”的时候不发图/不发表情/不戳人：人家在难过或者在对线，
    /// 机器人丢个表情包过去看着就像在笑。（语音也不算合适，一句就够，别弄得很热闹）
    /// </summary>
    private bool IsSoberVibe(string sourceKey)
        => CurrentVibe(sourceKey) is "低落" or "求助" or "吵架" or "生气" or "吐槽";

    // ---------- 入站消息 ----------

    /// <summary>
    /// 把群友消息**开头 / 结尾**的括号旁白转成标注（`〔旁白：笑〕`），
    /// 返回（是否整条都是旁白、标注后的文本）。
    ///
    /// 口径来自群里真实消息（号主反馈后我拉了 700 多条带括号的群消息看过）：
    ///   • “（雨哗啦啦）”            整条都是旁白 → `〔旁白：雨哗啦啦〕`（只接收，不触发回复）；
    ///   • “行（端在桌上）”          尾巴上的旁白 → `行〔旁白：端在桌上〕`；
    ///   • “（放在地上）来吧，猫猫，”  开头的旁白   → `〔旁白：放在地上〕来吧，猫猫，`；
    ///   • 中间位置的括号**不碰**（“（2026）年的计划”里的括号是正文，群样本里也没这种旁白）。
    ///
    /// 号主 2026-09-14 追加的口径（§25）：**不要单纯忽略，也要接收，但要特别注明** ——
    /// 旁白不再被丢掉，而是标成 `〔旁白：…〕` 一起进聊天记录与模型上下文：模型能拿它理解现场
    /// （“端到桌上”“抱着猫”这类动作本来就是语境），但一眼就知道那不是“他说的话”。
    ///
    /// 三个例外——机器人自己的内容标记（[图片] / [表情:斜眼笑] / [动画表情:…] / [语音]…）
    /// 是“对方发了啥”的记录，不是旁白：一律保留（把它们一起标注就等于把群友发表情/图片的记录抹了，踩过）。
    /// 私聊 / 带图 / @ 机器人的消息完全不动（宁可多回也不装死）。
    /// </summary>
    private static (bool AsideOnly, string Text) AnnotateBracketAsides(QqChatMessage msg)
    {
        var text = msg.Text?.Trim() ?? string.Empty;
        if (!msg.IsGroup || msg.MentionedSelf || msg.ImageUrls is { Count: > 0 } || text.Length == 0)
        {
            return (false, text);
        }

        // 开头的旁白（可能连着几段）→ 依次标在正文前面
        var leading = new StringBuilder();
        var rest = text;
        while (TryTakeLeadingBracket(rest, out var afterLead, out var inner))
        {
            leading.Append(MessageMarkers.AsAside(inner));
            rest = afterLead;
        }

        // 结尾的旁白（可能连着几段）→ 先倒着收，再按原顺序接到正文后面
        var trailing = new List<string>();
        while (TryTakeTrailingBracket(rest, out var afterTail, out var inner))
        {
            trailing.Add(MessageMarkers.AsAside(inner));
            rest = afterTail;
        }

        if (leading.Length == 0 && trailing.Count == 0)
        {
            return (false, text);   // 本来就没有旁白（如单一个“？”）→ 当普通消息
        }

        rest = rest.Trim();
        if (rest.Length == 0 || IsOnlyDecoration(rest))
        {
            // 剥完只剩标点/表情（如“（真的）？”）→ 整条就是旁白，标好的旁白就是全文
            trailing.Reverse();
            return (true, leading + string.Concat(trailing));
        }

        trailing.Reverse();
        return (false, leading + rest + string.Concat(trailing));
    }

    /// <summary>只剩标点、符号、emoji 与空白（不构成内容）。</summary>
    private static bool IsOnlyDecoration(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || char.IsSurrogate(ch))
            {
                continue;
            }

            if (ch is '～' or '~' or '…' or '·' or '　' or '〰' or '﹏')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static readonly char[] BracketOpen = ['（', '(', '［', '[', '【', '｛', '{', '〈', '《'];
    private static readonly char[] BracketClose = ['）', ')', '］', ']', '】', '｝', '}', '〉', '》'];

    /// <summary>开头就是一段括号（且不是我们的内容标记）→ 取走它，并把括号里的内容给出去。</summary>
    private static bool TryTakeLeadingBracket(string text, out string rest, out string inner)
    {
        rest = text;
        inner = string.Empty;
        if (text.Length < 2 || Array.IndexOf(BracketOpen, text[0]) < 0)
        {
            return false;
        }

        var close = FindClose(text, 0);
        if (close < 0)
        {
            return false;   // 括号没闭合，当普通文本
        }

        var content = text[1..close];
        if (IsContentMarker(content) || LooksNumeric(content))
        {
            return false;
        }

        inner = content;
        rest = text[(close + 1)..];
        return true;
    }

    /// <summary>结尾是一段括号（且不是我们的内容标记）→ 取走它，并把括号里的内容给出去。</summary>
    private static bool TryTakeTrailingBracket(string text, out string rest, out string inner)
    {
        rest = text;
        inner = string.Empty;
        if (text.Length < 2 || Array.IndexOf(BracketClose, text[^1]) < 0)
        {
            return false;
        }

        // 从右往左找配对的左括号（简单配对：碰到另一个右括号就放弃，不当旁白）
        var openIndex = -1;
        for (var i = text.Length - 2; i >= 0; i--)
        {
            if (Array.IndexOf(BracketClose, text[i]) >= 0)
            {
                return false;
            }

            if (Array.IndexOf(BracketOpen, text[i]) >= 0)
            {
                openIndex = i;
                break;
            }
        }

        if (openIndex < 0)
        {
            return false;
        }

        var content = text[(openIndex + 1)..^1];
        if (IsContentMarker(content) || LooksNumeric(content))
        {
            return false;
        }

        inner = content;
        rest = text[..openIndex];
        return true;
    }

    /// <summary>
    /// 括号里是不是“纯数字/符号”（如「（2026）」「（1.5）」）—— 这类是正文的一部分，不当旁白剥掉。
    /// 判据：里面一个字母/汉字都没有。（汉字在 .NET 里算 Letter，所以一个判断就够）
    /// </summary>
    private static bool LooksNumeric(string inner)
        => inner.Trim().Length > 0 && !inner.Any(char.IsLetter);

    private static int FindClose(string text, int openIndex)
    {
        for (var i = openIndex + 1; i < text.Length; i++)
        {
            if (Array.IndexOf(BracketClose, text[i]) >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 这段括号里是不是“机器人写的内容标记”（对方发表情/图片/语音的记录）。
    /// 这些不是旁白，不能当括号剥掉——不然群友发的表情就被抹掉了（踩过）。
    /// </summary>
    private static bool IsContentMarker(string inner)
    {
        var t = inner.Trim();
        foreach (var marker in new[] { "图片", "表情", "动画表情", "语音", "视频", "文件", "音乐", "合并转发", "戳一戳", "分享", "卡片", "位置", "链接", "视频通话" })
        {
            if (t.StartsWith(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 认出“这条消息引用回复的是哪一条”，返回（那个人叫什么、原话、上下文里到底找到没有、是不是机器人自己说的）。
    /// 查找顺序（先免费的后花钱的）：
    ///   ① 会话上下文里按 id 找 —— 绝大多数引用都是刚发过的消息，这里命中；
    ///   ② 机器人自己发出去的消息表（从发送响应里拿到 id，见 RememberOwnMessage；现在会落盘，重启不清）；
    ///   ③ 协议端在 reply 段里自带的摘要文本（有就用；段里还带了被引用者的 QQ 时，谁说的也当场就知道）。
    /// 都找不到时不编内容，只标一句“更早的一条”（随后由 EnrichQuotedFromProtocolAsync 事后补齐原文）。
    ///
    /// <para>2026-09-19 修“引用机器人发的消息被吞”：IsSelf 以前只看内存表，部署重启（每次部署都会重启）
    /// 后表是空的 —— 群里“引用机器人上一句再说话”就不算直接对它说，于是整条被静默丢掉。</para>
    /// </summary>
    private (string Name, string Text, bool Hit, bool IsSelf) ResolveQuotedMessage(BotConversation conversation, QqChatMessage msg)
    {
        // 落盘的那份要在**查询之前**就绪：它是懒加载的，而重启后第一件事往往就是“有人引用了上一句”，
        // 那时候机器人还没发过任何消息（只在发送时加载的话，这里就会永远查不到 —— 2026-09-19 差点踩到）。
        EnsureOwnMessagesLoaded();
        var botUin = _settings.NormalizedUin;
        var quotedFromProtocol = msg.ReplyToSenderId is long quotedSender
                                 && !string.IsNullOrWhiteSpace(botUin)
                                 && quotedSender.ToString() == botUin;

        if (msg.ReplyToMessageId is long quotedId)
        {
            var messages = conversation.Messages;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].QqMessageId != quotedId)
                {
                    continue;
                }

                var quoted = messages[i];
                var self = quoted.Role == MessageRole.Self;
                // 自己说的那一条：对模型来说“你”才是有意义的称呼
                var name = self
                    ? "你"
                    : (string.IsNullOrWhiteSpace(quoted.SenderName) ? "某人" : quoted.SenderName!);
                return (name, quoted.Text ?? string.Empty, true, self);
            }

            if (_ownMessages.TryGetValue(quotedId, out var mine))
            {
                return ("你", mine.Text, true, true);
            }
        }

        if (msg.ReplyToPreviewText is { Length: > 0 } preview)
        {
            return quotedFromProtocol ? ("你", preview, true, true) : ("某人", preview, true, false);
        }

        return (quotedFromProtocol ? "你" : string.Empty, string.Empty, false, quotedFromProtocol);
    }

    /// <summary>
    /// 把“引用回复”拼成给模型看的标注：`[回复 老王「你昨天说的那个 bug」]`。
    /// 为什么要把原话也带上：群聊里一句“我也是”全靠引用的那条才能懂 —— 上下文里虽然也有那条，
    /// 但位置可能隔着好几十条，模型不一定会自己去对（而且对不上就会编）。
    /// 引的是机器人自己那句就用“你”（模型才分得清是跟它说话）。
    /// </summary>
    private static string BuildReplyAnnotation(string name, string quotedText)
    {
        if (string.IsNullOrEmpty(name) && string.IsNullOrWhiteSpace(quotedText))
        {
            return "[回复一条更早的消息（我这边已经看不到原文了）]";
        }

        var text = (quotedText ?? string.Empty).Replace('\n', ' ').Trim();
        // 引用的那条自己可能也是回复（“[回复 你「…」] xxx”）：嵌套引号只会变成噪音，剥一层
        if (text.StartsWith("[回复", StringComparison.Ordinal))
        {
            var close = text.IndexOf(']');
            if (close > 0)
            {
                text = text[(close + 1)..].Trim();
            }
        }

        if (text.Length == 0)
        {
            return $"[回复 {name}（引用的内容我这边取不到）]";
        }

        return $"[回复 {name}「{Shorten(text, 40)}」]";
    }

    private void OnMessageReceived(QqChatMessage msg)
    {
        try
        {
            HandleInbound(msg);
        }
        catch (Exception ex)
        {
            EmitLog("处理入站消息异常: " + ex.Message);
        }
    }

    private void HandleInbound(QqChatMessage msg)
    {
        // 对话总开关（按通道）：官方那条在调试/被平台限制时，可以只把它静音，私域照旧。
        // 面板顶部那个「AI 开关」是**全局**的（两条一起断，且连“人在叫它”也不回）——两者不是一回事。
        var channelEnabled = Channels.IsOfficial(msg.Channel)
            ? _settings.OfficialChatEnabled
            : _settings.PrivateChatEnabled;
        if (!channelEnabled)
        {
            var label = Channels.Tag(msg.Channel) + (msg.IsGroup ? " 群 " + msg.GroupId : " 私聊 " + msg.UserId);
            LogThrottled("chanoff:" + Channels.ChannelOf(msg.Channel), $"忽略（{Channels.Display(msg.Channel)}通道的总开关是关的）: {label}");
            return;
        }

        if (!IsWhitelisted(msg))
        {
            // 忙群里这类日志会把日志文件和面板刷爆 → 同一来源每分钟最多一条
            var label = msg.IsGroup ? "群 " + msg.GroupId : "私聊 " + msg.UserId;
            // 把“用哪份名单、那份的状态”一并印出来：
            // 实际踩过——“官方通道永远不回”但日志只有一句“不在白名单”，看不出是名单选错了还是填了真实号。
            var tag = Channels.Tag(msg.Channel);
            var listState = Channels.IsOfficial(msg.Channel)
                ? (msg.IsGroup
                    ? $"官方群名单{WhitelistSummary(_officialWhitelistAllGroups, _officialWhitelistGroups)}"
                    : $"官方私聊名单{WhitelistSummary(_officialWhitelistAllPrivates, _officialWhitelistPrivates)}")
                : (msg.IsGroup ? "私域群名单" : "私域私聊名单");
            LogThrottled("ignore:" + label, $"忽略（不在白名单）: [{tag}] {label}（{listState}）");
            return;
        }

        // 机器人自己发的消息不入库（协议端可能回显）
        if (_selfId != 0 && msg.UserId == _selfId)
        {
            return;
        }

        // 括号旁白：**标注**（`〔旁白：…〕`）而不是忽略 —— 号主口径（2026-09-14）：
        // “不要单纯忽略，也要接收，但需要特别注明”。
        // 纯旁白（“（笑）”“（放在地上）来吧”里的括号段）会进聊天记录与上下文，但**不单独触发回复**
        // （旁白不是对谁说的话，不然“（笑）”就会把机器人拽出来接话 —— §20 的原始问题）；
        // 正文 + 旁白混着的照常触发（正文才是那句话）。
        var asideOnly = false;
        if (_settings.IgnoreBracketMessages)
        {
            var (isAsideOnly, annotated) = AnnotateBracketAsides(msg);
            asideOnly = isAsideOnly;

            if (annotated.Length > 0 && annotated != msg.Text)
            {
                msg = msg with { Text = annotated };   // 落库与上下文都用标注后的文本
            }

            if (asideOnly)
            {
                // 同一个人一分钟最多记一条：不然旁白刷屏时日志也跟着刷
                LogThrottled("bracket:" + msg.UserId,
                    $"旁白（只接收、不触发回复）: {msg.SenderName}({msg.UserId}): {Shorten(msg.Text, 30)}");
            }
        }

        var conversation = GetOrCreateConversation(msg);

        // 本机 Agent 命令（// 开头）：**不进人设路线** —— 它不是一个“插个嘴”，是一个真任务；
        // 也不该被适合度阈值/群冷却/复读守卫那些限流卡住（它们都是为“聊天”设计的）。handoff-4 §31
        if (!asideOnly && TryParseAgentCommand(msg.Text, out var agentPayload))
        {
            _ = RunAgentCommandSafeAsync(conversation, msg, agentPayload);
            return;
        }

        // 引用回复：把“在回哪条”标进正文。
        // 以前 reply 段被直接丢掉 → 模型只看到一句“我也是，哈哈”，不知道在回什么，
        // 也认不出“他在回机器人自己上一句”（号主反馈：识别不了引用回复消息 —— handoff-4 §27）。
        // 2026-09-19：引自己那句的判定不再只看内存表（重启就清空）—— 见 ResolveQuotedMessage 的 IsSelf。
        var quotedIsSelf = false;
        var quotedResolved = true;
        if (msg.ReplyToMessageId is not null || msg.ReplyToPreviewText is { Length: > 0 })
        {
            var (quotedName, quotedText, hit, isSelf) = ResolveQuotedMessage(conversation, msg);
            quotedIsSelf = isSelf;
            quotedResolved = hit;
            var annot = BuildReplyAnnotation(quotedName, quotedText);
            if (annot.Length > 0 && !msg.Text.StartsWith(annot, StringComparison.Ordinal))
            {
                // 正文可能是一个空格（只点“回复”不写字）：先 Trim，不然会拼出个尾巴空格
                var body = msg.Text.Trim();
                msg = msg with { Text = body.Length > 0 ? annot + " " + body : annot };
            }

            EmitLog($"引用回复：{msg.SenderName} 引用了" +
                    (hit ? $" {quotedName} 的「{Shorten(quotedText, 24)}」" : " 一条我这边已看不到的消息（先标“更早的一条”，同时去协议端补原文）"));
        }

        var appended = new ChatMessage
        {
            Role = MessageRole.Peer,
            SenderName = msg.IsGroup ? msg.SenderName : null,
            SenderId = msg.UserId,
            Text = msg.Text,
            Timestamp = msg.Time,
            QqMessageId = msg.MessageId,
            ImageUrls = msg.ImageUrls,
            // 被点名 = @ 了机器人自己，或引用了机器人发的那条（引了自己的话也是“在跟你说话”）
            DirectToBot = msg.MentionedSelf || quotedIsSelf
        };
        conversation.Append(appended);
        conversation.HasPendingReply = !asideOnly;   // 纯旁白不欠一次回复（也不该被静默兜底抳回来）
        Touch(conversation);
        MessageAdded?.Invoke(conversation.SourceKey, appended);
        Save();

        // 引用的原文本地一条都对不上（重启前的旧消息 / 早被清出上下文）→ 后台去协议端按 id 查一次，
        // 查到就把真实原文补写进这条消息。为什么是“事后补”而不是发之前查：网关是在接收循环里
        // 同步调我们的（GetAwaiter().GetResult()），在这里等协议端回包会死锁到超时。
        if (!quotedResolved && msg.ReplyToMessageId is long unresolvedId)
        {
            EnrichQuotedFromProtocolAsync(conversation, appended, unresolvedId);
        }
        // 人物档案（帮助模型认识群友/好友）
        var botUin = _settings.NormalizedUin;
        var isSelfSender = !string.IsNullOrWhiteSpace(botUin) && msg.UserId.ToString() == botUin;
        if (!isSelfSender)
        {
            _profiles.Append(
                msg.UserId.ToString(),
                msg.SenderName,
                msg.Text,
                msg.Time,
                msg.IsGroup ? conversation.Name : null,
                msg.IsGroup ? msg.GroupId : 0,
                appended.Seq);

            // 群成员身份（群主/管理员/群头衔）：先记下消息事件里带的 role（零成本），
            // 缺头衔或太久没更新时再后台去问协议端（ get_group_member_info 才能拿到自定义头衔）。
            if (msg.IsGroup)
            {
                RememberMemberRole(msg);
            }
        }

        EmitLog(
            $"收到 {(msg.IsGroup ? $"群{msg.GroupId}" : "私聊")} {msg.SenderName}({msg.UserId}): " +
            $"{(msg.Text.Length > 80 ? msg.Text[..80] + "…" : msg.Text)}" +
            (msg.ImageUrls is { Count: > 0 } ? $" [+{msg.ImageUrls.Count}图]" : string.Empty));

        // 表情包：群友发的图自动收进库（后台下载，不阻塞接收线程）
        if (_settings.EnableStickers && _settings.StickerLibraryMax > 0 && msg.IsGroup && msg.ImageUrls is { Count: > 0 })
        {
            var urls = msg.ImageUrls.ToList();
            var uid = msg.UserId.ToString();
            var group = msg.GroupId;
            var mid = msg.MessageId;
            _ = Task.Run(() => CollectStickersAsync(urls, uid, group, mid));
        }

        // 听音乐：识别到分享就后台去查歌词 + 下一份低码率音频分析波形。
        // 有歌的时候**先不回复** —— 等分析结果回来再让模型开口，否则它只能对着一个歌名瞎聊。
        var musicShares = _settings.EnableMusic ? msg.MusicShares : null;
        if (musicShares is { Count: > 0 })
        {
            _ = Task.Run(() => HandleMusicAsync(conversation, msg.SenderName, musicShares.ToList()));
        }

        // 链接：群里发的 URL（包括分享卡片里那个）真去打开看一眼，取回标题/摘要。
        // 有音乐分享时跳过 —— 音乐那条路自己会处理链接，不必看两遍。
        var linkUrls = musicShares is { Count: > 0 } || _links is null || !_settings.EnableLinkPreview
            ? []
            : LinkExtractor.Extract(msg.Text, Math.Clamp(_settings.LinkPreviewMax, 0, 5));
        if (linkUrls.Count > 0)
        {
            var key = conversation.SourceKey;
            var urls = linkUrls.ToList();
            var pending = Task.Run(async () =>
            {
                try
                {
                    var note = await _links!.DescribeAsync(urls, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        _linkNotes[key] = note!;
                        EmitLog($"[Link] 已看过 {urls.Count} 个链接：{string.Join("、", urls.Select(u => u.Length > 48 ? u[..48] + "…" : u))}");
                    }
                }
                catch (Exception ex)
                {
                    EmitLog($"[Link] 预览失败: {ex.Message}");
                }
                finally
                {
                    _linkTasks.TryRemove(key, out _);
                }
            });
            _linkTasks[key] = pending;
        }

        // 首个群消息时补历史上下文（同桌面版"点开会话拉历史"）
        if (msg.IsGroup)
        {
            EnsureGroupContext(conversation, msg.GroupId);
        }

        if (!asideOnly && musicShares is not { Count: > 0 })
        {
            // 被限流挡下也不丢：RequestReply 会记一笔，这一轮说完补一次评估（见该方法注释）
            RequestReply(conversation, msg.MessageId > 0 ? msg.MessageId : null, directInWindow: appended.DirectToBot);
        }
    }

    // ══════════ 本机 Agent（// 命令，handoff-4 §31）══════════

    /// <summary>允许用 agent 的 QQ 号集合（改设置后会重建）。</summary>
    private HashSet<long> _agentUsers = new();

    /// <summary>节流“你没权限用”的回复：每会话每分钟最多一条（不然人人试一下就是刷屏）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _agentDeniedAt = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _agentProgressAt = new();

    /// <summary>重建 agent 用户白名单（跟随设置热更新）。</summary>
    private void RebuildAgentUsers()
    {
        var set = new HashSet<long>();
        foreach (var piece in (_settings.AgentAllowedUsers ?? string.Empty)
                     .Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(piece.Trim(), out var qq))
            {
                set.Add(qq);
            }
        }

        // 没配就没收（新白名单空 = 谁都不能用）。
        // 不强行把号主自己加进去 —— 这种“能在电脑上执行命令”的开关必须写清楚才生效。
        //   * = 白名单会话里**所有人都能用**（号主显式写 * 才生效，不是默认）
        _agentUsers = set;
        _agentAllowAll = (_settings.AgentAllowedUsers ?? string.Empty)
            .Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(p => p.Trim() == "*");
    }

    /// <summary>“白名单会话里所有人都能用”（AgentAllowedUsers 里写了 <c>*</c>）。</summary>
    private bool _agentAllowAll;

    /// <summary>
    /// 这条消息是不是 agent 命令（<c>//</c> 开头）。是则拆出真正的提示词。
    /// 宽容之处：允许前面先 @ 机器人（群里习惯“@bot //xxx”），但**前缀必须真的在开头**。
    /// </summary>
    private bool TryParseAgentCommand(string rawText, out string payload)
    {
        payload = string.Empty;
        if (!_settings.EnableAgentBridge)
        {
            return false;
        }

        var prefix = string.IsNullOrWhiteSpace(_settings.AgentPrefix) ? "//" : _settings.AgentPrefix.Trim();
        var text = (rawText ?? string.Empty).TrimStart();

        // 剥掉开头的 @某人（含 @全体成员）：只剥 @QQ 号这种形状，不碰正文里的 @
        while (text.StartsWith('@'))
        {
            var space = text.IndexOf(' ');
            if (space <= 0 || space > 20)
            {
                break;
            }

            var handle = text[1..space];
            if (!handle.All(char.IsDigit) && handle != "全体成员")
            {
                break;
            }

            text = text[(space + 1)..].TrimStart();
        }

        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length <= prefix.Length)
        {
            // 光写一个前缀（“//”后面没东西）当用法提示处理，不当普通聊天
            if (text.Equals(prefix, StringComparison.Ordinal))
            {
                payload = string.Empty;
                return true;
            }

            return false;
        }

        payload = text[prefix.Length..].Trim();
        return true;
    }

    /// <summary>
    /// 把这条消息带的图片整理成 agent 任务能用的“附件说明”，追加在任务正文后面（纯文本）。
    /// </summary>
    /// <remarks>
    /// 为什么要有这玩意儿：`//` 任务此前只把**文本**交给 agent —— 消息里的图片段在解析时
    /// 已经变成「[图片]」三个字，URL 根本没跟过去，于是外部设备（本机 pi）与服务器 agent
    /// 都不知道有图（2026-09-19 号主报“给 Agent 发图片识别不了”，日志里就是 `agent 命令: [图片]`）。
    /// <para>
    /// 两种取法都给上，谁顺手用谁：
    /// ① QQ 直链（带时效 rkey，尽快取）；② 顺手下载一份留档到 <c>data/agent-images/</c>，
    /// 在这台服务器上跑的 agent 可以直接读文件（容器里是 <c>/data/…</c>）。
    /// 不改桥的协议 —— 只是正文里多几行。
    /// </para>
    /// </remarks>
    private async Task<string> BuildAgentImageNoteAsync(QqChatMessage msg)
    {
        if (msg.ImageUrls is not { Count: > 0 } urls)
        {
            return string.Empty;
        }

        var dir = Path.Combine(AppPaths.DataDir, "agent-images");
        var lines = new List<string>();
        var count = 0;
        foreach (var url in urls.Take(3))       // 与聊天识图一致：每条最多 3 张
        {
            count++;
            string? saved = null;
            try
            {
                // 给它 10 秒：下载慢不该把“收到，去跑”这句回话拖太久（失败也不影响任务）。
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var downloaded = await _brain.DownloadImageAsync(url, timeout.Token, msg.MessageId);
                if (downloaded is not null)
                {
                    Directory.CreateDirectory(dir);
                    var ext = string.IsNullOrWhiteSpace(downloaded.Value.Ext) ? ".jpg" : downloaded.Value.Ext;
                    var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{msg.MessageId}-{count}{ext}";
                    await File.WriteAllBytesAsync(Path.Combine(dir, name), downloaded.Value.Data);
                    saved = name;
                }
            }
            catch (Exception ex)
            {
                EmitLog($"agent 图片留档失败（不影响任务）：{ex.GetType().Name} {ex.Message}");
            }

            lines.Add($"  图{count}: {url}");
            if (saved is not null)
            {
                lines.Add($"        服务器留档: /data/agent-images/{saved}（宿主 /opt/qqchat/data/agent-images/{saved}）");
            }
        }

        PruneAgentImages(dir);

        EmitLog($"agent 任务带了 {count} 张图（已把直链/留档写进任务正文）");
        return "\n\n（这条消息带了 " + count + " 张图片：agent 侧看不到图本体，需要就自己取 ——\n" +
               string.Join("\n", lines) +
               "\n  直链带时效签名、会过期，要看得尽快；取回存成本地文件后当图片打开（本机 pi 可用 read 读图）；" +
               "服务器上的 agent 直接读留档路径。）";
    }

    /// <summary>清掉 agent 图片留档里超过 3 天的老文件（best-effort，出错不外传）。</summary>
    private static void PruneAgentImages(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(3))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败无所谓，下次再说
        }
    }

    /// <summary>agent 命令的主入口：权限 → 子命令（stop/status）→ 排任务。</summary>
    /// <remarks>
    /// 包一层 try/catch：调用处是 fire-and-forget（`_ = …`），不接住的话异常会被静默吞掉 ——
    /// 群里表现为“发了 //status 什么都没回”，而日志里一行痕迹都没有（2026-09-17 实测踩过）。
    /// </remarks>
    private async Task RunAgentCommandSafeAsync(BotConversation conversation, QqChatMessage msg, string payload)
    {
        try
        {
            await HandleAgentCommandAsync(conversation, msg, payload);
        }
        catch (Exception ex)
        {
            EmitLog($"agent 命令处理异常: {ex.GetType().Name} {ex.Message}");
        }
    }

    private async Task HandleAgentCommandAsync(BotConversation conversation, QqChatMessage msg, string payload)
    {
        // 本机 Agent 桥 / 服务器 agent 共用的入口
        if (_agentBridge is null && _serverAgent is null)
        {
            return;
        }

        var bridge = _agentBridge;

        var who = msg.UserId.ToString();
        var allowed = _agentAllowAll || (_agentUsers.Count > 0 && _agentUsers.Contains(msg.UserId));        if (!allowed)
        {
            // 权限不够：日志必记，回话节流（每会话 60 秒一条）
            EmitLog($"agent 命令被拒（{who} 不在 AgentAllowedUsers 里）: {Shorten(payload, 40)}");
            if (ShouldReplyDenied(conversation.SourceKey))
            {
                await SendPlainAsync(conversation, "这个功能只给白名单用户用～");
            }

            return;
        }

        EmitLog($"agent 命令（{who}）: {Shorten(payload, 120)}");

        // 子命令：//stop 取消、//status 看状态、//（空）看用法
        // @目标 要先剥掉：这样 //@server status / //@host new 这类写法也能认出来
        var (wantParsed, payloadStripped) = StripTargetPrefix(payload);
        payload = payloadStripped;

        var want = wantParsed.Length > 0 ? wantParsed : (_settings.AgentTarget ?? "auto").Trim();
        var named = want.Length > 0 &&
                    !new[] { "auto", "server", "服务器", "host", "外部" }
                        .Contains(want, StringComparer.OrdinalIgnoreCase)
            ? want
            : null;

        var head = payload.Split(' ', 2)[0].ToLowerInvariant();
        if (head is "stop" or "cancel" or "停止" or "取消" or "中断")
        {
            var cancelled = bridge?.Cancel(conversation.SourceKey) ?? 0;

            // 服务器内置 agent 也可能在跑：一起停（否则“//stop”对这个后端就是假的）
            if (_serverAgentCurrent.TryGetValue(conversation.SourceKey, out var running))
            {
                running.Task.CancelRequested = true;
                try
                {
                    running.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 刚跑完
                }

                cancelled++;
            }

            await SendPlainAsync(conversation,
                cancelled > 0 ? $"已让它停掉（{cancelled} 个任务）。" : "现在没有在跑的任务。");
            return;
        }

        if (head is "status" or "状态")
        {            var devices = bridge is null || !bridge.Connected
                ? "（无）"
                : string.Join("、", bridge.BridgeNames);
            var deviceDetail = bridge is null || !bridge.Connected
                ? string.Empty
                : string.Join("；", bridge.BridgeNames.Select(n =>
                {
                    var info = bridge.DeviceInfo(n);
                    return $"{n}（pi {info?.Pi ?? "?"}，目录 {info?.Cwd ?? "?"}）";
                }));
            var preferred = (_settings.AgentTarget ?? "auto").Trim();
            var hostOn = _settings.EnableHostAgent;
            var serverOn = _settings.EnableServerAgent;
            var online = bridge is not null && bridge.Connected;

            // “当前会走”用**和真实分发同一个函数**算（named 也传进去）：
            // 以前这里自己再推一遍，结果 `//@某台不在线的设备 …` 显示“走外部设备”、实际跑在服务器上。
            var decision = ResolveRoute(want, bridge, named);
            var willUse = decision.Backend switch
            {
                "server" => "服务器 agent",
                "host" => $"外部设备（{named ?? devices}）",
                _ => "（两个开关都关了，或指定的那边不可用）"
            };

            if (named is not null && decision.Backend != "host")
            {
                willUse += $"（你写的「{named}」没在用：{(decision.DeviceDisabled ? "面板里关掉了" : "不在线")}）";
            }

            await SendPlainAsync(conversation,
                $"外部设备 agent：{(hostOn ? "开" : "关")}（在线：{(online ? deviceDetail : "无")}）\n" +
                $"服务器内置 agent：{(serverOn ? "开" : "关")}（工具 {(_settings.AgentServerTools.Length == 0 ? "全部" : _settings.AgentServerTools)}）\n" +
                $"  QQ 动作：{QqActionCatalog.Summarize(QqActionCatalog.ParseAllowed(_settings.AgentServerQqActions))}\n" +
                $"优先：{preferred}\n当前会走：{willUse}\n单条指定：//@server … 或 //@host … 或 //@设备名 …");
            return;
        }

        if (head is "help" or "?" or "帮助" or "命令")
        {
            await SendPlainAsync(conversation, HelpText(conversation.SourceKey));
            return;
        }

        // ───── 会话管理（号主 2026-09-17：调用内置/外部 agent 时能自由切换/新建/删除会话）─────
        var rest = payload.Length > head.Length ? payload[(head.Length)..].Trim() : string.Empty;

        if (head is "sessions" or "会话" or "session")
        {
            // //sessions all = 所有聊天的总数与标题（“现在有多少个会话及其标题”）
            if (rest.Equals("all", StringComparison.OrdinalIgnoreCase) || rest is "全部" or "所有")
            {
                await SendPlainAsync(conversation, DescribeAllSessions(bridge));
                return;
            }

            await SendPlainAsync(conversation, DescribeSessions(conversation.SourceKey, bridge));
            return;
        }

        if (head is "runs" or "流水" or "记录")
        {
            var cur = _agentSessions.FindCurrent(conversation.SourceKey, ResolveRoute(want, bridge, named).Backend);
            if (cur is null)
            {
                await SendPlainAsync(conversation, "这个聊天还没有 agent 会话（发一条 //指令 会自动建一个，或 //new 新建）。");
                return;
            }

            var runs = _agentSessions.Runs(conversation.SourceKey, cur.Id);
            if (runs.Count == 0)
            {
                await SendPlainAsync(conversation, $"会话「{MaybeMask(cur.Name, conversation.SourceKey)}」还没有执行记录。");
                return;
            }

            var lines = new List<string> { $"会话「{MaybeMask(cur.Name, conversation.SourceKey)}」的执行记录（最近 {runs.Count} 次）：" };
            for (var i = 0; i < Math.Min(8, runs.Count); i++)
            {
                var r = runs[i];
                var when = r.At.ToString("MM-dd HH:mm");
                var state = r.Ok is null ? "⏳ 在跑" : r.Ok.Value ? "✅" : "❌";
                var extra = r.Ok is null ? string.Empty : $"{r.DurationMs / 1000.0:F0}s{(r.ToolCalls > 0 ? $"/{r.ToolCalls}工具" : string.Empty)}";
                lines.Add($"{i + 1}. {when} {state}{extra} {MaybeMask(Shorten(r.Prompt, 24), conversation.SourceKey)}" +
                          (r.Result.Length > 0 ? $" → {MaybeMask(Shorten(r.Result, 26), conversation.SourceKey)}" : string.Empty));
            }

            lines.Add("（//sessions 看会话、//pi 看设备上 pi 里的会话）");
            await SendPlainAsync(conversation, string.Join("\n", lines));
            return;
        }

        if (head is "pi" or "Pi" or "PI")
        {
            if (bridge is null || !bridge.Connected)
            {
                await SendPlainAsync(conversation, "外部设备不在线，列不出它上面的 pi 会话。");
                return;
            }

            var got = await bridge.RequestPiSessionsAsync(named ?? string.Empty);
            await Task.Delay(1200);
            var list = _agentSessions.LastPiSessions(named);
            if (!got || list.Count == 0)
            {
                await SendPlainAsync(conversation, "设备没上报 pi 会话（桥版本旧？重启一下桥）。");
                return;
            }

            var lines = new List<string> { $"设备上的 pi 会话（{list.Count} 个，最近的在前）：" };
            for (var i = 0; i < Math.Min(10, list.Count); i++)
            {
                var it = list[i];
                var title = it["title"]?.GetValue<string>();
                var when = DateTimeOffset.FromUnixTimeSeconds(it["mtime"]?.GetValue<long>() ?? 0).ToLocalTime().ToString("MM-dd HH:mm");
                var shown = string.IsNullOrWhiteSpace(title) ? "(无标题)" : MaybeMask(Shorten(title, 30), conversation.SourceKey);
                lines.Add($"{i + 1}. {when} {shown}");
            }

            lines.Add("想把某个接过来当自己的会话：//import 序号（或 //import <会话id>）");
            await SendPlainAsync(conversation, string.Join("\n", lines));
            return;
        }

        if (head is "import" or "导入")
        {
            if (rest.Length == 0)
            {
                await SendPlainAsync(conversation, "用法：//import <序号|会话id>（先 //pi 看设备上有什么）");
                return;
            }

            var list = _agentSessions.LastPiSessions(named);
            string? piId = null;
            string? piTitle = null;
            if (int.TryParse(rest, out var idx) && idx >= 1 && idx <= list.Count)
            {
                piId = list[idx - 1]["id"]?.GetValue<string>();
                piTitle = list[idx - 1]["title"]?.GetValue<string>();
            }
            else if (list.FirstOrDefault(x => x["id"]?.GetValue<string>() == rest.Trim()) is { } hit)
            {
                piId = rest.Trim();
                piTitle = hit["title"]?.GetValue<string>();
            }
            else
            {
                piId = rest.Trim();   // 也允许直接给 id（不在列表里也认）
            }

            if (string.IsNullOrWhiteSpace(piId))
            {
                await SendPlainAsync(conversation, "没认出你说的是哪个会话（先 //pi 列一遍，或直接给会话 id）。");
                return;
            }

            var created = _agentSessions.Create(conversation.SourceKey, "host",
                string.IsNullOrWhiteSpace(piTitle) ? null : AgentSessionStore.AutoTitle(piTitle), named, piId, piOwned: false);
            await SendPlainAsync(conversation,
                $"已把 pi 会话「{MaybeMask(created.Name, conversation.SourceKey)}」接过来当当前会话（id {MaybeMask(piId, conversation.SourceKey)}）——下一句 //指令 就接着它的上下文跑。\n" +
                "注意：这是设备上已有的会话，//del 只会从列表里去掉、不会删它的文件。");
            return;
        }

        if (head is "rename" or "改名")
        {
            if (rest.Length == 0)
            {
                await SendPlainAsync(conversation, "用法：//rename <新名字>（给`//sessions`里标「←」那个会话改名）");
                return;
            }

            // 关键：这里**不新建**会话，也不按“下一句会走哪个后端”去找 ——
            // 否则号主看到的是 A 会话，改的却是 B（甚至凭空建一个空的）；也正好是“rename 改错会话”那个 bug。
            var cur = _agentSessions.FindCurrent(conversation.SourceKey, ResolveRoute(want, bridge, named).Backend);
            if (cur is null)
            {
                await SendPlainAsync(conversation, "这个聊天还没有 agent 会话（发一条 //指令 会自动建一个，或 //new 新建）。");
                return;
            }

            if (_agentSessions.Rename(conversation.SourceKey, cur.Id, rest))
            {
                var where = cur.Backend == "server" ? "服务器内置" : $"外部 {cur.Device ?? bridge?.AnyBridge?.Name ?? "设备"}";
                // 回话里名字**原样回显**（主人自己打的字，再遮一道只会让人以为改错了）；
                // 但要写清楚改的是哪一个会话：哪条后端、多少轮。
                await SendPlainAsync(conversation,
                    $"已把 [{where}] 里那个会话（{cur.Turns} 轮）改名为「{rest}」。\n" +
                    "（它现在是手动命名，按上下文自动综结不会再动它；//sessions 里那行会带「←」）");
            }
            else
            {
                await SendPlainAsync(conversation, "改名没成功（名字空？）。");
            }

            return;
        }

        if (head is "new" or "新会话")
        {
            var backend = ResolveRoute(want, bridge, named).Backend;
            var created = _agentSessions.Create(conversation.SourceKey, backend, rest.Length > 0 ? rest : null, named);
            await SendPlainAsync(conversation,
                $"已开新会话「{MaybeMask(created.Name, conversation.SourceKey)}」（{(backend == "server" ? "服务器内置" : $"外部 {created.Device ?? bridge?.AnyBridge?.Name ?? "设备"}")}）。" +
                "下一句 //指令 就从空上下文开始；想切回去用 //use 名字。");
            return;
        }

        if (head is "use" or "switch" or "切换")
        {
            if (rest.Length == 0)
            {
                await SendPlainAsync(conversation, "用法：//use <会话名或序号>（先 //sessions 看列表）");
                return;
            }

            var target = ResolveSessionRef(conversation.SourceKey, rest);
            if (target is null || !_agentSessions.Use(conversation.SourceKey, target, out var used))
            {
                await SendPlainAsync(conversation, $"没找到会话「{rest}」。先 //sessions 看看有哪些。");
                return;
            }

            await SendPlainAsync(conversation,
                $"好，切到会话「{MaybeMask(used!.Name, conversation.SourceKey)}」（{(used.Backend == "server" ? "服务器内置" : "外部设备")}，已有 {used.Turns} 轮）。" +
                "下一句 //指令 就接在它后面。");
            return;
        }

        if (head is "del" or "delete" or "rm" or "删除")
        {
            if (rest.Length == 0)
            {
                await SendPlainAsync(conversation, "用法：//del <会话名或序号>（先 //sessions 看列表）");
                return;
            }

            var target = ResolveSessionRef(conversation.SourceKey, rest);
            var deleted = target is null ? null : _agentSessions.Delete(conversation.SourceKey, target);
            if (deleted is null)
            {
                await SendPlainAsync(conversation, $"没找到会话「{rest}」。先 //sessions 看看有哪些。");
                return;
            }

            // 外部后端且这个 pi 会话是我们建的：让设备把它也删掉（导入进来的不动，那不是我们的）
            if (deleted.Backend != "server" && deleted.PiOwned && deleted.PiSessionId.Length > 0 && bridge is not null)
            {
                await bridge.ForgetSessionAsync(deleted.PiSessionId);
            }

            await SendPlainAsync(conversation, $"已删除会话「{MaybeMask(deleted.Name, conversation.SourceKey)}」。当前会话已自动换成新的。");
            return;
        }

        if (head is "reset" or "clear" or "清空")
        {
            // 两个后端各有一份当前会话，而 `//reset` 的语义是“这个聊天的 agent 记忆清空” ——
            // 所以**两边都清**。以前按“下一句会走哪边”只清一边，路由一变就清错：
            // 号主 `//@某台不在线的设备 …` 实际跑在服务器上，reset 却去清了那台空的外部会话，
            // 被污染的历史一直留着（2026-09-18 实测：reset 两次都没用）。
            var cleared = new List<string>();
            foreach (var backendName in new[] { "server", "host" })
            {
                var current = _agentSessions.FindCurrent(conversation.SourceKey, backendName);
                if (current is null)
                {
                    continue;
                }

                var had = current.History.Count;
                var hadRuns = current.Runs.Count;
                var oldTitle = current.Name;
                var reset = _agentSessions.Reset(conversation.SourceKey, current.Id);
                if (reset is not null && reset.Backend != "server" && reset.PiOwned &&
                    current.PiSessionId.Length > 0 && bridge is not null)
                {
                    await bridge.ForgetSessionAsync(current.PiSessionId);   // 旧的那份 pi 记录清掉
                }

                // 标题也换回中性的自动名：号主说“reset 并没有删除此会话的全部内容”——
                // 历史清了、记录清了，但标题还挂着“服务器资源与容器运行状态”这种旧话题，看着就像没清。
                if (oldTitle.Length > 0)
                {
                    RenameSessionQuietly(conversation.SourceKey, current.Id);
                }

                var label = backendName == "server" ? "服务器" : "外部";
                var parts = new List<string>();
                if (had > 0)
                {
                    parts.Add($"{had} 条历史");
                }

                if (hadRuns > 0)
                {
                    parts.Add($"{hadRuns} 条执行记录");
                }

                cleared.Add($"{label}「{MaybeMask(oldTitle, conversation.SourceKey)}」{(parts.Count > 0 ? "清了 " + string.Join("、", parts) : "本来就空")}");
            }

            if (cleared.Count == 0)
            {
                await SendPlainAsync(conversation, "这个聊天还没有 agent 会话（发一条 //指令 会自动建一个，或 //new 新建）。");
                return;
            }

            await SendPlainAsync(conversation,
                $"已清空（{string.Join("、", cleared)}）—— 下一句从零开始，不会再带着上一轮的话题。");
            return;
        }

        if (payload.Length == 0)
        {
            await SendPlainAsync(conversation, HelpText(conversation.SourceKey));
            return;
        }

        // 图片：agent 任务以前只传正文，图片在那条消息里只剩一个「[图片]」占位 —— 两个后端
        // 都看不到图（号主 2026-09-19 报“给 agent 发图片识别不了”）。把直链与“服务器留档”
        // 一起写进任务正文，不动桥的报文格式（两边都吃纯文本）。
        payload += await BuildAgentImageNoteAsync(msg);

        // ── 这一条走哪边？（号主 2026-09-17：两个开关各自管一边，还能单条指定）──
        //   ① 命令里带 @ 目标：`//@server …` / `//@host …` / `//@ZHAOSPC …`（优先级最高）
        //   ② 否则看 settings.AgentTarget：auto = 外部在线就用外部，否则服务器；server / host / 设备名 = 指定
        //   ③ 选中的那边被开关关了 / 不在线 → 若还有另一边可用就用另一边，否则如实报错


        var route = ResolveRoute(want, bridge, named);
        var hostSwitch = route.HostSwitch;
        var serverSwitch = route.ServerSwitch;
        var hostOnline = route.HostOnline;
        var deviceDisabled = route.DeviceDisabled;
        var onlineDeviceName = route.DeviceName;
        var useHost = route.UseHost;
        var useServer = route.UseServer;

        // 两边都不可用、或指定的那边不在 → 说人话（不要静默改道）
        if (!useHost && !useServer)
        {
            var parts = new List<string>();
            if (!hostSwitch)
            {
                parts.Add("外部设备 agent：开关是关的");
            }
            else if (deviceDisabled)
            {
                parts.Add($"外部设备「{onlineDeviceName}」在面板里被关掉了");
            }
            else if (!hostOnline)
            {
                parts.Add(named is null
                    ? "外部设备：不在线"
                    : $"外部设备「{named}」不在线（在线：{(bridge!.BridgeNames.Count == 0 ? "没有" : string.Join("、", bridge.BridgeNames))}）");
            }

            if (!serverSwitch)
            {
                parts.Add("服务器 agent：开关是关的");
            }

            await SendPlainAsync(conversation, "这条没法跑：" + string.Join("；", parts) +
                (parts.Count == 0 ? "没有可用的 agent" : string.Empty) + "。面板里打上开关、或指定另一边试试（//@server / //@host）。");
            return;
        }

        if (useHost)
        {
            var hostSession = _agentSessions.EnsureCurrent(conversation.SourceKey, "host");
            var task = bridge!.NewTask(conversation.SourceKey, payload, hostSession.PiSessionId, named);
            task.SessionRef = hostSession;
            task.RunId = _agentSessions.StartRun(conversation.SourceKey, hostSession.Id, payload, named);   // 记一条“小会话”
            // 注意：这里**不再**拿指令当标题。标题改成“跑完后按上下文综结”，
            // 否则每发一条新命令就把会话改名成那条命令的前几个字，会话号就认不出来了。
            if (!bridge.TryEnqueue(task))
            {
                await SendPlainAsync(conversation, $"这个会话已经排了 {bridge.QueuedCount} 个任务，等跑完再发吧。");
                return;
            }

            await SendPlainAsync(conversation,
                bridge.Current is null
                    ? $"收到，去{(named ?? bridge.AnyBridge?.Name ?? "号主设备")}上跑一下（会话「{MaybeMask(hostSession.Name, conversation.SourceKey)}」）：{Shorten(payload, 40)}"
                    : "收到，排在后面 —— 做完我告诉你。");

            // 面板里给这台设备配的模型它自己没有 → 提前说一声（不然群里只会看到结果，不知道降级了）
            var liveModel = _settings.DeviceConfigFor(named ?? bridge.AnyBridge?.Name)?.Model;
            if (!string.IsNullOrWhiteSpace(liveModel) && bridge.DeviceModels(named).Length > 0 &&
                !bridge.DeviceModels(named).Contains(liveModel, StringComparer.OrdinalIgnoreCase))
            {
                await SendPlainAsync(conversation,
                    $"⚠️ 面板里给这台设备配的模型「{liveModel}」它没有（pi 只认 provider/model 这种写法）—— " +
                    "这次先用它的默认模型跑，能选的话在面板设备表里重选一个。");
            }

            return;
        }

        // 服务器内置 agent（与外部设备**互不影响**：它不占外部队列，两边可以同时跑）
        if (_serverAgentBusy.TryAdd(conversation.SourceKey, true))
        {
            // “接着上一句说”只有两种入口：面板开关，或本条指令写 //接着 …（默认**不**带上下文）
            var (wantsContinue, promptText) = StripContinuePrefix(payload);
            var useHistory = wantsContinue || _settings.AgentServerKeepContext;

            var serverSession = _agentSessions.EnsureCurrent(conversation.SourceKey, "server");
            var seededHistory = useHistory ? _agentSessions.History(conversation.SourceKey, serverSession.Id) : new List<(string, string)>();
            var serverRunId = _agentSessions.StartRun(conversation.SourceKey, serverSession.Id, promptText, null);
            EmitLog(useHistory
                ? $"[会话] 内置 agent 本轮带 {seededHistory.Count} 条历史（会话「{serverSession.Name}」，{(wantsContinue ? "//接着" : "面板开关开着")}）"
                : $"[会话] 内置 agent 本轮**不带**上文（每条指令单独对待）：{Shorten(promptText, 40)}");
            var task = new AgentTask
            {
                Id = $"s{DateTimeOffset.Now.ToUnixTimeMilliseconds()}",
                SourceKey = conversation.SourceKey,
                Prompt = promptText,
                Session = serverSession.PiSessionId,
                SessionRef = serverSession,
                History = seededHistory,
                UseHistory = useHistory,
                // 现场：不做这一步，模型知道“点赞”却不知道给谁点、在哪条消息上点
                QqHost = BuildQqHost(conversation, msg)
            };
            task.RunId = serverRunId;

            // 自备一个 CTS：//stop 要能把正在跑的那条 bash 也杀掉（不能只标个标记）
            var cts = new CancellationTokenSource();
            _serverAgentCurrent[conversation.SourceKey] = (task, cts);

            await SendPlainAsync(conversation, $"收到，我在服务器上跑一下（会话「{MaybeMask(serverSession.Name, conversation.SourceKey)}」）：{Shorten(payload, 40)}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _serverAgent.RunAsync(task, cts.Token);
                }
                finally
                {
                    _serverAgentBusy.TryRemove(conversation.SourceKey, out _);
                    if (_serverAgentCurrent.TryRemove(conversation.SourceKey, out var done) &&
                        ReferenceEquals(done.Task, task))
                    {
                        done.Cts.Dispose();
                    }

                    OnAgentFinished(task);
                }
            });
            return;
        }

        // 同一个会话的服务器任务串行（不同会话不受影响）
        await SendPlainAsync(conversation, "这个会话上一条 //指令还在跑，等它完事再发。");
    }

    /// <summary>
    /// 抽出命令开头的 <c>@目标</c>（`@server` / `@host` / `@服务器` / `@外部` / `@<设备名>`）。
    /// 为什么要这个：面板里改的是“对以后所有命令生效”的优先项，但号主常常只是**这一条**想指定另一台设备 ⋯（“有时候需要修改不同的外部设备接入”）。
    /// </summary>
    private static (string Target, string Payload) StripTargetPrefix(string payload)
    {
        var text = payload.TrimStart();
        if (!text.StartsWith('@'))
        {
            return (string.Empty, payload);
        }

        var space = text.IndexOf(' ');
        if (space <= 1)
        {
            return (string.Empty, payload);   // 只有 @ 没内容 → 当普通提示词
        }

        var head = text[1..space].Trim();
        if (head.Length == 0 || head.Length > 32)
        {
            return (string.Empty, payload);
        }

        return (head, text[(space + 1)..].TrimStart());
    }

    /// <summary>面板“试一条”用的：直接在容器里跑一次服务器内置 agent（不经过 QQ、不经过外部设备）。</summary>
    public async Task<AgentTask> RunServerAgentDirectAsync(string prompt, int timeoutSeconds)
    {
        var task = new AgentTask
        {
            Id = $"p{DateTimeOffset.Now.ToUnixTimeMilliseconds()}",
            SourceKey = "panel:test",
            Prompt = prompt,
            Session = "qqchat-panel",
            // 面板试跑：没有哪个群/哪个人，参数里的 sender/this 用不了（写死 QQ 号仍可用）
            QqHost = _gateway is null ? null : new SessionQqActionHost(_gateway, true, 0, 0, 0, _selfId)
        };

        if (_serverAgent is null)
        {
            task.Fail("服务器内置 agent 没开（面板里打上）");
            return task;
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 900)));
        await _serverAgent.RunAsync(task, cts.Token);
        return task;
    }

    /// <summary>
    /// 剥掉“接着上一句说”的前缀（<c>//接着 …</c> / <c>//继续 …</c> / <c>//+ …</c>）。
    /// 只有写了前缀（或面板开了开关）才把上文喂给模型 —— 号主 2026-09-18：
    /// “每次发送新指令都会把旧指令的内容发送回来，要把每条指令输出单独对待”。
    /// </summary>
    private static (bool Continue, string Prompt) StripContinuePrefix(string payload)
    {
        var text = (payload ?? string.Empty).TrimStart();
        foreach (var prefix in new[] { "接着", "继续", "继承" })
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = text[prefix.Length..].TrimStart(' ', '：', ':', '，', ',', '。', '、');
            // 光写“接着”（后面没说干什么）= 让模型看着上文自己接一句
            return (true, rest.Length == 0 ? "接着上文继续（上一件事接着做或接着说）" : rest);
        }

        return (false, payload);
    }

    /// <summary>把会话改回中性的自动名（<c>//reset</c> 用；不动手动改过名的会话）。</summary>
    private void RenameSessionQuietly(string sourceKey, string sessionId)
    {
        try
        {
            var list = _agentSessions.List(sourceKey);
            var index = list.FindIndex(s => s.Id == sessionId);
            var name = index >= 0 ? $"新会话 {index + 1}" : "新会话";
            _agentSessions.SetAutoTitle(sourceKey, sessionId, name);
        }
        catch (Exception ex)
        {
            EmitLog($"会话改名失败（无所谓，不影响清空）: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>丢给服务器 agent 的 QQ 动作现场</summary>
    private IQqActionHost? BuildQqHost(BotConversation conversation, QqChatMessage msg)
    {
        if (_gateway is null)
        {
            return null;
        }

        var (isGroup, targetId) = conversation.Target;
        return new SessionQqActionHost(_gateway, isGroup, targetId, msg.UserId, msg.MessageId, _selfId);
    }

    /// <summary>服务器内置 agent 正在跑的会话（单会话串行）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _serverAgentBusy = new();

    /// <summary>agent 会话（新建/切换/删除/历史）—— 外部与内置两个后端共用一套。 </summary>
    private readonly AgentSessionStore _agentSessions;

    /// <summary>面板用的：某个聊天的 agent 会话表（含当前标记与外层信息）。</summary>
    public JsonArray BuildAgentSessionsPayload(string sourceKey)
        => new(_agentSessions.List(sourceKey).Select(s => (JsonNode)BuildSessionNode(sourceKey, s)).ToArray());

    /// <summary>面板总览：所有有 agent 会话的聊天（群/好友）。</summary>
    public JsonObject BuildAllAgentSessionsPayload()
    {
        var chats = new JsonObject();
        var total = 0;
        foreach (var (key, sessions) in _agentSessions.AllChats())
        {
            var conv = Conversations.FirstOrDefault(c => c.SourceKey == key);
            var shown = conv is not null ? ChatLabel(conv) : (_settings.AgentMaskSensitive ? AgentMask.ChatLabel(key) : key);
            total += sessions.Count;
            chats[key] = new JsonObject
            {
                ["name"] = shown,
                ["nameRaw"] = conv?.Name ?? key,
                ["sessions"] = new JsonArray(sessions.Select(s => (JsonNode)BuildSessionNode(key, s)).ToArray())
            };
        }

        return new JsonObject
        {
            ["total"] = total,
            ["chatCount"] = chats.Count,
            ["chats"] = chats
        };
    }

    /// <summary>单个会话的面板节点（BuildAgentSessionsPayload 与总览共用）。</summary>
    private JsonObject BuildSessionNode(string sourceKey, AgentSessionStore.AgentSession s)
        => new()
        {
            ["id"] = s.Id,
            ["name"] = MaybeMask(s.Name, sourceKey),
            ["nameRaw"] = s.Name,
            ["backend"] = s.Backend,
            ["device"] = s.Device,
            ["turns"] = s.Turns,
            ["piSession"] = s.PiSessionId,
            ["createdAt"] = s.CreatedAt.ToString("O"),
            ["updatedAt"] = s.UpdatedAt.ToString("O"),
            ["current"] = _agentSessions.IsCurrent(sourceKey, s),
            ["autoNamed"] = s.AutoNamed,
            ["piOwned"] = s.PiOwned,
            ["historyChars"] = s.History.Sum(h => h.Text.Length),
            ["runs"] = new JsonArray(s.Runs.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id,
                ["at"] = r.At.ToString("O"),
                ["prompt"] = r.Prompt,
                ["ok"] = r.Ok,
                ["durationMs"] = r.DurationMs,
                ["toolCalls"] = r.ToolCalls,
                ["result"] = r.Result,
                ["device"] = r.Device,
                ["piSession"] = r.PiSession
            }).ToArray())
        };

    /// <summary>面板/其它入口：新建 agent 会话。</summary>
    public AgentSessionStore.AgentSession CreateAgentSession(string sourceKey, string backend, string? name)
        => _agentSessions.Create(sourceKey, backend == "server" ? "server" : "host", name);

    /// <summary>面板/其它入口：切换会话。</summary>
    public bool UseAgentSession(string sourceKey, string idOrName)
        => _agentSessions.Use(sourceKey, idOrName, out _);

    /// <summary>面板/其它入口：删除会话（外部会话同时让设备删 pi 那份）。</summary>
    public bool DeleteAgentSession(string sourceKey, string idOrName)
    {
        var deleted = _agentSessions.Delete(sourceKey, idOrName);
        if (deleted is null)
        {
            return false;
        }

        if (deleted.Backend != "server" && deleted.PiOwned && deleted.PiSessionId.Length > 0 && _agentBridge is not null)
        {
            _ = _agentBridge.ForgetSessionAsync(deleted.PiSessionId);
        }

        return true;
    }

    /// <summary>面板/其它入口：清空会话历史。</summary>
    /// <summary>面板/其它入口：把设备上 pi 里的会话接过来用（新建一个指向它的会话）。</summary>
    public AgentSessionStore.AgentSession? ImportPiSession(string sourceKey, string piSessionId, string? name)
    {
        if (string.IsNullOrWhiteSpace(piSessionId))
        {
            return null;
        }

        // 从设备上报的 pi 会话里找标题（有就用它当名字）
        var hit = _agentSessions.LastPiSessions()
            .FirstOrDefault(x => x["id"]?.GetValue<string>() == piSessionId.Trim());
        var title = name is { Length: > 0 } ? name : hit?["title"]?.GetValue<string>();
        return _agentSessions.Create(sourceKey, "host",
            string.IsNullOrWhiteSpace(title) ? null : AgentSessionStore.AutoTitle(title),
            null, piSessionId.Trim(), piOwned: false);
    }

    /// <summary>面板/其它入口：给会话改名（改过就不再被自动标题覆盖）。</summary>
    public bool RenameAgentSession(string sourceKey, string idOrName, string title)
        => _agentSessions.Rename(sourceKey, idOrName, title);

    public bool ResetAgentSession(string sourceKey, string idOrName)
    {
        var session = _agentSessions.Find(sourceKey, idOrName);
        var reset = session is null ? null : _agentSessions.Reset(sourceKey, session.Id);
        if (session is null || reset is null)
        {
            return false;
        }

        if (session.Backend != "server" && session.PiOwned && session.PiSessionId.Length > 0 && _agentBridge is not null)
        {
            _ = _agentBridge.ForgetSessionAsync(session.PiSessionId);
        }

        return true;
    }

    /// <summary>这条命令会走哪个后端（供会话管理用：//new 就在这个后端里开）。</summary>
    private string WillUseBackend(string want, AgentBridgeServer? bridge, string? named)
        => ResolveRoute(want, bridge, named).Backend;

    /// <summary>
    /// 这一条命令**实际**会走哪边 —— 分发、`//status`、`//reset` 共用这一个判断。
    ///
    /// 为什么必须同源：以前 `//status`/`//reset` 只看到“写了 @设备名”就当成外部设备，
    /// 而实际分发会因为那台设备不在线、开关关了等原因改走服务器 ——
    /// 于是 `//@某台不在线的设备 看下日志` 跑在服务器上，号主 `//reset` 却去清了**那台空的外部会话**，
    /// 被污染的历史一直没清掉（2026-09-18 实测：reset 两次都没用）。
    /// </summary>
    private sealed record AgentRoute(
        string Backend,          // host / server / none
        bool UseHost,
        bool UseServer,
        bool HostSwitch,
        bool ServerSwitch,
        bool HostOnline,
        bool DeviceDisabled,
        string? DeviceName);

    private AgentRoute ResolveRoute(string want, AgentBridgeServer? bridge, string? named)
    {
        var hostSwitch = _settings.EnableHostAgent;
        var serverSwitch = _settings.EnableServerAgent;
        var hostOnline = bridge is not null && bridge.IsDeviceOnline(named);

        // 面板里把某台设备关掉了 → 就当它不可用（并告诉号主是开关关的，不是没连上）
        var deviceName = named ?? bridge?.AnyBridge?.Name;
        var deviceDisabled = deviceName is not null && !_settings.IsDeviceEnabled(deviceName);

        var hostOnly = named is not null ||
                       want.Equals("host", StringComparison.OrdinalIgnoreCase) ||
                       want.Equals("外部", StringComparison.OrdinalIgnoreCase);
        var serverOnly = want.Equals("server", StringComparison.OrdinalIgnoreCase) ||
                         want.Equals("服务器", StringComparison.OrdinalIgnoreCase);

        var canHost = hostSwitch && !deviceDisabled && bridge is not null && hostOnline;
        var canServer = serverSwitch && _serverAgent is not null;

        var useHost = canHost && (hostOnly || (!serverOnly &&
            (named is not null || want.Equals("auto", StringComparison.OrdinalIgnoreCase) || want.Length == 0)));
        var useServer = !useHost && canServer;

        return new AgentRoute(useHost ? "host" : useServer ? "server" : "none",
            useHost, useServer, hostSwitch, serverSwitch, hostOnline, deviceDisabled, deviceName);
    }

    /// <summary>把“名字”或“序号”解析成会话 id（//sessions 里给的序号可用）。</summary>
    private string ResolveSessionRef(string sourceKey, string reference)
    {
        var list = _agentSessions.List(sourceKey);
        if (int.TryParse(reference.Trim(), out var index) && index >= 1 && index <= list.Count)
        {
            return list[index - 1].Id;
        }

        return reference.Trim();
    }

    /// <summary>这个聊天里出现过的昵称（脱敏时把它们换成 群友A/B…）。</summary>
    private List<string> KnownNames(string sourceKey)
    {
        var names = new List<string>();
        var conversation = Conversations.FirstOrDefault(c => c.SourceKey == sourceKey);
        if (conversation is not null)
        {
            names.AddRange(conversation.Messages
                .Where(m => m.SenderName is { Length: >= 2 })
                .Select(m => m.SenderName!));
        }

        return names.Distinct().ToList();
    }

    /// <summary>按开关决定要不要遮盖文本（开关关掉就原样返回）。</summary>
    private string MaybeMask(string text, string? sourceKey = null)
        => _settings.AgentMaskSensitive
            ? AgentMask.Text(text, sourceKey is null ? null : KnownNames(sourceKey))
            : text;

    /// <summary>按开关决定聊天的显示名：开=「群聊 940***75」，关=真名。</summary>
    private string ChatLabel(BotConversation conversation)
        => _settings.AgentMaskSensitive
            ? AgentMask.ChatLabel(conversation.SourceKey)
            : conversation.Name;

    /// <summary>//help：把所有命令列出来（号主：“忘记一些命令可以添加一个 help 命令”）。</summary>
    private string HelpText(string sourceKey)
    {
        var list = _agentSessions.List(sourceKey);
        var current = list.FirstOrDefault(s => _agentSessions.IsCurrent(sourceKey, s));
        return
            "本机/服务器 Agent 命令：\n" +
            "//<要做的事>         把这句话交给 agent 去干（例：//看下 E:/bot 里最新的报错）\n" +
            "//@server <事>       这一条强制走服务器内置 agent\n" +
            "//@host <事>        这一条强制走外部设备（//@设备名 指定哪一台）\n" +
            "//sessions         列出这个聊天的会话（几个、叫什么、多少轮、哪个是当前）\n" +
            "//sessions all     列出**所有聊天**的会话总数与标题\n" +
            "//new [名字]       开一个新会话并切过去（不带名字就用第一句话自动起标题）\n" +
            "//use 序号|名字     切到某个会话\n" +
            "//rename 名字      给当前会话改名字\n" +
            "//reset            清空当前会话（历史与外部那边的记录一起清）\n" +
            "//del 序号|名字     删掉某个会话\n" +
            "//stop             停掉正在跑的任务\n" +
            "//status           看两个后端的开关/在线情况/当前会走哪边\n" +
            "//help             看这份说明\n" +
            $"（当前会话：{(current is null ? "还没有，发一句 //指令 会自动建" : $"「{MaybeMask(current.Name, sourceKey)}」 {current.Turns} 轮")}）";
    }

    /// <summary>//sessions all：所有聊天的会话总数与标题。</summary>
    private string DescribeAllSessions(AgentBridgeServer? bridge)
    {
        var chats = _agentSessions.AllChats();
        var total = chats.Sum(c => c.Sessions.Count);
        if (total == 0)
        {
            return "现在一个 agent 会话都没有（发一句 //指令 就有了）。";
        }

        var lines = new List<string> { $"全部 agent 会话：{chats.Count} 个聊天 / 共 {total} 个会话" };
        var chatIndex = 0;
        foreach (var (key, sessions) in chats.Take(10))
        {
            chatIndex++;
            var conv = Conversations.FirstOrDefault(c => c.SourceKey == key);
            var name = conv is not null ? ChatLabel(conv) : (_settings.AgentMaskSensitive ? AgentMask.ChatLabel(key) : key);
            var titles = sessions.Take(6).Select((s, i) =>
                $"{i + 1}) {(s.Backend == "server" ? "服务器" : (s.Device ?? "外部"))}·{MaybeMask(s.Name, key)}({s.Turns}轮){(_agentSessions.IsCurrent(key, s) ? "←" : string.Empty)}");
            var more = sessions.Count > 6 ? $" 等 {sessions.Count} 个" : string.Empty;
            lines.Add($"{chatIndex}. {name}（{sessions.Count} 个）：{string.Join("、", titles)}{more}");
        }

        if (chats.Count > 10)
        {
            lines.Add($"（还有 {chats.Count - 10} 个聊天的没列出来）");
        }

        lines.Add("看某个聊天的完整列表：在那个聊天里发 //sessions。");
        return string.Join("\n", lines);
    }

    /// <summary>//sessions 的展示文本。</summary>
    private string DescribeSessions(string sourceKey, AgentBridgeServer? bridge)
    {
        var list = _agentSessions.List(sourceKey);
        if (list.Count == 0)
        {
            return "这个会话还没有 agent 会话（发第一条 //指令 时会自动建一个）。\n" +
                   "用法：//new [名字] 新建、//use <名字|序号> 切换、//del <名字|序号> 删除、//reset 清空当前。";
        }

        var primary = _agentSessions.FindCurrent(sourceKey, ResolveRoute(_settings.AgentTarget, bridge, null).Backend);
        var lines = new List<string> { $"agent 会话（共 {list.Count} 个，← 是下一句指令会用的；改它们用 //use 序号）：" };
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            var where = s.Backend == "server" ? "服务器内置" : $"外部 {s.Device ?? bridge?.AnyBridge?.Name ?? "设备"}";
            var ago = DateTimeOffset.Now - s.UpdatedAt;
            var when = ago.TotalMinutes < 1 ? "刚刚"
                : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} 分钟前"
                : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} 小时前"
                : $"{(int)ago.TotalDays} 天前";
            // “当前”要标得让人一眼分清：只有主那个（下一句真会用的）带 ←，
            // 另一个后端的当前会话写成「另一路的当前」—— 不然 //rename/`//reset` 改到哪个全靠猜。
            var mark = primary is not null && s.Id == primary.Id
                ? " ←"
                : _agentSessions.IsCurrent(sourceKey, s) ? "（另一路的当前）" : string.Empty;
            lines.Add($"{i + 1}. {MaybeMask(s.Name, sourceKey)} [{where}] {s.Turns} 轮 · {when}{mark}");
        }

        lines.Add("用法：//new [名字] 新建并切换、//use 序号|名字 切换、//rename 名字 改名（改标 ← 那个）、//del 序号|名字 删除、//reset 清空标 ← 那个；//help 看全部命令。");
        return string.Join("\n", lines);
    }

    /// <summary>正在跑的服务器 agent 任务（//stop 要能把它连命令一起杀掉）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (AgentTask Task, CancellationTokenSource Cts)> _serverAgentCurrent = new();

    private bool ShouldReplyDenied(string sourceKey)
    {
        var now = DateTimeOffset.Now;
        if (_agentDeniedAt.TryGetValue(sourceKey, out var last) && now - last < TimeSpan.FromSeconds(60))
        {
            return false;
        }

        _agentDeniedAt[sourceKey] = now;
        return true;
    }

    /// <summary>发一条纯文本（agent 回话专用：不走人设、不分句、不受群冷却限制）。</summary>
    private async Task SendPlainAsync(BotConversation conversation, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var (isGroup, targetId) = conversation.Target;
        var index = 0;
        foreach (var segment in SplitForChat(text, Math.Clamp(_settings.AgentReplyMaxChars, 200, 3000)))
        {
            index++;
            var result = await _source.SendTextAsync(isGroup, targetId, segment);
            EmitLog($"agent 回话 → {(isGroup ? "群" : "私聊")}{targetId}（第 {index} 段，{segment.Length} 字，{(result.Ok ? "已发出" : "发送失败")}）: {Shorten(segment.Replace('\n', ' '), 60)}");
            RememberOwnMessage(result, segment);

            // 记进上下文：下一轮人设路线能看到“本机 agent 刚做了什么”，不会把它当外人说的话
            var appended = new ChatMessage
            {
                Role = MessageRole.Self,
                Text = segment,
                Timestamp = DateTimeOffset.Now,
                QqMessageId = result.MessageId > 0 ? result.MessageId : null
            };
            conversation.Append(appended);
            MessageAdded?.Invoke(conversation.SourceKey, appended);
        }

        Touch(conversation);
        Save();
    }

    /// <summary>把长文本切成能发出去的消息（QQ 单条太长会被吞；按行/句尽量切得好看）。</summary>
    private static IEnumerable<string> SplitForChat(string text, int maxChars)
    {
        text = text.Replace("\r\n", "\n").Trim();
        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        var rest = text;
        var index = 0;
        while (rest.Length > 0 && index < 8)     // 最多 8 条，剩下用省略号收尾
        {
            index++;
            if (rest.Length <= maxChars)
            {
                yield return rest;
                yield break;
            }

            var cut = rest.LastIndexOf('\n', maxChars - 1);
            if (cut < maxChars / 3)
            {
                cut = rest.LastIndexOf('。', maxChars - 1);
            }

            if (cut < maxChars / 3)
            {
                cut = maxChars - 1;
            }

            yield return rest[..(cut + 1)].TrimEnd();
            rest = rest[(cut + 1)..].TrimStart();
        }

        if (rest.Length > 0)
        {
            yield return $"（输出太长，后面省略了 {rest.Length} 字）";
        }
    }

    /// <summary>agent 任务的进度/结果回群。供 AgentBridgeServer 的事件调。</summary>
    private async void OnAgentProgress(AgentTask task)
    {
        try
        {
            var every = Math.Clamp(_settings.AgentProgressSeconds, 0, 3600);
            if (every <= 0 || !ConversationsByKey(task.SourceKey, out var conversation))
            {
                return;
            }

            var now = DateTimeOffset.Now;
            if (_agentProgressAt.TryGetValue(task.SourceKey, out var last) && now - last < TimeSpan.FromSeconds(every))
            {
                return;
            }

            _agentProgressAt[task.SourceKey] = now;
            var elapsed = now - task.StartedAt;
            var note = string.IsNullOrWhiteSpace(task.LastNote) ? string.Empty : $"（{MaybeMask(task.LastNote, task.SourceKey)}）";
            var place = string.IsNullOrWhiteSpace(task.DeviceName) ? "服务器" : task.DeviceName!;
            await SendPlainAsync(conversation, $"⏳ 还在{place}上跑…已 {elapsed.TotalSeconds:F0} 秒{note}");
        }
        catch (Exception ex)
        {
            EmitLog("agent 进度回话失败: " + ex.Message);
        }
    }

    private async void OnAgentFinished(AgentTask task)
    {
        try
        {
            // 服务器内置后端：把这一轮的对话存回会话（下一轮同一会话能接上）
            if (task.Conversation is { Count: > 0 } && task.SessionRef is { } session &&
                session.Backend == "server")
            {
                _agentSessions.AppendTurn(task.SourceKey, session.Id, task.Conversation);
                var saved = _agentSessions.History(task.SourceKey, session.Id);
                EmitLog($"[会话] 「{session.Name}」记下本轮对话（共 {saved.Count} 条 / {saved.Sum(x => x.Text.Length)} 字）——下一句接着聊");
            }

            // 不论哪个后端，都把这次执行的结果写进“小会话”记录
            if (task.SessionRef is { } runSession && task.RunId is { Length: > 0 } runId)
            {
                _agentSessions.FinishRun(task.SourceKey, runSession.Id, runId, task.Ok, task.DurationMs,
                    task.ToolCalls, task.Ok ? (task.Text ?? string.Empty) : (task.Error ?? "失败"));
            }

            if (!ConversationsByKey(task.SourceKey, out var conversation))
            {
                return;
            }

            var seconds = task.DurationMs > 0 ? task.DurationMs / 1000.0 : (DateTimeOffset.Now - task.StartedAt).TotalSeconds;
            if (task.Ok)
            {
                EmitLog($"agent 完成（{seconds:F0}s，{task.ToolCalls} 次工具调用）: {Shorten(task.Text ?? string.Empty, 80)}");

                // agent 会翻日志/数据，结论里很可能带 QQ 号或群友昵称 —— 回群前过一遍脱敏开关
                //（面板里的“列出会话时脱敏”默认是开的；关掉就原样发，号主自己的选择）。
                await SendPlainAsync(conversation, MaybeMask(task.Text ?? string.Empty, task.SourceKey));
            }
            else
            {
                EmitLog($"agent 失败（{seconds:F0}s）: {task.Error}");
                await SendPlainAsync(conversation, $"❌ 本机那边报错（{seconds:F0}s）：{Shorten(task.Error ?? "未知错误", 300)}");
            }

            // 跑完再综结标题：拿最近的轮次（含刚刚这轮）给模型，综结出一个能认出“这个会话在干什么”的标题。
            // 放在回话之后（主人先看到结果），失败也不影响任何东西；手动改过名的会话不动。
            await SummarizeSessionTitleAsync(task);
        }
        catch (Exception ex)
        {
            EmitLog("agent 结果回话失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 一轮跑完后按上下文综结会话标题（只改自动名的会话）。
    /// 号主要求：“不要每发一条指令就重新命名，执行完按上下文内容综结标题，而不是简单复用”。
    /// </summary>
    private async Task SummarizeSessionTitleAsync(AgentTask task)
    {
        try
        {
            if (task.SessionRef is not { } session || _brain is null)
            {
                return;
            }

            // 手动改过名的会话不动（//rename 的意图优先）
            if (_agentSessions.Find(task.SourceKey, session.Id) is not { AutoNamed: true } current)
            {
                return;
            }

            var digest = _agentSessions.SessionDigest(task.SourceKey, session.Id);
            if (string.IsNullOrWhiteSpace(digest))
            {
                return;
            }

            var title = await _brain.SummarizeSessionTitleAsync(digest, current.Name, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            if (_agentSessions.SetAutoTitle(task.SourceKey, session.Id, title))
            {
                EmitLog($"[会话] 按上下文综结标题：「{current.Name}」→「{title}」");
            }
        }
        catch (Exception ex)
        {
            EmitLog("[会话] 综结标题失败（不影响任务）: " + ex.Message);
        }
    }

    /// <summary>按 sourceKey 找会话（agent 结果回来时只能用 key）。</summary>
    private bool ConversationsByKey(string sourceKey, out BotConversation conversation)
    {
        conversation = Conversations.FirstOrDefault(c => c.SourceKey == sourceKey)!;
        return conversation is not null;
    }

    /// <summary>
    /// 后台真去搜一次，把结果留给下一轮（并在允许时叫醒模型）。
    /// 为什么要冷却：搜索是一次真实的模型调用 + 几秒等待；群里连问几个问题就排队了。
    /// </summary>
    private void QueueWebSearchAsync(BotConversation conversation, string query)
    {
        var key = conversation.SourceKey;
        var now = DateTimeOffset.Now;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.WebSearchCooldownSeconds));
        if (cooldown > TimeSpan.Zero && _lastSearch.TryGetValue(key, out var last) && now - last < cooldown)
        {
            EmitLog($"[Search] 这次不搜（同会话 {cooldown.TotalSeconds:F0}s 内刚搜过）：{query}");
            return;
        }

        _lastSearch[key] = now;
        EmitLog($"[Search] 模型想搜「{query}」");
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _search!.SearchAsync(query, CancellationToken.None);
                var note = result.Describe();
                _searchNotes.AddOrUpdate(key, note, (_, old) => old + "\n\n" + note);

                if (!result.HasContent)
                {
                    EmitLog($"[Search] 没搜到「{query}」：{result.Error}");
                }

                // 搜到了就给它一次开口机会（没搜到也给 —— 让它能如实说“没查到”）
                RequestReply(conversation, null);
            }
            catch (Exception ex)
            {
                EmitLog($"[Search] 搜「{query}」失败: {ex.Message}");
            }
        });
    }

    /// <summary>后台读一个网页的正文（模型填 read 时），留给下一轮。</summary>
    private void QueuePageReadAsync(BotConversation conversation, string url)
    {
        var key = conversation.SourceKey;
        EmitLog($"[Search] 模型想读页面 {Shorten(url, 80)}");
        _ = Task.Run(async () =>
        {
            try
            {
                var (text, error) = await _search!.ReadPageAsync(url, CancellationToken.None);
                var note = text is null
                    ? $"读页面「{url}」失败：{error}（如实说没读到就行，别猜页面里写了什么。）"
                    : $"页面 {url} 的正文（已抽取）：\n{text}";

                _searchNotes.AddOrUpdate(key, note, (_, old) => old + "\n\n" + note);
                EmitLog(text is null ? $"[Search] 读页面失败：{error}" : $"[Search] 已读到页面正文（{text.Length} 字）");

                RequestReply(conversation, null);
            }
            catch (Exception ex)
            {
                EmitLog($"[Search] 读页面失败: {ex.Message}");
            }
        });
    }

    /// <summary>面板自测：真跑一次搜索，把结果（或失败原因）原样给面板看。</summary>
    public async Task<WebSearchResult> TestSearchAsync(string query, CancellationToken ct)
        => _search is null
            ? new WebSearchResult(query, null, Array.Empty<WebSearchHit>(), "无", "搜索服务还没初始化")
            : await _search.SearchAsync(query, ct);

    /// <summary>面板自测：真读一个页面。</summary>
    public async Task<(string? Text, string? Error)> TestReadPageAsync(string url, CancellationToken ct)
        => _search is null ? (null, "搜索服务还没初始化") : await _search.ReadPageAsync(url, ct);

    /// <summary>最近一次“面板自测听歌”的歌名（给 /api/music/test 回报用）。</summary>
    public string? LastMusicTestHeader { get; private set; }

    /// <summary>语音（TTS）客户端（面板试听用）。</summary>
    public VoiceService? Voice => _voice;

    /// <summary>
    /// 面板自测：真合成一句语音（走 /speak），把 wav 字节还给面板自己播。
    /// 只合成、不发群 —— 面板里重点验证的是“TTS 服务通不通、音色/语速对不对”。
    /// </summary>
    public async Task<(byte[]? Data, string? Error)> TestVoiceAsync(
        string text,
        string? voiceOverride,
        int? speedOverride,
        CancellationToken ct)
    {
        if (_voice is null)
        {
            return (null, "语音服务还没初始化");
        }

        var (data, error) = await _voice.SynthesizeAsync(text, voiceOverride, speedOverride, ct);
        EmitLog(data is null
            ? $"[Voice] 面板试听失败：{error}"
            : $"[Voice] 面板试听成功（{text.Length} 字 → {data.Length / 1024} KB wav）");
        return (data, error);
    }

    /// <summary>
    /// 面板自测：把“听音乐”整套链路跑一遍（搜歌 → 歌词 → 低码率音源 → 波形分析）。
    /// 返回给模型看的“事实描述”（拿不到就返回 null）。
    /// 本方法只在面板自测时用，不影响群里的正常流程。
    /// </summary>
    public async Task<string?> TestMusicAsync(string song, CancellationToken ct)
    {
        LastMusicTestHeader = null;
        if (_music is null)
        {
            return null;
        }

        var note = await _music.DescribeByNameAsync(song, "面板自测", ct);
        LastMusicTestHeader = song;
        EmitLog(note is null
            ? $"[Music] 面板自测「{song}」：没搜到或没听到"
            : $"[Music] 面板自测「{song}」完成");
        return note;
    }

    /// <summary>
    /// 听音乐：拿歌词 + 低码率音频做波形分析，把实测到的事实留给下一轮回复。
    /// 分析完成后单独触发一次发言机会 —— 这样模型是“听完再说”，而不是先瞎猜一遍再补课。
    /// </summary>
    private async Task HandleMusicAsync(BotConversation conversation, string sender, List<MusicShare> shares)
    {
        if (_music is null)
        {
            return;
        }

        var heard = false;
        foreach (var share in shares)
        {
            try
            {
                var note = await _music.DescribeAsync(share, sender, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(note))
                {
                    continue;
                }

                // 一次发好几首时合并，别让后一首盖掉前一首
                _musicNotes.AddOrUpdate(conversation.SourceKey, note!, (_, old) => old + "\n\n" + note);
                heard = true;
                EmitLog($"[Music] 已听过：{share.Describe()}");
            }
            catch (Exception ex)
            {
                EmitLog($"[Music] 处理失败（{share.Describe()}）: {ex.Message}");
            }
        }

        if (heard)
        {
            RequestReply(conversation, null); // 没有触发消息 → 不引用（沿用 replyTo 那套规则）
        }
    }

    // ══════════ 戳一戳 ══════════

    private void OnPoked(QqPokeEvent poke)
    {
        try
        {
            HandlePoke(poke);
        }
        catch (Exception ex)
        {
            EmitLog("处理戳一戳事件异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 戳一戳怎么处理：
    ///   • 戳了机器人 → 记进上下文（模型才看得到），并触发一次回复；同一个人连着戳时冷却内只回一次。
    ///   • 别人互戳 → 只记进上下文，**不**主动插话（群里互相戳得很多，每条都回就是刷屏）。
    /// 模型回答时可选地在 JSON 里给 poke 字段（对方 QQ 号）戳回去，安全阀在 GenerateReplyAsync 里校验。
    /// </summary>
    private void HandlePoke(QqPokeEvent poke)
    {
        if (!_settings.EnablePoke)
        {
            return;
        }

        // 自己戳的（协议端可能回显）不管
        if (_selfId != 0 && poke.UserId == _selfId)
        {
            return;
        }

        var sourceId = poke.IsGroup ? poke.GroupId : poke.UserId;
        if (!IsSourceAllowed(poke.IsGroup, sourceId))
        {
            LogThrottled("poke-ignore:" + sourceId,
                $"忽略戳一戳（不在白名单）: {(poke.IsGroup ? "群 " + poke.GroupId : "私聊 " + poke.UserId)}");
            return;
        }

        var conversation = GetOrCreateConversation(new QqChatMessage(
            0, poke.IsGroup, poke.UserId, poke.GroupId, string.Empty, string.Empty, poke.Time, false));

        // 名字尽量从历史里找（notice 事件本身不带昵称/群名片）
        var pokerName = ResolveDisplayName(conversation, poke.UserId);
        var text = poke.IsSelfPoked
            ? $"（戳一戳）{pokerName} 戳了你一下"
            : $"（戳一戳）{pokerName} 戳了 {ResolveDisplayName(conversation, poke.TargetId)} 一下";

        var appended = new ChatMessage
        {
            Role = MessageRole.Peer,
            SenderName = pokerName,
            SenderId = poke.UserId,
            Text = text,
            Timestamp = poke.Time,
            QqMessageId = 0
        };
        conversation.Append(appended);
        Touch(conversation);
        MessageAdded?.Invoke(conversation.SourceKey, appended);
        Save();

        EmitLog($"收到戳一戳：{(poke.IsGroup ? $"群{poke.GroupId}" : "私聊")} {pokerName}({poke.UserId}) → " +
                $"{(poke.IsSelfPoked ? "机器人" : ResolveDisplayName(conversation, poke.TargetId))}({poke.TargetId})");

        if (!poke.IsSelfPoked)
        {
            return; // 别人互戳只进上下文
        }

        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.PokeCooldownSeconds));
        var now = DateTimeOffset.Now;
        if (cooldown > TimeSpan.Zero &&
            _lastPoke.TryGetValue(conversation.SourceKey, out var last) &&
            last.PokerId == poke.UserId &&
            now - last.At < cooldown)
        {
            EmitLog($"同一个人的连续戳 → 这次不回应（{cooldown.TotalSeconds - (now - last.At).TotalSeconds:F0}s 后放行）: {conversation.Name}");
            return;
        }

        _lastPoke[conversation.SourceKey] = (poke.UserId, now);
        // 心情的客观来源：被戳的次数（越频繁越烦，也会随时间自己消）
        _mood.RecordPoke(now);

        // 戳一戳没有消息 id，引用目标交给模型自己用 replyTo 指认
        RequestReply(conversation, null);
    }

    /// <summary>最近一次“戳了机器人”的人（用来校验模型想戳回去的号码是否真的存在）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long PokerId, DateTimeOffset At)> _lastPoke = new();

    /// <summary>机器人当前心情：被戳次数（客观）+ 模型自己写的一句心情（主观）。</summary>
    private readonly MoodStore _mood = new();

    /// <summary>每个会话最近一次“按歌名去听”的时间（冷却，防同一话题反复搜歌）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastListen = new();

    /// <summary>每个会话正在跑的链接预览（回复前短暂等一下：快站点能当轮就用上）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _linkTasks = new();

    /// <summary>听音乐专用的 HttpClient（下载音频可能几 MB，超时给宽松点）。</summary>
    private readonly HttpClient _musicHttp = new() { Timeout = TimeSpan.FromSeconds(45) };

    /// <summary>语音（TTS）专用 HttpClient：合成一句要几秒（Piper 串行推理），超时给 30 秒。</summary>
    private readonly HttpClient _voiceHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>语音（TTS）客户端：拼 /speak 地址、面板试听时真取 wav。Start() 里组装。</summary>
    private VoiceService? _voice;

    /// <summary>每个会话最近一次发语音的时间（频率门：语音是“稀罕事”，不能每句都发）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastVoice = new();

    /// <summary>联网搜索专用 HttpClient：检索要等上游模型回话（含思考），超时给宽松点。</summary>
    private readonly HttpClient _netHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>联网搜索：模型自带搜索（Gemini grounding）+ 可插拔搜索源兑底。Start() 里组装。</summary>
    private WebSearchService? _search;

    /// <summary>每个会话最近一次“上网查”的时间（冷却：搜索要花模型调用与几秒时间，不能反复搜）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastSearch = new();

    /// <summary>每个会话刚查到的资料，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _searchNotes = new();

    /// <summary>
    /// 把“这一轮没说出口的资料”放回待办：搜索 = 一次真实模型调用 + 几秒等待，
    /// 模型选择沉默/说了句跟资料无关的话/请求失败时都不能白白浪费（下一轮还能说）。
    /// </summary>
    private void KeepSearchNotes(BotConversation conversation, string? searchText, string why)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return;
        }

        _searchNotes.AddOrUpdate(conversation.SourceKey, searchText, (_, old) => old + "\n\n" + searchText);
        EmitLog($"[Search] 查到的资料这次没说出去（{why}）→ 留着下一轮说");
    }

    /// <summary>同会话两次联网搜索的最小间隔（秒）。</summary>
    /// <summary>听音乐：识别到的分享 → 网易云歌词 + 低码率音频 → 波形分析。Start() 里组装。</summary>
    private MusicService? _music;

    /// <summary>链接预览：群里发链接时真去打开一下，取标题/摘要。Start() 里组装。</summary>
    private LinkPreviewer? _links;

    /// <summary>每个会话最近一次“链接里写了啥”的描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _linkNotes = new();

    /// <summary>每个会话最近一次“听过的歌”的事实描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _musicNotes = new();

    /// <summary>最近一次机器人主动戳人（频率门：一次只戳一个，不参与互戳拉锯）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long TargetId, DateTimeOffset At)> _lastPokeSent = new();

    /// <summary>每个会话最近一次“有人撤回消息”的时间（冷却：连着撤几条时不要每条都评论）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastRecallAt = new();

    /// <summary>每个会话最近一次撤回事件的描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _recallNotes = new();

    /// <summary>同会话两次“评论撤回”的最小间隔（秒）。</summary>
    private const int RecallCommentCooldownSeconds = 90;

    /// <summary>
    /// 有人撤回了一条消息。
    ///
    /// 为什么不能不管：撤回后群友已经看不到那条了，但机器人手里还有 ——
    /// 不管的话它下一轮会去接一句“群里已经不存在的消息”，或者把撤回的内容
    /// 当成公共信息接着聊（对方会觉得“我明明擦掉了”）。
    /// 做三件事：
    ///   ① 在上下文里把那条标成 <c>[已撤回] 原内容</c>（内容保留，但一眼能看出被收回去了）；
    ///   ② 给模型一次开口的机会（“撤回了啥”是人类最常见的反应），带冷却；
    ///   ③ 提示词里明确：“可以记得，但不要引用/复述/当众开玩笑”。
    /// </summary>
    private void OnMessageRecalled(QqRecallEvent recall)
    {
        try
        {
            // 撤回事件不带昵称，先用它给的身份把会话找到（没有就新建，与戳一戳同一套）
            var conversation = GetOrCreateConversation(new QqChatMessage(
                0, recall.IsGroup, recall.UserId, recall.GroupId, string.Empty, string.Empty, recall.Time, false));
            if (conversation is null)
            {
                return;
            }

            var key = conversation.SourceKey;
            var target = conversation.Messages.FirstOrDefault(m => m.QqMessageId == recall.MessageId);
            if (target is null)
            {
                // 常见于：那条消息已经被滚动窗口/归档挤掉了 —— 没什么要改的，也不值得评论
                EmitLog($"[Recall] {conversation.Name}：有一条消息被撤回（id={recall.MessageId}），但它不在当前上下文里");
                return;
            }

            if (target.Recalled)
            {
                return; // 重复事件：已经标过了，也不再评论
            }

            target.Recalled = true;
            Save();

            var sender = target.SenderName ?? ResolveDisplayName(conversation, recall.UserId);            var byOther = recall.OperatorId > 0 && recall.OperatorId != recall.UserId
                ? $"（由 {ResolveDisplayName(conversation, recall.OperatorId)} 撤回）"
                : string.Empty;
            EmitLog($"[Recall] {conversation.Name}：{sender} 撤回了一条消息{byOther} —— 原内容（已标进上下文）：{Shorten(target.Text, 40)}");

            var now = DateTimeOffset.Now;
            if (_lastRecallAt.TryGetValue(key, out var last) &&
                now - last < TimeSpan.FromSeconds(RecallCommentCooldownSeconds))
            {
                EmitLog($"[Recall] 这次不评论（同会话 {RecallCommentCooldownSeconds}s 内已经评论过一次）");
                return;
            }

            _lastRecallAt[key] = now;

            // 手误更正：撤回后同一个人又发了新消息（实测：把“固定bpc”改成“固定npc”）——
            // 这种时候去点评“撤回了啥”很尴尬（群里实测被怼过）。人类的做法是当没看见。
            var corrected = conversation.Messages.Any(m =>
                m.Role == MessageRole.Peer &&
                m.SenderId == target.SenderId &&
                m.Seq > target.Seq &&
                !m.Recalled);
            if (corrected)
            {
                EmitLog("[Recall] 看起来是手误更正（同一个人随后又发了消息）→ 不给模型开口机会，只标记");
                Save();
                return;
            }

            _recallNotes[key] = $"（刚有人撤回了一条消息：{sender}。上下文里那条已标成 [已撤回]。）";
            Touch(conversation);

            RequestReply(conversation, null);
        }
        catch (Exception ex)
        {
            EmitLog("[Recall] 处理撤回事件出错: " + ex.Message);
        }
    }

    /// <summary>从历史消息里找一个人的显示名（昵称/群名片）；找不到就写“成员 <qq>”。</summary>
    private string ResolveDisplayName(BotConversation conversation, long userId)
    {
        if (userId <= 0)
        {
            return "某人";
        }

        if (_selfId != 0 && userId == _selfId)
        {
            return "你";
        }

        foreach (var msg in conversation.Messages.AsEnumerable().Reverse())
        {
            if (msg.SenderId == userId && !string.IsNullOrWhiteSpace(msg.SenderName))
            {
                return msg.SenderName!;
            }
        }

        return $"成员 {userId}";
    }

    /// <summary>会话首次出现时：异步补全真实群名 + 拉取近期历史消息。</summary>
    private void EnsureGroupContext(BotConversation conversation, long groupId)
    {
        if (!_historyRequested.TryAdd(conversation.SourceKey, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var name = await _source.GetGroupNameAsync(groupId);
                if (!string.IsNullOrWhiteSpace(name) && name != conversation.Name)
                {
                    conversation.Name = name!;
                    Save();
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("History", "获取群名失败: " + ex.Message);
            }

            if (conversation.HistoryLoaded || _source is not OneBotGateway gateway || !gateway.IsConnected)
            {
                return;
            }

            // 会话已经满了：插入到顶部的旧消息会立刻被裁掉，白调一次协议端接口
            if (conversation.MessageCount >= _settings.MaxMessagesPerConversation)
            {
                conversation.HistoryLoaded = true;
                FileLog.Write("History", $"群 {groupId} 本地已有 {conversation.MessageCount} 条（达上限），跳过历史补录");
                return;
            }

            try
            {
                var history = await gateway.GetGroupMsgHistoryAsync(groupId, 20);
                if (history.Count == 0)
                {
                    return;
                }

                var restored = new List<ChatMessage>(history.Count);

                // 协议端返回的是**最新在前**，而 MergeHistoryAtTop 要求正序（旧→新）。
                // 不反转的话，补录消息在会话里的先后会颠倒，我的序号分配也会与时间相反。
                for (var i = history.Count - 1; i >= 0; i--)
                {
                    var m = history[i];
                    if (string.IsNullOrWhiteSpace(m.Text))
                    {
                        continue;
                    }

                    restored.Add(new ChatMessage
                    {
                        Role = MessageRole.Peer,
                        SenderName = m.IsGroup ? m.SenderName : null,
                        SenderId = m.UserId,
                        Text = m.Text,
                        Timestamp = m.Time,
                        QqMessageId = m.MessageId,
                        ImageUrls = m.ImageUrls
                    });
                }

                var inserted = conversation.MergeHistoryAtTop(restored);
                conversation.HistoryLoaded = true;
                if (inserted > 0)
                {
                    // 历史也写入人物档案
                    var botUin = _settings.NormalizedUin;
                    foreach (var m in restored)
                    {
                        if (m.SenderId is not long uid ||
                            (!string.IsNullOrWhiteSpace(botUin) && uid.ToString() == botUin))
                        {
                            continue;
                        }

                        _profiles.Append(uid.ToString(), m.SenderName ?? string.Empty, m.Text, m.Timestamp, conversation.Name, groupId, m.Seq);
                    }

                    Save();
                    FileLog.Write("History", $"群 {groupId} 补入 {inserted} 条历史消息");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("History", "拉取群历史失败: " + ex.Message);
            }
        });
    }

    // ---------- 会话管理 ----------

    private BotConversation GetOrCreateConversation(QqChatMessage msg)
    {
        // 通道前缀在 Channels.Key 里加（官方 = official:group:123；私域保持老格式 group:123）。
        // 前缀就是隔离：官方那边的 openid 与私域的真实 QQ 号哪怕数字碰上了，也是两个会话。
        var channel = Channels.IsOfficial(msg.Channel) ? Channels.Official : Channels.Private;
        var key = Channels.Key(channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);
        _source.RegisterTarget(channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);

        lock (_conversationsGate)
        {
            var existing = _conversations.FirstOrDefault(c => c.SourceKey == key);
            if (existing is not null)
            {
                return existing;
            }

            var name = msg.IsGroup
                ? $"{Channels.Tag(channel)}群聊 {msg.GroupId}"
                : (msg.SenderName ?? msg.UserId.ToString());
            var conversation = new BotConversation
            {
                SourceKey = key,
                Kind = msg.IsGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
                Name = name,
                MaxMessages = _settings.MaxMessagesPerConversation
            };
            conversation.OnEvicted = ArchiveEvicted;
            _conversations.Add(conversation);
            EmitLog($"新建会话 {name} ({key})");
            ConversationsChanged?.Invoke();
            return conversation;
        }
    }

    /// <summary>更新活跃时间并重排（最近活跃在前）。</summary>
    private void Touch(BotConversation conversation)
    {
        lock (_conversationsGate)
        {
            _conversations.Remove(conversation);
            _conversations.Insert(0, conversation);
        }

        ConversationsChanged?.Invoke();
    }

    private void RestoreConversations()
    {
        try
        {
            var records = _store.LoadAll();
            if (records.Count == 0)
            {
                return;
            }

            var restored = 0;
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.SourceKey) || !IsWhitelistedKey(record.SourceKey))
                {
                    continue;
                }

                lock (_conversationsGate)
                {
                    if (_conversations.Any(c => c.SourceKey == record.SourceKey))
                    {
                        continue;
                    }

                    var restoredConv = BotConversation.FromRecord(record, _settings.MaxMessagesPerConversation, ArchiveEvicted);
                    _conversations.Add(restoredConv);

                    // 告诉聚合器这条会话属于哪条通道：重启后面板代发、主动消息都得靠它选对通道
                    var (restoredIsGroup, restoredId) = restoredConv.Target;
                    if (restoredId > 0)
                    {
                        _source.RegisterTarget(restoredConv.Channel, restoredIsGroup, restoredId);
                    }
                }

                _historyRequested.TryAdd(record.SourceKey!, 0);
                restored++;
            }

            lock (_conversationsGate)
            {
                _conversations.Sort((a, b) => b.LastTime.CompareTo(a.LastTime));
            }

            FileLog.Write("Store", $"已恢复 {restored} 个会话");
            ConversationsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Write("Store", "恢复会话失败: " + ex.Message);
        }
    }

    private void Save()
    {
        Interlocked.Increment(ref _saveVersion);

        if (Interlocked.Exchange(ref _savePending, 1) == 1)
        {
            return; // 已有保存任务在跑，它会带上本次变更
        }

        _ = SaveLoopAsync();
    }

    /// <summary>
    /// 持久化循环（后台线程）。
    /// 以前这里在**WS 接收线程上同步**执行，而且 ToRecord() 是对所有会话做深拷贝 ——
    /// 消息一多就会直接阻塞收消息。现在改成后台任务 + 合并窗口 + 版本号重检。
    /// </summary>
    private async Task SaveLoopAsync()
    {
        try
        {
            while (true)
            {
                var version = Interlocked.Read(ref _saveVersion);

                await Task.Delay(150); // 合并窗口：短时间内的多次变更只写一次盘

                List<ConversationRecord> records;
                lock (_conversationsGate)
                {
                    records = _conversations.Select(c => c.ToRecord()).ToList();
                }

                _store.RequestSave(records);

                // 先放行，再检查期间是否有新变更 ——
                // 顺序不能反，否则会在临界区漏掉最后一次保存。
                Interlocked.Exchange(ref _savePending, 0);

                if (Interlocked.Read(ref _saveVersion) == version)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _savePending, 1) == 1)
                {
                    return; // 已被其他调用接手
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _savePending, 0);
            FileLog.Write("Store", "保存会话失败: " + ex.Message);
        }
    }

    /// <summary>节流日志：同一 key 在窗口内只输出一次（避免忙群里刷爆日志与面板）。</summary>
    private void LogThrottled(string key, string message, int windowSeconds = 60)
    {
        var now = Environment.TickCount64;

        if (_noisyLogAt.TryGetValue(key, out var last) && now - last < windowSeconds * 1000L)
        {
            return;
        }

        _noisyLogAt[key] = now;

        // 白名单外的来源可能很多：键数量做兵底
        if (_noisyLogAt.Count > 2000)
        {
            _noisyLogAt.Clear();
        }

        EmitLog(message);
    }

    // ---------- 白名单 ----------

    /// <summary>重建两张白名单（群聊 / 私聊各自一份）。
    /// 新字段留空就回落到旧的共用名单（<see cref="AppSettings.MessageWhitelist" />）——
    /// 老部署只配了 QQCHAT_WHITELIST，不改配置也能继续用。</summary>
    private void RebuildWhitelist()
    {
        var legacy = _settings.MessageWhitelist;

        _whitelistGroupsFromLegacy = string.IsNullOrWhiteSpace(_settings.WhitelistGroups);
        _whitelistPrivatesFromLegacy = string.IsNullOrWhiteSpace(_settings.WhitelistPrivates);

        (_whitelistGroups, _whitelistAllGroups) = ParseWhitelist(
            _whitelistGroupsFromLegacy ? legacy : _settings.WhitelistGroups);
        (_whitelistPrivates, _whitelistAllPrivates) = ParseWhitelist(
            _whitelistPrivatesFromLegacy ? legacy : _settings.WhitelistPrivates);

        // 官方通道：单独的名单；两份都留空 = 全部接受（官方平台自身有准入与额度）——
        // 不能沿用 ParseWhitelist 的“空 = 全拦”，否则没配名单时官方通道会直接死掉。
        var officialGroups = _settings.OfficialWhitelistGroups;
        var officialPrivates = _settings.OfficialWhitelistPrivates;
        (_officialWhitelistGroups, _officialWhitelistAllGroups) = string.IsNullOrWhiteSpace(officialGroups)
            ? (new HashSet<long>(), true)
            : ParseWhitelist(officialGroups);
        (_officialWhitelistPrivates, _officialWhitelistAllPrivates) = string.IsNullOrWhiteSpace(officialPrivates)
            ? (new HashSet<long>(), true)
            : ParseWhitelist(officialPrivates);
    }

    /// <summary>解析白名单。返回 (ID集合, 是否通配全部)。支持换行/中英文逗号/分号/空格/Tab 分隔，以及 * 通配。</summary>
    private static (HashSet<long> Ids, bool All) ParseWhitelist(string? whitelist)
    {
        var ids = new HashSet<long>();
        if (string.IsNullOrWhiteSpace(whitelist))
        {
            return (ids, false); // 严格模式：空名单 = 全部忽略
        }

        var parts = whitelist.Split(
            new[] { '\n', '\r', ',', '，', ';', '；', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var all = false;
        foreach (var part in parts)
        {
            if (part is "*" or "all" or "ALL")
            {
                all = true;
                continue;
            }

            if (long.TryParse(part, out var id))
            {
                ids.Add(id);
            }
        }

        return (ids, all);
    }

    /// <summary>
    /// 收消息那道闸：它必须**按通道**选名单。
    ///
    /// 2026-09-21 真实事故：这里原来写的是 <c>IsSourceAllowed(msg.IsGroup, …)</c> —— 也就是
    /// **只看私域那份白名单** ✗。后果是官方通道的消息无论官方名单怎么填都会被拦：
    /// 私域名单里当然没有别名号（8e15 起）→ “官方通道永远不回” ✗，
    /// 而且日志只写一句“忽略（不在白名单）: 群 8000…”，完全看不出是名单用错了 ✗。
    /// 现在统一走 <see cref="IsWhitelistedKey"/>（那里按 key 前缀分派到正确的名单）——
    /// 单一口径，以后不会再分叉。
    /// </summary>
    private bool IsWhitelisted(QqChatMessage msg)
        => IsWhitelistedKey(Channels.Key(
            // ⚠ 这里要传的是**通道名**（msg.Channel），不能包一层 Channels.ChannelOf ——
            // ChannelOf 是“从 key 前缀反推通道”的（它看 official: 前缀），
            // 传 "official" 进去会判成私域 → 又去查私域名单 → 官方永远被忽略（2026-09-21 实际卡了很久）。
            // Channels.Key 自己会归一化通道名，直接传 msg.Channel 就是对的。
            msg.Channel,
            msg.IsGroup,
            msg.IsGroup ? msg.GroupId : msg.UserId));

    /// <summary>群/私聊是否在白名单里（戳一戳事件没有 QqChatMessage，只能单拎一个判据）。
    ///
    /// 两边**各用各的名单**（号主 2026-09-18：“私聊白名单和群聊白名单两个框分开”）：
    /// 以前只比数字，所以把一个 QQ 号填进去，连“同号的群”也一起放行了。
    /// </summary>
    private bool IsSourceAllowed(bool isGroup, long id)
        => isGroup
            ? _whitelistAllGroups || _whitelistGroups.Contains(id)
            : _whitelistAllPrivates || _whitelistPrivates.Contains(id);

    /// <summary>
    /// 会话 key 能不能收（白名单）。
    /// 通道分开算：官方通道用的是 **另一份名单**（<see cref="AppSettings.OfficialWhitelistGroups"/>
    /// <see cref="AppSettings.OfficialWhitelistPrivates"/>，存别名号）——
    /// 两份名单**默认不共用**：QQ 那份名单里写的是真实群号，官方通道的号是别名，
    /// 混用只会出现“官方通道永远被拦”这种看不懂的结果。
    /// 官方那边如果两份名单都留空，就是**全部接受**（官方平台自身有准入与额度限制）。
    /// </summary>
    private bool IsWhitelistedKey(string sourceKey)
    {
        var (isGroup, id) = Channels.Parse(sourceKey);
        if (id <= 0)
        {
            return false;
        }

        return Channels.IsOfficial(Channels.ChannelOf(sourceKey))
            ? isGroup
                ? _officialWhitelistAllGroups || _officialWhitelistGroups.Contains(id)
                : _officialWhitelistAllPrivates || _officialWhitelistPrivates.Contains(id)
            : IsSourceAllowed(isGroup, id);
    }

    // ---------- 回复流程 ----------

    /// <summary>限流：私聊/群聊各自冷却，防止连发刷屏。</summary>
    private bool AllowReply(BotConversation conversation)
    {
        var cooldown = conversation.Kind == ConversationKind.GroupChat
            ? TimeSpan.FromSeconds(Math.Max(0, _settings.GroupCooldownSeconds))
            : TimeSpan.FromSeconds(Math.Max(0, _settings.PrivateCooldownSeconds));

        if (cooldown == TimeSpan.Zero)
        {
            return true;
        }

        var key = conversation.SourceKey;
        var now = DateTimeOffset.Now;
        if (_replyCooldown.TryGetValue(key, out var last) && now - last < cooldown)
        {
            EmitLog($"冷却中（{(cooldown - (now - last)).TotalSeconds:F1}s 后放行）: {conversation.Name}");
            return false;
        }

        _replyCooldown[key] = now;
        return true;
    }

    /// <summary>限流期间被挡下的触发：等这一轮生成结束再补一次评估（见 RunReplyAsync 的 finally）。</summary>
    /// <param name="TriggerId">要拿来当触发的那条消息 id（0 = 事件没有消息 id，例如戳一戳）。</param>
    /// <param name="Direct">这一窗口里有没有“直接跟机器人说话”的（@ 你 / 引用你的话）——那种必须被回答。</param>
    private readonly record struct DeferredTrigger(long TriggerId, bool Direct);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeferredTrigger> _deferredTriggers = new();

    /// <summary>
    /// “要不要现在回” + “不能回就记下来”——**统一入口**。
    /// 以前各处写的是 <c>if (AllowReply(...)) EnqueueReply(...)</c>：被冷却拦下的触发就永久丢了，
    /// 群里连珠炮时表现为“怎么都不理我”（号主 2026-09-16 反馈）。
    /// 现在被拦下会记一笔，本轮生成结束后补一次评估；@ 你 / 引用你的话那种“直接跟你说话”的，
    /// 在补评估时会带上 Direct 标记（阈值不再把它压成沉默）。
    /// </summary>
    private void RequestReply(
        BotConversation conversation,
        long? triggerMessageId,
        bool proactive = false,
        bool catchUp = false,
        bool directInWindow = false)
    {
        if (!_settings.AiModeEnabled)
        {
            return;
        }

        if (catchUp || AllowReply(conversation))
        {
            if (catchUp)
            {
                // 补评估是“这一轮的尾巴”，也要刷新冷却时间，否则下一句又立刻放行、连珠炮变刷屏
                _replyCooldown[conversation.SourceKey] = DateTimeOffset.Now;
            }

            EnqueueReply(conversation, triggerMessageId, proactive);
            return;
        }

        // 被挡下：记一笔待补（窗口里出现过“直接跟你说话”的，优先拿它当补评估的触发）
        var key = conversation.SourceKey;
        _deferredTriggers.AddOrUpdate(
            key,
            _ => new DeferredTrigger(triggerMessageId ?? 0, directInWindow),
            (_, old) =>
            {
                var direct = old.Direct || directInWindow;
                // 有直接跟你说话的 → 用那条当触发（那是最该回答的一条）；否则用最新的
                var id = directInWindow || old.TriggerId == 0 ? (triggerMessageId ?? old.TriggerId) : old.TriggerId;
                return new DeferredTrigger(id, direct);
            });
        EmitLog($"等这一轮说完再评估（被限流挡下的新消息已记下）: {conversation.Name}");
    }

    /// <summary>把一条待回复请求排入该会话的 FIFO 链，并唤醒调度器。</summary>
    private void EnqueueReply(BotConversation conversation, long? triggerMessageId, bool proactive = false)
    {
        if (triggerMessageId is not null)
        {
            LastActiveRequestTime = DateTime.Now; // 主动请求刷新活跃时间
        }

        _pendingConversations[conversation.SourceKey] = conversation;
        _pendingReplies
            .GetOrAdd(conversation.SourceKey, _ => new System.Collections.Concurrent.ConcurrentQueue<PendingReply>())
            .Enqueue(new PendingReply(triggerMessageId, proactive));

        _ = DrainReplyQueueAsync();
    }

    /// <summary>
    /// 调度器：从各会话的 FIFO 链头取请求交给并发 worker。
    ///   • 同一会话同时只能有一个在途请求（保证回复顺序与引用正确）
    ///   • 不同会话并行执行（一个慢请求不再阻塞其它群）
    ///   • 全局并发上限由 _replyGate 控制
    /// </summary>
    private async Task DrainReplyQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _replyWorkerRunning, 1, 0) == 1)
        {
            return; // 已有调度器在跑
        }

        try
        {
            while (true)
            {
                var fired = false;

                foreach (var (key, queue) in _pendingReplies)
                {
                    if (_inFlight.ContainsKey(key))
                    {
                        continue; // 该会话已在跑 → 保持顺序，等它完成
                    }

                    if (queue.IsEmpty || !queue.TryDequeue(out var pending))
                    {
                        continue;
                    }

                    if (!_inFlight.TryAdd(key, 0))
                    {
                        // 并发抢占失败：把触发消息放回队首位置（重新入队到尾部也可，
                        // 因为同一会话此时必定无其它待处理项）
                        queue.Enqueue(pending);
                        continue;
                    }

                    if (!_pendingConversations.TryGetValue(key, out var conversation))
                    {
                        _inFlight.TryRemove(key, out _);
                        continue;
                    }

                    fired = true;
                    _ = RunReplyAsync(conversation, key, pending.TriggerMessageId, pending.Proactive);
                }

                if (fired)
                {
                    continue; // 可能还有别的会话可跑
                }

                // 本轮一无所获：要么全在途（等释放），要么真的没活了
                var waiting = _pendingReplies.Any(kv => !kv.Value.IsEmpty && _inFlight.ContainsKey(kv.Key));
                if (!waiting)
                {
                    CleanupPending();
                    break;
                }

                await Task.Delay(150);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _replyWorkerRunning, 0);
            if (_pendingReplies.Any(kv => !kv.Value.IsEmpty))
            {
                _ = DrainReplyQueueAsync();
            }
        }
    }

    /// <summary>清掉已排空且不在途的会话条目，避免字典无限增长。</summary>
    private void CleanupPending()
    {
        foreach (var key in _pendingReplies.Keys.ToList())
        {
            if (_inFlight.ContainsKey(key))
            {
                continue;
            }

            if (_pendingReplies.TryGetValue(key, out var queue) && queue.IsEmpty)
            {
                _pendingReplies.TryRemove(key, out _);
                _pendingConversations.TryRemove(key, out _);
            }
        }
    }

    /// <summary>延迟回收被替换掉的闸门：等所有可能还在等它的请求都结束再 Dispose。</summary>
    private static void RetireGate(SemaphoreSlim gate)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(90)); // HttpClient 超时 60s，留余量
            try
            {
                gate.Dispose();
            }
            catch
            {
                // 已释放或仍有等待者：忽略（不能影响退出流程）
            }
        });
    }

    // ══════════ QQ 账号在线探测 ══════════

    /// <summary>
    /// 探测 QQ 账号是否真的在线。
    /// 仅凭「WebSocket 连着」无法判定 —— 登录失效/被顶号时连接照旧，
    /// 只是从此一条消息都收不到（这正是本次事故的表现）。
    /// </summary>
    private async Task CheckAccountOnlineAsync()
    {
        if (_disposed == 1 || _source is not OneBotGateway gateway || !gateway.IsConnected)
        {
            return;
        }

        if (Interlocked.Exchange(ref _healthRunning, 1) == 1)
        {
            return; // 上一轮还没回来
        }

        try
        {
            var online = await gateway.GetAccountOnlineAsync();
            if (online is null)
            {
                return; // 协议端没明确答复 → 不误报，保持原状态
            }

            var next = online.Value ? 1 : 0;
            var previous = Interlocked.Exchange(ref _accountOnline, next);
            if (previous == next)
            {
                return; // 状态未变，不刷日志
            }

            if (next == 0)
            {
                EmitLog("⚠ QQ 账号已离线（登录失效或被顶号）：从现在起收不到任何消息。" +
                        "请打开 NapCat 管理面板重新扫码登录（面板地址见部署目录 README）。");
            }
            else
            {
                EmitLog("QQ 账号已上线，恢复正常接收消息。");
            }

            StateChanged?.Invoke();
        }
        catch
        {
            // 探测失败不打扰用户：下一轮自然会重试
        }
        finally
        {
            Interlocked.Exchange(ref _healthRunning, 0);
        }
    }

    /// <summary>执行一次回复（受全局并发闸门限制）。</summary>
    private async Task RunReplyAsync(BotConversation conversation, string sourceKey, long? triggerMessageId, bool proactive = false)
    {
        // 捕获当前闸门实例：配置变更会整体替换 _replyGate，
        // Wait 与 Release 必须作用在**同一个对象**上。
        var gate = _replyGate;
        var acquired = false;
        try
        {
            await gate.WaitAsync();
            acquired = true;
            conversation.HasPendingReply = false;
            await GenerateReplyAsync(conversation, triggerMessageId, proactive);
        }
        catch (Exception ex)
        {
            EmitLog("回复流程异常: " + ex.Message);
        }
        finally
        {
            // 顺序很重要：先释放闸门（可能抛），再清在途标记。
            // 任何一个环节失败都不能让 _inFlight 残留 —— 残留意味着该会话永远不再被调度。
            if (acquired)
            {
                try
                {
                    gate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // 闸门已被回收：忽略，不能阻断下面的清理
                }
            }

            _inFlight.TryRemove(sourceKey, out _);
            LastActiveRequestTime = DateTime.Now;

            // 限流期间被挡下的消息**不能就这么算了**（号主反馈“连续多人对话不回应”的根因）：
            // 以前 AllowReply 一返回 false，那条触发就彻底没人评估了 —— 群里连着说话时，
            // 只要第一轮生成完是沉默（或慢），后面那几条就要等到 60 秒静默兜底才有下一次机会。
            // 现在：这一轮说完，若中间又有消息被挡下，就**补一次评估**（每轮生成只补一次，不会打转）。
            if (_deferredTriggers.TryRemove(sourceKey, out var deferred))
            {
                var why = deferred.TriggerId > 0 ? $"最近一条 #{deferred.TriggerId}" : "（无消息 id 的事件）";
                EmitLog($"补一次评估（上一轮生成期间又有新消息，{why}）: {conversation.Name}");
                RequestReply(conversation, deferred.TriggerId > 0 ? deferred.TriggerId : null,
                    catchUp: true, directInWindow: deferred.Direct);
            }

            _ = DrainReplyQueueAsync(); // 唤醒调度器处理该会话的后续项
        }
    }

    /// <summary>静默兜底：超过 IdleFallbackSeconds 没有主动请求时，为待处理会话补一次请求。</summary>
    private void IdleFallbackTick()
    {
        if (_disposed == 1 || !_settings.AiModeEnabled)
        {
            return;
        }

        if (DateTime.Now - LastActiveRequestTime < TimeSpan.FromSeconds(_settings.IdleFallbackSeconds))
        {
            return;
        }

        foreach (var conversation in Conversations)
        {
            if (!conversation.HasPendingReply)
            {
                continue;
            }

            conversation.HasPendingReply = false;
            EmitLog($"静默兜底触发: {conversation.Name}");
            EnqueueReply(conversation, null);
        }

        TryProactiveSpeak();
    }

    /// <summary>主动开口前，群里需要安静多久（秒）。太短会显得坐不住。</summary>
    private const int ProactiveQuietDefaultSeconds = 120;

    /// <summary>“有人在住的话题”的判据：最近 5 分钟内至少这么多条群友发言。</summary>
    private const int ProactiveBurstMessages = 3;

    /// <summary>
    /// 主动开口：没有人 @ 它、也没人在问它的时候，它自己接一句。
    ///
    /// 为什么要它：号主要的是“陪伴感” —— 只在被叫时才出声，本质是个应答机器。
    /// 什么情况才允许主动（宁可少也不能烦人）：
    ///   • 群聊 + AI 开着 + 白名单内 + 没在冷却；
    ///   • 群里已经安静下来（≥ <see cref="AppSettings.ProactiveQuietSeconds" /> 秒没人说话）—— 不然就是抢话；
    ///   • 机器人上一条不是最最后一条（上一条是它说的，就不要再自说自话）；
    ///   • 同一会话距上次主动 ≥ ProactiveCooldownSeconds；
    ///   • 而且得有个“由头”：要么它上一轮读到有人情绪低落/在求助，要么群里刚刚聊得热（≥ 3 条/5 分钟）—— 接一句话题。
    /// 不满足就什么都不做（不出声也是陪伴）。
    /// </summary>
    private void TryProactiveSpeak()
    {
        if (!_settings.EnableProactive)
        {
            return;
        }

        var cooldown = TimeSpan.FromSeconds(Math.Max(60, _settings.ProactiveCooldownSeconds));
        var now = DateTimeOffset.Now;
        var quiet = TimeSpan.FromSeconds(Math.Max(1, _settings.ProactiveQuietSeconds));

        foreach (var conversation in Conversations)
        {
            if (conversation.Kind != ConversationKind.GroupChat || conversation.HasPendingReply)
            {
                continue;
            }

            if (_inFlight.ContainsKey(conversation.SourceKey))
            {
                continue;
            }

            if (!IsWhitelistedKey(conversation.SourceKey) || !AllowReply(conversation))
            {
                continue;
            }

            if (_lastProactive.TryGetValue(conversation.SourceKey, out var lastAt) && now - lastAt < cooldown)
            {
                continue;
            }

            var messages = conversation.Messages;
            if (messages.Count == 0)
            {
                continue;
            }

            var last = messages[^1];
            var lastAt2 = last.Timestamp;
            if (now - lastAt2 < quiet)
            {
                continue;   // 群里刚刚还在说，别抢
            }

            if (last.Role == MessageRole.Self)
            {
                continue;   // 最后一句是它自己说的 → 不再自说自话
            }

            // “由头”：情绪低落/求助那边可以主动关心；热闹话题可以接着聊
            var vibe = CurrentVibe(conversation.SourceKey);
            var caringMoment = vibe is "低落" or "求助";
            var burst = messages.Count(m => m.Role == MessageRole.Peer && now - m.Timestamp <= TimeSpan.FromMinutes(5)) >= ProactiveBurstMessages;
            if (!caringMoment && !burst)
            {
                continue;
            }

            _lastProactive[conversation.SourceKey] = now;
            EmitLog($"[主动] {conversation.Name}：安静 {(now - lastAt2).TotalMinutes:F0} 分钟" +
                    (caringMoment ? $"、上轮气氛「{vibe}」" : "、刚聊得热") + " → 自己开一句");
            EnqueueReply(conversation, null, proactive: true);
            return;   // 一次 tick 只主动一个会话（避免同时到处说话）
        }
    }

    private async Task GenerateReplyAsync(BotConversation conversation, long? triggerMessageId, bool proactive = false)
    {
        var context = conversation.TakeLast(_settings.MaxContextMessages);
        if (context.Count == 0)
        {
            return;
        }

        // 当前上下文最早一条：档案只取比它更早的（按单调序号精确判定，避免时间戳秒级精度误判）
        var contextOldestSeq = context[0].Seq;
        var contextOldestUnix = context[0].Timestamp.ToUnixTimeSeconds();
        var (isGroupScope, scopeGroupId) = conversation.Target;

        // 收集最近 40 条上下文里出现过的发送者 QQ → 读取各自在**本会话**的人物档案（角色卡片）
        var profiles = new List<string>();
        var profileChars = 0;
        var recentSenders = context
            .TakeLast(40)
            .Where(m => m.Role == MessageRole.Peer && m.SenderId.HasValue)
            .Select(m => m.SenderId!.Value)
            .Distinct()
            .Take(Math.Max(0, _settings.ProfileLookupCount))
            .ToList();

        foreach (var uid in recentSenders)
        {
            var summary = _profiles.GetProfileSummary(
                uid.ToString(),
                isGroupScope ? scopeGroupId : 0,
                _settings.ProfileSummaryLines,
                contextOldestSeq,
                contextOldestUnix);

            if (string.IsNullOrWhiteSpace(summary))
            {
                continue; // 本会话没有更早的历史 → 不占提示词预算
            }

            if (profileChars + summary.Length > _settings.MaxProfileChars)
            {
                break; // 超出预算：后面的（发言更早的）丢弃
            }

            profiles.Add(summary);
            profileChars += summary.Length;
        }

        var started = DateTime.Now;
        SetThinking(conversation, true);

        // 表情包候选：拿最近的对话文字当检索词，从库里挑几张给模型选。
        // 先挑后发 —— 库可能有上千张，全塞进提示词既贵又不准。
        // 气氛“沉”（有人低落/在吵架）时不给候选：给了它就容易挑一张发出去，与气氛不搭。
        var stickerChoices = new List<StickerChoice>();
        if (_settings.EnableStickers && _settings.StickerLibraryMax > 0 && _settings.StickerCandidates > 0 &&
            !IsSoberVibe(conversation.SourceKey))
        {
            var query = string.Join(" ", context.TakeLast(8).Select(m => m.Text));
            stickerChoices = _stickers
                .PickCandidates(query, _settings.StickerCandidates)
                .Select(s => new StickerChoice(s.Id, StickerStore.Describe(s)))
                .ToList();
        }

        // 群成员身份（群主 / 管理员 / 群头衔）：只在“有值得说的人”时才给，不占 token。
        // 上限 14 人：再多人就只是个名字列表，既没用又费 token。
        // 先等一下“刚触发的身份查询”：查询就十几毫秒，等它一下，免得第一次说话那轮看不到头衔。
        if (isGroupScope)
        {
            await WaitForPendingRoleLookupsAsync(scopeGroupId, TimeSpan.FromMilliseconds(700));
        }

        var roleCount = 0;
        var groupRoles = isGroupScope ? _memberRoles.DescribeForPrompt(scopeGroupId, 14, out roleCount) : null;

        // 上一次读到的气氛（有 TTL）：给模型当底色，让它接得上
        var vibeHint = CurrentVibeHint(conversation.SourceKey);
        var previousVibe = CurrentVibe(conversation.SourceKey);

        EmitLog($"请求模型…（{conversation.Name}，上下文 {context.Count} 条，档案 {profiles.Count} 份/{profileChars} 字" +
                (roleCount > 0 ? $"，身份 {roleCount} 人" : string.Empty) +
                (previousVibe.Length > 0 ? $"，上轮气氛 {previousVibe}" : string.Empty) +
                (proactive ? "，主动开口" : string.Empty) +
                (stickerChoices.Count > 0 ? $"，表情包候选 {stickerChoices.Count} 张" : string.Empty) + "）");

        // 最近被戳过（10 分钟内）才给模型“可以戳回去”的指令，平时不浪费 token
        var pokeContext = _settings.EnablePoke &&
            (_lastPoke.TryGetValue(conversation.SourceKey, out var lastPoke) &&
             DateTimeOffset.Now - lastPoke.At < TimeSpan.FromMinutes(10));

        // 心情（被戳次数客观 + 模型主观写的）：影响还戳不戳回去、话多话少。
        // 只在“刚被戳过”或“模型写过心情”时给，平常不浪费 token。
        var moodNow = DateTimeOffset.Now;
        var moodText = pokeContext || _mood.CurrentText(moodNow) is not null ? _mood.Describe(moodNow) : null;

        // 刚“听过”的歌：把歌词与波形实测交给模型，用完就清（避免以后每轮都背上它）
        var musicText = _musicNotes.TryRemove(conversation.SourceKey, out var pendingMusic) ? pendingMusic : null;

        // 刚有人撤回了消息：把这件事告诉模型（但不告诉它撤回了什么）——用完就清
        var recallText = _recallNotes.TryRemove(conversation.SourceKey, out var pendingRecall) ? pendingRecall : null;

        // 刚上网查到的资料 / 读到的页面正文：交给模型，用完就清
        var searchText = _searchNotes.TryRemove(conversation.SourceKey, out var pendingSearch) ? pendingSearch : null;

        // 链接预览：给快站点 2.5 秒的机会当轮用上；太慢就先不等（完成后留给下一轮）
        if (_linkTasks.TryGetValue(conversation.SourceKey, out var linkTask))
        {
            await Task.WhenAny(linkTask, Task.Delay(TimeSpan.FromMilliseconds(2500)));
        }

        var linkText = _linkNotes.TryRemove(conversation.SourceKey, out var pendingLink) ? pendingLink : null;
        CompletionResult result;
        try
        {
            result = await _brain.CompleteAsync(
                context,
                profiles.Count > 0 ? string.Join("\n\n", profiles) : null,
                stickers: stickerChoices.Count > 0 ? stickerChoices : null,
                pokeContext: pokeContext,
                moodText: moodText,
                musicText: musicText,
                linkText: linkText,
                recallText: recallText,
                enableWebSearch: _settings.EnableWebSearch,
                searchText: searchText,
                groupRolesText: groupRoles,
                vibeHint: vibeHint,
                proactive: proactive,
                enableListen: _settings.EnableMusic,
                enableVoice: _settings.EnableVoice);
        }
        catch (Exception ex)
        {
            SetThinking(conversation, false);
            // 连异常类型和第一帧调用堆栈一起记：只记 Message 时，“空引用”这种根本看不出在哪（踩过）
            EmitLog($"模型请求失败: {ex.GetType().Name} {ex.Message}" +
                    (ex.StackTrace is { Length: > 0 } stack
                        ? "　@ " + stack.Split('\n').FirstOrDefault(l => l.Contains("QQChatAgent"))?.Trim()
                        : string.Empty));
            KeepSearchNotes(conversation, searchText, "模型请求失败");
            return;
        }

        var elapsed = (DateTime.Now - started).TotalMilliseconds;
        Volatile.Write(ref _lastGenerationMs, (long)elapsed);
        SetThinking(conversation, false);

        // 发言适合度门槛：以前只写在提示词里、代码不执行；现在真正生效。
        // 模型未按 JSON 输出（Suitability == null）时按普通文本回复处理，不拦截。
        // 例外：**这一轮带着刚查到的资料**时不受门槛限制 —— 模型自评“现在插嘴合适吗”时
        // 往往给低分（它只是回来汇报查到的东西，不是要插话），结果就是“查了半天啥也不说”。
        // 查都查了，就得让它说出来；真不想说（空回复）时下面会把资料留给下一轮。
        // 发言适合度门槛：以前只写在提示词里、代码不执行；现在真正生效。
        // 模型未按 JSON 输出（Suitability == null）时按普通文本回复处理，不拦截。
        //
        // 情绪介入（这一步才是“人性化陪伴”的关键）：先看它读到的气氛，再决定门槛 ——
        //   • 吵架/对线：不插嘴（门槛抬到 60，即“非说不可”才说）
        //   • 低落/求助/孤独：更愿意轻声接一句（门槛降 10），但禁掉表情包/语音/戳（人家难过时发图很尬）
        //   • 生气/吐槽：照常，但也不发表情包（容易像在嘲笑）
        var vibe = result.Vibe ?? "中性";
        var baseThreshold = Math.Clamp(_settings.SuitabilityThreshold, 0, 100);

        // 这一轮是不是“人家在跟你说话”：触发那条 @ 了你，或引用了你发的那句话。
        // 两个用途：① 自评再低也接（被点名不应该沉默）；② 引用优先挂给点名的人。
        var directAddress = triggerMessageId is long directTriggerId &&
                            conversation.Messages.FirstOrDefault(m => m.QqMessageId == directTriggerId)?.DirectToBot == true;
        var threshold = VibeAdjustedThreshold(vibe, baseThreshold, out var vibeReason);
        var soberMood = vibe is "低落" or "求助" or "吵架" or "生气" or "吐槽";

        if (vibe != "中性" && result.VibeNote is { Length: > 0 })
        {
            EmitLog($"[Vibe] {conversation.Name}：读到「{vibe}」——{result.VibeNote}" +
                    (vibeReason.Length > 0 ? $"（{vibeReason}）" : string.Empty));
        }

        // 不管这轮说不说话，读到的气氛都记下来：沉默也是一种回应，下一轮要接得上
        RememberVibe(conversation.SourceKey, vibe, result.VibeNote);

        if (result.Suitability is int score && score < threshold)
        {
            // 被点名（@ 你 / 引用你的话）就该答，门槛不适用于这种轮次：
            // 号主反馈“直接跟我说话它也不理”—— 自评低说明模型“不想插嘴”，但人家就是在问它，
            // 沉默在群里看着就是坏了（而且连珠炮场景下会连着好几条都不理）。
            // ⚠ 但“有人在对线”那一档是刻意设的规矩（不站队/不评理/不添柴）：哪怕 @ 你评理也不接。
            if (directAddress && vibe != "吵架")
            {
                EmitLog($"自评 {score} < 阈值 {threshold}，但这条是直接跟机器人说话（@ 你/引用了你的话）→ 照样接");
            }
            else if (searchText is null)
            {
                EmitLog($"适合度不足 → 沉默（评分 {score} < 阈值 {threshold}" +
                        (vibe != "中性" ? $"，气氛 {vibe}" : string.Empty) + $"，{elapsed:F0}ms）: {conversation.Name}");
                return;
            }
            else
            {
                EmitLog($"适合度不足（{score} < {threshold}）但本轮带着刚查到的资料 → 照样说");
            }
        }

        // 表情包：模型可以只发图不说话，也可以“文字 + 图”。
        // 校验一下 id（模型偶发会编造/多空格），拿不到就把这次当成纯文字。
        StickerRecord? sticker = null;
        // 联网搜索（search / read）：后台去查，拿到结果后再给它一次开口的机会。
        // 这两个是“两轮动作”—— 模型这轮照常接话（reply 可以写“我去查查”），下一轮拿着事实说。
        // search 优先于 read：模型一般只会填一个。
        if (_settings.EnableWebSearch && _search is not null && result.Search is { Length: > 0 } wantedQuery)
        {
            QueueWebSearchAsync(conversation, wantedQuery);
        }
        else if (_settings.EnableWebSearch && _search is not null && result.Read is { Length: > 0 } pageUrl)
        {
            QueuePageReadAsync(conversation, pageUrl);
        }

        // 模型想听一首歌（listen 字段）：后台去搜、去听，听完再给它一次开口的机会。
        // 这是群里说“去听一下 XXX”的唯一入口 —— 不靠正则猜句子，交给模型自己决定。
        if (_settings.EnableMusic && result.Listen is { Length: > 0 } wantedSong && _music is not null)
        {
            var key = conversation.SourceKey;
            var nowListen = DateTimeOffset.Now;
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.MusicListenCooldownSeconds));
            if (!_lastListen.TryGetValue(key, out var lastAt) || nowListen - lastAt >= cooldown)
            {
                _lastListen[key] = nowListen;
                EmitLog($"[Music] 模型想听「{wantedSong}」");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var note = await _music.DescribeByNameAsync(wantedSong, "群友", CancellationToken.None);
                        if (string.IsNullOrWhiteSpace(note))
                        {
                            EmitLog($"[Music] 没搜到/没听到「{wantedSong}」");
                            return;
                        }

                        _musicNotes.AddOrUpdate(key, note!, (_, old) => old + "\n\n" + note);
                        RequestReply(conversation, null);
                    }
                    catch (Exception ex)
                    {
                        EmitLog($"[Music] 听「{wantedSong}」失败: {ex.Message}");
                    }
                });
            }
            else
            {
                EmitLog($"[Music] 「{wantedSong}」还在冷却中（{Math.Round((nowListen - lastAt).TotalSeconds)}s 前刚听过）");
            }
        }

        // 模型想把某首歌分享给群里 → 搜到就发一张网易云卡片，顺手“听”一遍（下一轮它就能聊这首歌）。
        if (_settings.EnableMusic && result.ShareSong is { Length: > 0 } songToShare && _music is not null)
        {
            var key = conversation.SourceKey;
            var (shareIsGroup, shareTargetId) = conversation.Target;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (shareTargetId == 0 || !_source.IsConnected)
                    {
                        return;
                    }

                    var songId = await _music.ResolveSongIdByNameAsync(songToShare, CancellationToken.None);
                    if (string.IsNullOrWhiteSpace(songId))
                    {
                        EmitLog($"[Music] 想分享「{songToShare}」但没搜到，不发卡片");
                        return;
                    }

                    var ok = await _source.SendMusicAsync(shareIsGroup, shareTargetId, "163", songId, title: songToShare, ct: CancellationToken.None);
                    EmitLog(ok ? $"[Music] 已分享卡片「{songToShare}」(# {songId})" : $"[Music] 卡片发送失败，改用链接分享: {songToShare}");

                    // 协议端不接卡片（NapCat 各版本对 music 段的接受程度不一样）时退化成发链接：
                    // QQ 客户端会把网易云链接自己渲染成卡片，效果差不多，但绝不能什么都不发
                    if (!ok)
                    {
                        var link = $"https://music.163.com/song?id={songId}";
                        var sentLink = await _source.SendTextAsync(shareIsGroup, shareTargetId, link, CancellationToken.None);
                        EmitLog(sentLink.Ok ? $"[Music] 已用链接分享：{link}" : $"[Music] 链接也发送失败：{link}");
                    }

                    // 卡片发出去了，接着真去听一遍：下一轮发言时它就“听过这首歌”
                    var note = await _music.DescribeByNameAsync(songToShare, "（自己分享的）", CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(note) && !_musicNotes.ContainsKey(key))
                    {
                        _musicNotes[key] = note!;
                    }
                }
                catch (Exception ex)
                {
                    EmitLog($"[Music] 分享「{songToShare}」失败: {ex.Message}");
                }
            });
        }

        if (_settings.EnableStickers && result.StickerId is { } sid && !IsSoberVibe(conversation.SourceKey))
        {
            sticker = _stickers.Find(sid);
            if (sticker is null)
            {
                EmitLog($"模型挑的表情包 #{sid} 不在库里，已忽略（只发文字）");
            }
            else if (sticker.IsSticker != true)
            {
                // 还没通过“是不是表情包”审核的图不当表情包用：
                // 宁可这一轮不发，也不要把聊天截图/广告发出去
                EmitLog($"这张 #{sid} 还没通过“是不是表情包”审核，本轮不发");
                sticker = null;
            }
            else if (!AllowSticker(conversation, sticker.Id, out var stickerWhy))
            {
                // 频率门：库小的时候模型会每句都挂同一张（群里直接开愤：
                // “你别老是发这个表情包了”）。表情包是调味品，不是主食。
                EmitLog($"这次不发表情包（{stickerWhy}）: #{sticker.Id} {StickerStore.Describe(sticker)}");
                sticker = null;
            }
        }

        var reply = (result.Reply ?? string.Empty).Trim();

        // 模型想“用语音说这句”（speak）。真正的发送在下边（要等 isGroup/targetId），
        // 这里先把文本取出来：它得参与“沉默判定”与消息落库，否则“只发语音不说话”会被当成空回复。
        var voiceText = (result.Speak ?? string.Empty).Trim();

        // 模型可以顺手写一句“我现在的心情”——存下来，下一轮提示词里带上（空/太长会被忽略）
        if (_mood.SetText(result.Mood, DateTimeOffset.Now))
        {
            EmitLog($"心情变成：{_mood.Describe(DateTimeOffset.Now)}");
        }

        // 模型可以“只戳不说话”：这样也算有动作，不算沉默
        var pokeTarget = result.PokeTargetId;

        if (reply.Length == 0 && sticker is null && pokeTarget is null && voiceText.Length == 0)
        {
            // “上游把回复吞了”（200 但没 choices，已重试一次）与“模型自己决定不说话”不是一回事：
            // 以前两种都写成“模型选择沉默”，主人根本看不出是网关出事了（22:09 那条就是这样）。
            var why = result.UpstreamEmpty
                ? "上游空响应"
                : result.Suitability is int s2 ? $"自评 {s2}" : "空回复";
            EmitLog(result.UpstreamEmpty
                ? $"本轮没拿到模型输出（上游连续两次空响应，{elapsed:F0}ms，已重试）: {conversation.Name}"
                : $"模型选择沉默（{why}，{elapsed:F0}ms）: {conversation.Name}");
            KeepSearchNotes(conversation, searchText, why);
            return;
        }

        // 复读守卫：模型偶尔会把上下文里自己上一条发言一字不差地再说一遍 ——
        // 群里实测过单字“悼”连发 6 条：第一条件来自上游截断，之后模型读到自己那条“悼”，
        // 就把它当成“可以接的梗”反复发。一模一样的话紧接着再来一遍，对群里就是刷屏。
        if (reply.Length > 0 && IsRepeatingOwnLastMessage(conversation, reply))
        {
            EmitLog($"检测到复读（与上一条自己的发言完全相同）→ 沉默（{elapsed:F0}ms）: {Shorten(reply, 40)}");
            KeepSearchNotes(conversation, searchText, "复读守卫");
            return;
        }

        // 回复引用目标怎么定（两件事一起决定，别只看排队时记录的那个 id）：
        //   ① **模型自己指认的最准**：提示词里最近几条别人的消息都带了 (#id)，
        //      它在 JSON 里用 replyTo 说明“我在回哪条”。这是唯一能从根上对上号的办法 ——
        //      启发式只能猜“最新那条”或“排队时的触发”，都猜不准（线上两度因此看起来回错人）。
        //      采信条件：它必须在本次上下文里（防模型报个不存在的编号）。
        //   ② 没指认时：只有“触发消息还是最新诉求”才拿它当引用目标；如果触发已经过去
        //      （后面有人插话，比如模型慢了几秒），就**不引用** —— 以前这里会抽“上下文里最新那条
        //      别人发的消息”当目标，于是“正文回答 A、引用挂到 B”，群里看到的就是“回复错人”。
        //   ③ 紧挨着回就不引用：目标后面没有别的新消息时，引用是多余的（保留原有手感）。
        var messages = conversation.Messages;
        var lastSelfIndex = LastSelfMessageIndex(messages);
        var triggerIndex = IndexOfMessage(messages, triggerMessageId);
        var triggerIsCurrent = triggerIndex >= 0 && triggerIndex > lastSelfIndex;

        // 「本轮触发是在复读 / 模仿」时（有人原样重复了别人的话，包括学机器人说话），
        // 说话对象是**复读的那条**，不是被复读的原文。线上实测（handoff-4 §23）：群友 c 复读了
        // 机器人那句，机器人回“别学我说话！”，模型却把引用指到了上一条别人的消息上 ——
        // 它把“素材”当成了“对象”（§22 修的是启发式，治不了这一类）。
        // 提示词里已经把 replyTo 的语义写死，这里再兜一道：这种轮次里模型的指认只要不在触发那条上就不引用。
        // 口径跟 §22 一致：宁可不引，也不挂错人。
        var triggerWasEcho = triggerIndex >= 0 && IsEchoOfEarlierMessage(messages, triggerIndex);

        long? replyTo = null;
        if (result.ReplyToMessageId is long chosen &&
            // 已撤回的不算：引用一条群里已经看不到的消息，群友看到的就是莫名其妙
            // （实测踩过：模型从历史里拿了一个已被撤回的 id 填 replyTo，BotAgent 这边只校验
            //  “它在不在上下文里”—— 结果是给一条已撤回的消息挂了引用）
            // 还得是**别人发的**：自己引自己没意义（模型偶尔会把上下文里自己那条的 id 报回来）
            context.Any(m => m.QqMessageId == chosen && !m.Recalled && m.Role == MessageRole.Peer) &&
            IndexOfMessage(messages, chosen) >= 0)
        {
            // 人家在跟你说话（@ 你 / 引用了你的话），模型却把引用指给了别人：
            // 群里看到的就是“你正跟它说话，它去回另一个人”（号主 2026-09-16 反馈“回复引用错误”）。
            // 口径：**点名优先** —— 先把该回的人回了，想聊别人那条下一轮再说。
            if (directAddress && triggerMessageId is long directTrigger && chosen != directTrigger)
            {
                EmitLog($"模型想引 #{chosen}，但这一轮是 #{directTrigger} 在跟机器人说话 → 改引触发那条（点名优先）");
                replyTo = directTrigger;
            }
            else if (triggerWasEcho && chosen != triggerMessageId)
            {
                EmitLog($"不引用（本轮触发是复读/模仿，模型却指认了 #{chosen}）—— 宁可不引，也不把引用挂到被复读的原文上");
            }
            else
            {
                replyTo = chosen;
            }
        }
        else if (triggerMessageId is long trig)
        {
            // 只有“触发消息还是最新诉求”时才拿它当引用目标。
            // 触发已经过去了（后面有人插话）→ **不引用**：正文是在回答触发者，引用却会挂到
            // 插话的另一个人头上，QQ 里显示“回复某某”，群友看到就是“回复错人”（号主反馈的 bug）。
            // 不引用只是少一层上下文，挂错人却是实打实地抢了另一个人的话。
            if (triggerIsCurrent)
            {
                replyTo = trig;
            }
            else
            {
                EmitLog("不引用（触发消息已经过去了、后面有人插话）—— 宁可不引，也不把正文挂到别人头上");
            }
        }

        // 没有触发消息就**不引用**（戳一戳、主动发言都是这种）。
        // 线上实测（18:40）：被小明戳了之后回“手欠啊你”，引用却挂到了 55 分钟前另一条消息上 ——
        // 因为戳一戳不是消息、没有可引用的目标，启发式只能抽“上下文里最后一条别人的消息”。
        // QQ 客户端会把引用显示成“回复某某”，群友看到的就是“回复错人”。

        if (replyTo is long quoteTarget)
        {
            var targetIndex = IndexOfMessage(messages, quoteTarget);
            // 目标已被滚动窗口裁掉 → 不引用；目标后面还有人说话 → 需要引用指明回的是哪条
            replyTo = targetIndex >= 0 && targetIndex < messages.Count - 1 ? quoteTarget : null;
        }

        var (isGroup, targetId) = conversation.Target;

        // ───── 语音（模型填了 speak）─────
        // 怎么发：只把 TTS 的 /speak URL 交给协议端，让 NapCat 自己去下载 → 转 silk → 上传
        // （见 OneBotGateway.SendVoiceAsync）—— 机器人这边不碰音频编码。
        // 为什么得克制：合成要几秒 CPU、音频占流量、群里语音连发就是刷屏。
        // 提示词让它“偶尔用”，代码侧再加一道同会话 45 秒的闸门。
        string? voiceUrl = null;
        string? voiceSkipWhy = null;
        if (voiceText.Length > 0)
        {
            // 2026-09-21（号主要求“什么时候发语音让模型自己定”）：这里以前还有一道
            // “气氛沉（有人低落/在吵架）就一律不发语音”的硬拦，已删——那本来就是判断类的事，
            // 现在只把气氛（vibeHint）递给模型看，由它自己权衡。
            // 代码侧只留“技术性”限制：开关、字数上限（云端/协议端真有上限）、同会话频率下限（防刷屏）。
            var maxChars = Math.Clamp(_settings.VoiceMaxChars, 10, 300);
            if (!_settings.EnableVoice || _voice is null)
            {
                voiceSkipWhy = "语音消息开关是关的";
            }
            else if (voiceText.Length > maxChars)
            {
                voiceSkipWhy = $"{voiceText.Length} 字超过上限 {maxChars}";
            }
            else if (!AllowVoice(conversation, out var voiceReason))
            {
                voiceSkipWhy = voiceReason;
            }
            else if ((voiceUrl = _voice.BuildSpeakUrl(voiceText)) is null)
            {
                voiceSkipWhy = "TTS 服务地址没配置（应形如 http://tts:5000）";
            }
        }

        var voiceSent = false;
        if (voiceUrl is not null)
        {
            voiceSent = await _source.SendVoiceAsync(isGroup, targetId, voiceUrl);
            if (voiceSent)
            {
                _lastVoice[conversation.SourceKey] = DateTimeOffset.Now;
                EmitLog($"[Voice] 已发语音（{voiceText.Length} 字，音色 {_voice!.VoiceName}）：{Shorten(voiceText, 40)}");
            }
            else
            {
                // 失败就退化成文字：内容一定要落到群里（最差也得让群友看到它想说什么）。
                // 具体原因已由 SendVoiceAsync 把 retcode + 响应体打进日志。
                EmitLog("[Voice] record 段没发出去 → 改发文字");
            }
        }
        else if (voiceSkipWhy is not null)
        {
            EmitLog($"[Voice] 模型想用语音说，但{voiceSkipWhy} → 改发文字");
        }

        // 到底还发不发文字：
        //   • 语音发成功了、且 reply 就是那句话（或没写 reply）→ 不再重复发同一句；
        //   • 语音发成功了、但 reply 另写了内容 → 那是模型自己想补的话，照发；
        //   • 语音没发出去 → 至少把要说的话当文字发出去。
        var textReply = reply.Length > 0 ? reply : null;
        if (voiceText.Length > 0)
        {
            if (voiceSent)
            {
                if (string.Equals(textReply, voiceText, StringComparison.Ordinal))
                {
                    textReply = null;
                }
            }
            else
            {
                textReply ??= voiceText;
            }
        }

        // 落库的这条“自己说过的话”：要让模型下一轮知道自己刚才是用声音说的、说了什么
        var recordedText = voiceSent
            ? textReply is null ? $"[语音] {voiceText}" : $"{textReply}（同时用语音说：{voiceText}）"
            : reply.Length > 0 ? reply
            : voiceText.Length > 0 ? voiceText
            : "[表情包]";

        var appended = new ChatMessage
        {
            Role = MessageRole.Self,
            // 只发图（或只发语音）时也得在上下文里留个痕迹，否则模型下一轮不知道自己刚发过什么
            Text = recordedText,
            Timestamp = DateTimeOffset.Now
        };
        conversation.Append(appended);
        Touch(conversation);
        MessageAdded?.Invoke(conversation.SourceKey, appended);
        Save();

        var sent = voiceSent;
        if (textReply is not null && await SendWithCadenceAsync(isGroup, targetId, textReply, replyTo))
        {
            sent = true;
        }

        if (sticker is not null)
        {
            // 引用只给第一条消息，避免“文字 + 图”两条都带引用
            var sentImage = await SendStickerAsync(isGroup, targetId, sticker, textReply is not null ? null : replyTo);
            sent = sent || sentImage;
            _stickers.MarkUsed(sticker.Id);
            if (sentImage)
            {
                // 发出去才记账（失败不算）：这条日志也是排查“为什么又发了”的唯一现场
                var sinceText = _lastSticker.TryGetValue(conversation.SourceKey, out var prev)
                    ? $"（距上次发表情包 {(DateTimeOffset.Now - prev.At).TotalSeconds:F0} 秒）"
                    : string.Empty;
                _lastSticker[conversation.SourceKey] = (DateTimeOffset.Now, sticker.Id);
                EmitLog($"已发表情包 #{sticker.Id}{sinceText}");
            }
        }

        // 戳一戳（模型的可选动作）。只在“这个号码确实出现在本次上下文里”时才发 ——
        // 否则模型随口报个号也能戳到陌生人（同 replyTo 的防编造思路）。
        var pokeSent = false;
        if (pokeTarget is long pokeUserId)
        {
            var known = context.Any(m => m.SenderId == pokeUserId) ||
                        (_lastPoke.TryGetValue(conversation.SourceKey, out var lp) && lp.PokerId == pokeUserId);
            if (!known)
            {
                EmitLog($"模型想戳 {pokeUserId}，但这个人没在本次上下文里出现过 → 忽略（防编造号码）");
            }
            else if (!_mood.WillPokeBack(DateTimeOffset.Now, out var moodWhy))
            {
                // “不必每次被戳都回戳”：被戳太频繁时心情不好，代码侧直接拦下（不听模型的）
                EmitLog($"这次不戳 {pokeUserId}（{moodWhy}）");
            }
            else if (!AllowPokeSend(conversation, pokeUserId, out var pokeWhy))
            {
                EmitLog($"这次不戳 {pokeUserId}（{pokeWhy}）");
            }
            else
            {
                pokeSent = await _source.SendPokeAsync(isGroup, targetId, pokeUserId);
                if (pokeSent)
                {
                    _lastPokeSent[conversation.SourceKey] = (pokeUserId, DateTimeOffset.Now);
                }
                else
                {
                    EmitLog($"戳 {pokeUserId} 失败（协议端可能不支持戳一戳）");
                }
            }
        }

        // 引用目标写进日志（handoff-4 §23.3 B：“真验证需要把每次带引用的回复 + 上下文存下来人工看几十条”）。
        // 只写“带引用”的话，事后根本看不出引到了谁头上 —— 复盘只能靠猜。
        // 2026-09-16 加：连**触发那条**也写上 —— “正文回答 A、引用挂到 B”这类错位，
        // 只有把两边摆在一起才看得出来（号主反馈“引用错误”时就是靠这个定位的）。
        var quoteNote = string.Empty;
        if (triggerMessageId is long loggedTriggerId)
        {
            var trig = context.FirstOrDefault(m => m.QqMessageId == loggedTriggerId);
            if (trig is not null)
            {
                quoteNote += "，触发→" + (trig.SenderName ?? "?") + "「" + Shorten(trig.Text ?? string.Empty, 14) + "」";
            }
        }

        if (replyTo is long loggedQuoteId)
        {
            var quoted = context.FirstOrDefault(m => m.QqMessageId == loggedQuoteId);
            quoteNote = quoted is null
                ? "，带引用"
                : "，带引用→" + (quoted.SenderName ?? "?") + "「" + Shorten(quoted.Text ?? string.Empty, 18) + "」";
        }

        EmitLog(
            $"{(sent ? "已回复" : "回复失败")} {conversation.Name}（{elapsed:F0}ms 生成" +
            $"{(result.Suitability is int sc ? $"，自评 {sc}" : string.Empty)}" +
            (reply.Length > 0 ? $"，{reply.Length} 字" : string.Empty) +
            (voiceSent ? $"，语音 {voiceText.Length} 字" : string.Empty) +
            (sticker is not null ? $"，表情包 #{sticker.Id}（{StickerStore.Describe(sticker)}）" : string.Empty) +
            (pokeSent ? $"，戳了 {pokeTarget}" : string.Empty) +
            $"{quoteNote}）" +
            (reply.Length > 0 ? $": {reply}" : string.Empty));
    }

    /// <summary>每个会话最近一次主动戳人：用于戳一戳频率门。</summary>
    private bool AllowPokeSend(BotConversation conversation, long targetId, out string reason)
    {
        reason = string.Empty;
        if (!_lastPokeSent.TryGetValue(conversation.SourceKey, out var last))
        {
            return true;
        }

        var since = DateTimeOffset.Now - last.At;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.PokeCooldownSeconds));
        if (cooldown > TimeSpan.Zero && since < cooldown)
        {
            reason = $"距上次戳人才 {since.TotalSeconds:F0}s（下限 {cooldown.TotalSeconds:F0}s）";
            return false;
        }

        // 同一个人不反复戳：模型很容易顺着“你戳我我戳你”一直戳下去，群里看着就是刷屏
        if (last.TargetId == targetId && since < TimeSpan.FromMinutes(5))
        {
            reason = $"{since.TotalSeconds:F0}s 前刚戳过这个人";
            return false;
        }

        return true;
    }

    /// <summary>同一个会话两次发语音的最小间隔（秒）。见 <see cref="AllowVoice" />。</summary>
    /// <summary>
    /// 同一个会话两次发语音的最小间隔（秒）：**跟着面板的「语音积极性」缩放**。
    /// 为什么要跟着动：写死 45 秒时，“积极性拉到 100”其实一点也积极不起来（该发还是被拦）。
    /// 口径与提示词共用（<see cref="OpenAiClient.VoiceIntervalSeconds"/>）—— 模型看到的数字与真正拦住它的数字是同一个。
    /// </summary>
    private int VoiceMinIntervalSeconds => OpenAiClient.VoiceIntervalSeconds(_settings.VoiceEagerness);

    /// <summary>
    /// 语音频率门。为什么要它：
    ///   • 语音在群里是“稀罕事”，连发就是刷屏（和表情包同一个道理）；
    ///   • Piper 是 CPU 串行推理，一条要几秒，群里一热就是排队。
    /// 提示词里已经反复要求模型克制，这里再加一道代码闸门 —— 模型不听话时也能兜住。
    /// 想让它更松/更紧：改这个常量（故意不做成设置项，免得面板上多一个没人调的旋钮）。
    /// </summary>
    private bool AllowVoice(BotConversation conversation, out string reason)
    {
        reason = string.Empty;
        if (!_lastVoice.TryGetValue(conversation.SourceKey, out var last))
        {
            return true;
        }

        var since = DateTimeOffset.Now - last;
        if (since < TimeSpan.FromSeconds(VoiceMinIntervalSeconds))
        {
            reason = $"{since.TotalSeconds:F0}s 前刚发过语音（同一会话下限 {VoiceMinIntervalSeconds}s）";
            return false;
        }

        return true;
    }

    /// <summary>每个会话最近一次（实际发出去了的）表情包：用于频率门。key = 会话 key。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string Id)> _lastSticker = new();

    /// <summary>
    /// 表情包频率门。为什么要它：
    /// 线上实测库里只有两张时，模型会**每句都挂同一张**，群友直接开愤
    /// （“你别老是发这个表情包了”“这个bot只发奶龙”）。表情包是调味品，不是主食。
    /// </summary>
    private bool AllowSticker(BotConversation conversation, string stickerId, out string reason)
    {
        reason = string.Empty;
        var cooldown = Math.Max(0, _settings.StickerCooldownSeconds);
        if (!_lastSticker.TryGetValue(conversation.SourceKey, out var last))
        {
            return true;
        }

        var since = DateTimeOffset.Now - last.At;
        if (cooldown > 0 && since.TotalSeconds < cooldown)
        {
            reason = $"距上次发表情包只有 {since.TotalSeconds:F0} 秒（冷却 {cooldown} 秒）";
            return false;
        }

        // 同一张别在同一个会话里反复用（库里就那几张时最容易退化成“只会发这一张”）
        if (string.Equals(last.Id, stickerId, StringComparison.OrdinalIgnoreCase) && since.TotalMinutes < 10)
        {
            reason = $"这张 {stickerId} 十分钟内已经发过";
            return false;
        }

        return true;
    }

    /// <summary>发送一张表情包（读文件 → base64 → 协议端 image 段）。失败只记日志，不影响文字回复。</summary>
    private async Task<bool> SendStickerAsync(bool isGroup, long targetId, StickerRecord sticker, long? replyTo)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(sticker.AbsolutePath);
            return await _source.SendImageAsync(isGroup, targetId, bytes, replyToMessageId: replyTo);
        }
        catch (Exception ex)
        {
            EmitLog($"表情包发送失败（#{sticker.Id}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>日志用短文本（过长会把一行日志撞成好几行）。</summary>
    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>在会话里找某条 QQ 消息的下标（找不到返回 -1，例如已被滚动窗口裁掉）。</summary>
    private static int IndexOfMessage(IReadOnlyList<ChatMessage> messages, long? qqMessageId)
    {
        if (qqMessageId is not long id)
        {
            return -1;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].QqMessageId == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>机器人自己最后一条发言的下标（还没说过话返回 -1）。用来判断“这条触发消息是不是本轮的新诉求”。</summary>
    private static int LastSelfMessageIndex(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.Self)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 这条消息是不是“复读 / 模仿”：正文与上下文里**更早的一条**消息一字不差。
    /// 群里很常见的玩法（复读机、学机器人说话），但它是“引用挂错人”的高发场景 ——
    /// 这时候说话对象是复读的那个人，模型却容易把被复读的原文当成引用目标
    /// （线上实测：群友复读了机器人的话、机器人回“别学我说话！”，引用却挂到了别人那条上）。
    /// 只认“有实际内容的文本”：太短（“？”“6”）容易只是撞车，内容标记（[图片]/[表情:…]）是系统写的，都不算。
    /// </summary>
    private static bool IsEchoOfEarlierMessage(IReadOnlyList<ChatMessage> messages, int index)
    {
        if (index <= 0 || index >= messages.Count || messages[index].Recalled)
        {
            return false;
        }

        var text = messages[index].Text?.Trim() ?? string.Empty;
        if (!IsEchoCandidate(text))
        {
            return false;
        }

        for (var i = 0; i < index; i++)
        {
            if (messages[i].Recalled)
            {
                continue;   // 撤回的内容群里已经看不到了，谈不上“复读”
            }

            if (string.Equals(messages[i].Text?.Trim(), text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>能不能拿来做“复读”比对：一两个字的短句、以及内容标记（[图片]/[表情:…]）都不算。</summary>
    private static bool IsEchoCandidate(string text)
    {
        if (text.Length < 2)
        {
            return false;
        }

        if (text[0] is '[' or '【')
        {
            var close = text.IndexOfAny([']', '】']);
            if (close > 0 && IsContentMarker(text[1..close]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 本次要发的这句话，是不是和会话里自己上一条发言完全相同？
    /// （用于断掉“模型把自己上一条读进上下文 → 原样再发”的死循环）
    /// </summary>
    private static bool IsRepeatingOwnLastMessage(BotConversation conversation, string reply)
    {
        var messages = conversation.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != MessageRole.Self)
            {
                continue;
            }

            return string.Equals(messages[i].Text?.Trim() ?? string.Empty, reply.Trim(), StringComparison.Ordinal);
        }

        return false; // 自己还没说过话：不算复读
    }

    /// <summary>
    /// 发送回复。开启分句时按句末标点切分并留出打字间隔（更像真人）；
    /// 只有第一句带 QQ 的"回复"引用，后续分句不带。
    /// </summary>
    private async Task<bool> SendWithCadenceAsync(bool isGroup, long targetId, string reply, long? replyTo)
    {
        if (!_settings.SplitReplies)
        {
            var one = await _source.SendTextAsync(isGroup, targetId, reply, replyToMessageId: replyTo);
            RememberOwnMessage(one, reply);
            return one.Ok;
        }

        var segments = SplitSentences(reply);
        if (segments.Count <= 1)
        {
            var one = await _source.SendTextAsync(isGroup, targetId, reply, replyToMessageId: replyTo);
            RememberOwnMessage(one, reply);
            return one.Ok;
        }

        var allOk = true;
        for (var i = 0; i < segments.Count; i++)
        {
            var sent = await _source.SendTextAsync(
                isGroup,
                targetId,
                segments[i],
                replyToMessageId: i == 0 ? replyTo : null);
            RememberOwnMessage(sent, segments[i]);

            if (!sent.Ok)
            {
                allOk = false;
                EmitLog($"第 {i + 1}/{segments.Count} 段发送失败，停止后续分段");
                break;
            }

            // 打字节奏：基础间隔 + 按字数估算的输入时间
            if (i < segments.Count - 1)
            {
                var delay = Math.Max(0, _settings.SegmentDelayMs);
                await Task.Delay(delay);
            }
        }

        return allOk;
    }

    /// <summary>
    /// 记住“这句话是我（机器人）哪条消息发出去的”。为什么要记：
    /// 别人**引用回复机器人那句话**时，reply 段里只有被引用消息的 id —— 而机器人自己发的
    /// 消息在会话里没有 id（发送时协议端才给）。不记的话，模型只会看到“他在回一条我看不到的消息”，
    /// 而实际上他回的就是你上一句。只留最近 200 条（引旧消息的情况极少）。
    /// </summary>
    private void RememberOwnMessage(SendResult sent, string text)
    {
        if (!sent.Ok || sent.MessageId <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        EnsureOwnMessagesLoaded();
        _ownMessages[sent.MessageId] = (text, DateTimeOffset.Now);
        if (_ownMessages.Count > 200)
        {
            // 简单剪枝：把最老的一半丢掉（不做 LRU，没必要）
            foreach (var stale in _ownMessages.OrderBy(kv => kv.Value.At).Take(_ownMessages.Count / 2).ToList())
            {
                _ownMessages.TryRemove(stale.Key, out _);
            }
        }

        SaveOwnMessages();
    }

    /// <summary>机器人自己发出去的消息（id → 原话 + 时间），用于认出“别人引用回复了我说的哪句”。
    /// 落盘在 data/own-messages.json：以前只有内存表（上限 200、重启清空），
    /// 而每次部署都会重启 —— 于是“引用机器人上一句”在部署后全部认不出来（号主 2026-09-19 反馈被吞）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (string Text, DateTimeOffset At)> _ownMessages = new();
    private readonly object _ownMessagesFileLock = new();
    private int _ownMessagesLoaded;

    private static string OwnMessagesPath => Path.Combine(AppPaths.DataDir, "own-messages.json");

    /// <summary>首次用到时把落盘的“我发过哪些消息”读回来（懒加载，读失败就当空表）。</summary>
    private void EnsureOwnMessagesLoaded()
    {
        if (Interlocked.Exchange(ref _ownMessagesLoaded, 1) == 1)
        {
            return;
        }

        try
        {
            var path = OwnMessagesPath;
            if (!File.Exists(path))
            {
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idNode) ? idNode.GetInt64() : 0;
                var text = item.TryGetProperty("text", out var textNode) ? textNode.GetString() : null;
                var at = item.TryGetProperty("at", out var atNode) && atNode.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : DateTimeOffset.Now;
                if (id > 0 && !string.IsNullOrWhiteSpace(text))
                {
                    _ownMessages[id] = (text!, at);
                }
            }

            EmitLog($"记起了 {_ownMessages.Count} 条自己发过的消息（引用回复识别用）");
        }
        catch (Exception ex)
        {
            EmitLog("读取 own-messages.json 失败（当空表继续）：" + ex.Message);
        }
    }

    /// <summary>把“我发过哪些消息”落盘（只留最近 200 条，够认出引用回复）。</summary>
    private void SaveOwnMessages()
    {
        try
        {
            lock (_ownMessagesFileLock)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var items = _ownMessages
                    .OrderByDescending(kv => kv.Value.At)
                    .Take(200)
                    .Select(kv => new { id = kv.Key, text = kv.Value.Text, at = kv.Value.At })
                    .ToArray();
                File.WriteAllText(OwnMessagesPath, System.Text.Json.JsonSerializer.Serialize(items));
            }
        }
        catch (Exception ex)
        {
            EmitLog("写入 own-messages.json 失败（不影响聊天）：" + ex.Message);
        }
    }

    /// <summary>
    /// 引用原文的兜底：本地查不到时去协议端问一次（OneBot get_msg），查到就把真实原文补写进那条消息。
    /// fire-and-forget：不阻塞收消息（那是在接收循环上跑的），也不影响这一轮已经开始的生成。
    /// </summary>
    private void EnrichQuotedFromProtocolAsync(BotConversation conversation, ChatMessage appended, long quotedId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var (text, senderId) = await _source.GetMessageInfoAsync(quotedId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    EmitLog($"引用原文兜底：协议端取不到 #{quotedId} 的原文（保持“更早的消息”）");
                    return;
                }

                var botUin = _settings.NormalizedUin;
                var isSelf = !string.IsNullOrWhiteSpace(botUin) && senderId > 0 && senderId.ToString() == botUin;
                bool rewritten;
                lock (_quoteEnrichLock)
                {
                    // 期间可能已经被补过（或被裁剪），只认“还是那句更早的消息”的情况。
                    // 注意文字要与 BuildReplyAnnotation 的兜底原文完全一致：以前写成“更早的一条”，
                    // 顺序反了永远不匹配 —— 补写静默失效（2026-09-19 踩过）。
                    if (appended.Text is null || !appended.Text.Contains("已经看不到原文"))
                    {
                        return;
                    }

                    var body = appended.Text;
                    var close = body.IndexOf(']');
                    if (close >= 0 && body.StartsWith("[回复", StringComparison.Ordinal))
                    {
                        body = body[(close + 1)..].Trim();
                    }

                    var annot = BuildReplyAnnotation(isSelf ? "你" : "某人", text);
                    rewritten = conversation.RewriteText(appended, body.Length > 0 ? annot + " " + body : annot);
                }

                if (!rewritten)
                {
                    EmitLog($"引用原文兜底：取到了 #{quotedId} 的原文，但那条已经被裁出窗口（跳过）");
                    return;                      // 那条已经被裁出窗口了，没什么可补的
                }

                MessageAdded?.Invoke(conversation.SourceKey, appended);
                Save();
                EmitLog($"引用原文兜底：从协议端取到 #{quotedId} 的原文（{(isSelf ? "我发的" : "别人发的")}「{Shorten(text, 24)}」）");
            }
            catch (Exception ex)
            {
                EmitLog("引用原文兜底失败（不影响聊天）：" + ex.Message);
            }
        });
    }

    private readonly object _quoteEnrichLock = new();

    /// <summary>
    /// 按句末标点分句；过短的句子合并到相邻段，最多切 4 段（避免连发刷屏）。
    ///
    /// 这里踩过的坑（号主反馈“对标点或小数错误分段”）：
    ///   • 半角 `.` 曾经无条件当句末 —— “3.14”“1.5 倍”“v1.2”“github.com” 全被拦腰切；
    ///   • 连续的句末标点被拆开 —— “好耶！！！” 会在中间断，第二段以 “！！” 开头；
    ///   • 收尾的引号/括号落到下一段 —— “他说「好。」” 之后那段以 “」” 开头。
    /// 现在：半角点看前后文（前后是数字/字母就不算句末）、连续标点一次收走、
    /// 收尾符号跟着本段走；非常长的句子才退一步在逗号处断（不会憋出一条千字消息）。
    /// </summary>
    private static List<string> SplitSentences(string text)
    {
        const int MinSegmentLength = 6;
        const int MaxSegments = 4;
        const int SoftBreakLength = 60;   // 句内逗号处断行的长度下限

        var trimmed = text.Trim();
        if (trimmed.Length <= MinSegmentLength * 2)
        {
            return new List<string> { trimmed };
        }

        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            current.Append(ch);

            if (ch == '\n')
            {
                AddSegment(segments, current, MinSegmentLength);
                continue;
            }

            // 句内逗号：只有句子已经很长时才在它后面断（避免一逗就断、也避免千字一段）
            if (ch is '，' or '、' or '；' or ',' or ';' or '：' or ':')
            {
                if (current.Length >= SoftBreakLength)
                {
                    AddSegment(segments, current, MinSegmentLength);
                }

                continue;
            }

            if (!IsSentenceEnder(trimmed, i))
            {
                continue;
            }

            // 连续的句末标点一次收走：“！！！”“……”“？！” 不该被拆开
            while (i + 1 < trimmed.Length && IsSentenceEnder(trimmed, i + 1))
            {
                current.Append(trimmed[++i]);
            }

            // 收尾的引号 / 括号跟着本段走：“好。」” 不断在。后面
            while (i + 1 < trimmed.Length && IsSentenceCloser(trimmed[i + 1]))
            {
                current.Append(trimmed[++i]);
            }

            AddSegment(segments, current, MinSegmentLength);
        }

        // 尾部残句并入上一段，避免丢字
        if (current.Length > 0)
        {
            var tail = current.ToString().Trim();
            if (segments.Count == 0)
            {
                segments.Add(tail);
            }
            else
            {
                segments[^1] = (segments[^1] + tail).Trim();
            }
        }

        // 超过段数上限：多余内容合并进最后一段（不丢内容）
        if (segments.Count > MaxSegments)
        {
            var head = segments.Take(MaxSegments - 1).ToList();
            head.Add(string.Concat(segments.Skip(MaxSegments - 1)));
            segments = head;
        }

        return segments.Where(s => s.Length > 0).ToList();
    }

    /// <summary>够长就单独成段，否则继续往后攒（短句与下一句合并，读起来更像人）。</summary>
    private static void AddSegment(List<string> segments, System.Text.StringBuilder current, int minLength)
    {
        if (current.Length < minLength)
        {
            return;
        }

        segments.Add(current.ToString().Trim());
        current.Clear();
    }

    /// <summary>
    /// 这个位置算不算“句末”。半角点 / 叹号要额外看前后文：
    /// 前后是数字就是小数（3.14 / v1.2），后面紧接字母就是域名或文件名（github.com / a.exe）。
    /// </summary>
    private static bool IsSentenceEnder(string text, int index)
    {
        var ch = text[index];
        if (ch is '。' or '！' or '？' or '…' or '．' or '｡')
        {
            return true;
        }

        if (ch is not '!' and not '?' and not '.')
        {
            return false;
        }

        if (ch != '.')
        {
            return true;
        }

        var before = index > 0 ? text[index - 1] : '\0';
        var after = index + 1 < text.Length ? text[index + 1] : '\0';
        if (char.IsDigit(before) || char.IsDigit(after))
        {
            return false;   // 小数、版本号、IP、时间
        }

        return !char.IsLetter(after);   // 紧接字母：github.com / 文件名
    }

    /// <summary>收尾符号（引号、括号、波浪号）：断句时留在前一段。</summary>
    private static bool IsSentenceCloser(char ch)
        => ch is '」' or '』' or '】' or '》' or '〉' or '）' or ')' or ']' or '”' or '’' or '～' or '~' or '"' or '\'' or '〗' or '〞';

    // ══════════ 内部辅助 ══════════

    /// <summary>写文件日志，并推送给 Web UI 日志面板。</summary>
    private void EmitLog(string message)
    {
        FileLog.Write("Agent", message);
        try
        {
            LogLine?.Invoke(message);
        }
        catch
        {
            // UI 推送失败不影响主流程
        }
    }

    /// <summary>切换「AI 正在思考」状态并通知 UI。</summary>
    private void SetThinking(BotConversation conversation, bool thinking)
    {
        if (conversation.Thinking == thinking)
        {
            return;
        }

        conversation.Thinking = thinking;
        ThinkingChanged?.Invoke(conversation.SourceKey);
    }

    /// <summary>把超出滚动窗口的旧消息追加到归档文件（JSONL），不丢历史但也不占内存。</summary>
    private void ArchiveEvicted(string sourceKey, IReadOnlyList<ChatMessage> evicted)
    {
        // 归档进库（messages 表 archived=1），不再另开 .jsonl 文件：
        // 面板翻旧账、按时间/关键词查、以后做自动清理都是一句 SQL。
        try
        {
            _store.AppendArchive(sourceKey, evicted);
        }
        catch (Exception ex)
        {
            FileLog.Write("Archive", "归档失败: " + ex.Message);
        }
    }

    /// <summary>面板用：读某会话的归档（已溢出滚动窗口的旧消息，最新在前）。</summary>
    public List<ArchivedMessage> ReadArchive(string sourceKey, int limit)
        => _store.ReadArchive(sourceKey, limit);

    /// <summary>丢掉某会话的待回复项（删会话/改白名单时用）。在途那次不中斷，但不再补发后续。</summary>
    private void DropPending(string sourceKey)
    {
        _pendingReplies.TryRemove(sourceKey, out _);
        _pendingConversations.TryRemove(sourceKey, out _);
    }

    /// <summary>白名单变更后清掉不再允许的会话（与桌面版 ApplyWhitelistFilter 语义一致）。</summary>
    private void PruneNonWhitelisted()
    {
        List<BotConversation> removed;
        lock (_conversationsGate)
        {
            removed = _conversations.Where(c => !IsWhitelistedKey(c.SourceKey)).ToList();
            foreach (var c in removed)
            {
                _conversations.Remove(c);
            }
        }

        foreach (var c in removed)
        {
            _replyCooldown.TryRemove(c.SourceKey, out _);
            _historyRequested.TryRemove(c.SourceKey, out _);
            DropPending(c.SourceKey);
        }

        if (removed.Count > 0)
        {
            Save();
        }
    }
}
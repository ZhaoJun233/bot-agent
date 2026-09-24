using BotAgent.Domain.Agent;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Ports;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Panel;
using BotAgent.Services.Qq;
using System.Text.Json.Nodes;

namespace BotAgent.Services.Agent;

/// <summary>
/// 「//」agent 命令整块：解析 → 用户白名单 → 目标路由 → 跑起来 → 把结果说回群里，
/// 外加 agent 会话台账（新建/切换/重命名/删除/重置/导入 pi 会话）与面板要的会话清单。
///
/// 三条纪律（搬进来时原样保留，别改）：
///   ① **命令的权限落到具体某个人**：<c>AgentAllowedUsers</c> 空 = 谁都不能用；
///      官方通道额外认「官方白名单·私聊」里显式列出的人（别名号 8e15 起，见 <see cref="AgentHooks.IsOfficialUserAllowed" />）。
///   ② **结果只往它来的那个会话说**：<c>OnAgentProgress/OnAgentFinished</c> 按 sourceKey 回群，
///      找不到会话就丢（不回面板、也不乱发）。
///   ③ **命令不碰 UI**：所有回话都走 <see cref="AgentHooks.SendPlainAsync" />（不分句、不走人设）。
///
/// 宿主能力（日志、自己的 QQ 号、官方白名单、回话）从 <see cref="AgentHooks" /> 注入 —— 方向是「用例 → 宿主」。
/// </summary>
public sealed partial class AgentCommandService
{
    private readonly SettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly IModelClient _brain;
    private readonly ConversationRegistry _registry;
    private readonly PanelNotifier _ui;
    private readonly IAgentSessionStore _agentSessions;
    private readonly ServerAgentRunner _serverAgent;
    private readonly AgentBridgeServer? _agentBridge;
    private readonly IAgentImageStore _images;
    private readonly AgentHooks _hooks;

    public AgentCommandService(
        SettingsBox box,
        IQqChatSource source,
        IModelClient brain,
        ConversationRegistry registry,
        PanelNotifier ui,
        IAgentSessionStore agentSessions,
        ServerAgentRunner serverAgent,
        AgentBridgeServer? agentBridge,
        IAgentImageStore images,
        AgentHooks hooks)
    {
        _box = box;
        _source = source;
        _brain = brain;
        _registry = registry;
        _ui = ui;
        _agentSessions = agentSessions;
        _serverAgent = serverAgent;
        _agentBridge = agentBridge;
        _images = images;
        _hooks = hooks;

        // 进度/结果事件只服务这一块，订阅跟着搬过来（以前挂在 BotAgentHost 构造函数里）
        if (_agentBridge is not null)
        {
            _agentBridge.Progress += OnAgentProgress;
            _agentBridge.Finished += OnAgentFinished;
        }

        _serverAgent.Progress += OnAgentProgress;
        RebuildAgentUsers();
    }

    private AppSettings _settings => _box.Current;

    /// <summary>协议端（QQ 动作要用它）—— 只有 OneBot 这条路有。</summary>
    private IQqActions? Gateway => _source as IQqActions;

    // ══════════ 本机 Agent（// 命令，handoff-4 §31）══════════

    /// <summary>允许用 agent 的 QQ 号集合（改设置后会重建）。</summary>
    private HashSet<long> _agentUsers = new();

    /// <summary>节流“你没权限用”的回复：每会话每分钟最多一条（不然人人试一下就是刷屏）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _agentDeniedAt = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _agentProgressAt = new();

    /// <summary>重建 agent 用户白名单（跟随设置热更新）。</summary>
    public void RebuildAgentUsers()
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
                    var ext = string.IsNullOrWhiteSpace(downloaded.Value.Ext) ? ".jpg" : downloaded.Value.Ext;
                    var name = $"{Clock.LocalDateTime:yyyyMMdd-HHmmss}-{msg.MessageId}-{count}{ext}";
            saved = await _images.SaveAsync(dir, name, downloaded.Value.Data);
                }
            }
            catch (Exception ex)
            {
                _hooks.Log($"agent 图片留档失败（不影响任务）：{ex.GetType().Name} {ex.Message}");
            }

            lines.Add($"  图{count}: {url}");
            if (saved is not null)
            {
                lines.Add($"        服务器留档: /data/agent-images/{saved}（宿主 /opt/qqchat/data/agent-images/{saved}）");
            }
        }

            _images.Prune(dir, TimeSpan.FromDays(3));

        _hooks.Log($"agent 任务带了 {count} 张图（已把直链/留档写进任务正文）");
        return "\n\n（这条消息带了 " + count + " 张图片：agent 侧看不到图本体，需要就自己取 ——\n" +
               string.Join("\n", lines) +
               "\n  直链带时效签名、会过期，要看得尽快；取回存成本地文件后当图片打开（本机 pi 可用 read 读图）；" +
               "服务器上的 agent 直接读留档路径。）";
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
            _hooks.Log($"agent 命令处理异常: {ex.GetType().Name} {ex.Message}");
        }
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
            Id = $"p{Clock.Now.ToUnixTimeMilliseconds()}",
            SourceKey = "panel:test",
            Prompt = prompt,
            Session = "qqchat-panel",
            // 面板试跑：没有哪个群/哪个人，参数里的 sender/this 用不了（写死 QQ 号仍可用）
            QqHost = Gateway is null ? null : new SessionQqActionHost(Gateway, true, 0, 0, 0, _hooks.SelfId())
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
            _hooks.Log($"会话改名失败（无所谓，不影响清空）: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>丢给服务器 agent 的 QQ 动作现场</summary>
    private IQqActionHost? BuildQqHost(BotConversation conversation, QqChatMessage msg)
    {
        if (Gateway is null)
        {
            return null;
        }

        var (isGroup, targetId) = conversation.Target;
        return new SessionQqActionHost(Gateway, isGroup, targetId, msg.UserId, msg.MessageId, _hooks.SelfId());
    }

    /// <summary>服务器内置 agent 正在跑的会话（单会话串行）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _serverAgentBusy = new();

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
            var conv = _registry.Snapshot().FirstOrDefault(c => c.SourceKey == key);
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
    private JsonObject BuildSessionNode(string sourceKey, AgentSession s)
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
    public AgentSession CreateAgentSession(string sourceKey, string backend, string? name)
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
    public AgentSession? ImportPiSession(string sourceKey, string piSessionId, string? name)
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
            string.IsNullOrWhiteSpace(title) ? null : AgentSessionText.AutoTitle(title),
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
            var conv = _registry.Snapshot().FirstOrDefault(c => c.SourceKey == key);
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
            var ago = Clock.Now - s.UpdatedAt;
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
        var now = Clock.Now;
        if (_agentDeniedAt.TryGetValue(sourceKey, out var last) && now - last < TimeSpan.FromSeconds(60))
        {
            return false;
        }

        _agentDeniedAt[sourceKey] = now;
        return true;
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

            var now = Clock.Now;
            if (_agentProgressAt.TryGetValue(task.SourceKey, out var last) && now - last < TimeSpan.FromSeconds(every))
            {
                return;
            }

            _agentProgressAt[task.SourceKey] = now;
            var elapsed = now - task.StartedAt;
            var note = string.IsNullOrWhiteSpace(task.LastNote) ? string.Empty : $"（{MaybeMask(task.LastNote, task.SourceKey)}）";
            var place = string.IsNullOrWhiteSpace(task.DeviceName) ? "服务器" : task.DeviceName!;
            await _hooks.SendPlainAsync(conversation, $"⏳ 还在{place}上跑…已 {elapsed.TotalSeconds:F0} 秒{note}");
        }
        catch (Exception ex)
        {
            _hooks.Log("agent 进度回话失败: " + ex.Message);
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
                _hooks.Log($"[会话] 「{session.Name}」记下本轮对话（共 {saved.Count} 条 / {saved.Sum(x => x.Text.Length)} 字）——下一句接着聊");
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

            var seconds = task.DurationMs > 0 ? task.DurationMs / 1000.0 : (Clock.Now - task.StartedAt).TotalSeconds;
            if (task.Ok)
            {
                _hooks.Log($"agent 完成（{seconds:F0}s，{task.ToolCalls} 次工具调用）: {TextRules.Shorten(task.Text ?? string.Empty, 80)}");

                // agent 会翻日志/数据，结论里很可能带 QQ 号或群友昵称 —— 回群前过一遍脱敏开关
                //（面板里的“列出会话时脱敏”默认是开的；关掉就原样发，号主自己的选择）。
                await _hooks.SendPlainAsync(conversation, MaybeMask(task.Text ?? string.Empty, task.SourceKey));
            }
            else
            {
                _hooks.Log($"agent 失败（{seconds:F0}s）: {task.Error}");
                await _hooks.SendPlainAsync(conversation, $"❌ 本机那边报错（{seconds:F0}s）：{TextRules.Shorten(task.Error ?? "未知错误", 300)}");
            }

            // 跑完再综结标题：拿最近的轮次（含刚刚这轮）给模型，综结出一个能认出“这个会话在干什么”的标题。
            // 放在回话之后（主人先看到结果），失败也不影响任何东西；手动改过名的会话不动。
            await SummarizeSessionTitleAsync(task);
        }
        catch (Exception ex)
        {
            _hooks.Log("agent 结果回话失败: " + ex.Message);
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
                _hooks.Log($"[会话] 按上下文综结标题：「{current.Name}」→「{title}」");
            }
        }
        catch (Exception ex)
        {
            _hooks.Log("[会话] 综结标题失败（不影响任务）: " + ex.Message);
        }
    }

    // ══════════ 宿主要用的窄口（改名/转发用，不改语义） ══════════

    /// <summary>按开关决定要不要遮盖文本（开关关掉就原样返回）。策略在 <see cref="MaskingRules" />。</summary>
    private string MaybeMask(string text, string? sourceKey = null)
        => MaskingRules.Text(_settings.AgentMaskSensitive, text, sourceKey is null ? null : _registry.KnownNames(sourceKey));

    /// <summary>按开关决定聊天的显示名：开=「群聊 940***75」，关=真名。</summary>
    private string ChatLabel(BotConversation conversation)
        => MaskingRules.ChatLabel(_settings.AgentMaskSensitive, conversation);

    /// <summary>按 sourceKey 找会话（agent 结果回来时只能用 key）。</summary>
    private bool ConversationsByKey(string sourceKey, out BotConversation conversation)
    {
        conversation = _registry.Find(sourceKey)!;
        return conversation is not null;
    }

    // ══════════ 入站与面板的入口（同名转发，签名不变） ══════════

    /// <summary>这条消息是不是 agent 命令（<c>//</c> 开头）。是则拆出真正的提示词。</summary>
    public bool TryParseCommand(string rawText, out string payload) => TryParseAgentCommand(rawText, out payload);

    /// <summary>跑一条 agent 命令（异常只记日志：命令挂了不该带崩接收循环）。</summary>
    public Task RunCommandSafeAsync(BotConversation conversation, QqChatMessage msg, string payload)
        => RunAgentCommandSafeAsync(conversation, msg, payload);
}

/// <summary>
/// agent 命令这块要用到的宿主能力（由 BotAgentHost 提供实现）。
/// 全是回调而不是接口实现，是为了让宿主那边**一个方法都不新增**：接线只写在构造函数的参数里。
/// </summary>
/// <param name="Log">普通运行日志（写文件 + 推面板）。</param>
/// <param name="SelfId">登录的 QQ 号（0 = 未知）；QQ 动作里要带上它。</param>
/// <param name="IsOfficialUserAllowed">
/// 官方通道里这个（别名）号是不是被「官方白名单·私聊」显式列出 —— 列出才算有权限（空 = 全部接受不算）。
/// </param>
/// <param name="SendPlainAsync">发一条纯文本（agent 回话专用：不走人设、不分句、不受群冷却限制）。</param>
public readonly record struct AgentHooks(
    Action<string> Log,
    Func<long> SelfId,
    Func<long, bool> IsOfficialUserAllowed,
    Func<BotConversation, string, Task> SendPlainAsync);

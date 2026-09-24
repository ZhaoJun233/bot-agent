using BotAgent.Domain.Agent;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using System.Text.Json.Nodes;

namespace BotAgent.Services.Agent;

/// <summary>
/// `//` agent 命令的**分发与派发**（批次 6 从 AgentCommandService.cs 抽出来，纯搬迁）：
/// 「谁能用」→「是不是子命令」→「两个后端各跑一边」，各自一个方法。
///
/// 为什么分出来：那条 if/else 长链原本 530 行，读的人要在「权限判定 / 状态查询 / 会话管理 / 派任务」
/// 之间来回跳。拆完之后**主线只剩 60 行**，每段都能一眼看完 —— 判定条件、日志措辞、分支顺序一字未改。
/// </summary>
public sealed partial class AgentCommandService
{
    private async Task HandleAgentCommandAsync(BotConversation conversation, QqChatMessage msg, string payload)
    {
        // 本机 Agent 桥 / 服务器 agent 共用的入口
        if (_agentBridge is null && _serverAgent is null)
        {
            return;
        }
        var bridge = _agentBridge;


        // 权限闸单独一个方法：它与「这条命令到底怎么跑」无关，挤在分发链上只会让主线看不清。顺序一字未改。
        if (!await AllowAgentCommandAsync(conversation, msg, payload))
        {
            return;
        }

        _hooks.Log($"agent 命令（{msg.UserId}）: {TextRules.Shorten(payload, 120)}");

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


        // 不派任务的子命令（//stop //status //help + //sessions 那些会话管理）整块挪进了
        // TryHandleAgentSubCommandAsync —— 它们只动台账，不该混在「派任务」的主线里。
        if (await TryHandleAgentSubCommandAsync(conversation, msg, payload, want, named, bridge))
        {
            return;
        }

        if (payload.Length == 0)
        {
            await _hooks.SendPlainAsync(conversation, HelpText(conversation.SourceKey));
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
            await ReplyNoRouteAsync(conversation, named, bridge, hostSwitch, serverSwitch, hostOnline, deviceDisabled, onlineDeviceName);
            return;
        }

        if (useHost)
        {
            await RunOnHostAsync(conversation, bridge, payload, named);
            return;
        }

        // 服务器内置 agent（与外部设备**互不影响**：它不占外部队列，两边可以同时跑）
        await RunOnServerAsync(conversation, msg, payload);
    }

    /// <summary>
    /// 权限闸：这条 `//` 命令是不是白名单里的人发的（不是就记一条日志 + 节流回一句）。
    /// 官方通道额外认一种写法（把**别名号**加进「官方白名单·私聊」）——
    /// 2026-09-21 那次「官方白名单里的人用不了 `//` 指令」的修复，判定条件逐字搬来。
    /// </summary>

    private async Task<bool> AllowAgentCommandAsync(BotConversation conversation, QqChatMessage msg, string payload)
    {
        var who = msg.UserId.ToString();
        // 官方通道的“能用 agent 的人”额外认一种写法：把它加进**官方白名单·私聊**
        // （2026-09-21 号主报的“官方白名单里的人用不了 // 指令”：那边发送者是别名号 8e15 起，
        //  而 AgentAllowedUsers 里填的是私域真号 ✗ → 永远匹配不上 ✗ 直接被拒）。
        // 为什么只认“私聊白名单”而不认群：群白名单是“这个群可以用命令”的意思太宽了 ——
        // 执行命令的权限必须落到**具体某个人**身上（官方白名单显式列出才算，空 = 全部接受不算 ✗）。
        var officialExplicit = Channels.IsOfficial(msg.Channel) && _hooks.IsOfficialUserAllowed(msg.UserId);
        var allowed = _agentAllowAll
            || (_agentUsers.Count > 0 && _agentUsers.Contains(msg.UserId))
            || officialExplicit;
        if (!allowed)
        {
            // 权限不够：日志必记，回话节流（每会话 60 秒一条）
            _hooks.Log(Channels.IsOfficial(msg.Channel)
                ? $"agent 命令被拒（{who} 不在 AgentAllowedUsers 里；官方通道要么把这个**别名号**填进 AgentAllowedUsers，要么把它加进「官方白名单·私聊」）: {TextRules.Shorten(payload, 40)}"
                : $"agent 命令被拒（{who} 不在 AgentAllowedUsers 里）: {TextRules.Shorten(payload, 40)}");
            if (ShouldReplyDenied(conversation.SourceKey))
            {
                await _hooks.SendPlainAsync(conversation, "这个功能只给白名单用户用～");
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// 不派任务的子命令：`//stop`（取消）、`//status`（状态）、`//help`（用法），
    /// 以及会话管理（下面三个 TryHandleSession*）。返回 true = 这条命令已经被吃掉。
    /// ⚠ 这些分支必须留在回复派发之前：它们是纯台账操作，不该被当成任务派给 agent。
    /// </summary>
    private async Task<bool> TryHandleAgentSubCommandAsync(
        BotConversation conversation, QqChatMessage msg, string payload, string want, string? named, AgentBridgeServer? bridge)
    {
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

            await _hooks.SendPlainAsync(conversation,
                cancelled > 0 ? $"已让它停掉（{cancelled} 个任务）。" : "现在没有在跑的任务。");
            return true;
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

            await _hooks.SendPlainAsync(conversation,
                $"外部设备 agent：{(hostOn ? "开" : "关")}（在线：{(online ? deviceDetail : "无")}）\n" +
                $"服务器内置 agent：{(serverOn ? "开" : "关")}（工具 {(_settings.AgentServerTools.Length == 0 ? "全部" : _settings.AgentServerTools)}）\n" +
                $"  QQ 动作：{QqActionCatalog.Summarize(QqActionCatalog.ParseAllowed(_settings.AgentServerQqActions))}\n" +
                $"优先：{preferred}\n当前会走：{willUse}\n单条指定：//@server … 或 //@host … 或 //@设备名 …");
            return true;
        }

        if (head is "help" or "?" or "帮助" or "命令")
        {
            await _hooks.SendPlainAsync(conversation, HelpText(conversation.SourceKey));
            return true;
        }


        // 会话管理三块：查询（//sessions //runs //pi）、接用与改名（//import //rename）、增删切（//new //use //del //reset）
        if (await TryHandleSessionQueryAsync(conversation, payload, head, want, named, bridge))
        {
            return true;
        }

        if (await TryHandleSessionImportAsync(conversation, payload, head, want, named, bridge))
        {
            return true;
        }

        if (await TryHandleSessionMutationAsync(conversation, payload, head, want, named, bridge))
        {
            return true;
        }

        return false;
    }

    /// <summary>会话查询：`//sessions`（含 `//sessions all`）、`//runs`（执行记录）、`//pi`（设备上的 pi 会话）。</summary>
    private async Task<bool> TryHandleSessionQueryAsync(
        BotConversation conversation, string payload, string head, string want, string? named, AgentBridgeServer? bridge)
    {
        var rest = payload.Length > head.Length ? payload[(head.Length)..].Trim() : string.Empty;

        if (head is "sessions" or "会话" or "session")
        {
            // //sessions all = 所有聊天的总数与标题（“现在有多少个会话及其标题”）
            if (rest.Equals("all", StringComparison.OrdinalIgnoreCase) || rest is "全部" or "所有")
            {
                await _hooks.SendPlainAsync(conversation, DescribeAllSessions(bridge));
                return true;
            }

            await _hooks.SendPlainAsync(conversation, DescribeSessions(conversation.SourceKey, bridge));
            return true;
        }

        if (head is "runs" or "流水" or "记录")
        {
            var cur = _agentSessions.FindCurrent(conversation.SourceKey, ResolveRoute(want, bridge, named).Backend);
            if (cur is null)
            {
                await _hooks.SendPlainAsync(conversation, "这个聊天还没有 agent 会话（发一条 //指令 会自动建一个，或 //new 新建）。");
                return true;
            }

            var runs = _agentSessions.Runs(conversation.SourceKey, cur.Id);
            if (runs.Count == 0)
            {
                await _hooks.SendPlainAsync(conversation, $"会话「{MaybeMask(cur.Name, conversation.SourceKey)}」还没有执行记录。");
                return true;
            }

            var lines = new List<string> { $"会话「{MaybeMask(cur.Name, conversation.SourceKey)}」的执行记录（最近 {runs.Count} 次）：" };
            for (var i = 0; i < Math.Min(8, runs.Count); i++)
            {
                var r = runs[i];
                var when = r.At.ToString("MM-dd HH:mm");
                var state = r.Ok is null ? "⏳ 在跑" : r.Ok.Value ? "✅" : "❌";
                var extra = r.Ok is null ? string.Empty : $"{r.DurationMs / 1000.0:F0}s{(r.ToolCalls > 0 ? $"/{r.ToolCalls}工具" : string.Empty)}";
                lines.Add($"{i + 1}. {when} {state}{extra} {MaybeMask(TextRules.Shorten(r.Prompt, 24), conversation.SourceKey)}" +
                          (r.Result.Length > 0 ? $" → {MaybeMask(TextRules.Shorten(r.Result, 26), conversation.SourceKey)}" : string.Empty));
            }

            lines.Add("（//sessions 看会话、//pi 看设备上 pi 里的会话）");
            await _hooks.SendPlainAsync(conversation, string.Join("\n", lines));
            return true;
        }

        if (head is "pi" or "Pi" or "PI")
        {
            if (bridge is null || !bridge.Connected)
            {
                await _hooks.SendPlainAsync(conversation, "外部设备不在线，列不出它上面的 pi 会话。");
                return true;
            }

            var got = await bridge.RequestPiSessionsAsync(named ?? string.Empty);
            await Clock.Delay(1200);
            var list = _agentSessions.LastPiSessions(named);
            if (!got || list.Count == 0)
            {
                await _hooks.SendPlainAsync(conversation, "设备没上报 pi 会话（桥版本旧？重启一下桥）。");
                return true;
            }

            var lines = new List<string> { $"设备上的 pi 会话（{list.Count} 个，最近的在前）：" };
            for (var i = 0; i < Math.Min(10, list.Count); i++)
            {
                var it = list[i];
                var title = it["title"]?.GetValue<string>();
                var when = DateTimeOffset.FromUnixTimeSeconds(it["mtime"]?.GetValue<long>() ?? 0).ToLocalTime().ToString("MM-dd HH:mm");
                var shown = string.IsNullOrWhiteSpace(title) ? "(无标题)" : MaybeMask(TextRules.Shorten(title, 30), conversation.SourceKey);
                lines.Add($"{i + 1}. {when} {shown}");
            }

            lines.Add("想把某个接过来当自己的会话：//import 序号（或 //import <会话id>）");
            await _hooks.SendPlainAsync(conversation, string.Join("\n", lines));
            return true;
        }


        return false;
    }

    /// <summary>接用设备上的 pi 会话（`//import`）与给当前会话改名（`//rename`）。</summary>
    private async Task<bool> TryHandleSessionImportAsync(
        BotConversation conversation, string payload, string head, string want, string? named, AgentBridgeServer? bridge)
    {
        var rest = payload.Length > head.Length ? payload[(head.Length)..].Trim() : string.Empty;
        if (head is "import" or "导入")
        {
            if (rest.Length == 0)
            {
                await _hooks.SendPlainAsync(conversation, "用法：//import <序号|会话id>（先 //pi 看设备上有什么）");
                return true;
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
                await _hooks.SendPlainAsync(conversation, "没认出你说的是哪个会话（先 //pi 列一遍，或直接给会话 id）。");
                return true;
            }

            var created = _agentSessions.Create(conversation.SourceKey, "host",
                string.IsNullOrWhiteSpace(piTitle) ? null : AgentSessionText.AutoTitle(piTitle), named, piId, piOwned: false);
            await _hooks.SendPlainAsync(conversation,
                $"已把 pi 会话「{MaybeMask(created.Name, conversation.SourceKey)}」接过来当当前会话（id {MaybeMask(piId, conversation.SourceKey)}）——下一句 //指令 就接着它的上下文跑。\n" +
                "注意：这是设备上已有的会话，//del 只会从列表里去掉、不会删它的文件。");
            return true;
        }

        if (head is "rename" or "改名")
        {
            if (rest.Length == 0)
            {
                await _hooks.SendPlainAsync(conversation, "用法：//rename <新名字>（给`//sessions`里标「←」那个会话改名）");
                return true;
            }

            // 关键：这里**不新建**会话，也不按“下一句会走哪个后端”去找 ——
            // 否则号主看到的是 A 会话，改的却是 B（甚至凭空建一个空的）；也正好是“rename 改错会话”那个 bug。
            var cur = _agentSessions.FindCurrent(conversation.SourceKey, ResolveRoute(want, bridge, named).Backend);
            if (cur is null)
            {
                await _hooks.SendPlainAsync(conversation, "这个聊天还没有 agent 会话（发一条 //指令 会自动建一个，或 //new 新建）。");
                return true;
            }

            if (_agentSessions.Rename(conversation.SourceKey, cur.Id, rest))
            {
                var where = cur.Backend == "server" ? "服务器内置" : $"外部 {cur.Device ?? bridge?.AnyBridge?.Name ?? "设备"}";
                // 回话里名字**原样回显**（主人自己打的字，再遮一道只会让人以为改错了）；
                // 但要写清楚改的是哪一个会话：哪条后端、多少轮。
                await _hooks.SendPlainAsync(conversation,
                    $"已把 [{where}] 里那个会话（{cur.Turns} 轮）改名为「{rest}」。\n" +
                    "（它现在是手动命名，按上下文自动综结不会再动它；//sessions 里那行会带「←」）");
            }
            else
            {
                await _hooks.SendPlainAsync(conversation, "改名没成功（名字空？）。");
            }

            return true;
        }

        return false;
    }

    /// <summary>新建（`//new`）、切换（`//use`）、删除（`//del`）、清空历史（`//reset`）。</summary>
    private async Task<bool> TryHandleSessionMutationAsync(
        BotConversation conversation, string payload, string head, string want, string? named, AgentBridgeServer? bridge)
    {
        var rest = payload.Length > head.Length ? payload[(head.Length)..].Trim() : string.Empty;
        if (head is "new" or "新会话")
        {
            var backend = ResolveRoute(want, bridge, named).Backend;
            var created = _agentSessions.Create(conversation.SourceKey, backend, rest.Length > 0 ? rest : null, named);
            await _hooks.SendPlainAsync(conversation,
                $"已开新会话「{MaybeMask(created.Name, conversation.SourceKey)}」（{(backend == "server" ? "服务器内置" : $"外部 {created.Device ?? bridge?.AnyBridge?.Name ?? "设备"}")}）。" +
                "下一句 //指令 就从空上下文开始；想切回去用 //use 名字。");
            return true;
        }

        if (head is "use" or "switch" or "切换")
        {
            if (rest.Length == 0)
            {
                await _hooks.SendPlainAsync(conversation, "用法：//use <会话名或序号>（先 //sessions 看列表）");
                return true;
            }

            var target = ResolveSessionRef(conversation.SourceKey, rest);
            if (target is null || !_agentSessions.Use(conversation.SourceKey, target, out var used))
            {
                await _hooks.SendPlainAsync(conversation, $"没找到会话「{rest}」。先 //sessions 看看有哪些。");
                return true;
            }

            await _hooks.SendPlainAsync(conversation,
                $"好，切到会话「{MaybeMask(used!.Name, conversation.SourceKey)}」（{(used.Backend == "server" ? "服务器内置" : "外部设备")}，已有 {used.Turns} 轮）。" +
                "下一句 //指令 就接在它后面。");
            return true;
        }

        if (head is "del" or "delete" or "rm" or "删除")
        {
            if (rest.Length == 0)
            {
                await _hooks.SendPlainAsync(conversation, "用法：//del <会话名或序号>（先 //sessions 看列表）");
                return true;
            }

            var target = ResolveSessionRef(conversation.SourceKey, rest);
            var deleted = target is null ? null : _agentSessions.Delete(conversation.SourceKey, target);
            if (deleted is null)
            {
                await _hooks.SendPlainAsync(conversation, $"没找到会话「{rest}」。先 //sessions 看看有哪些。");
                return true;
            }

            // 外部后端且这个 pi 会话是我们建的：让设备把它也删掉（导入进来的不动，那不是我们的）
            if (deleted.Backend != "server" && deleted.PiOwned && deleted.PiSessionId.Length > 0 && bridge is not null)
            {
                await bridge.ForgetSessionAsync(deleted.PiSessionId);
            }

            await _hooks.SendPlainAsync(conversation, $"已删除会话「{MaybeMask(deleted.Name, conversation.SourceKey)}」。当前会话已自动换成新的。");
            return true;
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
                await _hooks.SendPlainAsync(conversation, "这个聊天还没有 agent 会话（发一条 //指令 会自动建一个，或 //new 新建）。");
                return true;
            }

            await _hooks.SendPlainAsync(conversation,
                $"已清空（{string.Join("、", cleared)}）—— 下一句从零开始，不会再带着上一轮的话题。");
            return true;
        }

        return false;
    }

    /// <summary>两个后端都用不了时的那句实话（哪边关着、哪边不在线、指定的设备怎么了）。</summary>
    private async Task ReplyNoRouteAsync(
        BotConversation conversation, string? named, AgentBridgeServer? bridge,
        bool hostSwitch, bool serverSwitch, bool hostOnline, bool deviceDisabled, string? onlineDeviceName)
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

            await _hooks.SendPlainAsync(conversation, "这条没法跑：" + string.Join("；", parts) +
                (parts.Count == 0 ? "没有可用的 agent" : string.Empty) + "。面板里打上开关、或指定另一边试试（//@server / //@host）。");
            return;
    }

    /// <summary>派给外部设备（本机那座桥）。队列满了 / 设备缺模型都要当场说清楚，别让人等一个不会来的结果。</summary>

    private async Task RunOnHostAsync(BotConversation conversation, AgentBridgeServer? bridge, string payload, string? named)
    {
            var hostSession = _agentSessions.EnsureCurrent(conversation.SourceKey, "host");
            var task = bridge!.NewTask(conversation.SourceKey, payload, hostSession.PiSessionId, named);
            task.SessionRef = hostSession;
            task.RunId = _agentSessions.StartRun(conversation.SourceKey, hostSession.Id, payload, named);   // 记一条“小会话”
            // 注意：这里**不再**拿指令当标题。标题改成“跑完后按上下文综结”，
            // 否则每发一条新命令就把会话改名成那条命令的前几个字，会话号就认不出来了。
            if (!bridge.TryEnqueue(task))
            {
                await _hooks.SendPlainAsync(conversation, $"这个会话已经排了 {bridge.QueuedCount} 个任务，等跑完再发吧。");
                return;
            }

            await _hooks.SendPlainAsync(conversation,
                bridge.Current is null
                    ? $"收到，去{(named ?? bridge.AnyBridge?.Name ?? "号主设备")}上跑一下（会话「{MaybeMask(hostSession.Name, conversation.SourceKey)}」）：{TextRules.Shorten(payload, 40)}"
                    : "收到，排在后面 —— 做完我告诉你。");

            // 面板里给这台设备配的模型它自己没有 → 提前说一声（不然群里只会看到结果，不知道降级了）
            var liveModel = _settings.DeviceConfigFor(named ?? bridge.AnyBridge?.Name)?.Model;
            if (!string.IsNullOrWhiteSpace(liveModel) && bridge.DeviceModels(named).Length > 0 &&
                !bridge.DeviceModels(named).Contains(liveModel, StringComparer.OrdinalIgnoreCase))
            {
                await _hooks.SendPlainAsync(conversation,
                    $"⚠️ 面板里给这台设备配的模型「{liveModel}」它没有（pi 只认 provider/model 这种写法）—— " +
                    "这次先用它的默认模型跑，能选的话在面板设备表里重选一个。");
            }

            return;
    }

    /// <summary>
    /// 派给服务器内置 agent（与外部设备互不影响：它不占外部队列）。
    /// “接着上一句说”只有两种入口：面板开关，或本条指令写 `//接着 …`（默认**不**带上下文）；
    /// 同一会话串行（忙的时候如实回一句），自备 CTS 是为了让 `//stop` 能把正在跑的 bash 也杀掉。
    /// </summary>

    private async Task RunOnServerAsync(BotConversation conversation, QqChatMessage msg, string payload)
    {
        if (_serverAgentBusy.TryAdd(conversation.SourceKey, true))
        {
            // “接着上一句说”只有两种入口：面板开关，或本条指令写 //接着 …（默认**不**带上下文）
            var (wantsContinue, promptText) = StripContinuePrefix(payload);
            var useHistory = wantsContinue || _settings.AgentServerKeepContext;

            var serverSession = _agentSessions.EnsureCurrent(conversation.SourceKey, "server");
            var seededHistory = useHistory ? _agentSessions.History(conversation.SourceKey, serverSession.Id) : new List<(string, string)>();
            var serverRunId = _agentSessions.StartRun(conversation.SourceKey, serverSession.Id, promptText, null);
            _hooks.Log(useHistory
                ? $"[会话] 内置 agent 本轮带 {seededHistory.Count} 条历史（会话「{serverSession.Name}」，{(wantsContinue ? "//接着" : "面板开关开着")}）"
                : $"[会话] 内置 agent 本轮**不带**上文（每条指令单独对待）：{TextRules.Shorten(promptText, 40)}");
            var task = new AgentTask
            {
                Id = $"s{Clock.Now.ToUnixTimeMilliseconds()}",
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

            await _hooks.SendPlainAsync(conversation, $"收到，我在服务器上跑一下（会话「{MaybeMask(serverSession.Name, conversation.SourceKey)}」）：{TextRules.Shorten(payload, 40)}");
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
        await _hooks.SendPlainAsync(conversation, "这个会话上一条 //指令还在跑，等它完事再发。");
    }
}

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
        // 服务器内置 agent 是 cfg-0.3.3 唯一保留的 // 后端。
        if (_serverAgent is null)
        {
            return;
        }

        // 权限闸单独一个方法：它与「这条命令到底怎么跑」无关，挤在分发链上只会让主线看不清。顺序一字未改。
        if (!await AllowAgentCommandAsync(conversation, msg, payload))
        {
            return;
        }

        // 隐私红线（AGENTS §1/§3）：日志里不写原始 QQ 号，也不写命令正文，只留脱敏标识与长度。
        // 与自然对话那条同口径（AgentCommandService.Natural.cs）。
        _hooks.Log($"agent 命令（{AgentMask.Shorten(msg.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture))}）: 长度 {payload.Length}");

        // 子命令：//stop 取消、//status 看状态、//（空）看用法
        // @目标 要先剥掉：这样 //@server status 这类写法也能认出来
        var (wantParsed, payloadStripped) = StripTargetPrefix(payload);
        payload = payloadStripped;

        var want = wantParsed.Length > 0 ? wantParsed : (_settings.AgentTarget ?? "auto").Trim();
        var named = want.Length > 0 &&
                    !new[] { "auto", "server", "服务器", "host", "外部" }
                        .Contains(want, StringComparer.OrdinalIgnoreCase)
            ? want
            : null;

        // 已移除目标必须在任何会话创建/入队/模型调用之前拒绝（含 //@host new、//import）。
        var route = ResolveRoute(want, named);
        if (route.RemovedTarget)
        {
            await ReplyRemovedTargetAsync(conversation, named ?? want);
            return;
        }

        // 不派任务的子命令（//stop //status //help + //sessions 那些会话管理）整块挪进了
        // TryHandleAgentSubCommandAsync —— 它们只动台账，不该混在「派任务」的主线里。
        if (await TryHandleAgentSubCommandAsync(conversation, msg, payload, want, named))
        {
            return;
        }

        if (payload.Length == 0)
        {
            await _hooks.SendPlainAsync(conversation, HelpText(conversation.SourceKey));
            return;
        }

        // 图片：agent 任务只传正文时图片只剩「[图片]」占位；把直链与“服务器留档”写进正文。
        payload += await BuildAgentImageNoteAsync(msg);

        if (!route.UseServer)
        {
            await ReplyNoRouteAsync(conversation, route.ServerSwitch);
            return;
        }

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
        // （2026-09-21 管理员报的“官方白名单里的人用不了 // 指令”：那边发送者是别名号 8e15 起，
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
        BotConversation conversation, QqChatMessage msg, string payload, string want, string? named)
    {
        var head = payload.Split(' ', 2)[0].ToLowerInvariant();
        if (head is "stop" or "cancel" or "停止" or "取消" or "中断")
        {
            var cancelled = 0;
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
        {
            var preferred = (_settings.AgentTarget ?? "auto").Trim();
            var serverOn = _settings.EnableServerAgent;
            var decision = ResolveRoute(want, named);
            var willUse = decision.RemovedTarget
                ? "（已退役的 host/@设备目标；请把 AgentTarget 改成 server 或 auto）"
                : decision.UseServer ? "服务器 agent" : "（服务器 agent 开关关闭）";

            await _hooks.SendPlainAsync(conversation,
                $"服务器内置 agent：{(serverOn ? "开" : "关")}（工具 {(_settings.AgentServerTools.Length == 0 ? "全部" : _settings.AgentServerTools)}）\n" +
                $"  QQ 动作：{QqActionCatalog.Summarize(QqActionCatalog.ParseAllowed(_settings.AgentServerQqActions))}\n" +
                $"优先：{preferred}\n当前会走：{willUse}\n单条指定：//@server …（//@host、//@设备名、//import、//pi 已退役）");
            return true;
        }

        if (head is "help" or "?" or "帮助" or "命令")
        {
            await _hooks.SendPlainAsync(conversation, HelpText(conversation.SourceKey));
            return true;
        }


        // 会话管理三块：查询（//sessions //runs）、改名（//rename）、增删切（//new //use //del //reset）
        if (await TryHandleSessionQueryAsync(conversation, payload, head, want, named))
        {
            return true;
        }

        if (await TryHandleSessionImportAsync(conversation, payload, head, want, named))
        {
            return true;
        }

        if (await TryHandleSessionMutationAsync(conversation, payload, head, want, named))
        {
            return true;
        }

        return false;
    }

    /// <summary>会话查询：`//sessions`（含 `//sessions all`）、`//runs`（执行记录）。</summary>
    private async Task<bool> TryHandleSessionQueryAsync(
        BotConversation conversation, string payload, string head, string want, string? named)
    {
        var rest = payload.Length > head.Length ? payload[(head.Length)..].Trim() : string.Empty;

        if (head is "sessions" or "会话" or "session")
        {
            // //sessions all = 所有聊天的总数与标题（“现在有多少个会话及其标题”）
            if (rest.Equals("all", StringComparison.OrdinalIgnoreCase) || rest is "全部" or "所有")
            {
                await _hooks.SendPlainAsync(conversation, DescribeAllSessions());
                return true;
            }

            await _hooks.SendPlainAsync(conversation, DescribeSessions(conversation.SourceKey));
            return true;
        }

        if (head is "runs" or "流水" or "记录")
        {
            var cur = _agentSessions.FindCurrent(conversation.SourceKey, "server");
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

            lines.Add("（//sessions 看会话）");
            await _hooks.SendPlainAsync(conversation, string.Join("\n", lines));
            return true;
        }

        if (head is "pi" or "Pi" or "PI" or "import" or "导入")
        {
            await _hooks.SendPlainAsync(conversation, "//pi / //import 已随本机桥退役；现在只有服务器内置 agent 会话。");
            return true;
        }


        return false;
    }

    /// <summary>接用 pi 会话已退役；保留 //rename。</summary>
    private async Task<bool> TryHandleSessionImportAsync(
        BotConversation conversation, string payload, string head, string want, string? named)
    {
        var rest = payload.Length > head.Length ? payload[(head.Length)..].Trim() : string.Empty;
        if (head is "import" or "导入")
        {
            await _hooks.SendPlainAsync(conversation, "//import 已随本机桥退役；现在只有服务器内置 agent 会话。");
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
            // 否则管理员看到的是 A 会话，改的却是 B（甚至凭空建一个空的）；也正好是“rename 改错会话”那个 bug。
            var cur = _agentSessions.FindCurrent(conversation.SourceKey, "server");
            if (cur is null)
            {
                await _hooks.SendPlainAsync(conversation, "这个聊天还没有 agent 会话（发一条 //指令 会自动建一个，或 //new 新建）。");
                return true;
            }

            if (_agentSessions.Rename(conversation.SourceKey, cur.Id, rest))
            {
                var where = "服务器内置";
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
        BotConversation conversation, string payload, string head, string want, string? named)
    {
        var rest = payload.Length > head.Length ? payload[(head.Length)..].Trim() : string.Empty;
        if (head is "new" or "新会话")
        {
            var route = ResolveRoute(want, named);
            if (!route.UseServer)
            {
                await ReplyNoRouteAsync(conversation, route.ServerSwitch);
                return true;
            }
            var created = _agentSessions.Create(conversation.SourceKey, "server", rest.Length > 0 ? rest : null, null);
            await _hooks.SendPlainAsync(conversation,
                $"已开新会话「{MaybeMask(created.Name, conversation.SourceKey)}」（服务器内置）。" +
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
                $"好，切到会话「{MaybeMask(used!.Name, conversation.SourceKey)}」（服务器内置，已有 {used.Turns} 轮）。" +
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

            // host 历史只读：Delete 对非 server 返回 null，这里不会删掉旧设备会话。
            await _hooks.SendPlainAsync(conversation, $"已删除会话「{MaybeMask(deleted.Name, conversation.SourceKey)}」。当前会话已自动换成新的。");
            return true;
        }

        if (head is "reset" or "clear" or "清空")
        {
            // cfg-0.3.3 起只清服务器会话；host 历史是只读留存，不能被 //reset 改掉。
            var current = _agentSessions.FindCurrent(conversation.SourceKey, "server");
            if (current is null)
            {
                await _hooks.SendPlainAsync(conversation, "这个聊天还没有服务器 agent 会话（发一条 //指令 会自动建一个，或 //new 新建）。");
                return true;
            }

            var had = current.History.Count;
            var hadRuns = current.Runs.Count;
            var oldTitle = current.Name;
            var reset = _agentSessions.Reset(conversation.SourceKey, current.Id);
            if (reset is null)
            {
                await _hooks.SendPlainAsync(conversation, "清空没成功（会话可能已不存在）。");
                return true;
            }

            // 标题也换回中性的自动名：历史清了但标题还挂着旧话题，看着就像没清。
            if (oldTitle.Length > 0)
            {
                RenameSessionQuietly(conversation.SourceKey, current.Id);
            }

            var parts = new List<string>();
            if (had > 0)
            {
                parts.Add($"{had} 条历史");
            }

            if (hadRuns > 0)
            {
                parts.Add($"{hadRuns} 条执行记录");
            }

            await _hooks.SendPlainAsync(conversation,
                $"已清空服务器会话「{MaybeMask(oldTitle, conversation.SourceKey)}」{(parts.Count > 0 ? "（" + string.Join("、", parts) + "）" : "（本来就空）")}—— 下一句从零开始。");
            return true;
        }

        return false;
    }

    /// <summary>服务器 agent 用不了时的那句实话（开关关着，或请求指向已退役的 host）。</summary>
    private async Task ReplyNoRouteAsync(BotConversation conversation, bool serverSwitch)
    {
        await _hooks.SendPlainAsync(conversation, serverSwitch
            ? "这条没法跑：服务器 agent 现在不可用。"
            : "这条没法跑：服务器 agent 开关是关的，请在面板里打上。");
    }

    /// <summary>已移除的 host/@设备目标：在创建会话、入队、模型调用之前明确拒绝。</summary>
    private async Task ReplyRemovedTargetAsync(BotConversation conversation, string target)
    {
        await _hooks.SendPlainAsync(conversation,
            $"「{MaybeMask(target, conversation.SourceKey)}」是已退役的外部设备目标；本机 pi 桥已移除，// 命令现在只走服务器内置 agent。" +
            "请把 AgentTarget 改成 server/auto，并使用 //@server。");
    }


    /// <summary>
    /// 派给服务器内置 agent（cfg-0.3.3 唯一保留的 // 后端）。
    /// “接着上一句说”只有两种入口：面板开关，或本条指令写 `//接着 …`（默认**不**带上下文）；
    /// 同一会话串行（忙的时候如实回一句），自备 CTS 是为了让 `//stop` 能把正在跑的 bash 也杀掉。
    /// </summary>

    private async Task RunOnServerAsync(BotConversation conversation, QqChatMessage msg, string payload,
        string? ack = null, bool natural = false)
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
                SourceLease = PlatformSourceExecution.Current,
                Prompt = promptText,
                Session = serverSession.PiSessionId,
                SessionRef = serverSession,
                History = seededHistory,
                UseHistory = useHistory,
                // 现场：不做这一步，模型知道“点赞”却不知道给谁点、在哪条消息上点
                QqHost = BuildQqHost(conversation, msg),
                Natural = natural
            };
            task.RunId = serverRunId;

            // 自备一个 CTS：//stop 要能把正在跑的那条 bash 也杀掉（不能只标个标记）
            var cts = task.SourceLease is { } lease
                ? CancellationTokenSource.CreateLinkedTokenSource(lease.Lifetime) : new CancellationTokenSource();
            _serverAgentCurrent[conversation.SourceKey] = (task, cts);

            // 自然对话：用模型自己那句接话（没有就给一句口语的），不报会话名、不复述指令
            await _hooks.SendPlainAsync(conversation, natural
                ? (string.IsNullOrWhiteSpace(ack) ? "好，我去弄一下～" : ack.Trim())
                : $"收到，我在服务器上跑一下（会话「{MaybeMask(serverSession.Name, conversation.SourceKey)}」）：{TextRules.Shorten(payload, 40)}");
            _ = Task.Run(async () =>
            {
                using var sourceScope = PlatformSourceExecution.Enter(task.SourceLease);
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
        await _hooks.SendPlainAsync(conversation, natural
            ? "上一件事我还在弄，等我弄完再接这个～（想让我先停下就说一声“停一下”）"
            : "这个会话上一条 //指令还在跑，等它完事再发。");
    }
}

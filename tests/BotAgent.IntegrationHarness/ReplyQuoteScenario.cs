using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S19 回归：QQ“回复引用”挂错消息。
///
/// 线上现象（用户截图）：群里 11:24 有人说“域名要配dns解析的”，随后话题转到了螺蛳粉上；
/// 机器人最后那句正文明明在回“明明是超香的好不好”，QQ 的回复引用却挂在那条 11:24 的旧消息上，
/// 看起来就是“回复错人/回错消息”。
///
/// 机制：每条消息都会 EnqueueReply(msgId)，进的是该会话的 FIFO 队列；模型慢（常见 15~20 秒）
/// 时队列会积压，等轮到某个旧触发生成时，模型看到的上下文早就是最新那几句了 ——
/// 而旧实现只认排队时那个 id，于是正文在聊新话题、引用挂在旧消息上。
///
/// 本场景构造：A 触发第一次请求（模型 2.5 秒才回），B/C/D 在请求在途期间陆续进来并排队。
/// 第二次生成（触发消息是 B）时，上下文里最新一条别人发的消息是 D：
///   ✓ 引用应该是 D（模型真正在回的那条）或干脆不带引用
///   ✗ 绝不能是 B（排队时那个旧触发）
///
/// 2026-09-15 追加（handoff-4 §23：管理员截图“别学我说话！”引用挂错人）：
///   replyTo 的语义（你在跟谁说话 / 不是素材）写进提示词；
///   代码侧再兜一道 —— 本轮触发若是“复读/模仿”，模型的指认不在触发那条上就不引用
///   （宁可不引，也不把引用挂到被复读的原文上）。
///
/// 2026-09-15 追加（handoff-4 §27：管理员“识别不了引用回复消息”）：
///   **收**方向的 reply 段不再丢掉 —— 标记成 `[回复 X「…」]` 进上下文；
///   引用目标依次从会话上下文、机器人自己发过的消息、段内摘要里找；
///   正文为空（只点“回复”）也算内容，不再整条丢弃。
/// </summary>
public static partial class Program
{
    internal static async Task RunReplyQuoteScenarioAsync()
    {
        Section("S19 回复引用不能挂到排队积压的旧消息上");

        const int openAiPort = 17822;   // 注意：18106–19065 被 Windows/Hyper-V 保留，别用
        const int botWsPort = 13030;
        const long groupId = 66681;

        // 四个消息 id 分开写清楚：A 触发第一次请求，B 是第二次请求的“旧触发”，D 是上下文里最新那条
        const long msgA = 7301;
        const long msgB = 7302;
        const long msgC = 7303;
        const long msgD = 7304;

        var dataDir = NewDataDir("s19");

        // 慢模型是复现的关键：请求在途期间，后面的消息才有机会排队积压
        using var openAi = new MockOpenAi(openAiPort) { ResponseDelayMs = 2500 };
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 99, "reply": "回A的那句"}""");
        openAi.EnqueueReply("""{"suitability": 99, "reply": "回最新那句"}""");
        openAi.EnqueueReply("""{"suitability": 99, "reply": "再回一句"}""");
        openAi.EnqueueReply("""{"suitability": 99, "reply": "第四句"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",   // 别分句，一条回复就是一次 send_group_msg
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_PROFILE_SUMMARY"] = "0", // 别让画像巡检占掉脚本回复
            ["QQCHAT_STICKERS"] = "0",        // 本场景只关心引用，关掉表情包避免干扰
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // A：第一次请求就地开始（此时上下文里只有 A）
        await protocol.SendGroupMessageAsync(groupId, 30001, "老王", "这个报错怎么修", msgA, ct: cts.Token);
        await Task.Delay(300);

        // B/C/D：模型还在生成第一条回复，它们只能排队 —— 于是 B 成了“积压的旧触发”
        await protocol.SendGroupMessageAsync(groupId, 30002, "群友A", "域名要配dns解析的", msgB, ct: cts.Token);
        await Task.Delay(300);
        await protocol.SendGroupMessageAsync(groupId, 30003, "小张", "我只是想吃个螺蛳粉", msgC, ct: cts.Token);
        await Task.Delay(300);
        await protocol.SendGroupMessageAsync(groupId, 30004, "小李", "明明超香的好不好", msgD, ct: cts.Token);

        // 等 4 次生成全部跑完（每次约 2.5s）
        await WaitUntilAsync(
            () => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") >= 4,
            TimeSpan.FromSeconds(60));
        await Task.Delay(1500);

        var sends = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => !string.IsNullOrWhiteSpace(MessageText(a)))
            .ToList();

        Check("4 条消息各自触发了一次回复", sends.Count >= 4, $"实际 {sends.Count} 次：{string.Join(" | ", sends.Select(MessageText))}");

        if (sends.Count == 0)
        {
            await bot.StopAsync();
            return;
        }

        var quotes = sends.Select(QuotedMessageId).ToList();

        // 第一次回复就是回 A，而且 A 后面确实有人说话 → 引用应该还在（证明引用没被整体关掉）
        Check("触发消息仍是最新诉求时照旧带引用（引用 A）",
            quotes[0] == msgA, $"引用 id = {quotes[0]}");

        // 核心断言：第二次生成触发的是 B，但模型看到的上下文里最新一条别人发的消息是 D。
        // 旧实现会把引用挂到 B（排队时那个旧触发）→ 线上表现就是“正文聊螺蛳粉、引用挂 11:24 那条”；
        // 后来改成“挂上下文里最新那条（D）”—— 仍然是把正文挂到了**另一个人**头上（D 是小李，不是提问的老王），
        // 群友看到的还是“回复错人”。现在的口径：**触发已经过去就不引用**。
        Check("★ 积压的旧触发不会被当成引用目标",
            quotes.All(q => q != msgB), $"引用 id = {string.Join(", ", quotes.Select(q => q?.ToString() ?? "无"))}");

        Check("★ 触发消息已过去时干脆不引用（宁可不引，不把正文挂到别人头上）",
            quotes.Count > 1 && quotes[1] is null,
            $"第二条引用 id = {(quotes.Count > 1 ? quotes[1]?.ToString() ?? "无" : "(缺)")}，期望“无”");

        // 顺带兜一层：不能把正文挂到别人头上（既不能是中间那条 C，也不能是最新的 D）
        Check("★ 正文不会被挂到别人头上（既不是 C 也不是 D）",
            quotes.All(q => q != msgC && q != msgD),
            $"引用 id = {string.Join(", ", quotes.Select(q => q?.ToString() ?? "无"))}");

        // ---- 根本解法：让模型自己指认“我在回哪条” ----
        // 启发式只能猜（最新那条 / 排队触发），而模型最清楚自己在接哪个哏：
        // 提示词里最近几条别人的消息都带了 (#id)，它可以回一个 replyTo。
        const long msgE = 7305;
        const long msgF = 7306;
        var before = sends.Count;

        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "接的是C那个哏", "replyTo": {{msgC}}}""");
        await protocol.SendGroupMessageAsync(groupId, 30005, "老王", "接着聊", msgE, ct: cts.Token);
        await WaitUntilAsync(
            () => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") > before,
            TimeSpan.FromSeconds(40));
        await Task.Delay(800);

        var afterChoice = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => MessageText(a).Contains("接的是C那个哏"))
            .LastOrDefault();
        Check("★ 模型用 replyTo 指认的目标被采信（它自己知道在回哪句）",
            QuotedMessageId(afterChoice) == msgC,
            $"引用 id = {QuotedMessageId(afterChoice)?.ToString() ?? "无"}，期望 {msgC}（模型指认的是 C）");

        // 编造一个不存在的编号：绝不能把引用挂到他不认识的消息上（QQ 会报错或引到别人头上）
        const long bogusId = 999999999;
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "编造编号", "replyTo": {{bogusId}}}""");
        await protocol.SendGroupMessageAsync(groupId, 30006, "老王", "再来一句", msgF, ct: cts.Token);
        await WaitUntilAsync(
            () => protocol.ActionsReceived
                .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
                .Any(a => MessageText(a).Contains("编造编号")),
            TimeSpan.FromSeconds(40));
        await Task.Delay(800);

        var afterBogus = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => MessageText(a).Contains("编造编号"))
            .LastOrDefault();
        Check("★ 模型编造的 replyTo 不会被采信（防挂到不存在的消息上）",
            QuotedMessageId(afterBogus) != bogusId,
            $"引用 id = {QuotedMessageId(afterBogus)?.ToString() ?? "无"}");

        // ══════════ 2026-09-15：引用指向“被吐槽的素材”而不是“自己在跟谁说话” ══════════
        // 管理员给的真实现场（handoff-4 §23）：“别学我说话！还有你，消停点别祸害大家了！”
        // 引用挂在了胡桃那条（被吐槽的内容）上，而真正在说话的对象是把它那句复读过去的 c。
        // 这是模型自己指认的 replyTo 语义错位 —— §22 的启发式修不了，只能在两处下手：
        //   ① 提示词把 replyTo 的语义写死（“你在跟谁说话”，不是“素材/出处”）；
        //   ② 代码侧兜底：本轮触发是“复读/模仿”时，模型的指认只要不在触发那条上就不引用。
        var quotePrompt = openAi.Requests.Select(SystemText).LastOrDefault(t => t.Contains("[回复谁]"));
        Check("★ 提示词写死了 replyTo 的语义（跟谁说话 / 不是素材 / 复读场景）",
            quotePrompt is not null &&
            quotePrompt.Contains("你这句话是在跟谁说话") && quotePrompt.Contains("复读") &&
            quotePrompt.Contains("一条消息只对一个人说话"),
            SectionOf(quotePrompt ?? "(没有一次请求带 [回复谁] 段)", "[回复谁]"));
        Check("★ 提示词里有『底线』约束（不骂人、不替别人赶人走）",
            quotePrompt is not null && quotePrompt.Contains("底线") && quotePrompt.Contains("不替别人赶人走"),
            SectionOf(quotePrompt ?? "", "[先读懂气氛再说话]"));

        // 拿机器人自己刚说过的一句当“被复读的原文”（群里就是这么玩的）
        var echoText = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Last(t => !string.IsNullOrWhiteSpace(t));

        // ① 有人复读了机器人那句话；模型却把引用指到了**更早的别人那条**上（msgC）→ 宁可不引
        const long msgG = 7307;
        const long msgH = 7308;
        const long msgI = 7309;
        const long msgJ = 7310;
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "别学我说话！", "replyTo": {{msgC}}}""");
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");          // 后面那条跟话 → 沉默
        var beforeEcho = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
        await protocol.SendGroupMessageAsync(groupId, 30007, "小李", echoText, msgG, ct: cts.Token);
        await Task.Delay(300);
        await protocol.SendGroupMessageAsync(groupId, 30008, "老王", "行了行了", msgH, ct: cts.Token);
        await WaitUntilAsync(
            () => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") > beforeEcho,
            TimeSpan.FromSeconds(40));
        await Task.Delay(1000);

        var afterEcho = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => MessageText(a).Contains("别学我说话"))
            .LastOrDefault();
        Check("★ 复读场景：模型把引用指到别处时不引用（不挂到被复读的原文/别人头上）",
            QuotedMessageId(afterEcho) is null,
            $"引用 id = {QuotedMessageId(afterEcho)?.ToString() ?? "无"}（模型指认的是 #{msgC}）");
        Check("★ 复读场景：日志写明了丢弃原因（运维能复盘）",
            bot.OutputLines.Any(l => l.Contains("复读/模仿") && l.Contains("不引用")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("不引用")).TakeLast(3)));

        // ② 同样是复读场景，但模型指认的就是**复读那条** → 引用照旧采信（引到复读的人）
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "学人说话挺没意思的。", "replyTo": {{msgI}}}""");
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");          // 后面那条跟话 → 沉默
        var beforeEcho2 = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
        await protocol.SendGroupMessageAsync(groupId, 30009, "小张", echoText, msgI, ct: cts.Token);
        await Task.Delay(300);
        // 后面这条让复读那条不再是“最新一条”—— 引用才有存在意义（目标后面得有人说话）
        await protocol.SendGroupMessageAsync(groupId, 30010, "老王", "接着说", msgJ, ct: cts.Token);
        await WaitUntilAsync(
            () => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") > beforeEcho2,
            TimeSpan.FromSeconds(40));
        await Task.Delay(1000);

        var afterEcho2 = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => MessageText(a).Contains("学人说话挺没意思"))
            .LastOrDefault();
        Check("★ 复读场景：模型指认复读那条时照旧引用（引到复读的人）",
            QuotedMessageId(afterEcho2) == msgI,
            $"引用 id = {QuotedMessageId(afterEcho2)?.ToString() ?? "无"}，期望 {msgI}");

        // ③ “一字不差”的两个故意例外：内容标记（[图片]/[表情:…]）与太短的文本不算复读。
        // 两个人各发一张图，正文都是我们自己写的 “[图片]” —— 若把它当复读，很容易误伤真引用（群里斗图很常见）。
        const long msgK = 7311;
        const long msgL = 7312;
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");        // 老王那张图 → 沉默（别占掉下一条脚本）
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "又在斗图？", "replyTo": {{msgK}}}""");
        await protocol.SendGroupMessageAsync(groupId, 30011, "老王", "[图片]", msgK, ct: cts.Token);
        await Task.Delay(300);
        await protocol.SendGroupMessageAsync(groupId, 30012, "小李", "[图片]", msgL, ct: cts.Token);
        await WaitUntilAsync(
            () => protocol.ActionsReceived
                .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
                .Any(a => MessageText(a).Contains("又在斗图")),
            TimeSpan.FromSeconds(40));
        await Task.Delay(800);

        var afterMarker = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => MessageText(a).Contains("又在斗图"))
            .LastOrDefault();
        Check("★ 内容标记（[图片]）不算复读：引用不会被误伤",
            QuotedMessageId(afterMarker) == msgK,
            $"引用 id = {QuotedMessageId(afterMarker)?.ToString() ?? "无"}，期望 {msgK}");

        // ══════════ 收方向的引用回复：以前 reply 段被直接丢掉（管理员反馈“识别不了引用回复消息”） ══════════
        // 现象：群友点“回复”引用某条时，机器人只看到新那句（“我也是”），不知道在回哪条，
        // 也认不出“他在回机器人自己上一句” —— 模型只能瞎猜。
        // 现在：reply 段会解析成目标 id，再由 BotAgentHost 从上下文（或“自己发过的消息”）查出原文，
        // 标成 `[回复 老王「…」]` / `[回复 你「…」]` 一起进模型上下文。
        var silence = """{"suitability": 0, "reply": ""}""";
        const long msgM = 7313;   // 老王：被引用的那条
        const long msgN = 7314;   // 小李：引用 msgM
        const long msgO = 7315;   // 老王：引用机器人自己上一句
        const long msgP = 7316;   // 小明：CQ 码形式的引用
        const long msgQ = 7317;   // 引用一个已经取不到的 id

        openAi.ClearRequests();
        openAi.EnqueueReply(silence);
        await protocol.SendGroupMessageAsync(groupId, 30013, "老王", "我昨天说的那个 bug", msgM, ct: cts.Token);
        await Task.Delay(400);

        // ① 引用别人的话 → 上下文里要能看到「被引用的是谁、说了什么」
        openAi.EnqueueReply(silence);
        await protocol.SendGroupMessageAsync(groupId, 30014, "小李", "我也是这么想的", msgN, replyTo: msgM, ct: cts.Token);
        var replied = await WaitForRequestAsync(openAi, r => r.Contains("我也是这么想的"), TimeSpan.FromSeconds(40));
        Check("★ 引用回复被认出来了：上下文里标出被引用的人与原话",
            replied is not null && replied.Contains("[回复 老王「我昨天说的那个 bug」]"),
            replied is null ? "(没等到请求)" : Snippet(replied, "[回复 老王"));
        Check("★ 落库的也是带标注的文本（面板与上下文一致）",
            await WaitUntilAsync(() => DbProbe.Count(dataDir,
                "SELECT COUNT(1) FROM messages WHERE text LIKE '%[回复 老王%'") >= 1, TimeSpan.FromSeconds(5)),
            DbProbe.Dump(dataDir, "SELECT text FROM messages WHERE source_key = 'group:66681' ORDER BY seq DESC LIMIT 3"));

        // ② 引用机器人自己那句 → 用“你”（模型才分得清别人是在跟它说话）
        var botMsgId = protocol.SentMessageIds.Last();
        var botText = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Last(t => !string.IsNullOrWhiteSpace(t));
        openAi.EnqueueReply(silence);
        await protocol.SendGroupMessageAsync(groupId, 30015, "老王", "那我再确认一下", msgO, replyTo: botMsgId, ct: cts.Token);
        var replyToBot = await WaitForRequestAsync(openAi, r => r.Contains("那我再确认一下"), TimeSpan.FromSeconds(40));
        Check("★ 引用机器人自己那句时标成「你」",
            replyToBot is not null && replyToBot.Contains($"[回复 你「{botText}」]"),
            replyToBot is null ? "(没等到请求)" : Snippet(replyToBot, "[回复 你"));

        // ③ CQ 码形式（有的协议端 raw_message 里就是 [CQ:reply,id=…]）也不能漏
        openAi.EnqueueReply(silence);
        await protocol.SendGroupRawCqAsync(groupId, 30016, "小明",
            $"[CQ:reply,id={msgM}]这条我同意", msgP, cts.Token);
        var cqReply = await WaitForRequestAsync(openAi, r => r.Contains("这条我同意"), TimeSpan.FromSeconds(40));
        Check("★ CQ 码形式（[CQ:reply,id=…]）同样能认出引用",
            cqReply is not null && cqReply.Contains("[回复 老王「我昨天说的那个 bug」]"),
            cqReply is null ? "(没等到请求)" : Snippet(cqReply, "[回复 老王"));

        // ④ 引用的那条已经不在我这边（更早/重启前）→ 不编内容，但明确说清“那是引用回复”
        openAi.EnqueueReply(silence);
        await protocol.SendGroupMessageAsync(groupId, 30017, "小明", "上面那条呢", msgQ, replyTo: 999999999, ct: cts.Token);
        var missReply = await WaitForRequestAsync(openAi, r => r.Contains("上面那条呢"), TimeSpan.FromSeconds(40));
        Check("★ 引用目标取不到时不编内容（只标“更早的消息”）",
            missReply is not null && missReply.Contains("更早的消息"),
            missReply is null ? "(没等到请求)" : Snippet(missReply, "[回复"));

        // ⑤ 只点“回复”不写正文（正文是一个空格）—— 线上验收就是这么测的（handoff-4 §27.5）：
        //    以前“正文为空”会被当成无内容的消息**在网关层直接丢掉**，连日志都没有，
        //    于是机器人既没记住这条、也没标出它引用了什么（管理员：还是没有看到识别）。
        const long msgR0 = 7318;   // 老王：被引用的那条（用一个唯一暗号，方便断言不撞旧请求）
        const long msgR = 7319;    // 小李：只点回复、不写正文
        openAi.EnqueueReply(silence);
        await protocol.SendGroupMessageAsync(groupId, 30018, "老王", "引用空正文的暗号ABC", msgR0, ct: cts.Token);
        await Task.Delay(400);

        openAi.EnqueueReply(silence);
        await protocol.SendGroupMessageAsync(groupId, 30019, "小李", " ", msgR, replyTo: msgR0, ct: cts.Token);
        var emptyBody = await WaitForRequestAsync(openAi, r => r.Contains("[回复 老王「引用空正文的暗号ABC」]"), TimeSpan.FromSeconds(40));
        Check("★ 只点“回复”不写正文时照样认出引用（不再整条丢掉）",
            emptyBody is not null,
            emptyBody is null ? "(没等到请求)" : Snippet(emptyBody, "[回复 老王"));
        Check("★ 空正文的引用回复也落库（正文就是那句标注）",
            await WaitUntilAsync(() => DbProbe.Count(dataDir,
                "SELECT COUNT(1) FROM messages WHERE text = $t", ("$t", "[回复 老王「引用空正文的暗号ABC」]")) >= 1,
                TimeSpan.FromSeconds(5)),
            DbProbe.Dump(dataDir, "SELECT text FROM messages WHERE source_key = 'group:66681' ORDER BY seq DESC LIMIT 3"));
        Check("★ 网关日志里能看到这条引用（排障不用猜）",
            bot.OutputLines.Any(l => l.Contains("引用回复：消息 7319")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("引用回复")).TakeLast(3)));

        // ══════════ 2026-09-16：人家在跟你说话，引用却挂到别人头上 ══════════
        // 真实现场（群里）：
        //   群友A：「跟你说话了吗」（**引用机器人那句**在质问它）
        //   机器人：「哼，等某某发力你都等到猴年马月了……」—— 引用挂的是**群友B**那条
        // 正文在答群友B 不算错，但在群里看到的是“有人正跟你说话，你却去回另一个人”。
        // 口径：**点名优先** —— 有人 @ 你 / 引用你的话时，先说给他的那句，引用也挂他（
        // 别人那条下一轮再说）。触发就是最后一条时按“紧接上一句不引用”的老规矩不挂引用。
        const long msgPing = 7401;   // 点名那条（@ 机器人）
        const long msgOther = 7402;  // 之后别人插的一句（模型更想接的那条）
        openAi.EnqueueReply($$"""{"suitability": 80, "reply": "我在，什么事", "replyTo": {{msgOther}}}""");
        openAi.EnqueueReply(silence);   // 后面那条插话如果也触发一轮，别把脚本吃空
        await protocol.SendGroupMessageAsync(groupId, 30016, "老王", "@10001 机器人你在吗", msgPing, mentionBot: true, ct: cts.Token);
        await Task.Delay(300);
        await protocol.SendGroupMessageAsync(groupId, 30017, "小李", "顺便说一句我换电脑了", msgOther, ct: cts.Token);
        await WaitUntilAsync(
            () => protocol.ActionsReceived.Any(a =>
                a["action"]?.GetValue<string>() == "send_group_msg" && MessageText(a).Contains("我在，什么事")),
            TimeSpan.FromSeconds(40));
        await Task.Delay(800);

        var afterPing = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => MessageText(a).Contains("我在，什么事"))
            .LastOrDefault();
        Check("★ 点名优先：人家在跟你说话时，引用挂给点名那条（不挂别人）",
            QuotedMessageId(afterPing) == msgPing,
            $"引用 id = {QuotedMessageId(afterPing)?.ToString() ?? "无"}，期望 {msgPing}");
        Check("★ 改引的理由写进了日志（复盘能看出为什么改）",
            bot.OutputLines.Any(l => l.Contains("在跟机器人说话") && l.Contains("点名优先")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("点名优先")).TakeLast(2)));
        Check("★ 回复日志带上了触发那条（正文/引用摆一起才看得出错位）",
            bot.OutputLines.Any(l => l.Contains("已回复") && l.Contains("触发→")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("触发→")).TakeLast(2)));

        // ══════════ 2026-09-19：引用机器人发的消息被吞（管理员反馈）══════════
        // 现场：群里“引用机器人上一句 + 说一句话” → 机器人不吭声。
        // 根因：判断“他是在回我”只看了内存表（上限 200、**重启就清空**），
        //       而每次部署都会重启容器 —— 部署之前发的那些全认不出来；群里不 @ 就不触发，于是整条被静默丢掉。
        // 现场二：引用的原文本地也查不到（重启后既不在内存表、也没留在上下文窗口）→ 模型只看到“更早的一条”。
        // 现在：① 自己发过的消息 id 落盘（批次 3 起是 own_messages 表，之前是 data/own-messages.json）；② 引自己的判定也看会话历史；
        //       ③ 协议端在 reply 段里带了被引用者 QQ 时直接采信；④ 本地全查不到时后台 get_msg 把原文补写回去。
        var lastBotMsgId = protocol.SentMessageIds.Last();
        var lastBotText = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Last(t => !string.IsNullOrWhiteSpace(t));

        // 判据从"文件里有这个 id"改成"台账表里有这一行"：存储介质批次 3 换成了 SQLite（见 Batch 3），
        // 这一条要钉的是**行为**（持久化了），不是介质本身。
        Check("★ 自己发过的消息会落盘（以前只在内存里，一重启就失忆）",
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM own_messages WHERE message_id = $id", ("$id", lastBotMsgId)) == 1,
            $"台账最近 5 行：{DbProbe.Dump(dataDir, "SELECT message_id FROM own_messages ORDER BY at_unix DESC LIMIT 5")}");

        await bot.StopAsync();
        bot.Dispose();
        await Task.Delay(600);

        using var bot2 = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
        });
        await WaitForPortAsync(botWsPort, cts.Token, bot2);
        using var protocol2 = new MockProtocol { SelfId = 10001 };
        await protocol2.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol2.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // 重启后引用部署前那句（不带 @、正文也没有关键词）→ 必须仍当成“在跟机器人说”。
        // 用“自评很低也必须接”来验证：directAddress 的唯一作用就是“被点名不应该沉默” ——
        // 被吞时正是自评低 → 沉默（messages 表里没有 direct_to_bot 列，只能从行为上验）。
        const long msgRestart = 7411;
        openAi.EnqueueReply("""{"suitability": 5, "reply": "重启前那句我接着说"}""");
        await protocol2.SendGroupMessageAsync(groupId, 30020, "老王", "重启前那句我还想问下", msgRestart,
            replyTo: lastBotMsgId, ct: cts.Token);
        Check("★★ 重启后引用机器人发过的消息：仍算“直接对它说”（自评低也照样接）",
            await WaitUntilAsync(() => bot2.OutputLines.Any(l => l.Contains("但这条是直接跟机器人说话")),
                TimeSpan.FromSeconds(40)),
            string.Join(" | ", bot2.OutputLines.Where(l => l.Contains("直接跟机器人说话")).TakeLast(2)));
        Check("★ 重启时确实把落盘的“我发过哪些消息”读回来了（不是靠会话历史撞上的）",
            await WaitUntilAsync(() => bot2.OutputLines.Any(l => l.Contains("自己发过的消息") && !l.Contains("记起了 0 条")),
                TimeSpan.FromSeconds(10)),
            string.Join(" | ", bot2.OutputLines.Where(l => l.Contains("自己发过的消息")).TakeLast(2)));
        Check("★★ 重启后引用原文也对上了（落库的就是带「你」的标注，不是“看不到原文”）",
            await WaitUntilAsync(() => DbProbe.Count(dataDir,
                "SELECT COUNT(1) FROM messages WHERE text LIKE '%[回复 你「%重启前那句我还想问下%'") >= 1,
                TimeSpan.FromSeconds(15)),
            DbProbe.Dump(dataDir, "SELECT text FROM messages ORDER BY seq DESC LIMIT 3"));

        await bot2.StopAsync();
        bot2.Dispose();
        await Task.Delay(400);

        // ③ 协议端在 reply 段里直接给了被引用者的 QQ —— 这一个信号就够认“他在回我”（id 是编的，本地查不到）
        var freshDir = NewDataDir("s19b");
        using var bot3 = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = freshDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
        });
        await WaitForPortAsync(botWsPort, cts.Token, bot3);
        using var protocol3 = new MockProtocol { SelfId = 10001 };
        await protocol3.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol3.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        const long msgProtoQq = 7412;
        openAi.EnqueueReply("""{"suitability": 5, "reply": "只有段里给了 qq 也接上了"}""");
        await protocol3.SendGroupMessageAsync(groupId, 30021, "老王", "段里只有qq没正文", msgProtoQq,
            replyTo: 777777777, replyQuotedQq: protocol3.SelfId, ct: cts.Token);
        Check("★★ reply 段给了被引用者 QQ 时，哪怕 id/正文都查不到也算“在跟机器人说”",
            await WaitUntilAsync(() => protocol3.ActionsReceived.Any(a =>
                a["action"]?.GetValue<string>() == "send_group_msg" && MessageText(a).Contains("只有段里给了 qq 也接上了")),
                TimeSpan.FromSeconds(40)),
            string.Join(" | ", protocol3.ActionsReceived
                .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
                .Select(MessageText).TakeLast(3)));

        // ④ 本地啥都不知道、协议端认识这条（它是机器人收过的）—— 后台 get_msg 把原文补写回来。
        //   关键：原话必须发到**另一个会话**（不在白名单的那个群）—— 这样协议端记得它，
        //   但本会话的上下文里没有，本地才会查不到、才会去 get_msg。（发在同一个群就只能算“上下文里找到了”）
        var beforeGetMsg = protocol3.GetMsgHits;
        const long otherGroupId = 66682;
        const long msgOld = 7421;
        const long msgQuoteOld = 7422;
        openAi.EnqueueReply(silence);
        await protocol3.SendGroupMessageAsync(otherGroupId, 30022, "老王", "这是很早以前的一句原话", msgOld, ct: cts.Token);
        await Task.Delay(400);
        await protocol3.SendGroupMessageAsync(groupId, 30023, "小李", "刚才那句再说一遍", msgQuoteOld,
            replyTo: msgOld, ct: cts.Token);
        Check("★★ 引用的原文本地查不到时，会去协议端 get_msg 兜底（并补写进那条消息）",
            await WaitUntilAsync(() => protocol3.GetMsgHits > beforeGetMsg && DbProbe.Count(freshDir,
                "SELECT COUNT(1) FROM messages WHERE text LIKE '%[回复 某人「这是很早以前的一句原话」]%'") >= 1,
                TimeSpan.FromSeconds(40)),
            $"get_msg 被问了 {protocol3.GetMsgHits - beforeGetMsg} 次；" +
            DbProbe.Dump(freshDir, "SELECT text FROM messages ORDER BY seq DESC LIMIT 3"));

        await bot3.StopAsync();
    }

    /// <summary>取系统提示里某一段（从 header 到下个空行），断言失败时能直接看到那一段。</summary>
    private static string SectionOf(string system, string header)
    {
        var start = system.IndexOf(header, StringComparison.Ordinal);
        if (start < 0)
        {
            return $"(没有 {header} 段)";
        }

        var end = system.IndexOf("\n\n", start + 1, StringComparison.Ordinal);
        return end < 0 ? system[start..] : system[start..end];
    }
}

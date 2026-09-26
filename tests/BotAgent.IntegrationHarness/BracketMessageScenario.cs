using System.Text;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S29 群友消息里的括号旁白：**标注而不是忽略**（开关）。
///
/// 管理员的原始口径（2026-09-14）：整条都是旁白（「（笑）」「（bushi）」）不要；前后带的旁白（「行（端在桌上）」）只留正文。
/// 同一天追加（§25）：**不要单纯忽略，也要接收，但要特别注明** —— 所以现在：
///   • 旁白标成 〔旁白：…〕 进聊天记录与模型上下文（内容一字不丢）；
///   • 整条都是旁白时**不单独触发一次回复**（“（笑）”不是对谁说的话），但下一条真消息会带上它；
///   • 整条旁白也不给引用编号（不会被当成“一句话”去引用）；
///   • 机器人自己的内容标记（[表情:斜眼笑]/[图片]）不是旁白，不能标注（标了等于抹掉群友发表情的记录）；
///   • 句子中间的括号（「（2026）年的计划」）不碰；
///   • 私聊 / 带图 / @ 了机器人三种情况完全不动。
/// </summary>
public static partial class Program
{
    private static async Task RunBracketMessageScenarioAsync()
    {
        Section("S29 括号旁白：标注成 〔旁白：…〕 而不是忽略（开关 + 不误伤 + 三种例外）");

        const int openAiPort = 17836;
        const int botWsPort = 13045;
        const int panelPort = 18110;
        const long groupId = 66711;
        const long friendId = 30121;

        var dataDir = NewDataDir("s29");
        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = $"{groupId},{friendId}",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_IGNORE_BRACKETS"] = "1"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        var (_, settings) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("★ 面板能读到这个开关（默认关，这里是按环境变量开着的）",
            settings.Contains("\"ignoreBracketMessages\":true"), Snippet(settings, "ignoreBracketMessages"));

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // ---- 1) 整条旁白：**标注后进库**，但自己不触发模型（旁白不是对谁说的话）----
        openAi.ClearRequests();
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "（笑）", 9911, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "（笑）（跑）", 9912, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "(bushi)", 9913, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "【摸鱼】", 9914, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "（真的）？", 9915, ct: cts.Token);
        await Task.Delay(4000);

        var annotated = await WaitUntilAsync(
            () => DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE text LIKE '%〔旁白：%笑%〕%'") >= 2,
            TimeSpan.FromSeconds(5));
        Check("★ 整条旁白不再被丢掉：标注成 〔旁白：…〕 落库", annotated,
            DbProbe.Dump(dataDir, "SELECT text FROM messages WHERE source_key = 'group:66711'"));
        Check("★ 标注保留了旁白内容本身（笑 / bushi / 摸鱼 / 真的）",
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE text LIKE '%〔旁白：bushi〕%'") >= 1 &&
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE text LIKE '%〔旁白：摸鱼〕%'") >= 1,
            DbProbe.Dump(dataDir, "SELECT text FROM messages WHERE source_key = 'group:66711'"));
        Check("★ 整条旁白不单独触发一次模型请求（“（笑）”不该把机器人拽出来接话）",
            openAi.Requests.Count == 0, $"本轮请求 {openAi.Requests.Count} 次");

        // ---- 2) “接收”的含义：旁白真的进了下一轮的上下文，而且带标注、不带引用编号 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "来啦。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "大家好啊", 9916, ct: cts.Token);
        var withAside = await WaitForRequestAsync(openAi, r => r.Contains("大家好啊"), TimeSpan.FromSeconds(30));
        Check("★ 旁白照样进模型上下文（不是被忽略，只是被标注）",
            withAside is not null && withAside.Contains("〔旁白：笑〕"),
            withAside is null ? "(没进上下文)" : Snippet(withAside, "〔旁白："));
        Check("★ 整条旁白不给 (#编号)：模型不会把“（笑）”当成一句话去引用",
            withAside is not null && !withAside.Contains("(#9911)") && !withAside.Contains("(#9912)"),
            withAside is null ? "(没进上下文)" : Snippet(withAside, "[回复谁]"));

        // ---- 3) 前后带的旁白：正文留着、旁白标注在旁（管理员说的“不是整条都是括号”）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "好嘞。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30022, "老王", "行（端在桌上）", 9917, ct: cts.Token);
        var tail = await WaitForRequestAsync(openAi, r => r.Contains("行"), TimeSpan.FromSeconds(30));
        Check("★ 尾部旁白改成标注（「行（端在桌上）」→「行〔旁白：端在桌上〕」）",
            tail is not null && tail.Contains("行〔旁白：端在桌上〕"),
            tail is null ? "(没进上下文)" : Snippet(tail, "行"));
        Check("★ 落库的也是标注后的文本",
            await WaitUntilAsync(() => DbProbe.Count(dataDir,
                "SELECT COUNT(1) FROM messages WHERE text = $t", ("$t", "行〔旁白：端在桌上〕")) >= 1,
                TimeSpan.FromSeconds(5)),
            DbProbe.Dump(dataDir, "SELECT text FROM messages WHERE text LIKE '行%'"));

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "来了来了。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30022, "老王", "（放在地上）来吧，猫猫，", 9918, ct: cts.Token);
        var lead = await WaitForRequestAsync(openAi, r => r.Contains("来吧，猫猫"), TimeSpan.FromSeconds(30));
        Check("★ 开头旁白改成标注（「（放在地上）来吧」→「〔旁白：放在地上〕来吧」）",
            lead is not null && lead.Contains("〔旁白：放在地上〕来吧，猫猫，"),
            lead is null ? "(没进上下文)" : Snippet(lead, "来吧"));

        // ---- 4) 机器人自己的内容标记不能被当旁白标注 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "哈哈。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30023, "小红", "[表情:斜眼笑]", 9919, ct: cts.Token);
        var face = await WaitForRequestAsync(openAi, r => r.Contains("表情:斜眼笑"), TimeSpan.FromSeconds(30));
        Check("★ 群友发的 QQ 表情不是旁白（[表情:斜眼笑] 原样保留）",
            face is not null && !face.Contains("〔旁白：表情"), face is null ? "(表情被当旁白吃了)" : "表情还在");

        // ---- 5) 句子中间的括号不碰 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "那得看计划。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30024, "小明", "（2026）年的计划还做吗", 9920, ct: cts.Token);
        var middle = await WaitForRequestAsync(openAi, r => r.Contains("（2026）年的计划"), TimeSpan.FromSeconds(30));
        Check("★ 句子中间的括号是正文（不标注成旁白）", middle is not null,
            middle is null ? "(中间括号被动了)" : "括号保留");

        // ---- 6) 例外一：@ 了机器人 → 完全不动 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "在的，你说。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30025, "小红", "（真的吗）", 9921, mentionBot: true, ct: cts.Token);
        var mentioned = await WaitForRequestAsync(openAi, r => r.Contains("（真的吗）"), TimeSpan.FromSeconds(30));
        Check("★ @ 了机器人的括号消息完全不动（直接叫它就得理）",
            mentioned is not null && !mentioned.Contains("〔旁白：真的吗〕"),
            mentioned is null ? "(被忽略了)" : "已原样进上下文");

        // ---- 7) 例外二：私聊 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "笑什么呀。"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "（笑）", 9922, ct: cts.Token);
        var privateOne = await WaitForRequestAsync(openAi, r => r.Contains("（笑）"), TimeSpan.FromSeconds(30));
        Check("★ 私聊里同样的括号消息不动（一对一必须理）",
            privateOne is not null && !privateOne.Contains("〔旁白：笑〕"),
            privateOne is null ? "(被忽略了)" : "已原样进上下文");

        // ---- 8) 例外三：带图 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "这图有点意思。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30026, "小美", "（图）", 9923,
            imageUrl: "http://127.0.0.1:9/none.png", ct: cts.Token);
        await Task.Delay(3000);
        Check("★ 带图的括号消息不动（落库的是原文 + 内容标记，不是旁白标注）",
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE text LIKE '（图）%'") >= 1 &&
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE text LIKE '%〔旁白：图〕%'") == 0,
            DbProbe.Dump(dataDir, "SELECT text FROM messages WHERE text LIKE '%图%'"));

        // ---- 9) 关掉开关 → 旁白原样保留（不标注）----
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using (var offResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
                   new StringContent("""{"ignoreBracketMessages":false}""", Encoding.UTF8, "application/json"), cts.Token))
        {
            Check("★ 面板能关掉这个开关", offResp.IsSuccessStatusCode);
        }

        await Task.Delay(400);
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "笑就笑吧。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30027, "小明", "（笑死）", 9924, ct: cts.Token);
        var afterOff = await WaitForRequestAsync(openAi, r => r.Contains("（笑死）"), TimeSpan.FromSeconds(30));
        Check("★ 关掉开关后旁白原样进上下文（开关真的生效，不再改文本）",
            afterOff is not null && !afterOff.Contains("〔旁白：笑死〕"),
            afterOff is null ? "(关掉后仍被标注)" : "已原样进上下文");

        bot.Dispose();
    }
}

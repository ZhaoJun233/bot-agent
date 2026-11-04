using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S56–S59：四条核心链路不变量（#126）。
///
/// 为什么单独成组：这四条是重构（#106 大文件拆分 / #108 管线 / #110 存储）**最容易悄悄改坏**的行为，
/// 而它们原本要么完全没有断言（① 每会话 FIFO、② 静默短路在发送前），要么分散在别的场景里顺带验证
/// （③ 引用只挂第一条在 S19、④ 语音文字去重在 S24）。这一组把四条各写成一个**显式的、命名带不变量编号的**
/// 场景，供重构 PR 直接引用：
///
///   ① 每会话 FIFO —— 同一 source_key 的回复按到达顺序串行，不并发乱序（S56）
///   ② 静默短路在发送前 —— 判定“不回复”时必须在发送动作之前短路，不能先发再撤回（S57）
///   ③ 引用只挂第一条 —— 多段回复只有第一条带引用（S58）
///   ④ 语音文字去重 —— 同一轮里 TTS 语音与文字不重复播报同一句（S59）
///
/// 纪律：这组场景**只新增断言**，不改任何既有断言；它们必须在重构之前就是绿的。
/// </summary>
public static partial class Program
{
    /// <summary>不变量场景共用的宿主环境：单群白名单、关冷却/兜底/分句，端口全部走 FreePort。</summary>
    private static Dictionary<string, string> InvariantEnv(string dataDir, string baseUrl, int botWsPort, params (string Key, string Value)[] extra)
    {
        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = baseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = "http://0.0.0.0:" + botWsPort,
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "66741,66742,66743,66744",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        };
        foreach (var (key, value) in extra)
        {
            env[key] = value;
        }

        return env;
    }

    /// <summary>把一条动作的 message 段渲染成可读形态（断言失败时贴出来能一眼看出问题）。</summary>
    private static string DescribeMessage(JsonObject? action)
    {
        var segments = action?["params"]?["message"];
        return segments is null ? "(无 message)" : segments.ToJsonString();
    }

    // ───────────────────────────── 不变量①：每会话 FIFO ─────────────────────────────

    private static async Task RunFifoInvariantScenarioAsync()
    {
        Section("S56 不变量①：同一会话的回复按到达顺序串行（FIFO，不并发乱序）");

        var openAiPort = FreePort(17871);
        var botWsPort = FreePort(13071);
        const long groupId = 66741;

        var dataDir = NewDataDir("s56");

        // 单轮模型延迟 1500ms：第二条消息会在第一条还在生成时就到达，正好压 FIFO 串行这条路。
        using var openAi = new MockOpenAi(openAiPort) { ResponseDelayMs = 1500 };
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 95, "reply": "甲段的回复"}""");
        openAi.EnqueueReply("""{"suitability": 95, "reply": "乙段的回复"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        using var bot = StartBot(InvariantEnv(dataDir, openAi.BaseUrl, botWsPort));
        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        await protocol.SendGroupMessageAsync(groupId, 20002, "群友A", "@10001 第一句", 7401, mentionBot: true, ct: cts.Token);
        await Task.Delay(250);
        await protocol.SendGroupMessageAsync(groupId, 20004, "群友B", "@10001 第二句", 7402, mentionBot: true, ct: cts.Token);

        var afterFirst = await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(60));
        var firstAt = DateTimeOffset.UtcNow;
        var both = await WaitForSendsAsync(protocol, 2, TimeSpan.FromSeconds(60));
        var secondAt = DateTimeOffset.UtcNow;

        var firstText = both.Count > 0 ? MessageText(both[0]) : string.Empty;
        var secondText = both.Count > 1 ? MessageText(both[1]) : string.Empty;

        Check("★ 不变量①：两条触发各拿到一条回复（第二句没被冷却/去重吃掉）",
            both.Count >= 2,
            $"本次发出 {both.Count} 条：{string.Join(" | ", both.Select(DescribeMessage).Select(s => Snippet(s, "type")))}");

        Check("★ 不变量①：回复顺序 = 到达顺序（先“甲段”后“乙段”）",
            both.Count >= 2 && firstText.Contains("甲段的回复") && secondText.Contains("乙段的回复"),
            $"第 1 条「{firstText}」/ 第 2 条「{secondText}」");

        var gap = (secondAt - firstAt).TotalMilliseconds;
        Check("★ 不变量①：同一会话串行（第二条在第一轮结束后才发；模型单轮延迟 1500ms）",
            gap >= 700,
            $"两条回复间隔 {gap:F0}ms（并发实现会接近 0）");

        var secondRequest = openAi.Requests.Count >= 2 ? openAi.DescribeRequest(1) : string.Empty;
        Check("★ 不变量①：第二条请求里已经能看到第一条回复（证明是排队串行，不是并发各拼各的上下文）",
            secondRequest.Contains("甲段的回复"),
            secondRequest.Length == 0 ? "(没等到第二条模型请求)" : Snippet(secondRequest, "甲段"));

        Check("★ 不变量①：两条触发只换来两次模型请求（没有多余的补评估打转）",
            openAi.Requests.Count == 2,
            $"模型请求 {openAi.Requests.Count} 次");

        await bot.StopAsync();
    }

    // ──────────────────────── 不变量②：静默短路在发送前 ────────────────────────

    private static async Task RunSilenceShortCircuitInvariantScenarioAsync()
    {
        Section("S57 不变量②：判定“不回复”时在发送动作之前短路（复读守卫：生成了也不发）");

        var openAiPort = FreePort(17872);
        var botWsPort = FreePort(13072);
        const long groupId = 66742;

        var dataDir = NewDataDir("s57");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        // 复读守卫：模型把上一条自己说过的话一字不差再来一遍 → 必须在**发送之前**短路。
        // 这里刻意给足正文（而不是 reply 空串）：空串沉默只能证明“没东西可发”，
        // 证明不了“生成了但不该发”，也就挡不住“先发再撤回”这种实现。
        openAi.EnqueueReply("""{"suitability": 99, "reply": "同一句话反复说"}""");
        openAi.EnqueueReply("""{"suitability": 99, "reply": "同一句话反复说"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        using var bot = StartBot(InvariantEnv(dataDir, openAi.BaseUrl, botWsPort,
            ("QQCHAT_PROFILE_SUMMARY", "0"),
            ("QQCHAT_STICKERS", "0")));
        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        await protocol.SendGroupMessageAsync(groupId, 20002, "群友A", "@10001 说句话", 7501, mentionBot: true, ct: cts.Token);
        await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(60));
        await Task.Delay(500);

        var mark = protocol.ActionsReceived.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "群友A", "@10001 再说一句", 7502, mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => openAi.Requests.Count >= 2, TimeSpan.FromSeconds(60));
        // 若实现会“先发再撤回”，第二条一定在生成完成后的这几秒里冒出来。
        await Task.Delay(4000);

        var actions = protocol.ActionsReceived;
        var sends = actions.Where(a => a["action"]?.GetValue<string>() is "send_group_msg" or "send_private_msg").ToList();
        var recalls = actions.Where(a => a["action"]?.GetValue<string>() is "delete_msg" or "set_msg_emoji_like").ToList();
        var afterMark = GroupSendsSince(protocol, mark);

        Check("★ 不变量②：第一轮正常发出（先确认守卫没有把正常的也拦掉）",
            sends.Count >= 1 && MessageText(sends[0]).Contains("同一句话反复说"),
            sends.Count == 0 ? "(第一轮就没发出来)" : $"第一条「{MessageText(sends[0])}」");
        Check("★ 不变量②：第二轮复读被拦下，一条都没发",
            afterMark.Count == 0,
            afterMark.Count == 0 ? "0 条" : string.Join(" | ", afterMark.Select(DescribeMessage)));
        Check("★ 不变量②：也没有“先发再撤回”（没有 delete_msg / 表情回应）",
            recalls.Count == 0,
            recalls.Count == 0 ? "0 条撤回动作" : string.Join(" | ", recalls.Select(a => a["action"]?.GetValue<string>() ?? "?")));
        Check("★ 不变量②：第二轮确实生成了（模型请求 ≥2），是判定不是没触发",
            openAi.Requests.Count >= 2,
            $"模型请求 {openAi.Requests.Count} 次");
        Check("★ 不变量②：日志里有复读判定（说明走的是“判定→短路”，不是发送失败）",
            bot.OutputLines.Any(line => line.Contains("检测到复读")),
            string.Join(" / ", bot.OutputLines.Where(line => line.Contains("复读") || line.Contains("沉默")).Take(3)));

        await bot.StopAsync();
    }

    // ───────────────────────── 不变量③：引用只挂第一条 ─────────────────────────

    private static async Task RunQuoteFirstSegmentInvariantScenarioAsync()
    {
        Section("S58 不变量③：多段回复时引用只挂在第一条消息上");

        var openAiPort = FreePort(17873);
        var botWsPort = FreePort(13073);
        const long groupId = 66743;
        const long msgA = 7601;
        const long msgB = 7602;
        const string replyText = "第一段说点正事。第二段补个细节。第三段收个尾。";

        var dataDir = NewDataDir("s58");

        // 慢模型 + A 在途时 B 插话：只有“触发消息已经不是最新诉求”时才会出引用，
        // 所以这个场景必须复现“积压”，单发一条是永远看不到引用的。
        using var openAi = new MockOpenAi(openAiPort) { ResponseDelayMs = 1500 };
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 99, "reply": "第一段说点正事。第二段补个细节。第三段收个尾。"}""");
        openAi.EnqueueReply("""{"suitability": 99, "reply": "乙段的回复"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var bot = StartBot(InvariantEnv(dataDir, openAi.BaseUrl, botWsPort,
            ("QQCHAT_SPLIT_REPLIES", "1"),
            ("QQCHAT_PROFILE_SUMMARY", "0"),
            ("QQCHAT_STICKERS", "0")));
        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var mark = protocol.ActionsReceived.Count;
        await protocol.SendGroupMessageAsync(groupId, 30001, "群友A", "这个报错怎么修", msgA, ct: cts.Token);
        await Task.Delay(300);
        await protocol.SendGroupMessageAsync(groupId, 30002, "群友B", "域名要配 DNS 解析的", msgB, ct: cts.Token);

        await WaitUntilAsync(
            () => GroupSendsSince(protocol, mark).Count >= 3,
            TimeSpan.FromSeconds(90));
        await Task.Delay(1500);

        var all = GroupSendsSince(protocol, mark);
        var turnOne = all.Where(a => !MessageText(a).Contains("乙段的回复")).ToList();
        var quotes = turnOne.Select(QuotedMessageId).ToList();
        var joined = string.Concat(turnOne.Select(MessageText)).Replace(" ", string.Empty);

        Check("★ 不变量③：多段回复真的被拆成多条发出",
            turnOne.Count >= 2,
            $"本次发出 {all.Count} 条：{string.Join(" | ", all.Select(MessageText))}");
        Check("★ 不变量③：第一条带引用且指向触发消息",
            turnOne.Count >= 1 && quotes[0] == msgA,
            turnOne.Count == 0 ? "(一条都没发)" : $"第一条引用 id = {quotes[0]?.ToString() ?? "无"}，期望 {msgA}");
        Check("★ 不变量③：后续各段都不带引用（引用只挂第一条）",
            turnOne.Count >= 2 && quotes.Skip(1).All(q => q is null),
            $"引用 id 序列 = {string.Join(", ", quotes.Select(q => q?.ToString() ?? "无"))}");
        Check("★ 不变量③：分段不丢字（各段拼起来等于模型给的整句）",
            joined == replyText.Replace(" ", string.Empty),
            $"拼起来「{joined}」，期望「{replyText}」");

        await bot.StopAsync();
    }

    // ─────────────────────── 不变量④：语音与文字不重复 ───────────────────────

    private static async Task RunVoiceTextDedupInvariantScenarioAsync()
    {
        Section("S59 不变量④：同一轮里 TTS 语音与文字不重复播报同一句");

        var openAiPort = FreePort(17874);
        var botWsPort = FreePort(13074);
        var ttsPort = FreePort(18174);
        const long groupId = 66744;
        const string spoken = "这句话只用语音说一遍";

        var dataDir = NewDataDir("s59");

        using var tts = new MockTtsHost(ttsPort);
        tts.Start();

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        // 模型只填 speak、reply 留空：正确实现发一条 record，且不再补一条同内容的文字。
        openAi.EnqueueReply($$"""{"suitability": 90, "reply": "", "speak": "{{spoken}}"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var bot = StartBot(InvariantEnv(dataDir, openAi.BaseUrl, botWsPort,
            ("QQCHAT_ENABLE_VOICE", "1"),
            ("QQCHAT_VOICE", "zh_CN-huayan-medium"),
            ("QQCHAT_VOICE_MAX_CHARS", "60"),
            ("QQCHAT_TTS_URL", tts.BaseUrl)));
        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var mark = protocol.ActionsReceived.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "群友A", "@10001 说一句", 7701, mentionBot: true, ct: cts.Token);
        await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(60));
        // 再等一会儿：若实现会补发文字，第二条一定会在这个窗口里出现。
        await Task.Delay(2500);
        var all = GroupSendsSince(protocol, mark);

        var records = all.Where(a => SegmentType(a, "record") is not null).ToList();
        var voiceUrl = records.Count > 0 ? SegmentFile(records[0], "record") : null;
        var text = string.Concat(all.Select(MessageText));

        Check("★ 不变量④：语音发出来了（record 段，URL 交给协议端）",
            records.Count == 1 && voiceUrl is not null && voiceUrl.StartsWith(tts.BaseUrl + "/speak?", StringComparison.Ordinal),
            voiceUrl is null ? $"本次发出 {all.Count} 条：{string.Join(" | ", all.Select(DescribeMessage))}" : Snippet(voiceUrl, "text="));
        Check("★ 不变量④：同一轮只有一条消息（不再补一条同内容的文字）",
            all.Count == 1,
            $"本次发出 {all.Count} 条，文字段「{text}」");
        Check("★ 不变量④：没有把同一句再当成文字发一遍",
            !text.Contains(spoken),
            text.Length == 0 ? "(没有文字段)" : text);

        await bot.StopAsync();
    }
}

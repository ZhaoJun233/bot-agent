using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S38 服务器内置 agent 的 **QQ 动作**（号主 2026-09-18：「给服务器内置 agent 一些 napcat qq 的 api 行为调用（比如点赞）」）。
///
/// 这里钉的五件事：
///   ① 模型说「点赞」真的会在 QQ 里发出 `send_like`（且 user_id 能写 `sender` = 发指令的人）；
///   ② 中文别名（戳一戳）也能翻成规范动作，戳的是指令里指定的人；
///   ③ **危险动作默认没开**：模型想禁言，协议端那一步根本不会发生（不是靠提示词自觉）；
///   ④ 面板点名打开 `ban` 之后，同一个动作就真发出去了（duration 10m → 600 秒）；
///   ⑤ 一轮最多 5 个 QQ 动作（模型想“给每个人都点一遍”也刷不动），并且结论回群前过脱敏。
/// </summary>
public static partial class Program
{
    private static async Task RunServerAgentQqActionScenarioAsync()
    {
        Section("S38 服务器内置 agent 的 QQ 动作（点赞 / 戳一戳 / 危险档要点名 / 每轮上限）");

        const int openAiPort = 17851;
        const int agentAiPort = 17852;
        const int botWsPort = 13071;
        const int healthPort = 18121;
        const long groupId = 66780;
        const long ownerId = 20002;         // 白名单里的号主（发指令的人）
        const long targetId = 300030003;    // 被点赞/戳的人（9 位：顺便验证回群脱敏）
        const string token = "tok-s38-secret";

        var dataDir = NewDataDir("s38");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // 服务器 agent 走自己的假网关（聊天那套另有 openAi）
        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();

        // ① 点赞：user_id 写 sender（现场里 = 发这条指令的人）
        agentAi.AddRule("给发指令的人点个赞",
            """{"thought":"现场里有 sender","tool":"qq","action":"like","user_id":"sender","times":3}""",
            """{"thought":"成了","final":"已经给他点了 3 个赞。"}""");

        // ② 中文别名 + 戳一戳（群里也能戳别人，带明确 QQ 号）
        agentAi.AddRule("戳戳老王",
            $$"""{"tool":"qq","action":"戳一戳","user_id":{{targetId}}}""",
            """{"final":"戳完了，他应该收到了。"}""");

        // ③ 危险档默认没开：模型自己决定要禁言（不是提示词拦住它，是工具层拦住）
        agentAi.AddRule("把老王封一下",
            $$"""{"tool":"qq","action":"ban","user_id":{{targetId}},"duration":"10m"}""",
            """{"final":"封不了，这个动作没开。"}""");

        // ④ 面板点名打开 ban → 同一个动作就能真发出去（10m → 600 秒）
        agentAi.AddRule("这次真封老王",
            $$"""{"tool":"qq","action":"ban","user_id":{{targetId}},"duration":"10m"}""",
            """{"final":"已禁言 10 分钟。"}""");

        // ⑤ 每轮上限：让模型连点 6 次赞（第 6 次必须被拦住）
        var likeStep = $$"""{"tool":"qq","action":"like","user_id":{{targetId}},"times":1}""";
        agentAi.AddRule("给每个人都点一遍",
            Enumerable.Repeat(likeStep, 6).Append("""{"final":"点完了。"}""").ToArray());

        // ⑥ 脱敏：结论里带 QQ 号 → 回群前要被遮掉
        agentAi.AddRule("报一下你给谁点了赞",
            $$"""{"final":"给 {{targetId}} 点过赞了。"}""");

        // ⑦ 非好友点赞：QQ 会回“对方权限设置/点不了” → 工具要把“改用戳一戳”这条路告诉模型
        //     （现实里很常见：群里陌生人开了“仅好友可赞”，send_like 必失败）
        agentAi.AddRule("给那个陌生人点个赞",
            $$"""{"tool":"qq","action":"like","user_id":{{targetId}},"times":1}""");

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
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_HEALTH_PORT"] = healthPort.ToString(),
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = ownerId.ToString(),
            ["QQCHAT_AGENT_TOKEN"] = token,
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_TARGET"] = "server",
            ["QQCHAT_AGENT_SERVER_URL"] = agentAi.BaseUrl,
            ["QQCHAT_AGENT_SERVER_MODEL"] = "custom-agent-model",
            // 号主开了 qq 工具（不写这个的话默认是 bash/read/write/fetch 全开，qq 也要显式在名单里）
            ["QQCHAT_AGENT_SERVER_TOOLS"] = "bash,read,qq",
            ["QQCHAT_AGENT_PROGRESS"] = "0",
            ["QQCHAT_AGENT_MASK"] = "1"
            // QQCHAT_AGENT_SERVER_QQ_ACTIONS 故意不设：验证“留空 = 默认安全档”
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(healthPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var panel = $"http://127.0.0.1:{healthPort}";

        int ActionCount(string action) => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == action);

        List<string> Sent() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        JsonObject? LastAction(string action) => protocol.ActionsReceived
            .LastOrDefault(a => a["action"]?.GetValue<string>() == action);

        // ── 0) 默认档：留空 = 安全档（面板上要能看到“真正会开哪几个”）──
        var (_, settingsBody) = await HttpGetAsync($"{panel}/api/settings");
        var runtime = (JsonNode.Parse(settingsBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("★ 面板默认显示安全档（留空 ≠ 全开：dangerous 的得点名）",
            runtime["agentServerQqActions"]?.GetValue<string>() == "" &&
            (runtime["agentServerQqActionsEffective"]?.GetValue<string>() ?? "").Contains("like/poke/emoji_like/recall"),
            $"actions={runtime["agentServerQqActions"]?.ToJsonString()} 生效={runtime["agentServerQqActionsEffective"]?.ToJsonString()}");

        // ── ① 点赞：user_id 写 sender ──
        var before1 = ActionCount("send_like");
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//给发指令的人点个赞", 17001, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => ActionCount("send_like") > before1, TimeSpan.FromSeconds(60));
        await Task.Delay(400);

        var like = LastAction("send_like");
        Check("★ 模型说点赞，协议端真的收到了 send_like（这是“行为调用”本身）",
            ActionCount("send_like") == before1 + 1, $"send_like 共 {ActionCount("send_like")} 次");
        Check("★ user_id 写 sender 被翻成发指令那个人的 QQ 号，times 原样带过去",
            like?["params"]?["user_id"]?.GetValue<long>() == ownerId &&
            like?["params"]?["times"]?.GetValue<int>() == 3,
            like?.ToJsonString() ?? "(没收到动作)");
        Check("★ 结论回到群里（号主知道做成了）",
            Sent().Any(t => t.Contains("已经给他点了 3 个赞")), string.Join(" | ", Sent().TakeLast(3)));

        // ── ② 中文别名：戳一戳 ──
        var before2 = ActionCount("group_poke");
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//戳戳老王", 17002, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => ActionCount("group_poke") > before2, TimeSpan.FromSeconds(60));
        await Task.Delay(400);

        var poke = LastAction("group_poke");
        Check("★ 「戳一戳」这种中文写法也能翻成动作，并戳在指定的那个人身上",
            poke?["params"]?["user_id"]?.GetValue<long>() == targetId &&
            poke?["params"]?["group_id"]?.GetValue<long>() == groupId,
            poke?.ToJsonString() ?? "(没收到动作)");

        // ── ③ 危险档默认没开：模型想禁言 → 工具层拦住，协议端一步都不该发生 ──
        var before3 = ActionCount("set_group_ban");
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//把老王封一下", 17003, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("封不了")), TimeSpan.FromSeconds(60));
        await Task.Delay(400);

        Check("★★ 没点名的危险动作（禁言）连协议端都没碰到 —— 不是靠提示词自觉",
            ActionCount("set_group_ban") == before3, $"set_group_ban 发生了 {ActionCount("set_group_ban") - before3} 次");
        Check("★ 模型拿到了“这个动作没开”的说明（所以它能如实回话，而不是以为自己做了）",
            Enumerable.Range(0, agentAi.Requests.Count).Any(i => agentAi.DescribeRequest(i).Contains("没开")),
            $"agent 接口收到 {agentAi.Requests.Count} 次请求");

        // ── ④ 面板点名打开 ban（顺手验证别名规范化 + 写错的忽略）──
        var (setCode, _) = await PostJsonAsync($"{panel}/api/settings",
            """{"agentServerQqActions":"点赞, 戳一戳, 没这个动作, ban"}""");
        Check("面板能保存 QQ 动作", setCode == 200, $"HTTP {setCode}");

        var (_, afterBody) = await HttpGetAsync($"{panel}/api/settings");
        var after = (JsonNode.Parse(afterBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("★ 保存时把别名规范成动作名、写错的名字直接丢掉（面板回读的就是真正生效的那份）",
            after["agentServerQqActions"]?.GetValue<string>() == "like,poke,ban",
            after["agentServerQqActions"]?.ToJsonString() ?? "(空)");
        Check("★ 生效摘要里点名了 ban（号主一眼能看到危险档开了什么）",
            (after["agentServerQqActionsEffective"]?.GetValue<string>() ?? "").Contains("已点名打开 ban"),
            after["agentServerQqActionsEffective"]?.ToJsonString() ?? "(空)");

        var before4 = ActionCount("set_group_ban");
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//这次真封老王", 17004, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => ActionCount("set_group_ban") > before4, TimeSpan.FromSeconds(60));
        await Task.Delay(400);

        var ban = LastAction("set_group_ban");
        Check("★★ 点名之后同一个动作就真发出去了（10m → 600 秒，禁的是指令里那个人、在那个群）",
            ban?["params"]?["user_id"]?.GetValue<long>() == targetId &&
            ban?["params"]?["group_id"]?.GetValue<long>() == groupId &&
            ban?["params"]?["duration"]?.GetValue<int>() == 600,
            ban?.ToJsonString() ?? "(没收到动作)");
        Check("★ 结论也回了群",
            Sent().Any(t => t.Contains("已禁言 10 分钟")), string.Join(" | ", Sent().TakeLast(3)));

        // ── ⑤ 每轮上限：模型连点 6 次，最多只该出去 5 次 ──
        var before5 = ActionCount("send_like");
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//给每个人都点一遍", 17005, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("点完了")), TimeSpan.FromSeconds(90));
        await Task.Delay(400);

        var likesInRound = ActionCount("send_like") - before5;
        Check("★★ 一轮最多 5 个 QQ 动作（第 6 次被工具层拦下，协议端只收到 5 次）",
            likesInRound == 5, $"这一轮 send_like = {likesInRound} 次");

        // ── ⑥ 结论回群前过脱敏（agent 会翻日志，结论里很容易带 QQ 号/昵称）──
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//报一下你给谁点了赞", 17006, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("点过赞了")), TimeSpan.FromSeconds(60));
        await Task.Delay(400);

        var report = Sent().LastOrDefault(t => t.Contains("点过赞了")) ?? string.Empty;
        Check("★ 结论里的长数字（QQ 号）回群前被遮住",
            report.Contains("300***03") && !report.Contains(targetId.ToString()),
            report.Length > 0 ? report : "(没收到结论)");

        Check("★ 全程没有出现过“不认识的动作”这类内部错误回群",
            Sent().All(t => !t.Contains("不认识的动作") && !t.Contains("工具执行出错")),
            string.Join(" | ", Sent().TakeLast(3)));

        // ── ⑦ 非好友点赞被 QQ 回绝：不硬试，改推“戳一戳”（现实里最常见的一种失败）──
        protocol.FailActions.Add("send_like");   // 让协议端对 send_like 回 retcode≠0（跟真机的“对方权限设置”同一条路）
        var before7 = ActionCount("send_like");
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "//给那个陌生人点个赞", 17007, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => ActionCount("send_like") > before7, TimeSpan.FromSeconds(60));
        await Task.Delay(600);

        Check("★ 协议端回绝时工具仍然真的尝试了（不是静默跳过、也不是假报成功）",
            ActionCount("send_like") > before7, $"send_like 共 {ActionCount("send_like")} 次");
        Check("★ 给模型的失败提示里写清了原因并且给了替代方案（poke / emoji_like）",
            Enumerable.Range(0, agentAi.Requests.Count).Any(i => agentAi.DescribeRequest(i).Contains("这次换个方式")),
            $"agent 接口收到 {agentAi.Requests.Count} 次请求");
        Check("★ like 的工具说明本身就讲明“只有好友/对方允许陌生人点赞”才点得成（模型第一轮就知道不该硬试）",
            Enumerable.Range(0, agentAi.Requests.Count).Any(i => agentAi.DescribeRequest(i).Contains("只有**好友**")),
            $"agent 接口收到 {agentAi.Requests.Count} 次请求");
        protocol.FailActions.Remove("send_like");
    }
}

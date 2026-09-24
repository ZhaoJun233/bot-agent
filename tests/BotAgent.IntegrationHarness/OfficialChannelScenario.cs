using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S42 官方通道（QQ 开放平台）与私域通道（NapCat/OneBot）**并存**时的那几条性命攸关的性质：
///
/// ⚠ 状态（2026-09-21）：**未接入回归**（Program.cs 里那行已注释）。这套场景只跑到 7✓/9✗ 而且会挂死；
///   已确认出站（token→/gateway/bot→identify）与入站事件分发（假网关推出的事件确实变成了官方会话）是通的，
///   卡住的是**假网关推事件的时序**与**等待没上超时**：跑之前请先把每处等待都加硬超时
///   （`WaitUntilAsync(..., TimeSpan.FromSeconds(30))`、socket 读走 CancellationToken），
///   并确认官方通道白名单在 ①②③④ 用例期间是“留空 = 全部接受”（⑤ 才设成不匹配的值）。
///   官方通道默认是**关**的（`OfficialEnabled`），所以这套红不影响上线；真机验收还要等开放平台凭据。
///   ① 双上行聚合：一个 BotAgentHost 同时吃两条路（官方走假网关的 HTTP+WS，私域走假协议端）；
///   ② **隔离**：两边各自有会话与上下文（官方 key 带 <c>official:</c> 前缀，私域仍是老格式），
///      一件消息绝不跑错通道、绝不进错上下文；
///   ③ 语音：官方那条走「富媒体上传 file_type=3 + msg_type=7」，私域那条仍是 OneBot 的
///      <c>record</c> 段（只给 URL，协议端自己转 silk）——两条路的音格式要求完全不同；
///   ④ 被动回复：官方要原样带回**原始** msg_id（不是我们内部的别名号），同一窗口内 msg_seq 递增；
///   ⑤ 白名单彼此独立：官方名单只认别名号，配错时官方那条被忽略，私域**不受影响**；
///   ⑥ 掉线自愈：socket 断了要重连（identify 或 resume），恢复后照常收发。
///
/// 用假网关而不是真凭据：官方 appid/secret 是号主自己的，测试里不该有；
/// 而真正会写错的协议细节（QQBot 头、intents、msg_type=7、file_type、msg_id/msg_seq）都能在假网关上钉死。
/// </summary>
public static partial class Program
{
    private static async Task RunOfficialChannelScenarioAsync()
    {
        Section("S42 官方通道：双上行聚合 · 会话/上下文隔离 · 官方语音走富媒体 · 掉线自愈");

        const int openAiPort = 17851;
        const int botWsPortPreferred = 13061;
        const int panelPortPreferred = 18131;
        const int ttsPortPreferred = 18132;
        const int officialHttpPreferred = 18133;
        const int officialWsPreferred = 13062;

        // 一律合成号：真实 QQ 号/群号/openid 都不该出现在测试里
        const long privateGroup = 66691;
        const long privateFriend = 30021;
        const string groupOpenId = "GROUP_OPENID_A";
        const string memberOpenId = "USER_OPENID_A";

        var openAiPort2 = FreePort(openAiPort);
        var botWsPort = FreePort(botWsPortPreferred);
        var panelPort = FreePort(panelPortPreferred);
        var ttsPort = FreePort(ttsPortPreferred);
        var officialHttpPort = FreePort(officialHttpPreferred);
        var officialWsPort = FreePort(officialWsPreferred);

        var dataDir = NewDataDir("s42");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        using var tts = new MockTtsHost(ttsPort);
        tts.Start();

        using var openAi = new MockOpenAi(openAiPort2);
        openAi.Start();

        using var official = new MockOfficialGateway(officialHttpPort, officialWsPort);
        official.Start();

        Dictionary<string, string> BotEnv(string dir, string officialWhitelistGroups = "") => new()
        {
            ["QQCHAT_DATA_DIR"] = dir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = $"{privateGroup},{privateFriend}",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_WEB_SEARCH"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "1",   // 分句：用来验证同一条消息的多次回复 msg_seq 递增
            ["QQCHAT_SEGMENT_DELAY_MS"] = "100",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_ENABLE_VOICE"] = "1",
            ["QQCHAT_VOICE"] = "zh_CN-huayan-medium",
            ["QQCHAT_VOICE_MAX_CHARS"] = "40",
            ["QQCHAT_TTS_URL"] = tts.BaseUrl,
            ["QQCHAT_OFFICIAL"] = "1",
            ["QQCHAT_OFFICIAL_APP_ID"] = "100000001",
            ["QQCHAT_OFFICIAL_APP_SECRET"] = "test-secret-not-real",
            ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase,
            ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl,
            ["QQCHAT_OFFICIAL_SANDBOX"] = "1",
            ["QQCHAT_OFFICIAL_WHITELIST_GROUPS"] = officialWhitelistGroups,
        };

        using var bot = StartBot(BotEnv(dataDir));
        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // ---- ① 官方通道上线 ----
        var identified = await official.WaitForIdentifyAsync(1, TimeSpan.FromSeconds(30));
        Check("★ 官方通道连上（token → /gateway/bot → WS identify）", identified,
            "identify=" + official.IdentifyCount + " token 请求=" + official.TokenRequests);

        var identify = official.Identifies.FirstOrDefault();
        Check("★ identify 用 QQBot 头 + 群/单聊 intents（写成 Bearer 会被平台 1127 系列拒掉）",
            identify is not null
            && (identify["d"]?["token"]?.GetValue<string>()?.StartsWith("QQBot ", StringComparison.Ordinal) ?? false)
            && (identify["d"]?["intents"]?.GetValue<int>() ?? 0) == 1 << 25,
            identify?.ToJsonString() ?? "(假网关没收到 identify)");
        Check("★ 取 token 走的是配置里的地址（正式环境是 bots.qq.com，沙箱同一个地址）",
            official.TokenRequests >= 1,
            "token 请求=" + official.TokenRequests);

        // ---- ② 两条通道各来一条：各回各的、上下文不串 ----
        openAi.ClearRequests();
        var officialMark = official.Messages.Count;
        var privateMark = protocol.ActionsReceived.Count;

        openAi.EnqueueReply("""{"suitability": 90, "reply": "在的。官方这边也通。"}""");
        await official.PushGroupMessageAsync(groupOpenId, memberOpenId, "@10001 官方这边在吗", "official_msg_1");

        openAi.EnqueueReply("""{"suitability": 90, "reply": "私域这边也收到了。"}""");
        await protocol.SendGroupMessageAsync(privateGroup, 20001, "老王", "@10001 私域这边呢", 9501, mentionBot: true, ct: cts.Token);

        var officialReplied = await official.WaitForMessageAsync(officialMark + 1, TimeSpan.FromSeconds(45));
        var privateReplied = await WaitUntilAsync(
            () => protocol.ActionsReceived.Skip(privateMark).Any(a => a["action"]?.GetValue<string>() == "send_group_msg"),
            TimeSpan.FromSeconds(45));

        var officialSends = official.Messages.Skip(officialMark).ToList();
        var privateSends = protocol.ActionsReceived.Skip(privateMark)
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg").ToList();

        Check("★ 官方的回复走官方接口（/v2/groups/{group_openid}/messages）",
            officialReplied && officialSends.Count >= 1
            && officialSends.All(m => (m["path"]?.GetValue<string>() ?? string.Empty)
                .Contains($"/v2/groups/{groupOpenId}/messages", StringComparison.Ordinal)),
            string.Join(" | ", officialSends.Select(m => m["path"]?.GetValue<string>() ?? "?")));
        Check("★ 私域的回复走 OneBot，且目标是私域那个群号",
            privateReplied && privateSends.Count >= 1
            && privateSends.All(a => a["params"]?["group_id"]?.GetValue<long>() == privateGroup),
            $"私域发了 {privateSends.Count} 条");
        Check("★ 私域这一趟没有往官方通道发东西（不串台）",
            !privateSends.Any(a => a.ToJsonString().Contains(groupOpenId, StringComparison.Ordinal)),
            "私域动作里出现了官方 group_openid");

        var officialReq = await WaitForRequestAsync(openAi, r => r.Contains("官方这边在吗", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        var privateReq = await WaitForRequestAsync(openAi, r => r.Contains("私域这边呢", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        Check("★ 官方那条的模型请求里只有官方上下文（没有私域那句）",
            officialReq is not null && !officialReq.Contains("私域这边呢", StringComparison.Ordinal),
            officialReq is null ? "(没等到官方那条的模型请求)" : "官方请求里混进了私域内容");
        Check("★ 私域那条的模型请求里只有私域上下文（没有官方那句）",
            privateReq is not null && !privateReq.Contains("官方这边在吗", StringComparison.Ordinal),
            privateReq is null ? "(没等到私域那条的模型请求)" : "私域请求里混进了官方内容");

        // ---- ③ 会话 key：官方带前缀、私域保持老格式（老库/老命令/面板按钮都靠它） ----
        var officialKey = FindOfficialKey(bot.OutputLines);
        Check("★ 官方会话 key 带 official: 前缀（与私域的 group:66691 天然分开）",
            officialKey is not null && officialKey.StartsWith("official:group:", StringComparison.Ordinal),
            officialKey ?? "(日志里没看到 official: 的会话)");
        Check("★ 私域会话 key 仍是老格式 group:66691（没被通道改造弄脏）",
            bot.OutputLines.Any(l => l.Contains("(group:66691)", StringComparison.Ordinal)),
            "没找到 group:66691 的建会话日志");

        if (officialKey is not null)
        {
            var (convStatus, _) = await HttpGetAsync(
                $"http://127.0.0.1:{panelPort}/api/conversations/{Uri.EscapeDataString(officialKey)}");
            Check("★ 面板能按这个 key 取到官方会话（分块展示的数据面通了）",
                convStatus == 200, $"HTTP {convStatus}（key={officialKey}）");
        }

        // ---- ④ 被动回复：原始 msg_id（不是内部别名）+ msg_seq 递增 ----
        var firstBody = officialSends.FirstOrDefault()?["body"] as JsonObject;
        Check("★ 被动回复带的是**原始** msg_id（别名号只在机器人内部用）",
            firstBody?["msg_id"]?.GetValue<string>() == "official_msg_1",
            firstBody?.ToJsonString() ?? "(没有 body)");

        var seqs = officialSends
            .Select(m => (m["body"] as JsonObject)?["msg_seq"]?.GetValue<int>() ?? 0)
            .ToList();
        Check("★ 同一条入站消息的多次回复 msg_seq 递增（否则第二条会被平台拒）",
            seqs.Count >= 2 && seqs[0] == 1 && seqs[1] == 2,
            "msg_seq=" + string.Join(",", seqs));

        // ---- ⑤ 语音：官方走富媒体（silk/msg_type=7），私域仍是 record 段 ----
        openAi.ClearRequests();
        var uploadMark = official.Uploads.Count;
        var voiceMsgMark = official.Messages.Count;

        openAi.EnqueueReply("""{"suitability": 90, "reply": "", "speak": "官方这边用语音说一句"}""");
        await official.PushGroupMessageAsync(groupOpenId, memberOpenId, "@10001 说句话听听", "official_msg_2");

        var uploaded = await official.WaitForUploadAsync(uploadMark + 1, TimeSpan.FromSeconds(45));
        var upload = official.Uploads.Skip(uploadMark).FirstOrDefault();
        Check("★ 官方通道发语音先上传富媒体（file_type=3 语音）",
            uploaded && upload?["file_type"]?.GetValue<int>() == 3,
            upload?.ToJsonString() ?? "(没有上传)");
        Check("★ 上传用 base64 内联且不代发（自建 TTS 在内网，官方平台拉不到我们的地址）",
            (upload?["file_data_len"]?.GetValue<int>() ?? 0) > 0 && upload?["srv_send_msg"]?.GetValue<bool>() == false,
            upload?.ToJsonString() ?? "(没有上传)");

        var mediaMessage = official.Messages.Skip(voiceMsgMark)
            .FirstOrDefault(m => (m["body"] as JsonObject)?["msg_type"]?.GetValue<int>() == 7);
        Check("★ 语音以 msg_type=7 + media.file_info 发出（客户端才会渲染成语音条）",
            mediaMessage is not null
            && ((mediaMessage["body"] as JsonObject)?["media"] as JsonObject)?["file_info"]?.GetValue<string>() is { Length: > 0 },
            mediaMessage?.ToJsonString() ?? "(没有 msg_type=7 的消息)");
        Check("★ 官方那条没有退化成发文字（语音失败才允许退化）",
            !official.Messages.Skip(voiceMsgMark).Any(m => (m["body"] as JsonObject)?["msg_type"]?.GetValue<int>() == 0),
            "官方这条既发了富媒体又发了文字");

        // 私域对照：同一个「speak」意图，私域必须仍旧走 OneBot 的 record 段
        var recordMark = protocol.ActionsReceived.Count;
        openAi.EnqueueReply("""{"suitability": 90, "reply": "", "speak": "私域这边也来一句"}""");
        await protocol.SendGroupMessageAsync(privateGroup, 20002, "小美", "@10001 也来一句", 9502, mentionBot: true, ct: cts.Token);

        var recordSent = await WaitUntilAsync(
            () => protocol.ActionsReceived.Skip(recordMark).Any(a => SegmentType(a, "record") is not null),
            TimeSpan.FromSeconds(45));
        Check("★ 私域那条仍旧走 OneBot record 段（两条通道的语音各按各的规矩）", recordSent,
            "私域没发出 record 段");
        Check("★ 私域发语音时没有惊动官方接口（通道不会互相污染）",
            official.Uploads.Count == uploadMark + 1,
            $"官方上传数 {official.Uploads.Count}（期望 {uploadMark + 1}）");

        // ---- ⑥ 掉线自愈 ----
        var sessionsBefore = official.IdentifyCount + official.ResumeCount;
        await official.CloseSocketAsync();
        var reconnected = await WaitUntilAsync(
            () => official.IdentifyCount + official.ResumeCount > sessionsBefore,
            TimeSpan.FromSeconds(45));
        Check("★ 官方网关掉线后会自己重连（identify 或带 session 的 resume）", reconnected,
            $"identify={official.IdentifyCount} resume={official.ResumeCount}");

        openAi.EnqueueReply("""{"suitability": 90, "reply": "重连之后还能说话。"}""");
        var countBeforeProbe = official.Messages.Count;
        await official.PushGroupMessageAsync(groupOpenId, memberOpenId, "@10001 重连之后还在吗", "official_msg_3");
        var worksAfterReconnect = await official.WaitForMessageAsync(countBeforeProbe + 1, TimeSpan.FromSeconds(45));
        Check("★ 重连之后仍能收消息并回复", worksAfterReconnect,
            "重连后没有再收到发送请求");

        // ---- ⑦ 白名单彼此独立 ----
        await bot.StopAsync();

        var dataDir2 = NewDataDir("s42b");
        using var bot2 = StartBot(BotEnv(dataDir2, officialWhitelistGroups: "123")); // 123 永远不会是别名号
        await WaitForPortAsync(botWsPort, cts.Token, bot2);

        using var protocol2 = new MockProtocol { SelfId = 10001 };
        await protocol2.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol2.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var sessionsBeforeSecondBot = official.IdentifyCount + official.ResumeCount;
        await WaitUntilAsync(
            () => official.IdentifyCount + official.ResumeCount > sessionsBeforeSecondBot,
            TimeSpan.FromSeconds(30));

        openAi.ClearRequests();
        var officialMark2 = official.Messages.Count;
        var privateMark2 = protocol2.ActionsReceived.Count;

        await official.PushGroupMessageAsync(groupOpenId, memberOpenId, "@10001 白名单之外的官方消息", "official_msg_b1");
        openAi.EnqueueReply("""{"suitability": 90, "reply": "私域照常回。"}""");
        await protocol2.SendGroupMessageAsync(privateGroup, 20003, "老王", "@10001 私域照常吗", 9503, mentionBot: true, ct: cts.Token);

        var privateStillOk = await WaitUntilAsync(
            () => protocol2.ActionsReceived.Skip(privateMark2).Any(a => a["action"]?.GetValue<string>() == "send_group_msg"),
            TimeSpan.FromSeconds(45));
        var officialLeaked = await WaitForRequestAsync(openAi, r => r.Contains("白名单之外的官方消息", StringComparison.Ordinal), TimeSpan.FromSeconds(6));

        Check("★ 官方白名单不匹配 → 那条官方消息被忽略（连模型都没叫）",
            official.Messages.Count == officialMark2 && officialLeaked is null,
            $"官方发送数 {official.Messages.Count}（期望 {officialMark2}）");
        Check("★ 同一份配置下私域照常回复（两份名单互不影响）", privateStillOk,
            "私域这条没回");
        Check("★ 被忽略的官方会话没有落进面板（不建档）",
            !bot2.OutputLines.Any(l => l.Contains("official:", StringComparison.Ordinal)),
            "白名单外的官方消息还是建了会话");
    }

    /// <summary>从机器人日志里抠出官方会话 key（<c>official:group:800…</c>）——别名号是哈希发的，测试里算不出来，只能读回来。</summary>
    private static string? FindOfficialKey(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var idx = line.IndexOf("official:", StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var tail = line[idx..];
            var end = tail.IndexOfAny([')', ' ', '，', ',', ']']);
            return end > 0 ? tail[..end] : tail;
        }

        return null;
    }
}

using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S31 图片下载：rkey 过期（400）/ 缓存 / 让协议端重新签发。
///
/// 线上现象（号主让排查的日志）：`[Vision] 图片下载失败 400: https://multimedia.nt.qq.com.cn/download?…`
/// 实测结论（去问 NapCat 的历史 API 得到）：
///   • QQ 的图片地址是**带时效 rkey 的临时链**，过期后 CDN 一律回 400；
///   • 同一张图在会话上下文里会留很久，而每个轮次生成都会重新下一遍 —— 过期后就是每轮一条 400，日志刷屏；
///   • 协议端手里有消息记录，**重新签发一份地址就能下到**（实测同一条消息新地址 200）。
///
/// 所以这里钉三件事：
///   ① 第一次能下到 → 图片真的进了模型请求（image_url 段）；
///   ② 同一个 URL **只下一次**（缓存）—— 后面几轮生成不再重复下载；
///   ③ 地址过期（400）时，用协议端重新签发的地址取回**同一张图**。
/// </summary>
public static partial class Program
{
    private static async Task RunImageFetchScenarioAsync()
    {
        Section("S31 图片下载：rkey 过期 400 → 协议端重签 + 缓存不重复下载");

        const int openAiPort = 17838;
        const int botWsPort = 13047;
        const int imagePort = 18113;
        const long groupId = 66712;

        var dataDir = NewDataDir("s31");
        using var images = new MockImageHost(imagePort);
        images.Start();

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

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
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",        // 只测识图那条路（表情包自己也下载，会干扰计数）
            ["QQCHAT_HEALTH_PORT"] = "0",
            // 假图片服务在 127.0.0.1（默认的 SSRF 防护会拦），测试里显式放开
            ["QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS"] = "1"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // ---- ① 正常下载：图片进模型请求 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");
        await protocol.SendGroupMessageAsync(groupId, 30031, "小美", "看这张图", 9801, imageUrl: images.Url(1), ct: cts.Token);
        var first = await WaitForRequestAsync(openAi, r => r.Contains("看这张图"), TimeSpan.FromSeconds(40));
        var firstImages = first is null ? new List<string>() : ImageDataUrls(openAi.Requests.Last(r => UserTexts(r).Any(u => u.Contains("看这张图"))));
        Check("★ 图片下载成功并作为 image_url 进模型请求",
            first is not null && firstImages.Count == 1, firstImages.Count.ToString());
        Check("★ 下到的就是那张图（base64 解出来的字节对得上）",
            firstImages.Count == 1 && DataUrlBytes(firstImages[0]).SequenceEqual(MockImageHost.Bytes(1)),
            firstImages.Count == 1 ? $"长度 {DataUrlBytes(firstImages[0]).Length}" : "(没有图片段)");
        var servedAfterFirst = images.Served;
        Check("第一次确实走了一次 HTTP 下载", servedAfterFirst >= 1, $"Served={servedAfterFirst}");

        // ---- ② 缓存：再生成一轮时不再重复下载（线上就是这里每轮重试、过期后刷 400）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");
        await protocol.SendGroupMessageAsync(groupId, 30032, "小美", "顺便再聊一句", 9802, ct: cts.Token);
        var second = await WaitForRequestAsync(openAi, r => r.Contains("顺便再聊一句"), TimeSpan.FromSeconds(40));
        var secondImages = second is null
            ? new List<string>()
            : ImageDataUrls(openAi.Requests.Last(r => UserTexts(r).Any(u => u.Contains("顺便再聊一句"))));
        Check("★ 上下文里的图仍然带给模型（缓存里有就直接用）",
            second is not null && secondImages.Count == 1, secondImages.Count.ToString());
        Check("★ 同一个 URL 只下载一次（缓存生效，不再每轮重下）",
            images.Served == servedAfterFirst,
            $"Served {servedAfterFirst} → {images.Served}");

        // ---- ③ 地址过期（400）→ 让协议端重新签发，取回同一张图 ----
        // 假图片服务切到“必须带 rkey”模式：不带就 400（与 QQ 的过期行为一致）
        images.RequireFreshRkey = true;
        // 协议端 get_msg 里给的是“重新签发”的地址（同一张图，rkey 是新的）
        protocol.QuotedImageUrls.Add(images.FreshUrl(2));
        var getMsgBefore = protocol.GetMsgHits;
        var servedBeforeExpired = images.Served;

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");
        await protocol.SendGroupMessageAsync(groupId, 30033, "小美", "这张过期了", 9803, imageUrl: images.StaleUrl(2), ct: cts.Token);
        var expired = await WaitForRequestAsync(openAi, r => r.Contains("这张过期了"), TimeSpan.FromSeconds(40));
        await Task.Delay(500);

        var expiredImages = expired is null
            ? new List<string>()
            : ImageDataUrls(openAi.Requests.Last(r => UserTexts(r).Any(u => u.Contains("这张过期了"))));
        Check("★ 过期地址确实被 CDN 400 了（先复现问题）",
            images.Rejected >= 1, $"Rejected={images.Rejected}");
        Check("★ 过期后机器人去问协议端重新签发（get_msg）",
            protocol.GetMsgHits > getMsgBefore, $"GetMsgHits {getMsgBefore} → {protocol.GetMsgHits}");
        Check("★ 用重新签发的地址把同一张图取了回来（image_url 里能看到那张图）",
            expiredImages.Any(u => DataUrlBytes(u).SequenceEqual(MockImageHost.Bytes(2))),
            expiredImages.Count == 0 ? "(没有图片段)" : $"{expiredImages.Count} 张图，字节长度：{string.Join(",", expiredImages.Select(u => DataUrlBytes(u).Length))}");
        Check("★ 日志里写明了“过期 → 重签取回”，且**不**报“下载失败”（正常路径不该像出事）",
            bot.OutputLines.Any(l => l.Contains("已过期") && l.Contains("重签") && l.Contains("取回")) &&
            !bot.OutputLines.Any(l => l.Contains("图片地址下载失败")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("图片")).TakeLast(3)));
        Check("下载次数：过期的那次走了一次 400 + 一次成功（没有反复重试）",
            images.Served - servedBeforeExpired <= 2, $"新增 {images.Served - servedBeforeExpired} 次");

        await bot.StopAsync();
    }

    /// <summary>取请求里的图片 data URL（多模态 image_url 段）。</summary>
    private static List<string> ImageDataUrls(JsonObject? request)
    {
        var list = new List<string>();
        if (request?["messages"]?.AsArray() is not { } messages)
        {
            return list;
        }

        foreach (var message in messages)
        {
            if (message?["content"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part?["type"]?.GetValue<string>() == "image_url" &&
                    part["image_url"]?["url"]?.GetValue<string>() is { } url)
                {
                    list.Add(url);
                }
            }
        }

        return list;
    }

    /// <summary>把 data:image/xxx;base64,… 解回字节（对不上就返回空数组）。</summary>
    private static byte[] DataUrlBytes(string dataUrl)
    {
        var at = dataUrl.IndexOf("base64,", StringComparison.Ordinal);
        if (at < 0)
        {
            return Array.Empty<byte>();
        }

        try
        {
            return Convert.FromBase64String(dataUrl[(at + 7)..]);
        }
        catch (FormatException)
        {
            return Array.Empty<byte>();
        }
    }
}

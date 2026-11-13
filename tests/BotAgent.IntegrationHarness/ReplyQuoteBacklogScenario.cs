using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    // A silent image turn leaves no self message to distinguish B from the newer C.
    // B only mentions another peer; it must not become the quote for a reply about A.
    private static async Task RunReplyQuoteBacklogScenarioAsync()
    {
        Section("S66 quote backlog: silence, image and a mention of another peer");
        const long groupId = 10001;
        const long recallGroupId = 10002;
        const long imageId = 10011;
        const long mentionId = 10012;
        const long latestId = 10013;
        const string inferredReply = "图片里的合成图形有三条边。";
        const string chosenReply = "这次明确回答前面那张合成图片。";
        var botWsPort = FreePort(13089);
        using var images = new MockImageHost(FreePort(17889));
        images.Start();
        using var openAi = new MockOpenAi(FreePort(17890)) { ResponseDelayMs = 3000 };
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "{{inferredReply}}"}""");
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "{{chosenReply}}", "replyTo": {{imageId}}}""");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var bot = StartBot(InvariantEnv(NewDataDir("s66"), openAi.BaseUrl, botWsPort,
            ("QQCHAT_WHITELIST", $"{groupId},{recallGroupId}"),
            ("QQCHAT_LOG_FILE", "0"),
            ("QQCHAT_REPLY_PACING", "0"),
            ("QQCHAT_PROFILE_SUMMARY", "0"),
            ("QQCHAT_STICKERS", "0"),
            ("QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS", "1")));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        await protocol.SendGroupMessageAsync(groupId, 20002, "群友A", "合成图形", imageId,
            imageUrl: images.Url(1), ct: cts.Token);
        Check("image starts the first generation before later peers arrive",
            await WaitUntilAsync(() => openAi.Requests.Count >= 1, TimeSpan.FromSeconds(15)),
            $"requestCount={openAi.Requests.Count}");
        // HandleAsync captures the delay before recording the request: later turns stay fast.
        openAi.ResponseDelayMs = 0;
        await protocol.SendGroupSegmentsAsync(groupId, 20004, "群友B", mentionId,
            new JsonArray { new JsonObject { ["type"] = "at", ["data"] = new JsonObject { ["qq"] = "20006" } } },
            "[CQ:at,qq=20006]", cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 20006, "群友C", "另一个合成话题", latestId, ct: cts.Token);
        Check("all three queued generations complete",
            await WaitUntilAsync(() => protocol.ActionsReceived.Any(a => MessageText(a) == chosenReply),
                TimeSpan.FromSeconds(30)), $"requestCount={openAi.Requests.Count}");

        var backlogRequest = openAi.Requests.ElementAtOrDefault(1);
        var peerTurns = backlogRequest?["messages"]?.AsArray()
            .Where(m => m?["role"]?.GetValue<string>() == "user").ToList() ?? new();
        var imageTurn = peerTurns.FirstOrDefault(m => m?["content"] is JsonArray);
        var imageParts = imageTurn?["content"] as JsonArray;
        var imageText = imageParts?.FirstOrDefault(p => p?["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>();
        var mentionTurn = peerTurns.FirstOrDefault(m => m?["content"] is JsonValue v && v.ToString().Contains($"(#{mentionId})"));
        var newestTurn = peerTurns.FirstOrDefault(m => m?["content"] is JsonValue v && v.ToString().Contains($"(#{latestId})"));
        Check("the image and its own reply id stay in the same model message",
            imageText?.Contains($"(#{imageId})") == true &&
            imageParts?.Any(p => p?["type"]?.GetValue<string>() == "image_url") == true,
            $"imageId={imageId}, imageAttached={imageParts?.Count > 1}");
        Check("queued B sees both the mention of a peer and the newer C",
            mentionTurn is not null && newestTurn is not null &&
            mentionTurn?["content"]?.ToString().Contains("@20006") == true,
            $"mentionSeen={mentionTurn is not null}, newerPeerSeen={newestTurn is not null}");

        var inferredSend = protocol.ActionsReceived.LastOrDefault(a => MessageText(a) == inferredReply);
        var chosenSend = protocol.ActionsReceived.LastOrDefault(a => MessageText(a) == chosenReply);
        Check("a stale queued trigger after silence does not invent a quote target",
            inferredSend is not null && QuotedMessageId(inferredSend) is null,
            $"trigger={mentionId}, quote={QuotedMessageId(inferredSend)?.ToString() ?? "none"}");
        Check("an explicit image target survives a mention of another peer",
            chosenSend is not null && QuotedMessageId(chosenSend) == imageId,
            $"chosen={imageId}, quote={QuotedMessageId(chosenSend)?.ToString() ?? "none"}");
        Check("the initial silent turn sends no message and creates no self boundary",
            protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") == 2,
            $"sendCount={protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg")}");

        // A different conversation starts without self messages. E starts with F newer
        // in its snapshot; G then arrives and F is recalled while E is being generated.
        // The mutable message objects must not promote E into a current trigger.
        const long silentId = 10111;
        const long staleId = 10112;
        const long recalledId = 10113;
        const long correctionId = 10114;
        const string recallReply = "这是一条合成的撤回边界回复。";
        var beforeRecall = openAi.Requests.Count;
        openAi.ResponseDelayMs = 2000;
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "{{recallReply}}"}""");
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");
        openAi.EnqueueReply("""{"suitability": 0, "reply": ""}""");
        await protocol.SendGroupMessageAsync(recallGroupId, 20002, "群友A", "合成的静默触发", silentId, ct: cts.Token);
        Check("recall case starts a silent generation before the backlog",
            await WaitUntilAsync(() => openAi.Requests.Count > beforeRecall, TimeSpan.FromSeconds(10)),
            $"requestCount={openAi.Requests.Count}");
        openAi.ResponseDelayMs = 3000;
        await protocol.SendGroupMessageAsync(recallGroupId, 20004, "群友B", "合成的旧触发", staleId, ct: cts.Token);
        await protocol.SendGroupMessageAsync(recallGroupId, 20006, "群友C", "合成的待撤回消息", recalledId, ct: cts.Token);
        Check("stale generation begins with a newer peer in its snapshot",
            await WaitUntilAsync(() => openAi.Requests.Count >= beforeRecall + 2, TimeSpan.FromSeconds(10)),
            $"requestCount={openAi.Requests.Count}");
        var recallRequest = openAi.Requests.ElementAtOrDefault(beforeRecall + 1);
        Check("recall snapshot has the newer peer and excludes the later correction",
            recallRequest?.ToJsonString().Contains($"(#{recalledId})") == true &&
            recallRequest?.ToJsonString().Contains($"(#{correctionId})") == false,
            $"newerId={recalledId}, laterId={correctionId}");
        openAi.ResponseDelayMs = 0;
        await protocol.SendGroupMessageAsync(recallGroupId, 20006, "群友C", "合成的更正消息", correctionId, ct: cts.Token);
        await protocol.SendGroupRecallAsync(recallGroupId, 20006, recalledId, ct: cts.Token);
        Check("recall boundary generations complete",
            await WaitUntilAsync(() => openAi.Requests.Count >= beforeRecall + 4 &&
                protocol.ActionsReceived.Any(a => MessageText(a) == recallReply), TimeSpan.FromSeconds(15)),
            $"requestCount={openAi.Requests.Count}");
        var afterRecallRequest = openAi.Requests.ElementAtOrDefault(beforeRecall + 2);
        var afterRecallTurns = afterRecallRequest?["messages"]?.AsArray()
            .Where(m => m?["role"]?.GetValue<string>() == "user")
            .Select(m => m?["content"]?.ToString() ?? string.Empty).ToList() ?? new();
        Check("later generation observes the recall and its correction",
            afterRecallTurns.Any(text => text.Contains("[已撤回]")) &&
            afterRecallTurns.Any(text => text.Contains($"(#{correctionId})")),
            $"recalledId={recalledId}, correctionId={correctionId}");
        var recallSend = protocol.ActionsReceived.LastOrDefault(a => MessageText(a) == recallReply);
        Check("recall during generation cannot promote a stale trigger into a quote",
            recallSend is not null && QuotedMessageId(recallSend) is null,
            $"trigger={staleId}, quote={QuotedMessageId(recallSend)?.ToString() ?? "none"}");
        await bot.StopAsync();
    }
}

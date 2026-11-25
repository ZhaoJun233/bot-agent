using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S164 / S165 Phase 5 真实闭环场景（全合成数据）：
///   S164: 私聊 //remind 增查停删 + 群聊 //todo 拒绝 + 私聊 //todo 自动联动定时提醒及完成级联取消 + ADR-0006 //identity 默认关闭/挑战/自确认拦截；
///   S165: Web 专属工作台（workspace:owner）只读令牌 403 拦截、管理员带 .txt 附件对话持久落库、workspace.message.appended 无正文事件与群聊零泄漏。
/// </summary>
public static partial class Program
{
    private static async Task RunPhase5SchedulerTodoIdentityScenarioAsync()
    {
        Section("S164 Phase 5 //remind + //todo 联动级联取消 + ADR-0006 //identity 边界");

        var openAiPort = FreePort(17965);
        var botWsPort = FreePort(13175);
        var panelPort = FreePort(18225);

        const long groupId = 10001;
        const long userId = 20001;
        const long otherUserId = 20009;
        const string adminToken = "s164-zba-admin-token";
        const string readToken = "s164-zba-read-token";

        var dataDir = NewDataDir("s164");
        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

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
            ["QQCHAT_WHITELIST_PRIVATES"] = $"{userId},{otherUserId}",
            ["QQCHAT_AGENT_USERS"] = userId.ToString(),
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_ENABLE_POKE"] = "0",
            ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_ENABLE_MUSIC"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = SyntheticPanelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
            ["QQCHAT_AGENT"] = "0",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var zbaRoot = $"http://127.0.0.1:{panelPort}/zba/v1";

        using var protocol = new MockProtocol { SelfId = 10001 };
        protocol.SetRole(groupId, userId, "member");
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Task.Delay(400);

        long msgId = 64000;

        async Task<string> SendPrivateAsync(string cmd)
        {
            var before = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg");
            await protocol.SendPrivateMessageAsync(userId, "合成用户", cmd, ++msgId, ct: cts.Token);
            var sent = await protocol.WaitForActionAsync("send_private_msg", TimeSpan.FromSeconds(20), skip: before);
            return sent?["params"]?["message"]?.ToString() ?? string.Empty;
        }

        async Task<string> SendGroupAsync(string cmd)
        {
            var before = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
            await protocol.SendGroupMessageAsync(groupId, userId, "合成群友", cmd, ++msgId, mentionBot: false, ct: cts.Token);
            var sent = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(20), skip: before);
            return sent?["params"]?["message"]?.ToString() ?? string.Empty;
        }

        // 1. //remind add -> list -> pause -> delete
        var remindAdd = await SendPrivateAsync("//remind add 30m | s164喝水测试");
        var jobMatch = Regex.Match(remindAdd, @"job-[a-f0-9-]+");
        var jobId = jobMatch.Success ? jobMatch.Value : string.Empty;
        Check("s164 私聊 //remind add 创建一次性提醒并返回 jobId",
            jobId.Length > 4 && remindAdd.Contains("已创建提醒", StringComparison.Ordinal),
            $"reply={remindAdd}");

        var remindList = await SendPrivateAsync("//remind list");
        var remindPause = await SendPrivateAsync($"//remind pause {jobId}");
        var remindDel = await SendPrivateAsync($"//remind delete {jobId}");
        Check("s164 //remind list / pause / delete 闭环正常",
            remindList.Contains(jobId, StringComparison.Ordinal)
            && remindPause.Contains("暂停", StringComparison.Ordinal)
            && remindDel.Contains("删除", StringComparison.Ordinal),
            $"list={remindList} pause={remindPause} del={remindDel}");

        // 2. Group //todo rejected; Private //todo add with due links job; //todo done cascade-deletes linked job
        var groupTodo = await SendGroupAsync("//todo add 群聊违规待办");
        Check("s164 群聊内触发 //todo 被明确拒绝（仅限私聊）",
            groupTodo.Contains("仅限可靠身份的私聊", StringComparison.Ordinal),
            $"reply={groupTodo}");

        var privTodo = await SendPrivateAsync("//todo add 整理周报 | 30m | 合成备注");
        var todoMatch = Regex.Match(privTodo, @"todo-[a-f0-9]+");
        var linkedMatch = Regex.Match(privTodo, @"job-todo-[a-f0-9]+");
        var todoId = todoMatch.Success ? todoMatch.Value : string.Empty;
        var linkedJobId = linkedMatch.Success ? linkedMatch.Value : string.Empty;

        var todoList = await SendPrivateAsync("//todo list");
        var (jobsReadSt, jobsReadBody) = await ZbaAsync(http, "GET", zbaRoot + "/scheduler/jobs", readToken);
        var (todosReadSt, _) = await ZbaAsync(http, "GET", zbaRoot + "/todos", readToken);
        var (todosBeforeSt, todosBeforeBody) = await ZbaAsync(http, "GET", zbaRoot + "/todos", adminToken);
        var parsedJobs = jobsReadSt == 200 ? JsonNode.Parse(jobsReadBody)?["jobs"]?.AsArray() : null;
        var linkedJobNode = parsedJobs?.FirstOrDefault(j => string.Equals(j?["jobId"]?.GetValue<string>(), linkedJobId, StringComparison.Ordinal));
        Check("s164 私聊 //todo add 创建待办并自动挂接提醒，只读令牌 GET /todos 403 且调度接口脱敏正文",
            todoId.Length > 5 && linkedJobId.Length > 9
            && todoList.Contains(todoId, StringComparison.Ordinal)
            && jobsReadSt == 200
            && string.Equals(linkedJobNode?["text"]?.GetValue<string>(), "[受保护]", StringComparison.Ordinal)
            && !string.Equals(linkedJobNode?["scopeKey"]?.GetValue<string>(), $"private:{userId}", StringComparison.Ordinal)
            && !jobsReadBody.Contains("整理周报", StringComparison.Ordinal)
            && todosReadSt == 403
            && todosBeforeSt == 200 && !todosBeforeBody.Contains(todoId, StringComparison.Ordinal),
            $"reply={privTodo} list={todoList} jobsRead={jobsReadBody} todosRead={todosReadSt} todosAdmin={todosBeforeBody}");

        var todoDone = await SendPrivateAsync($"//todo done {todoId}");
        var (jobsAfterSt, jobsAfterBody) = await ZbaAsync(http, "GET", zbaRoot + "/scheduler/jobs", adminToken);
        Check("s164 //todo done 完成待办并级联撤销关联定时提醒",
            todoDone.Contains("已完成", StringComparison.Ordinal)
            && jobsAfterSt == 200 && !jobsAfterBody.Contains(linkedJobId, StringComparison.Ordinal),
            $"done={todoDone} jobsAfter={jobsAfterBody}");

        // 3. ADR-0006 //identity disabled by default -> non-owner blocked -> toggle enable -> issue challenge -> reject same-endpoint self-confirm
        var saveOwnerRes = await ZbaAsync(http, "POST", zbaRoot + "/permissions/roles/save", adminToken,
            new JsonObject
            {
                ["expectedRevision"] = 0,
                ["idempotencyKey"] = "s164-owner-role-1",
                ["bindings"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["platformId"] = "qq.private",
                        ["accountScope"] = "legacy",
                        ["instanceId"] = "",
                        ["scopeConversationKey"] = null,
                        ["nativeUserId"] = userId.ToString(),
                        ["role"] = 4,
                        ["note"] = "s164-bot-owner"
                    }
                }
            }.ToJsonString());
        var idStatus = await SendPrivateAsync("//identity status");
        var idStartDisabled = await SendPrivateAsync("//identity link start");
        Check("s164 //identity 默认关闭并拒绝发起挑战",
            saveOwnerRes.Status == 200
            && idStatus.Contains("默认关闭", StringComparison.Ordinal)
            && idStartDisabled.Contains("默认关闭", StringComparison.Ordinal),
            $"saveRole={saveOwnerRes.Status} status={idStatus} start={idStartDisabled}");

        var beforeNonOwner = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_private_msg");
        await protocol.SendPrivateMessageAsync(otherUserId, "非主人私聊用户", "//identity status", ++msgId, ct: cts.Token);
        var nonOwnerSent = await protocol.WaitForActionAsync("send_private_msg", TimeSpan.FromSeconds(20), skip: beforeNonOwner);
        var nonOwnerReply = nonOwnerSent?["params"]?["message"]?.ToString() ?? string.Empty;
        Check("s164 非 BotOwner 私聊触发 //identity 被明确拒绝",
            nonOwnerReply.Contains("BotOwner", StringComparison.Ordinal),
            $"reply={nonOwnerReply}");

        var toggleRes = await ZbaAsync(http, "POST", zbaRoot + "/workspace/identity/toggle", adminToken,
            """{"enabled":true,"shareTodos":true,"shareKnowledge":false,"sharePreferences":false}""");
        var idStartEnabled = await SendPrivateAsync("//identity link start");
        var chMatch = Regex.Match(idStartEnabled, @"ID-[A-F0-9]{16}");
        var chCode = chMatch.Success ? chMatch.Value : string.Empty;
        var idSelfConfirm = chCode.Length > 0
            ? await SendPrivateAsync($"//identity link confirm {chCode}")
            : string.Empty;
        Check("s164 开启后 //identity link start 签发16位挑战码，并拒绝同端自我确认",
            toggleRes.Status == 200 && chCode.Length > 0
            && idSelfConfirm.Contains("不能在同一端自我确认", StringComparison.Ordinal),
            $"start={idStartEnabled} confirm={idSelfConfirm}");
    }

    private static async Task RunPhase5WorkspacePrivacyScenarioAsync()
    {
        Section("S165 Phase 5 个人工作台隔离 · 无正文增量事件与附件问答");

        var openAiPort = FreePort(17966);
        var botWsPort = FreePort(13176);
        var panelPort = FreePort(18226);

        const long groupId = 10002;
        const long userId = 20002;
        const string adminToken = "s165-zba-admin-token";
        const string readToken = "s165-zba-read-token";
        const string groupSecret = "S165_GROUP_SECRET_DO_NOT_LEAK_TO_WORKSPACE";
        const string wsPrompt = "请总结这份合成工作台附件要点";
        const string wsReply = "已阅工作台附件：关键结论已记录。";

        var dataDir = NewDataDir("s165");
        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.AddRule(groupSecret, """{"suitability":95,"reply":"群里收到合成消息。"}""");
        openAi.AddRule("S165_WORKSPACE_ATTACHMENT_TOKEN", $$"""{"suitability":95,"reply":"{{wsReply}}"}""");

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
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_ENABLE_POKE"] = "0",
            ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_ENABLE_MUSIC"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = SyntheticPanelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken,
            ["QQCHAT_AGENT"] = "0",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var zbaRoot = $"http://127.0.0.1:{panelPort}/zba/v1";

        using var protocol = new MockProtocol { SelfId = 10001 };
        protocol.SetRole(groupId, userId, "member");
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Task.Delay(400);

        // 1. Group message goes to QQ group only
        var gBefore = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
        await protocol.SendGroupMessageAsync(groupId, userId, "合成群友", groupSecret, 65001, mentionBot: true, ct: cts.Token);
        await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(20), skip: gBefore);

        // 2. Read-only ZBA token cannot read workspace messages (403)
        var readDenied = await ZbaAsync(http, "GET", zbaRoot + "/workspace/messages", readToken);
        Check("s165 只读令牌访问 /zba/v1/workspace/messages 被 403 拒绝",
            readDenied.Status == 403, $"status={readDenied.Status}");

        // 3. Send workspace prompt + bounded .txt attachment via admin token
        var eventsBefore = await ZbaAsync(http, "GET", zbaRoot + "/events?after=0&limit=50", readToken);
        var afterSeq = 0L;
        if (eventsBefore.Status == 200 && JsonNode.Parse(eventsBefore.Body)?["nextSequence"] is { } ns)
            afterSeq = ns.GetValue<long>();

        var attachBytes = Encoding.UTF8.GetBytes("S165_WORKSPACE_ATTACHMENT_TOKEN\n" + new string('A', 1_250_000));
        var sendPayload = new JsonObject
        {
            ["idempotencyKey"] = "s165-ws-send-1",
            ["text"] = wsPrompt,
            ["mode"] = "chat",
            ["attachmentName"] = "owner-note.txt",
            ["attachmentBase64"] = Convert.ToBase64String(attachBytes),
        }.ToJsonString();

        var sendRes = await ZbaAsync(http, "POST", zbaRoot + "/workspace/messages/send", adminToken, sendPayload);
        var readOk = await ZbaAsync(http, "GET", zbaRoot + "/workspace/messages", adminToken);
        var eventsAfter = await ZbaAsync(http, "GET", $"{zbaRoot}/events?after={afterSeq}&limit=50", readToken);

        var readJson = readOk.Status == 200 ? JsonNode.Parse(readOk.Body) : null;
        var readScope = readJson?["scopeKey"]?.GetValue<string>() ?? string.Empty;
        var readMsgs = (readJson?["messages"] as JsonArray)?
            .Select(m => $"{m?["role"]?.GetValue<string>()}:{m?["text"]?.GetValue<string>()}:{m?["attachmentName"]?.GetValue<string>()}")
            .ToArray() ?? [];
        var joinedMsgs = string.Join("\n", readMsgs);

        Check("s165 工作台携带附件发送并获模型回复，严格写入 workspace:owner 且绝不混入群聊内容",
            sendRes.Status == 200
            && readOk.Status == 200
            && readScope == "workspace:owner"
            && joinedMsgs.Contains(wsPrompt, StringComparison.Ordinal)
            && joinedMsgs.Contains(wsReply, StringComparison.Ordinal)
            && joinedMsgs.Contains("owner-note.txt", StringComparison.Ordinal)
            && !joinedMsgs.Contains(groupSecret, StringComparison.Ordinal),
            $"send={sendRes.Status} scope={readScope} msgs={joinedMsgs} raw={readOk.Body}");

        Check("s165 /zba/v1/events 广播无正文增量事件 workspace.message.appended（绝不含对话正文）",
            eventsAfter.Status == 200
            && eventsAfter.Body.Contains("workspace.message.appended", StringComparison.Ordinal)
            && !eventsAfter.Body.Contains(wsPrompt, StringComparison.Ordinal)
            && !eventsAfter.Body.Contains(wsReply, StringComparison.Ordinal)
            && !eventsAfter.Body.Contains("S165_WORKSPACE_ATTACHMENT_TOKEN", StringComparison.Ordinal),
            $"events={eventsAfter.Body}");

        // 4. Schema v15 invariant
        var dbPath = Path.Combine(dataDir, "data", "botagent.db");
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(cts.Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var userVer = Convert.ToInt64(await cmd.ExecuteScalarAsync(cts.Token));
        Check("s165 持久化数据库 PRAGMA user_version 保持 15（零表迁移）",
            userVer == 15, $"user_version={userVer}");
    }
}

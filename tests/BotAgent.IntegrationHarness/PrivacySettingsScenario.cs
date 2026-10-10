using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S36 「列出会话时脱敏」开关 + 「Agent 附加提示词」（管理员 2026-09-18：
/// 开发/排查时**不许读取群聊正文与成员隐私**，默认提示词就要把这条写死）。
///
/// 这个场景钉住四件事：
///   ① 面板默认：脱敏**开**、附加提示词 = 那条隐私红线，而且默认值由服务端下发（前端不抄一份）；
///   ② Agent 列出会话时，群名 / 会话标题里的长数字只留前 3 后 2（`group:123456789` → `群聊 123***89`），
///      群里的 `//sessions all` 与面板总览都遵守；关掉开关才露真名；
///   ③ 显示归显示：`nameRaw` 仍是真名（面板改名要用真名，否则会把「群友A」写回去）；
///   ④ 附加提示词真的随任务下发 —— 服务器内置 agent 的**系统提示词**里能看到它；
///      面板改成自定义值/清空后，下一轮立刻按新的来（不是启动时读一次就定死）。
///
/// 注意：这里的断言只看**形状与标志串**，不看群聊正文 —— 这也是这个功能本身的意义。
/// </summary>
public static partial class Program
{
    private static async Task RunPrivacySettingsScenarioAsync()
    {
        await RunOfficialSecretBoundaryScenarioAsync();
        Section("S36 列出会话时脱敏 + Agent 附加提示词（默认：不读取敏感信息）");

        const int openAiPort = 17881;
        const int agentAiPort = 17882;
        const int botWsPort = 13095;
        const int panelPort = 18115;
        const long groupId = 66760;
        const string fakeChatKey = "group:123456789";   // 刻意用长 id：好验「前 3 后 2」
        const string fakeName = "会话 123456789";

        var panel = $"http://127.0.0.1:{panelPort}";
        var dataDir = NewDataDir("s36");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // 服务器内置 agent 走自己的假网关：这一轮要看它的**系统提示词**里有没有那份附加提示词
        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();
        agentAi.EnqueueReply("""{"final":"默认提示词那轮看完了"}""");
        agentAi.EnqueueReply("""{"final":"自定义提示词那轮看完了"}""");
        agentAi.EnqueueReply("""{"final":"清空提示词那轮看完了"}""");
        agentAi.EnqueueReply("""{"final":"备用结论"}""");

        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["BOTAGENT_DATA_DIR"] = dataDir,
            ["QQCHAT_LOG_FILE"] = "0",
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
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = "20002",
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_TARGET"] = "server",
            ["QQCHAT_AGENT_SERVER_WORKDIR"] = "/tmp",
            ["QQCHAT_AGENT_SERVER_URL"] = agentAi.BaseUrl,
            ["QQCHAT_AGENT_SERVER_MODEL"] = "custom-agent-model",
            ["QQCHAT_AGENT_PROGRESS"] = "0"
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await Task.Delay(800);

        List<string> Sent() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // ── ① 面板默认：脱敏开 + 附加提示词就是那条隐私红线 ──
        var (settingsCode, settingsBody) = await PanelGetAsync($"{panel}/api/settings");
        // 响应是 { runtime: {...面板设置...}, env: {...} } —— 开关与提示词都在 runtime 里
        var settings = (JsonNode.Parse(settingsBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("面板拿得到设置", settingsCode == 200 && settings.Count > 0, $"HTTP {settingsCode}");

        Check("★ 脱敏开关默认开（列表默认不漏群名/昵称/QQ 号）",
            settings["enableAgentMask"]?.GetValue<bool>() == true,
            settings["enableAgentMask"]?.ToJsonString() ?? "(没有这个字段)");

        var defaultPrompt = settings["agentPrompt"]?.GetValue<string>() ?? string.Empty;
        Check("★ 默认附加提示词写的就是「不读取敏感信息」",
            defaultPrompt.Contains("隐私红线") && defaultPrompt.Contains("不要读取") &&
            defaultPrompt.Contains("ERROR") && defaultPrompt.Contains("message"),
            defaultPrompt.Length > 90 ? defaultPrompt[..90].Replace('\n', ' ') + "…" : defaultPrompt);
        Check("★ 默认那份由服务端下发（面板「恢复默认」不用前端抄一份，避免两处漂移）",
            settings["agentPromptDefault"]?.GetValue<string>() == defaultPrompt);

        // ── ② 建一个「长 id 群」的会话：它必须以脱敏形状出现在列表里 ──
        var (newCode, newBody) = await PanelPostJsonAsync($"{panel}/api/agent/sessions",
            $$"""{"key":"{{fakeChatKey}}","action":"new","name":"{{fakeName}}","backend":"server"}""");
        Check("面板能建会话（用来验脱敏）", newCode == 200,
            $"HTTP {newCode} {Snippet(newBody, "ok")}");

        var (listCode, listBody) = await PanelGetAsync($"{panel}/api/agent/sessions");
        var all = JsonNode.Parse(listBody) as JsonObject ?? new JsonObject();
        var chat = (all["chats"] as JsonObject)?[fakeChatKey] as JsonObject;
        var panelSession = chat?["sessions"]?.AsArray().FirstOrDefault() as JsonObject;
        Check("★ 面板总览：群名只留前 3 后 2",
            chat?["name"]?.GetValue<string>() == "群聊 123***89",
            chat?["name"]?.GetValue<string>() ?? "(这个聊天不在列表里)");
        Check("★ 面板总览：会话标题里的 QQ 号也被遮住",
            panelSession?["name"]?.GetValue<string>() == "会话 123***89",
            panelSession?["name"]?.GetValue<string>() ?? "(没有会话)");
        Check("★ 显示归显示：nameRaw 仍是真名（面板改名要用真名）",
            panelSession?["nameRaw"]?.GetValue<string>() == fakeName,
            panelSession?["nameRaw"]?.GetValue<string>() ?? "(没有 nameRaw)");

        // ── ③ 群里那条路：//sessions all 也不能露真名 ──
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//sessions all", 19001, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("123***89")), TimeSpan.FromSeconds(30));
        var maskedReply = Sent().LastOrDefault(t => t.Contains("群聊 ")) ?? string.Empty;
        Check("★ 群里 //sessions all：群名与标题都脱敏（长数字只留前 3 后 2）",
            maskedReply.Contains("群聊 123***89") && maskedReply.Contains("会话 123***89") && !maskedReply.Contains("123456789"),
            Snippet(maskedReply, "群聊 "));
        Check("★ //sessions all 带序号：聊天 1. / 会话 1)（对着 //use 序号不用数）",
            maskedReply.Contains("1. ") && maskedReply.Contains("1) "),
            Snippet(maskedReply, "1."));

        // ── ④ 面板关掉开关 → 立刻能看到真名（开关真的在生效，不是摆设）──
        var (offCode, _) = await PanelPostJsonAsync($"{panel}/api/settings", """{"enableAgentMask":false}""");
        Check("面板关掉脱敏成功", offCode == 200, $"HTTP {offCode}");
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//sessions all", 19002, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("123456789")), TimeSpan.FromSeconds(30));
        Check("★ 关掉开关后群里能看到真名（对照组，证明前面遮的是它）",
            Sent().LastOrDefault(t => t.Contains("会话 "))?.Contains("123456789") == true,
            Snippet(Sent().LastOrDefault(t => t.Contains("群聊 ")) ?? "(没有群聊那行)", "群聊 "));

        var (onCode, _) = await PanelPostJsonAsync($"{panel}/api/settings", """{"enableAgentMask":true}""");
        Check("再把脱敏打开成功", onCode == 200, $"HTTP {onCode}");

        // ── ⑤ 附加提示词随任务下发：服务器内置 agent 的系统提示词里能看到默认那份 ──
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//看看磁盘剩多少", 19011, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => agentAi.Requests.Count >= 1, TimeSpan.FromSeconds(40));
        var system1 = agentAi.Requests.Count > 0 ? SystemText(agentAi.Requests[0]) : string.Empty;
        Check("★ 附加提示词进了 agent 的系统提示词（默认 = 隐私红线）",
            system1.Contains("隐私红线") && system1.Contains("不要读取"),
            Snippet(system1, "隐私红线"));
        Check("★ 任务正文照旧是用户那句话（提示词是附加，不是替换）",
            agentAi.Requests.Count > 0 && UserTexts(agentAi.Requests[0]).Any(t => t.Contains("看看磁盘剩多少")),
            agentAi.Requests.Count == 0 ? "(没有请求)" : Snippet(UserTexts(agentAi.Requests[0]).LastOrDefault() ?? "(空)", "看看磁盘"));
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("默认提示词那轮看完了")), TimeSpan.FromSeconds(20));

        // ── ⑥ 面板改成自定义值 → 下一轮就用新的 ──
        const string customPrompt = "自定义提示词：只准看 /tmp，不许读聊天记录";
        var (customCode, _) = await PanelPostJsonAsync($"{panel}/api/settings",
            $$"""{"agentPrompt":"{{customPrompt}}"}""");
        Check("面板保存自定义附加提示词成功", customCode == 200, $"HTTP {customCode}");

        var requestsBefore = agentAi.Requests.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//再看一眼", 19012, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => agentAi.Requests.Count > requestsBefore, TimeSpan.FromSeconds(40));
        var system2 = agentAi.Requests.Count > requestsBefore ? SystemText(agentAi.Requests[^1]) : string.Empty;
        Check("★ 换成自定义提示词后，系统提示词立刻跟着变（不用重启）",
            system2.Contains(customPrompt) && !system2.Contains("隐私红线"),
            Snippet(system2, customPrompt));
        await WaitUntilAsync(() => Sent().Any(t => t.Contains("自定义提示词那轮看完了")), TimeSpan.FromSeconds(20));

        // ── ⑦ 清空 → 明确“不带”，不是回落到默认 ──
        var (clearCode, _) = await PanelPostJsonAsync($"{panel}/api/settings", """{"agentPrompt":""}""");
        Check("面板清空附加提示词成功", clearCode == 200, $"HTTP {clearCode}");

        requestsBefore = agentAi.Requests.Count;
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "//最后一眼", 19013, mentionBot: false, ct: cts.Token);
        await WaitUntilAsync(() => agentAi.Requests.Count > requestsBefore, TimeSpan.FromSeconds(40));
        var system3 = agentAi.Requests.Count > requestsBefore ? SystemText(agentAi.Requests[^1]) : string.Empty;
        Check("★ 清空 = 真的不带（既没有自定义那句，也没有默认那句）",
            !system3.Contains(customPrompt) && !system3.Contains("隐私红线"),
            Snippet(system3, "工作目录"));

        await bot.StopAsync();
    }

    private static async Task RunOfficialSecretBoundaryScenarioAsync()
    {
        Section("S36 R2 official secret: environment only, rejected writes, no value echoes");
        const string storedSecret = "SYN-STORED-r2-secret-29";
        const string fileSecret = "FIL-ONLY-r2-secret-84";
        const string envSecret = "ENV-ONLY-r2-secret-73";
        const string rotatedSecret = "ROT-ONLY-r2-secret-65";
        const string submittedSecret = "REQ-ONLY-r2-secret-91";
        var dataDir = NewDataDir("s36-official-secret");
        var dataPath = Path.Combine(dataDir, "data");
        Directory.CreateDirectory(dataPath);
        var databasePath = Path.Combine(dataPath, "qqchat.db");
        var secretFile = Path.Combine(dataDir, "official-secret-fixture.txt");
        File.WriteAllText(secretFile, fileSecret);
        File.WriteAllText(Path.Combine(dataPath, "settings.json"), """
            {"AiDesire":17,"OfficialEnabled":true,"OfficialAppId":"10001",
             "OfficialAppSecret":"JSN-IGNORED-r2-secret-48","IdleFallbackSeconds":0,
             "EnableStickers":false,"EnablePoke":false,"EnableVoice":false,"EnableMusic":false}
            """);
        // This minimal legacy row is created only in the new synthetic root, never a real database.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE secrets(name TEXT PRIMARY KEY, value TEXT NOT NULL, updated_unix INTEGER NOT NULL); "
                + "INSERT INTO secrets VALUES('officialAppSecret', $secret, 0)";
            command.Parameters.AddWithValue("$secret", storedSecret);
            command.ExecuteNonQuery();
        }
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        (string Settings, string Secret, long Audits) StoredState()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            string Text(string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar()?.ToString() ?? "";
            }
            return (Hash(Text("SELECT json FROM settings WHERE id=1")),
                Hash(Text("SELECT value FROM secrets WHERE name='officialAppSecret'")),
                long.Parse(Text("SELECT COUNT(*) FROM security_audit_log")));
        }
        var panelPort = FreePort(18336);
        var wsPort = FreePort(13336);
        var panel = $"http://127.0.0.1:{panelPort}";
        const string panelToken = "synthetic-r2-panel-token";
        using var model = new MockOpenAi(FreePort(17936));
        model.Start();
        using var official = new MockOfficialGateway(FreePort(18436), FreePort(13436));
        official.Start();
        // Legacy saved behavior can override environment seeds: pin protocol endpoints in the fixture too.
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(dataPath, "settings.json")))!;
        fixture["OfficialApiBase"] = official.HttpBase;
        fixture["OfficialTokenUrl"] = official.TokenUrl;
        File.WriteAllText(Path.Combine(dataPath, "settings.json"), fixture.ToJsonString());
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        void NoSecretEcho(string label, string body)
        {
            var forbidden = new[] { storedSecret, fileSecret, envSecret, rotatedSecret, submittedSecret,
                "JSN-IGNORED-r2-secret-48", "SYN***29", "FIL***84", "ENV***73", "ROT***65", "REQ***91" };
            Check(label + ": no secret value, fragments or masked field", forbidden.All(s => !body.Contains(s, StringComparison.Ordinal))
                && !body.Contains("officialSecretMasked", StringComparison.OrdinalIgnoreCase)
                && !body.Contains("officialAppSecret", StringComparison.OrdinalIgnoreCase)
                && (JsonNode.Parse(body)?["runtime"] as JsonObject)?.All(field =>
                    !field.Key.Contains("official", StringComparison.OrdinalIgnoreCase)
                    || !field.Key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    || field.Key is "officialSecretConfigured" or "officialSecretSource") != false,
                "safe response shape only");
        }
        try
        {
            var phases = new[]
            {
                (Name: "stored ignored", Secret: "", File: false),
                (Name: "file ignored", Secret: "", File: true),
                (Name: "blank env ignored", Secret: "   ", File: true),
                (Name: "env restored", Secret: " " + envSecret + " ", File: true),
                (Name: "env rotated", Secret: rotatedSecret, File: true),
                (Name: "env removed", Secret: "", File: false)
            };
            foreach (var phase in phases)
            {
                var expectedSecret = phase.Secret.Trim();
                var requestsBefore = official.HttpRequests.Count;
                using var bot = StartBot(new Dictionary<string, string>
                {
                    ["QQCHAT_DATA_DIR"] = dataDir, ["BOTAGENT_DATA_DIR"] = dataDir,
                    ["QQCHAT_API_KEY"] = "sk-mock", ["QQCHAT_BASE_URL"] = model.BaseUrl, ["QQCHAT_MODEL"] = "mock-model",
                    ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket", ["QQCHAT_ONEBOT_URL"] = $"http://127.0.0.1:{wsPort}",
                    ["QQCHAT_UIN"] = "10001", ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(), ["QQCHAT_HEALTH_BIND"] = "127.0.0.1",
                    ["QQCHAT_PANEL_TOKEN"] = panelToken, ["QQCHAT_LOG_FILE"] = "0",
                    ["QQCHAT_OFFICIAL_APP_SECRET"] = phase.Secret,
                    ["QQCHAT_OFFICIAL_APP_SECRET_FILE"] = phase.File ? secretFile : "",
                    ["QQCHAT_OFFICIAL_API_BASE"] = official.HttpBase, ["QQCHAT_OFFICIAL_TOKEN_URL"] = official.TokenUrl
                });
                await WaitForPortAsync(wsPort, cts.Token, bot);
                await WaitForPortAsync(panelPort, cts.Token, bot);
                var (code, body) = await PanelGetAsync(panel + "/api/settings", panelToken);
                var runtime = JsonNode.Parse(body)?["runtime"];
                Check(phase.Name + ": environment-only status", code == 200
                    && runtime?["officialSecretConfigured"]?.GetValue<bool>() == (expectedSecret.Length > 0)
                    && runtime?["officialSecretSource"]?.GetValue<string>() == (expectedSecret.Length > 0 ? "env" : "none"),
                    "configured boolean and source enum only");
                Check(phase.Name + ": only synthetic root", JsonNode.Parse(body)?["env"]?["dataDir"]?.GetValue<string>() == dataDir,
                    "temp root equality only");
                NoSecretEcho(phase.Name + " GET", body);
                if (expectedSecret.Length > 0)
                    await WaitUntilAsync(() => official.HttpRequests.Skip(requestsBefore)
                        .Any(r => r["path"]?.GetValue<string>() == "/app/getAppAccessToken"), TimeSpan.FromSeconds(8));
                else
                    await Task.Delay(250, cts.Token);
                var tokenRequests = official.HttpRequests.Skip(requestsBefore)
                    .Where(r => r["path"]?.GetValue<string>() == "/app/getAppAccessToken").ToArray();
                Check(phase.Name + ": real adapter uses only the direct environment secret",
                    expectedSecret.Length == 0 ? tokenRequests.Length == 0 : tokenRequests.Length > 0
                        && tokenRequests.All(r => r["body"]?["clientSecret"]?.GetValue<string>() == expectedSecret),
                    "request count and secret equality only; no protocol payload output");
                Check(phase.Name + ": legacy row retained", StoredState().Secret == Hash(storedSecret), "stored hash equality only");
                if (phase.Name == "stored ignored")
                {
                    var (scriptCode, scriptBody) = await PanelGetAsync(panel + "/app.js", panelToken);
                    Check("real host serves the credential-free script", scriptCode == 200
                        && scriptBody.Contains("officialSecretStatus", StringComparison.Ordinal)
                        && !scriptBody.Contains("setOfficialAppSecret", StringComparison.Ordinal)
                        && !scriptBody.Contains("officialSecretMasked", StringComparison.Ordinal), "static asset shape only");
                    var (pageCode, pageBody) = await PanelGetAsync(panel + "/", panelToken);
                    Check("real host serves the status-only official secret control", pageCode == 200
                        && pageBody.Contains("id=\"officialSecretStatus\"", StringComparison.Ordinal)
                        && !pageBody.Contains("id=\"setOfficialAppSecret\"", StringComparison.Ordinal), "static control ids only");
                    var attempts = new[]
                    {
                        "{\"officialAppSecret\":\"" + submittedSecret + "\",\"aiDesire\":37}",
                        "{\"officialAppSecret\":\"\",\"aiDesire\":37}",
                        "{\"officialAppSecret\":null,\"aiDesire\":37}",
                        "{\"officialAppSecret\":{},\"aiDesire\":37}",
                        "{\"clearOfficialAppSecret\":true,\"aiDesire\":37}",
                        "{\"clearOfficialAppSecret\":false,\"aiDesire\":37}",
                        "{\"clearOfficialAppSecret\":null,\"aiDesire\":37}",
                        "{\"OfficialAppSecret\":\"" + submittedSecret + "\",\"aiDesire\":37}",
                        "{\"CLEAROFFICIALAPPSECRET\":true,\"aiDesire\":37}"
                    };
                    for (var i = 0; i < attempts.Length; i++)
                    {
                        var before = StoredState();
                        var (status, response) = await PanelPostJsonAsync(panel + "/api/settings", attempts[i], panelToken);
                        Check($"forbidden request {i}: rejected before side effects", status == 400
                            && JsonNode.Parse(response)?["error"]?.GetValue<string>() == "official_secret_environment_only", "HTTP status and safe error code only");
                        NoSecretEcho($"forbidden request {i}", response);
                        Check($"forbidden request {i}: no settings secret or audit write", StoredState() == before,
                            "stored hashes and audit count equality only");
                        var (_, readback) = await PanelGetAsync(panel + "/api/settings", panelToken);
                        Check($"forbidden request {i}: no partial runtime publication",
                            JsonNode.Parse(readback)?["runtime"]?["aiDesire"]?.GetValue<int>() == 17, "runtime value equality only");
                    }
                    var (saveCode, saveBody) = await PanelPostJsonAsync(panel + "/api/settings", "{\"aiDesire\":31}", panelToken);
                    Check("ordinary settings still save", saveCode == 200 && JsonNode.Parse(saveBody)?["runtime"]?["aiDesire"]?.GetValue<int>() == 31);
                    NoSecretEcho("ordinary POST", saveBody);
                }
                else
                    Check(phase.Name + ": ordinary save survived restart", runtime?["aiDesire"]?.GetValue<int>() == 31);
                if (expectedSecret.Length > 0)
                {
                    var (saveCode, saveBody) = await PanelPostJsonAsync(panel + "/api/settings", "{\"aiDesire\":31}", panelToken);
                    Check(phase.Name + ": ordinary save preserves environment-only credential state", saveCode == 200
                        && JsonNode.Parse(saveBody)?["runtime"]?["officialSecretConfigured"]?.GetValue<bool>() == true
                        && JsonNode.Parse(saveBody)?["runtime"]?["officialSecretSource"]?.GetValue<string>() == "env");
                    NoSecretEcho(phase.Name + " ordinary POST", saveBody);
                }
                await bot.StopAsync();
            }
        }
        catch (Exception ex)
        {
            Fail("s36 R2 synthetic secret boundary", "exceptionType=" + ex.GetType().Name + "; no payload or logs emitted");
        }
    }

    /// <summary>POST 一段 JSON（场景里只用来打面板的设置/会话接口）。</summary>
    private static async Task<(int Code, string Body)> PostJsonAsync(string url, string json, string? panelToken = null)
    {
        using var http = panelToken is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }
            : CreatePanelHttpClient(new Uri(url).Port, 15, panelToken);
        var res = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }
}

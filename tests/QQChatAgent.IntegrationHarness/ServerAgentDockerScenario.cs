using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S40 服务器 agent 的 **docker 权限**（号主 2026-09-18：“再加一个允许 agent 透过 docker
/// 进行服务器文件操作的权限（相关开关要体现在后台管理面板上）”）。
///
/// 两条锁：
///   ① 面板开关 <c>agentServerDocker</c>（默认关，高权限）—— 关着时工具表里没有 docker，
///      提示词里也不提 <c>/host/qqchat</c> 与 <c>docker run -v /:/host</c> 这些用法；
///   ② 就算号主在“工具”里手写了 docker，开关没开也不给用。
///
/// 这里不依赖机器上真有 docker（CI/开发机不同）：断言的是**我们自己的行为** ——
///   • 关着时：模型看不到 docker 工具、调用被拒（请求体里能看到“没开”）、日志里没有 docker 行；
///   • 打开后：提示词里出现 docker 用法、真去调了 `docker ps -a`（日志里有那一行）、
///     并能看到 <c>/host/qqchat</c> 这条挂载路径。
/// </summary>
public static partial class Program
{
    private static async Task RunServerAgentDockerScenarioAsync()
    {
        Section("S40 服务器 agent 的 docker 权限（默认关 / 面板开关 / 工具表与提示词同步）");

        const int openAiPort = 17871;
        const int agentAiPort = 17872;
        const int botWsPort = 13091;
        const int healthPort = 18141;
        const long groupId = 66800;
        const long ownerId = 20002;
        const string token = "tok-s40-secret";

        var dataDir = NewDataDir("s40");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var agentAi = new MockOpenAi(agentAiPort);
        agentAi.Start();

        // 关着开关那一轮：模型自己决定要用 docker（不被提示词引导，纯看它有没有权限）
        agentAi.AddRule("看看容器情况",
            """{"thought":"用 docker 看一下","tool":"docker","command":"ps -a"}""",
            """{"final":"看完了（这轮没权限就算了）"}""");

        // 开关打开那一轮
        agentAi.AddRule("这次真的看看容器",
            """{"thought":"用 docker 看一下","tool":"docker","command":"ps -a"}""",
            """{"final":"看完啦"}""");

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
            // 号主手写了 docker —— 但开关没开，照样不给用（两把锁）
            ["QQCHAT_AGENT_SERVER_TOOLS"] = "bash,read,docker",
            ["QQCHAT_AGENT_PROGRESS"] = "0"
            // QQCHAT_AGENT_SERVER_DOCKER 故意不设 = 默认关
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(healthPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var panel = $"http://127.0.0.1:{healthPort}";

        List<string> Sent() => protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        string RequestWith(string marker)
        {
            for (var i = agentAi.Requests.Count - 1; i >= 0; i--)
            {
                var text = agentAi.DescribeRequest(i);
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    return text;
                }
            }

            return string.Empty;
        }

        async Task SendAndWaitAsync(string text, long messageId)
        {
            var before = bot.OutputLines.Count(l => l.Contains("agent 完成") || l.Contains("agent 失败"));
            await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", text, messageId, mentionBot: false, ct: cts.Token);
            await WaitUntilAsync(
                () => bot.OutputLines.Count(l => l.Contains("agent 完成") || l.Contains("agent 失败")) > before,
                TimeSpan.FromSeconds(90));
            await Task.Delay(500);
        }

        int DockerToolLines() => bot.OutputLines.Count(l => l.Contains("[ServerAgent] docker "));

        // ── ① 默认关：工具表里没有 docker，调用被拒 ──
        await SendAndWaitAsync("//看看容器情况", 19001);

        var offRequest = RequestWith("看看容器情况");
        Check("★ 开关关着时，提示词里**不出现** docker 工具（模型压根看不到这个能力）",
            offRequest.Length > 0 && !offRequest.Contains("docker：跑一条 docker 命令"),
            offRequest.Length == 0 ? "(没找到那次请求)" : $"含 docker 工具描述={offRequest.Contains("docker：跑一条 docker 命令")}");
        Check("★ 关着时也不提 /host/qqchat 这条挂载路径（不诱导它去读服务器文件）",
            offRequest.Length > 0 && !offRequest.Contains("/host/qqchat"),
            offRequest.Length == 0 ? "(没找到那次请求)" : $"含 /host/qqchat={offRequest.Contains("/host/qqchat")}");
        Check("★★ 就算号主在“工具”里手写了 docker，开关没开也调不动（模型收到“没开”）",
            RequestWith("没开").Contains("没开") && DockerToolLines() == 0,
            $"docker 工具被执行的次数={DockerToolLines()}；请求里有没有“没开”={RequestWith("没开").Contains("没开")}");
        Check("★ 结论还是回了群（没有因为权限被拒就卡死）",
            Sent().Any(t => t.Contains("看完了")), string.Join(" | ", Sent().TakeLast(3)));

        // ── ② 面板打开开关 ──
        var (code, _) = await PostJsonAsync($"{panel}/api/settings", """{"agentServerDocker":true}""");
        Check("面板能打开「允许 agent 透过 docker 操作服务器」", code == 200, $"HTTP {code}");

        var (_, body) = await HttpGetAsync($"{panel}/api/settings");
        var runtime = (JsonNode.Parse(body) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("★ 面板回读里这个开关是开着的（保存真的生效，不是只写了个字段）",
            runtime["agentServerDocker"]?.GetValue<bool>() == true,
            runtime["agentServerDocker"]?.ToJsonString() ?? "(没有这个字段)");

        await Task.Delay(400);
        await SendAndWaitAsync("//这次真的看看容器", 19002);

        var onRequest = RequestWith("这次真的看看容器");
        Check("★★ 打开后提示词里出现了 docker 的用法（含透过 docker 读整个文件系统的姿势）",
            onRequest.Length > 0 && onRequest.Contains("docker：跑一条 docker 命令") && onRequest.Contains("-v /:/host"),
            onRequest.Length == 0 ? "(没找到那次请求)" : $"含工具描述={onRequest.Contains("docker：跑一条 docker 命令")} 含 -v /:/host={onRequest.Contains("-v /:/host")}");
        Check("★★ 打开后 agent 真的去调 docker 了（我们自己的日志里有那一行，带上了它要跑的命令）",
            DockerToolLines() >= 1 && bot.OutputLines.Any(l => l.Contains("[ServerAgent] docker ") && l.Contains("ps -a")),
            string.Join(" ｜ ", bot.OutputLines.Where(l => l.Contains("[ServerAgent] docker ")).TakeLast(2)));
        Check("★ 结论照旧回群", Sent().Any(t => t.Contains("看完啦")), string.Join(" | ", Sent().TakeLast(3)));
    }
}

using BotAgent.IntegrationHarness;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S63 白名单在运行时热生效（#111）：面板改名单后不重启主机，下一条消息就按新名单判。
///
/// 这份场景钉住的是解析与生效口径，不是面板 UI：
///   ① 老的单名单（QQCHAT_WHITELIST）在没有新字段时仍是回落来源；
///   ② 面板写入新的群聊名单后，群聊判定立刻换名单（旧单名单不再兜底）；
///   ③ 名单里重复/带空白/中文逗号混写时不重复处理、不误拦；
///   ④ 通配 * 全部放行；
///   ⑤ 把群聊名单清空 ⇒ 回落到旧单名单（“清空”是“恢复回落”，不是“全拦”）；
///   ⑥ 群聊与私聊两份名单各管各的，改一份不影响另一份。
/// 全部为合成设置与合成端点，不接真实模型。
/// </summary>
public static partial class Program
{
    private static async Task RunWhitelistHotReloadScenarioAsync()
    {
        Section("S63 白名单热生效（面板改名单不重启；通配/重复/清空回落/群私分开）");

        var openAiPort = FreePort(17870);
        var botWsPort = FreePort(13080);
        var panelPort = FreePort(18130);
        var dataDir = NewDataDir("s63");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock-s63",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            // 只有老的单名单：没有新字段时它是回落来源。
            ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
        });
        var panel = $"http://127.0.0.1:{panelPort}";
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // 一次群消息 = 一次模型请求；用请求数判定“收没收”，比等回复稳。
        async Task<bool> GroupAnsweredAsync(long groupId, long messageId)
        {
            var before = openAi.Requests.Count;
            await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "@机器人 你好", messageId, mentionBot: true, ct: cts.Token);
            return await WaitUntilAsync(() => openAi.Requests.Count > before, TimeSpan.FromSeconds(20));
        }

        async Task<bool> GroupIgnoredAsync(long groupId, long messageId)
        {
            var before = openAi.Requests.Count;
            await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "@机器人 你好", messageId, mentionBot: true, ct: cts.Token);
            await Task.Delay(2500);
            return openAi.Requests.Count == before;
        }

        async Task<bool> PrivateAnsweredAsync(long userId, long messageId)
        {
            var before = openAi.Requests.Count;
            await protocol.SendPrivateMessageAsync(userId, "老王", "你好", messageId, cts.Token);
            return await WaitUntilAsync(() => openAi.Requests.Count > before, TimeSpan.FromSeconds(20));
        }

        async Task<bool> PrivateIgnoredAsync(long userId, long messageId)
        {
            var before = openAi.Requests.Count;
            await protocol.SendPrivateMessageAsync(userId, "老王", "你好", messageId, cts.Token);
            await Task.Delay(2500);
            return openAi.Requests.Count == before;
        }

        async Task<bool> SaveWhitelistAsync(string json)
        {
            var (code, body) = await PanelPostJsonAsync($"{panel}/api/settings", json);
            Check($"面板保存白名单 {json} 成功（HTTP {code}）", code == 200, Truncate(body, 240));
            return code == 200;
        }

        // ① 基线：新字段为空 ⇒ 回落到老的单名单。
        Check("★ 新字段为空时回落到老单名单：名单内的群正常回复", await GroupAnsweredAsync(99999, 7301));
        Check("★ 新字段为空时老单名单仍然生效：名单外的群被忽略", await GroupIgnoredAsync(88888, 7302));

        // ② 面板写入群聊名单：立刻生效，且不再回落到老名单。
        await SaveWhitelistAsync("""{"whitelistGroups":"88888"}""");
        Check("★★ 面板改名单后不重启即生效：新名单内的群正常回复", await GroupAnsweredAsync(88888, 7303));
        Check("★★ 新名单覆盖老单名单：老的 99999 不再放行", await GroupIgnoredAsync(99999, 7304));

        // ③ 重复/空白/中文逗号混写：不重复处理，也不误拦。
        await SaveWhitelistAsync("""{"whitelistGroups":"  88888 , 88888，88888 "}""");
        var dupBefore = openAi.Requests.Count;
        await protocol.SendGroupMessageAsync(88888, 20002, "老王", "@机器人 你好", 7305, mentionBot: true, ct: cts.Token);
        Check("★ 重复 id 不重复处理：一条消息只产生一次模型请求",
            await WaitUntilAsync(() => openAi.Requests.Count > dupBefore, TimeSpan.FromSeconds(20)),
            "重复写法把同一消息处理了两遍");

        // ④ 通配：两种 id 都放行。
        await SaveWhitelistAsync("""{"whitelistGroups":"*"}""");
        Check("★ 群聊名单为 * 时任意群都放行（88888）", await GroupAnsweredAsync(88888, 7306));
        Check("★ 群聊名单为 * 时任意群都放行（99999）", await GroupAnsweredAsync(99999, 7307));

        // ⑤ 清空群聊名单 ⇒ 回落老单名单（“清空”不等于“全拦”）。
        await SaveWhitelistAsync("""{"whitelistGroups":""}""");
        Check("★★ 清空群聊名单后回落到老单名单：99999 正常回复", await GroupAnsweredAsync(99999, 7308));
        Check("★★ 清空群聊名单后回落到老单名单：88888 被忽略", await GroupIgnoredAsync(88888, 7309));

        // ⑥ 群聊与私聊两份名单各管各的：改群名单不动私聊判定。
        await SaveWhitelistAsync("""{"whitelistGroups":"88888","whitelistPrivates":"77777"}""");
        Check("★ 私聊名单里的好友正常回复", await PrivateAnsweredAsync(77777, 7310));
        Check("★ 群聊名单里的群号不会把同号私聊放行", await PrivateIgnoredAsync(88888, 7311));
        Check("★ 私聊名单不影响群聊判定", await GroupAnsweredAsync(88888, 7312));
    }
}

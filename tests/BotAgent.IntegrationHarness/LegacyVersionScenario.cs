using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S62 旧面板保存的版本校验（#132 S1）。
///
/// 旧入口 POST /api/settings 一直走 Legacy 档：配置库对 Legacy 完全不做版本校验（ExpectedVersion 固定 0），
/// 两个客户端并发保存时后写的静默覆盖前一个。本场景钉住三件事：
///   ① 请求带上自己读到的 expectedVersion 且匹配时正常提交，回执标 verified；
///   ② 同一个版本再保存一次（等价于两个客户端带同一版本并发）必须拿 409 而不是静默覆盖；
///   ③ 不带 expectedVersion 的旧调用仍然按原行为提交，但回执显式标 legacy_unverified，让「没有并发保护」可见。
/// 全部为合成设置与合成端点，不接真实模型。
/// </summary>
public static partial class Program
{
    private static async Task RunLegacyVersionScenarioAsync()
    {
        Section("S62 旧面板保存可带版本（匹配即提交 / 过期即 409 / 不带版本标 legacy_unverified）");

        var botWsPort = FreePort(13079);
        var panelPort = FreePort(18129);
        var dataDir = NewDataDir("s62");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock-legacy",
            ["QQCHAT_BASE_URL"] = "http://127.0.0.1:1/v1",
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "66702",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
        });
        var panel = $"http://127.0.0.1:{panelPort}";
        await WaitForPortAsync(panelPort, cts.Token, bot);

        // ① 不带版本：旧面板的正常保存路径不变，但回执必须标明「这次没有并发保护」。
        var (unversionedCode, unversionedBody) = await PanelPostJsonAsync($"{panel}/api/settings",
            """{"groupCooldownSeconds":5}""");
        var unversioned = JsonNode.Parse(unversionedBody)?.AsObject() ?? new JsonObject();
        var firstVersion = unversioned["configurationResult"]?["currentVersion"]?.GetValue<string>();
        Check("不带版本的旧面板保存仍然成功", unversionedCode == 200 && firstVersion is not null,
            $"HTTP {unversionedCode} version={firstVersion}");
        Check("不带版本的回执显式标 legacy_unverified",
            (string?)unversioned["legacyVerification"] == "legacy_unverified",
            $"legacyVerification={unversioned["legacyVerification"]}");

        // ② 带匹配版本：与 ZBA 的 Strict 档同口径，正常提交并标 verified。
        var pinnedRequest = new JsonObject { ["groupCooldownSeconds"] = 6, ["expectedVersion"] = firstVersion };
        var (pinnedCode, pinnedBody) = await PanelPostJsonAsync($"{panel}/api/settings",
            pinnedRequest.ToJsonString());
        var pinned = JsonNode.Parse(pinnedBody)?.AsObject() ?? new JsonObject();
        var pinnedVersion = pinned["configurationResult"]?["currentVersion"]?.GetValue<string>();
        Check("带匹配版本的保存成功并标 verified",
            pinnedCode == 200 && (string?)pinned["legacyVerification"] == "verified"
            && pinnedVersion is not null && pinnedVersion != firstVersion,
            $"HTTP {pinnedCode} version={pinnedVersion} legacyVerification={pinned["legacyVerification"]}");

        // ③ 复用旧版本（等价于两个客户端带同一版本并发）：必须 409，且不能覆盖已经落库的值。
        var staleRequest = new JsonObject { ["groupCooldownSeconds"] = 7, ["expectedVersion"] = firstVersion };
        var (staleCode, staleBody) = await PanelPostJsonAsync($"{panel}/api/settings",
            staleRequest.ToJsonString());
        var stale = JsonNode.Parse(staleBody)?.AsObject() ?? new JsonObject();
        Check("带过期版本的保存返回 409 冲突", staleCode == 409 && (int?)stale["code"] == 3,
            $"HTTP {staleCode} body={staleBody}");
        var (_, afterBody) = await PanelGetAsync($"{panel}/api/settings");
        var afterRuntime = JsonNode.Parse(afterBody)?["runtime"];
        Check("冲突的保存没有覆盖已落库的值", (int?)afterRuntime?["groupCooldownSeconds"] == 6,
            $"groupCooldownSeconds={afterRuntime?["groupCooldownSeconds"]}");

        // ④ 版本字段本身不合法时明确 400，而不是当成「没带版本」静默放行。
        var invalidRequest = new JsonObject { ["groupCooldownSeconds"] = 8, ["expectedVersion"] = "abc" };
        var (invalidCode, invalidBody) = await PanelPostJsonAsync($"{panel}/api/settings",
            invalidRequest.ToJsonString());
        Check("不合法的 expectedVersion 返回 400", invalidCode == 400
            && (string?)JsonNode.Parse(invalidBody)?["error"] == "invalid_expected_version",
            $"HTTP {invalidCode} body={invalidBody}");
    }
}

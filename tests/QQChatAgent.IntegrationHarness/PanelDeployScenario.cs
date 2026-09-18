using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S41 面板**一键部署**的 API 面（号主 2026-09-18：“面板有没有提供一键部署”）。
///
/// 这套东西真跑起来会替换容器（测试环境跑不了、也不该跑），所以这里只钉**门槛与校验**——
/// 恰恰是出错会伤到生产的那几处：
///   • 开关默认关：关着时上传/地址/回滚全都要 403（不会因为一个接口没加鉴权就能重装机器人）；
///   • 产物必须是 gzip（不是 gzip 就拒，别拿去 build）；
///   • 地址只收 http(s)（别让面板变成任意协议客户端）；
///   • 没有回滚点时明确拒绝，而不是默默做一件别的事；
///   • GET /api/deploy 能读出开关/当前产物/镜像信息（面板卡片靠它）。
/// </summary>
public static partial class Program
{
    private static async Task RunPanelDeployScenarioAsync()
    {
        Section("S41 面板一键部署（门槛 / 产物校验 / 地址校验 / 回滚点）");

        const int openAiPort = 17881;
        const int botWsPort = 13101;
        const int healthPort = 18151;
        const long groupId = 66810;

        var dataDir = NewDataDir("s41");

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
            ["QQCHAT_HEALTH_PORT"] = healthPort.ToString()
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(healthPort, cts.Token, bot);

        var panel = $"http://127.0.0.1:{healthPort}";

        async Task<(int Code, string Body)> PostRawAsync(string url, byte[] body, string contentType = "application/gzip")
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            using var resp = await http.PostAsync(url, content, cts.Token);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(cts.Token));
        }

        // ── ① 默认关：三个动作全拒 ──
        var (getCode, getBody) = await HttpGetAsync($"{panel}/api/deploy");
        Check("GET /api/deploy 能读（面板卡片靠它；开关默认关）",
            getCode == 200 && getBody.Contains("\"enabled\":false"),
            $"HTTP {getCode} {getBody[..Math.Min(120, getBody.Length)]}");

        var (upCode, upBody) = await PostRawAsync($"{panel}/api/deploy/upload", Encoding.UTF8.GetBytes("not-a-tarball"));
        Check("★★ 关着时上传被拒（403，不落盘、不 build）",
            upCode == 403, $"HTTP {upCode} {upBody[..Math.Min(120, upBody.Length)]}");

        var (urlOffCode, urlOffBody) = await PostJsonAsync($"{panel}/api/deploy/url", """{"url":"https://example.com/app.tar.gz"}""");
        Check("★★ 关着时“从地址部署”也被拒（403）",
            urlOffCode == 403, $"HTTP {urlOffCode} {urlOffBody[..Math.Min(120, urlOffBody.Length)]}");

        var (rbOffCode, rbOffBody) = await PostJsonAsync($"{panel}/api/deploy/rollback", "{}");
        Check("★★ 关着时回滚也被拒（403）",
            rbOffCode == 403, $"HTTP {rbOffCode} {rbOffBody[..Math.Min(120, rbOffBody.Length)]}");

        // ── ② 打开开关 ──
        var (setCode, _) = await PostJsonAsync($"{panel}/api/settings", """{"panelDeployEnabled":true}""");
        Check("面板能打开「面板一键部署」开关", setCode == 200, $"HTTP {setCode}");
        var (_, afterBody) = await HttpGetAsync($"{panel}/api/settings");
        var runtime = (JsonNode.Parse(afterBody) as JsonObject)?["runtime"] as JsonObject ?? new JsonObject();
        Check("★ 开关回读是开的（保存真生效）",
            runtime["panelDeployEnabled"]?.GetValue<bool>() == true,
            runtime["panelDeployEnabled"]?.ToJsonString() ?? "(没有这个字段)");

        // ── ③ 产物校验：不是 gzip 就拒（这条能救命：拿错文件去 build 会造出一个起不来的镜像）──
        var (badCode, badBody) = await PostRawAsync($"{panel}/api/deploy/upload", Encoding.UTF8.GetBytes("this is not a gzip file, just text"));
        Check("★★ 非 gzip 的产物被拒（不会拿去 build）",
            badCode == 400 && badBody.Contains("gzip"),
            $"HTTP {badCode} {badBody[..Math.Min(160, badBody.Length)]}");

        // ── ④ 地址校验：只收 http(s) ──
        var (ftpCode, ftpBody) = await PostJsonAsync($"{panel}/api/deploy/url", """{"url":"ftp://example.com/app.tar.gz"}""");
        Check("★★ 非 http(s) 地址被拒（别让面板变成任意协议客户端）",
            ftpCode == 400 && ftpBody.Contains("http"),
            $"HTTP {ftpCode} {ftpBody[..Math.Min(160, ftpBody.Length)]}");

        // ── ⑤ 回滚点：测试环境没有 docker/prev，必须明确拒绝而不是瞎做 ──
        var (rbCode, rbBody) = await PostJsonAsync($"{panel}/api/deploy/rollback", "{}");
        Check("★★ 没有回滚点（qqchat-agent:prev）时明确拒绝",
            rbCode == 400 && rbBody.Contains("回滚"),
            $"HTTP {rbCode} {rbBody[..Math.Min(200, rbBody.Length)]}");

        var (healthCode, _) = await HttpGetAsync($"{panel}/healthz");
        Check("★ 全程没有因为部署接口把机器人搞崩（还能正常应答）",
            healthCode == 200, $"healthz HTTP {healthCode}");
    }
}

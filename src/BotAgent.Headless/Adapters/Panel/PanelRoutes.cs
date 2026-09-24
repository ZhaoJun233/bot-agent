using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Adapters.Panel;

/// <summary>路由怎么匹配。</summary>
internal enum PanelMatch
{
    /// <summary>整条路径完全相同（大小写不敏感）。</summary>
    Exact,

    /// <summary>整条路径相同，但比较前先去掉尾部的 '/'（静态资源那几条，见 <c>RouteAsync</c> 原实现）。</summary>
    ExactFile,

    /// <summary>路径以它开头；去掉前缀的剩余部分给处理函数（子资源用）。</summary>
    Prefix,
}

/// <summary>一次请求（表驱动分派后交给处理函数的东西）。</summary>
/// <param name="Context">底层上下文（读写都在它上面）。</param>
/// <param name="Path">原始路径。</param>
/// <param name="Method">大写的 HTTP 方法。</param>
/// <param name="Rest">Prefix 命中时"去掉前缀"的剩余部分；否则为空串。</param>
internal sealed record PanelRequest(HttpListenerContext Context, string Path, string Method, string Rest);

/// <summary>一条路由：方法 + 匹配方式 + 路径 + 处理函数。</summary>
internal readonly record struct PanelRoute(string Method, PanelMatch Match, string Path, Func<PanelRequest, Task> Handler)
{
    /// <summary>命中？<paramref name="rest" /> 只在 <see cref="PanelMatch.Prefix" /> 时有意义。</summary>
    public bool Matches(string method, string path, out string rest)
    {
        rest = string.Empty;

        // "*" = 任何方法（与改造前一致：原来那批 handler 自己也校验方法）
        if (Method != "*" && !string.Equals(Method, method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        switch (Match)
        {
            case PanelMatch.Exact:
                return string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);

            case PanelMatch.ExactFile:
                return string.Equals(Path, path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

            default:
                if (!path.StartsWith(Path, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                rest = path[Path.Length..];
                return true;
        }
    }
}

/// <summary>
/// 面板路由表（§5.2：把原来那条 379 行的 if/else 链换成表）。
///
/// 三条纪律：
///   ① **顺序即优先级**：先精确、再前缀；表里怎么排就怎么匹配（不再靠"我记得把这条写在前面"）；
///   ② **方法为 "*" 的条目自己要校验方法**（与改造前逐字一致：那批 handler 内部就是 <c>if (method == "POST")</c>）；
///   ③ **路径大小写不敏感**（与改造前一致 —— 原实现每条都是 <c>OrdinalIgnoreCase</c>）。
/// </summary>
public sealed partial class WebUiServer
{
    private IReadOnlyList<PanelRoute>? _routes;

    private IReadOnlyList<PanelRoute> Routes => _routes ??= BuildRoutes();

    private IReadOnlyList<PanelRoute> BuildRoutes() => new PanelRoute[]
    {
        // ─────────── 静态资源 ───────────
        new("*", PanelMatch.ExactFile, "", (r) => WriteAssetAsync(r.Context, "index.html", "text/html; charset=utf-8")),
        new("*", PanelMatch.ExactFile, "/app.css", (r) => WriteAssetAsync(r.Context, "app.css", "text/css; charset=utf-8")),
        new("*", PanelMatch.ExactFile, "/app.js", (r) => WriteAssetAsync(r.Context, "app.js", "application/javascript; charset=utf-8")),
        new("*", PanelMatch.ExactFile, "/favicon.ico", (r) => WriteBytesAsync(r.Context, 204, "image/x-icon", Array.Empty<byte>())),

        // ─────────── 健康检查 ───────────
        new("GET", PanelMatch.Exact, "/healthz", (r) => WriteJsonAsync(r.Context, 200, new JsonObject { ["status"] = "ok" })),
        new("GET", PanelMatch.Exact, "/readyz", (r) =>
        {
            var ready = _gateway.IsConnected;
            return WriteJsonAsync(r.Context, ready ? 200 : 503, new JsonObject
            {
                ["ready"] = ready,
                ["onebot"] = _gateway.IsConnected ? "connected" : "disconnected"
            });
        }),
        new("GET", PanelMatch.Exact, "/status", (r) => WriteJsonAsync(r.Context, 200, BuildStatus())),

        // ─────────── QQ 登录二维码（面板内扫码） ───────────
        // 为什么放在这里而不是让前端直接连 NapCat WebUI：
        //   浏览器直连 NapCat 需要另一道 Basic 认证 + 跨域，而容器网络里只有本进程能到 napcat:6099。
        new("GET", PanelMatch.Exact, "/api/qqlogin", HandleQqLoginAsync),
        new("GET", PanelMatch.Exact, "/api/qqlogin/qrcode.svg", HandleQqLoginSvgAsync),

        // ─────────── 首屏与日志 ───────────
        new("GET", PanelMatch.Exact, "/api/state", (r) => WriteJsonAsync(r.Context, 200, BuildState())),
        new("GET", PanelMatch.Exact, "/api/logs", HandleLogsAsync),

        // ─────────── 一键部署 / 重启 ───────────
        new("*", PanelMatch.Prefix, "/api/deploy", (r) => HandlePanelDeployAsync(r.Context, r.Path, r.Method)),
        new("*", PanelMatch.Exact, "/api/restart", (r) => HandleRestartAsync(r.Context, r.Method)),

        // ─────────── 设置（GET 读 / POST 存） ───────────
        new("*", PanelMatch.Exact, "/api/settings", (r) => r.Method == "POST"
            ? HandleSettingsSaveAsync(r.Context)
            : WriteJsonAsync(r.Context, 200, BuildSettingsPayload())),

        // ─────────── 服务器健康日报 ───────────
        new("*", PanelMatch.Exact, "/api/health-report", (r) => r.Method == "POST"
            ? HandleHealthReportAsync(r.Context)
            : WriteJsonAsync(r.Context, 200, BuildHealthReportPayload())),

        // ─────────── 本机 Agent 桥（handoff-4 §31） ───────────
        // 桥是本机那个进程主动连过来的（本机在 NAT 后面，只能它出站）；这里把 WS 接住。
        // 注意：**这个端口能让人在号主电脑上执行命令** —— 所以：没配令牌就直接拒绝。
        new("*", PanelMatch.Exact, "/agent-bridge", (r) => HandleAgentBridgeAsync(r.Context)),
        new("GET", PanelMatch.Exact, "/api/agent/status", (r) => WriteJsonAsync(r.Context, 200, BuildAgentStatusPayload())),
        new("POST", PanelMatch.Exact, "/api/agent/test", (r) => HandleAgentTestAsync(r.Context)),
        new("*", PanelMatch.Exact, "/api/agent/models", (r) => HandleAgentModelsAsync(r.Context, r.Method)),
        new("GET", PanelMatch.Exact, "/api/agent/setup", (r) => HandleAgentSetupAsync(r.Context)),
        new("*", PanelMatch.Exact, "/api/agent/disconnect", HandleAgentDisconnectAsync),
        new("*", PanelMatch.Exact, "/api/agent/sessions", (r) => HandleAgentSessionsAsync(r.Context, r.Method)),
        new("GET", PanelMatch.Exact, "/api/agent/pi-sessions", HandlePiSessionsAsync),
        new("GET", PanelMatch.Exact, "/agent-bridge-script", (r) => WriteEmbeddedAgentScriptAsync(r.Context, "pi-bridge.py")),
        // 服务器文件操作脚本（sftp 包装；同样从仓库 tools/ 内嵌）—— 给外部 pi agent 用
        new("GET", PanelMatch.Exact, "/agent-sftp-script", (r) => WriteEmbeddedAgentScriptAsync(r.Context, "server-files.py")),

        // ─────────── 参与状态（工程 V3 · P1，**只读**）───────────
        // 为什么只读：状态机目前**只观测不拦截**，面板不该也不能改它 —— 改的是上面的参数。
        // 会话 key 走脱敏开关（显示层脱敏、内部 key 不变）；只返回结构化字段，不含任何正文。
        new("GET", PanelMatch.Exact, "/api/participation", HandleParticipationAsync),

        // ─────────── AI 总开关 ───────────
        new("POST", PanelMatch.Exact, "/api/ai-mode", HandleAiModeAsync),

        // ─────────── 会话 / 档案 / 归档 ───────────
        new("*", PanelMatch.Prefix, "/api/conversations/", (r) => HandleConversationAsync(r.Context, r.Rest, r.Method)),
        new("GET", PanelMatch.Prefix, "/api/profiles/", (r) => WriteJsonAsync(r.Context, 200, new JsonObject
        {
            ["uid"] = Uri.UnescapeDataString(r.Rest),
            // allScopes：面板要看**全部会话**的画像（含群聊），否则只能看到私聊记录；
            // 该视图不用于模型注入（模型注入始终按会话隔离），所以放宽这里不会串味。
            ["summary"] = _profiles.GetProfileSummary(Uri.UnescapeDataString(r.Rest), limit: 30, allScopes: true)
        })),
        new("GET", PanelMatch.Exact, "/api/archive", (r) =>
        {
            var key = r.Context.Request.QueryString["key"] ?? string.Empty;
            var limit = int.TryParse(r.Context.Request.QueryString["limit"], out var n) ? Math.Clamp(n, 1, 2000) : 200;
            return WriteJsonAsync(r.Context, 200, ReadArchive(key, limit));
        }),

        // ─────────── 网易云 / 音乐 / 搜索 ───────────
        new("*", PanelMatch.Prefix, "/api/netease/qr", (r) => HandleNeteaseQrAsync(r.Context, r.Path, r.Method)),
        // 一键验证"听音乐"链路（搜索 → 歌词 → 低码率音源 → 波形分析）：依赖外部 API，挂了只能在群里碰运气，
        // 这里可以直接跑一遍把实测结果贴出来（排障与上线验收都用得上）。
        new("*", PanelMatch.Exact, "/api/music/test", (r) => HandleMusicTestAsync(r.Context, r.Method)),
        // 一键验证"联网搜索"（模型自带搜索 or 搜索源），把结果原样贴出来
        new("*", PanelMatch.Exact, "/api/search/test", (r) => HandleSearchTestAsync(r.Context, r.Method)),

        // ─────────── 语音（TTS 自测 / 音色复刻） ───────────
        // 只合成、不发群 —— 想验证"群里真能听到"得开开关让模型发，或者看 /api/voice/health。
        new("*", PanelMatch.Exact, "/api/voice/test", (r) => HandleVoiceTestAsync(r.Context, r.Method)),
        new("GET", PanelMatch.Exact, "/api/voice/health", (r) => HandleVoiceHealthAsync(r.Context)),
        new("GET", PanelMatch.Exact, "/api/voice/clones", (r) => HandleVoiceClonesAsync(r.Context)),
        new("*", PanelMatch.Exact, "/api/voice/clone/delete", (r) => HandleVoiceCloneDeleteAsync(r.Context, r.Method)),
        new("*", PanelMatch.Exact, "/api/voice/clone", (r) => HandleVoiceCloneAsync(r.Context, r.Method)),

        // ─────────── 表情包库（列表 / 取图 / 删除 / 立即巡检 / 导入） ───────────
        new("*", PanelMatch.Exact, "/api/stickers", (r) => HandleStickersAsync(r.Context, r.Path, r.Method)),
        new("*", PanelMatch.Prefix, "/api/stickers/", (r) => HandleStickersAsync(r.Context, r.Path, r.Method)),
    };

    /// <summary>按表分派；一条都不命中就是 404（与改造前同一句）。</summary>
    private async Task RouteAsync(HttpListenerContext context, string path, string method)
    {
        foreach (var route in Routes)
        {
            if (!route.Matches(method, path, out var rest))
            {
                continue;
            }

            await route.Handler(new PanelRequest(context, path, method, rest));
            return;
        }

        await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "not found", ["path"] = path });
    }
}

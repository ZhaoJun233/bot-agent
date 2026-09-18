using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QQChatAgent.Models;
using QQChatAgent.Services;
using QQChatAgent.Services.Agent;
using QQChatAgent.Services.NapCat;
using QQChatAgent.Services.OneBot;
using QQChatAgent.Services.Ops;

namespace QQChatAgent.Configuration;

/// <summary>
/// Web 面板 + 健康检查服务（复用 in-box HttpListener，不引入 ASP.NET，保持镜像体积）。
///
///   面板      GET  /                       WinUI 界面的 Web 复刻
///   静态      GET  /app.css  /app.js
///   健康      GET  /healthz               存活（docker HEALTHCHECK）
///             GET  /readyz                就绪（已连协议端）
///             GET  /status                状态 JSON（兼容原接口）
///   API       GET  /api/state             面板首屏快照
///             GET  /api/logs              最近的运行日志（面板刷新后也能看到历史，不再“一刷新就空”）
///             GET  /api/conversations/{key}/messages
///             POST /api/conversations/{key}/send|read|delete
///             GET  /api/profiles/{uid}
///             GET  /api/settings
///             POST /api/settings
///             POST /api/ai-mode
///   实时      GET  /api/events            SSE：新消息/未读/思考中/日志/状态
///   QQ 登录   GET  /api/qqlogin           当前登录二维码信息（URL / 剩余新鲜度 / 错误）
///             GET  /api/qqlogin/qrcode.svg 二维码图片（&lt;img&gt; 不能带自定义头，所以支持 ?token=）
/// </summary>
public sealed partial class WebUiServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly int _port;
    private readonly string _bind;
    private readonly AppSettings _settings;
    private readonly OneBotGateway _gateway;
    private readonly BotAgent _agent;
    private readonly LoginQrService _loginQr;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _clientsGate = new();
    private readonly List<SseClient> _clients = new();

    private HttpListener? _listener;
    private Task? _loop;
    private int _convBroadcastPending;
    private long _convBroadcastVersion;

    public WebUiServer(int port, AppSettings settings, OneBotGateway gateway, BotAgent agent, LoginQrService loginQr,
        AgentBridgeServer? agentBridge = null, HealthReportService? healthReports = null)
    {
        _port = port;
        _settings = settings;
        _gateway = gateway;
        _agent = agent;
        _loginQr = loginQr;
        _agentBridge = agentBridge;
        _healthReports = healthReports;
        _bind = Environment.GetEnvironmentVariable("QQCHAT_HEALTH_BIND")?.Trim() is { Length: > 0 } custom
            ? custom
            : "+";
    }

    /// <summary>本机 Agent 桥（没启用时为 null）。</summary>
    private readonly AgentBridgeServer? _agentBridge;

    /// <summary>服务器健康日报（号主 2026-09-18：定时私聊推送；不经过外部设备 agent）。</summary>
    private readonly HealthReportService? _healthReports;

    /// <summary>实际监听的前缀（启动失败为 null）。</summary>
    public string? ListeningOn { get; private set; }

    public void Start()
    {
        // 订阅 Agent 事件 → 实时推送
        _agent.MessageAdded += OnMessageAdded;
        _agent.ConversationsChanged += OnConversationsChanged;
        _agent.StateChanged += OnStateChanged;
        _agent.ThinkingChanged += OnThinkingChanged;
        // 注意：**不再**订阅 _agent.LogLine —— 那一份日志已经由 FileLog.LineWritten 推了
        // （EmitLog → FileLog.Write）。两边都订就会让面板里每条 Agent 日志重两遍。
        // 面板日志页的数据源是 FileLog：它包含所有组件（Agent/OneBot/Voice/Store…）的行，
        // 而且整个进程只有这一个漏斗 —— 实时推流与历史回填用同一份文本，不会两套格式。
        FileLog.LineWritten += OnFileLogLine;

        var candidates = _bind == "+" && !OperatingSystem.IsWindows()
            ? new[] { "+" }
            : _bind == "+"
                ? new[] { "+", "127.0.0.1" }
                : new[] { _bind };

        foreach (var bind in candidates)
        {
            var prefix = $"http://{bind}:{_port}/";
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                _listener = listener;
                ListeningOn = prefix;
                _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
                FileLog.Write("Web", $"面板已监听 {prefix}（/ 、/healthz、/api/*）");
                return;
            }
            catch (Exception ex)
            {
                FileLog.Write("Web", $"绑定 {prefix} 失败：{ex.Message}");
            }
        }

        FileLog.Warn("Web",
            $"面板无法启动（端口 {_port}）。可设 QQCHAT_HEALTH_BIND=127.0.0.1，或 QQCHAT_HEALTH_PORT=0 关闭。");
    }

    // ══════════════ HTTP ══════════════

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => DispatchAsync(context));
        }
    }

    private async Task DispatchAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var method = context.Request.HttpMethod;

            // 鉴权：/healthz 与 /readyz 放行（容器 HEALTHCHECK 靠它们）
            if (!path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/readyz", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAuthorized(context))
                {
                    await WriteJsonAsync(context, 401, new JsonObject
                    {
                        ["error"] = "unauthorized",
                        ["hint"] = "本面板已设置 QQCHAT_PANEL_TOKEN，请在地址后加 ?token=你的令牌 打开"
                    });
                    context.Response.Close();
                    return;
                }

                // 防 CSRF：浏览器跨站发起的 POST 会带 Origin/Referer，非浏览器客户端（curl）不带。
                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && !IsSameOrigin(context))
                {
                    FileLog.Write("Web", $"已拒绝跨站 POST：{path}（Origin={context.Request.Headers["Origin"]}）");
                    await WriteJsonAsync(context, 403, new JsonObject { ["error"] = "cross-origin request rejected" });
                    context.Response.Close();
                    return;
                }
            }

            // SSE 是长连接，单独处理（不能走 finally 里的 Close）
            if (path.Equals("/api/events", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSseAsync(context, _cts.Token);
                return;
            }

            try
            {
                await RouteAsync(context, path, method);
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // 忽略
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write("Web", $"请求处理异常: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// 令牌校验。未配置 QQCHAT_PANEL_TOKEN 时直接放行（回环部署的常见情况）。
    /// 配置后：面板与 /api/* 必须带 X-Panel-Token 头或 ?token= 参数。
    /// </summary>
    private bool IsAuthorized(HttpListenerContext context)
    {
        var expected = _settings.PanelToken?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            return true;
        }

        var provided = context.Request.Headers["X-Panel-Token"] ?? context.Request.QueryString["token"];
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        // 定时安全比较：长度不等直接返回 false，不会抛异常
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// 同源校验（防 CSRF）。
    /// 浏览器发 POST 必定带 Origin；只有源自面板自身的 Origin 才放行。
    /// 无 Origin/Referer = 非浏览器客户端（curl、脚本），不适用 CSRF 场景，放行。
    /// </summary>
    private static bool IsSameOrigin(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
        {
            origin = context.Request.Headers["Referer"];
        }

        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var requestHost = context.Request.Url?.Host ?? string.Empty;
        if (string.Equals(uri.Host, requestHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // localhost 与 127.0.0.1 视为同一台机器的两种写法
        return uri.IsLoopback && requestHost is "localhost" or "127.0.0.1" or "::1";
    }

    private async Task RouteAsync(HttpListenerContext context, string path, string method)
    {
        // ---------- 静态资源 ----------
        switch (path.TrimEnd('/'))
        {
            case "":
                await WriteAssetAsync(context, "index.html", "text/html; charset=utf-8");
                return;
            case "/app.css":
                await WriteAssetAsync(context, "app.css", "text/css; charset=utf-8");
                return;
            case "/app.js":
                await WriteAssetAsync(context, "app.js", "application/javascript; charset=utf-8");
                return;
            case "/favicon.ico":
                await WriteBytesAsync(context, 204, "image/x-icon", Array.Empty<byte>());
                return;
        }

        // ---------- 健康检查 ----------
        switch (path)
        {
            case "/healthz":
                await WriteJsonAsync(context, 200, new JsonObject { ["status"] = "ok" });
                return;
            case "/readyz":
                var ready = _gateway.IsConnected;
                await WriteJsonAsync(context, ready ? 200 : 503, new JsonObject
                {
                    ["ready"] = ready,
                    ["onebot"] = _gateway.IsConnected ? "connected" : "disconnected"
                });
                return;
            case "/status":
                await WriteJsonAsync(context, 200, BuildStatus());
                return;
        }

        // ---------- QQ 登录二维码（面板内扫码） ----------
        // 为什么放在这里而不是让前端直接连 NapCat WebUI：
        //   浏览器直连 NapCat 需要另一道 Basic 认证 + 跨域，而容器网络里只有本进程能到 napcat:6099。
        if (path.Equals("/api/qqlogin", StringComparison.OrdinalIgnoreCase))
        {
            var refresh = IsTruthy(context.Request.QueryString["refresh"]);
            var snapshot = await _loginQr.SnapshotAsync(refresh, _cts.Token);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = snapshot.Ok,
                ["configured"] = snapshot.Configured,
                ["url"] = snapshot.Url,
                ["ageSeconds"] = snapshot.AgeSeconds,
                ["key"] = snapshot.Key,
                ["refreshAfterSeconds"] = Math.Max(5, 100 - snapshot.AgeSeconds),
                // 超过这个时间还没换新码，说明 NapCat 那边不再轮换二维码了：
                // 用户扫的必然是过期码，面板要把这句话说出来（而不是默默展示一张死码）。
                ["stale"] = snapshot.Ok && snapshot.AgeSeconds > 180,
                ["error"] = snapshot.Error
            });
            return;
        }

        if (path.Equals("/api/qqlogin/qrcode.svg", StringComparison.OrdinalIgnoreCase))
        {
            var refresh = IsTruthy(context.Request.QueryString["refresh"]);
            var snapshot = await _loginQr.SnapshotAsync(refresh, _cts.Token);
            var svg = snapshot.Ok ? _loginQr.Svg() : null;
            if (svg is null)
            {
                await WriteJsonAsync(context, 503, new JsonObject
                {
                    ["error"] = snapshot.Error ?? "暂无二维码",
                    ["configured"] = snapshot.Configured
                });
                return;
            }

            // 图随二维码变，缓存会让用户扫到已失效的旧图
            context.Response.Headers["Cache-Control"] = "no-store, must-revalidate";
            await WriteBytesAsync(context, 200, "image/svg+xml; charset=utf-8", Encoding.UTF8.GetBytes(svg));
            return;
        }

        // ---------- API ----------
        if (path.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context, 200, BuildState());
            return;
        }

        // 面板日志页首屏：把最近的运行日志（内存环形缓冲，含启动时从日志文件回填的历史）还给浏览器。
        // 以前日志只活在浏览器内存里 —— 一刷新页面就“被清空”，只有之后的新行（号主反馈）。
        if (path.Equals("/api/logs", StringComparison.OrdinalIgnoreCase))
        {
            var raw = context.Request.QueryString["limit"];
            var limit = int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, 1, FileLog.RecentCapacity) : 300;
            var lines = new JsonArray();
            foreach (var (time, text) in FileLog.RecentLines(limit))
            {
                lines.Add(new JsonObject { ["time"] = time, ["text"] = text });
            }

            await WriteJsonAsync(context, 200, new JsonObject { ["lines"] = lines });
            return;
        }

        // ─────────── 面板一键部署（上传/拉取 app.tar.gz → 重建镜像 → 替换自己）───────────
        // GET  /api/deploy                状态（开关/当前镜像/上一条日志）
        // POST /api/deploy/upload         上传产物并部署
        // POST /api/deploy/url            从地址拉取产物并部署（地址会记住）
        // POST /api/deploy/rollback       回滚到上一个镜像
        if (path.StartsWith("/api/deploy", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePanelDeployAsync(context, path, method);
            return;
        }

        // ─────────── 文件管理（已按号主要求撤下：SFTP 由外部 agent 主导）───────────
        if (path.Equals("/api/settings", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "POST")
            {
                await HandleSettingsSaveAsync(context);
            }
            else
            {
                await WriteJsonAsync(context, 200, BuildSettingsPayload());
            }

            return;
        }

        // ─────────── 服务器健康日报（定时私聊推送）───────────
        // GET  /api/health-report        开关/时刻/收件人/下次推送/上次结果（面板卡片用）
        // POST /api/health-report        发一条或只看会发什么（{mode:"send"|"preview"}）
        if (path.Equals("/api/health-report", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "POST")
            {
                await HandleHealthReportAsync(context);
            }
            else
            {
                await WriteJsonAsync(context, 200, BuildHealthReportPayload());
            }

            return;
        }

        // ─────────── 本机 Agent 桥（handoff-4 §31）───────────
        // 桥是本机那个进程主动连过来的（本机在 NAT 后面，只能它出站）；这里把 WS 接住。
        // 注意：**这个端口能让人在号主电脑上执行命令** —— 所以：没配令牌就直接拒绝。
        if (path.Equals("/agent-bridge", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAgentBridgeAsync(context);
            return;
        }

        if (path.Equals("/api/agent/status", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context, 200, BuildAgentStatusPayload());
            return;
        }

        if (path.Equals("/api/agent/test", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            await HandleAgentTestAsync(context);
            return;
        }

        if (path.Equals("/api/agent/models", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAgentModelsAsync(context, method);
            return;
        }

        // 一键连接：吐一份**带地址与令牌**的启动脚本（本机下一行命令就能接上来）
        if (path.Equals("/api/agent/setup", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAgentSetupAsync(context);
            return;
        }

        // 桥脚本本体（内嵌在 DLL 里，从仓库 tools/ 编译进去）—— 面板里直接下载
        if (path.Equals("/agent-bridge-script", StringComparison.OrdinalIgnoreCase))
        {
            await WriteEmbeddedAgentScriptAsync(context, "pi-bridge.py");
            return;
        }

        // 服务器文件操作脚本（sftp 包装；同样从仓库 tools/ 内嵌）—— 给外部 pi agent 用
        if (path.Equals("/agent-sftp-script", StringComparison.OrdinalIgnoreCase))
        {
            await WriteEmbeddedAgentScriptAsync(context, "server-files.py");
            return;
        }

        // 断开某台设备（面板里踢掉；本机那边会自动重连，所以也是“重连”按钮）
        if (path.Equals("/api/agent/disconnect", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            var body = await ReadJsonAsync(context);
            var device = body?["device"]?.GetValue<string>();
            var ok = _agentBridge is not null && await _agentBridge.DisconnectAsync(device);
            await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = ok });
            return;
        }

        // Agent 会话：列出 / 新建 / 切换 / 删除 / 清空（面板与群里同一套）
        if (path.Equals("/api/agent/sessions", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAgentSessionsAsync(context, method);
            return;
        }

        // 设备上 pi 里的会话（面板“从 pi 导入”用）
        if (path.Equals("/api/agent/pi-sessions", StringComparison.OrdinalIgnoreCase))
        {
            var device = context.Request.QueryString["device"];
            if (_agentBridge is not null && _agentBridge.Connected)
            {
                await _agentBridge.RequestPiSessionsAsync(device ?? string.Empty);
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(500);
                    if (_agentBridge.DevicePiSessions(device).Count > 0)
                    {
                        break;
                    }
                }
            }

            var list = _agentBridge is null ? new List<JsonObject>() : _agentBridge.DevicePiSessions(device);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["device"] = device,
                ["connected"] = _agentBridge?.Connected ?? false,
                ["sessions"] = new JsonArray(list.Select(x => (JsonNode)MaskPiSession(x)).ToArray())
            });
            return;
        }

        if (path.Equals("/api/ai-mode", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            var body = await ReadJsonAsync(context);
            _agent.AiModeEnabled = body?["enabled"]?.GetValue<bool>() ?? !_agent.AiModeEnabled;
            await WriteJsonAsync(context, 200, new JsonObject { ["aiMode"] = _agent.AiModeEnabled });
            return;
        }

        // /api/conversations/{key}/...
        if (path.StartsWith("/api/conversations/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConversationAsync(context, path["/api/conversations/".Length..], method);
            return;
        }

        // /api/netease/qr：面板内扫码登录网易云（登录态存在自建 API 容器里，登完 VIP 歌才有播放地址）
        if (path.StartsWith("/api/netease/qr", StringComparison.OrdinalIgnoreCase))
        {
            await HandleNeteaseQrAsync(context, path, method);
            return;
        }

        // /api/music/test：一键验证“听音乐”链路（搜索 → 歌词 → 低码率音源 → 波形分析）
        // 为什么要这个入口：这条链路依赖外部 API，挂了只能在群里碰运气 ——
        // 这里可以直接跑一遍，把实测结果贴出来（排障与上线验收都用得上）。
        if (path.Equals("/api/music/test", StringComparison.OrdinalIgnoreCase))
        {
            await HandleMusicTestAsync(context, method);
            return;
        }

        // /api/voice/test：合成一句语音给面板自己播（验证 TTS 服务、音色、语速）。
        // 只合成、不发群 —— 想验证“群里真能听到”得开开关让模型发，或者看 /api/voice/health。
        if (path.Equals("/api/voice/test", StringComparison.OrdinalIgnoreCase))
        {
            await HandleVoiceTestAsync(context, method);
            return;
        }

        // /api/voice/health：问一下 TTS 服务自己（活着吗、有哪些音色），用于一键排障
        if (path.Equals("/api/voice/health", StringComparison.OrdinalIgnoreCase))
        {
            await HandleVoiceHealthAsync(context);
            return;
        }

        // /api/search/test：一键验证“联网搜索”（模型自带搜索 or 搜索源），把结果原样贴出来
        if (path.Equals("/api/search/test", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSearchTestAsync(context, method);
            return;
        }

        // /api/stickers 及子路径（表情包库：列表 / 取图 / 删除 / 立即巡检 / 导入）
        if (path.Equals("/api/stickers", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/stickers/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStickersAsync(context, path, method);
            return;
        }

        // /api/profiles/{uid}
        if (path.StartsWith("/api/profiles/", StringComparison.OrdinalIgnoreCase))
        {
            var uid = Uri.UnescapeDataString(path["/api/profiles/".Length..]);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["uid"] = uid,
                ["summary"] = _agent.GetProfileSummary(uid)
            });
            return;
        }

        // /api/archive?key=group:123&limit=200　归档消息（已溢出滚动窗口的旧历史）
        if (path.Equals("/api/archive", StringComparison.OrdinalIgnoreCase))
        {
            var key = context.Request.QueryString["key"] ?? string.Empty;
            var limit = int.TryParse(context.Request.QueryString["limit"], out var n) ? Math.Clamp(n, 1, 2000) : 200;
            await WriteJsonAsync(context, 200, ReadArchive(key, limit));
            return;
        }

        await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "not found", ["path"] = path });
    }

    private async Task HandleConversationAsync(HttpListenerContext context, string rest, string method)
    {
        var parts = rest.Split('/', 2);
        var key = Uri.UnescapeDataString(parts[0]);
        var action = parts.Length > 1 ? parts[1].ToLowerInvariant() : "messages";

        switch (action, method)
        {
            case ("messages", "GET"):
            {
                var conversation = _agent.Find(key);
                if (conversation is null)
                {
                    await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "no such conversation" });
                    return;
                }

                var limit = int.TryParse(context.Request.QueryString["limit"], out var n)
                    ? Math.Clamp(n, 1, 2000)
                    : 300;

                var messages = conversation.TakeLast(limit).Select(ToMessageDto).ToList();
                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["key"] = key,
                    ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m).ToArray())
                });
                return;
            }

            case ("send", "POST"):
            {
                var body = await ReadJsonAsync(context);
                var text = body?["text"]?.GetValue<string>() ?? string.Empty;
                var ok = await _agent.SendAsBotAsync(key, text);
                await WriteJsonAsync(context, ok ? 200 : 400, new JsonObject
                {
                    ["ok"] = ok,
                    ["error"] = ok ? null : (_gateway.IsConnected ? "发送失败" : "QQ 未连接")
                });
                return;
            }

            case ("read", "POST"):
                _agent.MarkRead(key);
                await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true });
                return;

            case ("delete", "POST"):
                var deleted = _agent.DeleteConversation(key);
                await WriteJsonAsync(context, deleted ? 200 : 404, new JsonObject { ["ok"] = deleted });
                return;

            default:
                await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
                return;
        }
    }

    /// <summary>
    /// 本机 Agent 桥的 WS 接入点。
    /// 为什么要求令牌：这个连接建立后，对方能让 pi 在号主电脑上干活 —— 不配令牌一律拒，
    /// 不是“回环部署就放行”那种方便口径（handoff-4 §31）。
    /// </summary>
    private async Task HandleAgentBridgeAsync(HttpListenerContext context)
    {
        var bridge = _agentBridge;
        if (bridge is null)
        {
            await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "agent bridge disabled" });
            return;
        }

        var token = _settings.AgentToken?.Trim() ?? string.Empty;
        if (token.Length == 0)
        {
            FileLog.Write("Agent", "agent 桥连接被拒：没有配置 QQCHAT_AGENT_TOKEN（不配令牌不接受任何桥连接）");
            await WriteJsonAsync(context, 403, new JsonObject { ["error"] = "agent token not configured" });
            return;
        }

        var given = context.Request.QueryString["token"] ?? context.Request.Headers["X-Agent-Token"];
        if (!string.Equals(given?.Trim(), token, StringComparison.Ordinal))
        {
            FileLog.Write("Agent", $"agent 桥连接被拒：令牌不对（来自 {context.Request.RemoteEndPoint}）");
            await WriteJsonAsync(context, 401, new JsonObject { ["error"] = "bad token" });
            return;
        }

        if (!context.Request.IsWebSocketRequest)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "websocket required" });
            return;
        }

        HttpListenerWebSocketContext ws;
        try
        {
            ws = await context.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            FileLog.Write("Agent", "agent 桥握手失败: " + ex.Message);
            return;
        }

        // 注意：进到 WS 之后**不能再写 HTTP 响应**（AcceptWebSocketAsync 已经把连接拿走了）
        await bridge.HandleAsync(ws.WebSocket, _cts.Token);
    }

    /// <summary>
    /// 模型列表：
    ///   • target=server（或 refresh 为空）→ 问服务器 agent 自己的接口（GET <AgentServerBaseUrl>/models）；
    ///   • device=&lt;设备名&gt; → 让那台外部设备现场重问一遍 pi（`pi --list-models`），然后回列表。
    /// 面板里的下拉就靠它 —— 号主不用手敲模型名。
    /// </summary>
    private async Task HandleAgentModelsAsync(HttpListenerContext context, string method)
    {
        var query = context.Request.QueryString;
        var target = (query["target"] ?? "server").Trim();
        var bridge = _agentBridge;

        // 外部设备：先让它刷新，再取（刷新是异步的，给它一两秒）
        if (target.Equals("host", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("device", StringComparison.OrdinalIgnoreCase) ||
            (target.Length > 0 && !target.Equals("server", StringComparison.OrdinalIgnoreCase) &&
             !target.Equals("服务器", StringComparison.OrdinalIgnoreCase)))
        {
            var device = target is "host" or "device" ? null : target;
            if (bridge is null || !bridge.Connected)
            {
                await WriteJsonAsync(context, 409, new JsonObject { ["error"] = "外部设备不在线" });
                return;
            }

            await bridge.RequestModelsAsync(device);
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(500);
                var models = bridge.DeviceModels(device);
                if (models.Length > 0)
                {
                    await WriteJsonAsync(context, 200, new JsonObject
                    {
                        ["target"] = "host",
                        ["device"] = device,
                        ["models"] = new JsonArray(models.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray())
                    });
                    return;
                }
            }

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["target"] = "host",
                ["device"] = device,
                ["models"] = new JsonArray(),
                ["note"] = "设备没说有哪些模型（桥的版本太旧？重启一下 start-pi-bridge.cmd）"
            });
            return;
        }

        // 服务器 agent 的接口里拉 /models
        var baseUrl = string.IsNullOrWhiteSpace(_settings.AgentServerBaseUrl)
            ? _settings.ModelBaseUrl
            : _settings.AgentServerBaseUrl;
        var key = string.IsNullOrWhiteSpace(_settings.AgentServerApiKey) ? _settings.ApiKey : _settings.AgentServerApiKey;
        var url = baseUrl.Trim().TrimEnd('/');
        if (!url.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            url += "/models";
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            if (!string.IsNullOrWhiteSpace(key))
            {
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Trim());
            }

            var body = await http.GetStringAsync(url);
            var list = new JsonArray();

            // 用 JsonDocument 而不是 JsonNode.Parse：容器是 trimmed/AOT 发布的，
            // JsonNode.Parse 会抛 “JsonSerializerOptions instance must specify a TypeInfoResolver…”（实测）。
            using (var doc = JsonDocument.Parse(body))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idNode) &&
                            idNode.ValueKind == JsonValueKind.String &&
                            idNode.GetString() is { Length: > 0 } id)
                        {
                            // 必须 JsonValue.Create：容器是 trimmed 发布，
                            // list.Add(string) 会造出 JsonValueCustomized<string>，序列化时抛
                            // “JsonSerializerOptions instance must specify a TypeInfoResolver…”（实测）。
                            list.Add(JsonValue.Create(id));
                        }
                    }
                }
            }

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["target"] = "server",
                ["url"] = url,
                ["models"] = list
            });
        }
        catch (Exception ex)
        {
            // 把完整异常写日志（只回 message 的时候，这类序列化/裁剪问题的栈跟本就看不到）
            FileLog.Warn("Web", $"拉模型列表失败（{url}）：{ex}");
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["target"] = "server",
                ["url"] = url,
                ["models"] = new JsonArray(),
                ["error"] = ex.Message
            });
        }
    }

    /// <summary>把内嵌的 pi-bridge.py 原样吐给下载方（用户本机跑的那个桥）。</summary>
    private static async Task WriteEmbeddedAgentScriptAsync(HttpListenerContext context, string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            await WriteJsonAsync(context, 404, new JsonObject { ["error"] = $"{fileName} not embedded" });
            return;
        }

        await using var stream = assembly.GetManifestResourceStream(name)!;
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/x-python; charset=utf-8";
        context.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
        await stream.CopyToAsync(context.Response.OutputStream);
        context.Response.Close();
    }

    /// <summary>
    /// 面板的「外部设备」表：在线的 + 只在配置里出现过的（离线）都得列出来 ——
    /// 号主要能给一台**还没接上来**的设备先写好名字/模型/目录，接上来就直接用。
    /// </summary>
    private JsonArray BuildDeviceListPayload()
    {
        var bridge = _agentBridge;
        var list = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cfg in AppSettings.ParseDeviceConfigs(_settings.AgentDevices))
        {
            var info = bridge?.DeviceInfo(cfg.Name);
            seen.Add(cfg.Name);
            list.Add(new JsonObject
            {
                ["name"] = cfg.Name,
                ["online"] = bridge?.IsDeviceOnline(cfg.Name) ?? false,
                ["enable"] = cfg.Enable,
                ["model"] = cfg.Model ?? string.Empty,
                ["workdir"] = cfg.WorkDir ?? string.Empty,
                ["tools"] = cfg.Tools ?? string.Empty,
                ["timeoutSec"] = cfg.TimeoutSeconds,
                ["pi"] = info?.Pi,
                ["cwd"] = info?.Cwd,
                ["models"] = new JsonArray((bridge?.DeviceModels(cfg.Name) ?? Array.Empty<string>())
                    .Select(m => (JsonNode)JsonValue.Create(m)!).ToArray())
            });
        }

        foreach (var name in bridge?.BridgeNames ?? (IReadOnlyList<string>)Array.Empty<string>())
        {
            if (!seen.Add(name))
            {
                continue;
            }

            var info = bridge!.DeviceInfo(name);
            list.Add(new JsonObject
            {
                ["name"] = name,
                ["online"] = true,
                ["enable"] = true,
                ["model"] = string.Empty,
                // 没在面板里配过的设备：workdir 必须留空（=没配），**不能**拿桥自报的 cwd 充数 ——
                // 否则面板上看着像“设备专属目录 = E:/bot”，而实际生效的是全局目录，改全局怎么都不动。
                ["workdir"] = string.Empty,
                ["tools"] = string.Empty,
                ["timeoutSec"] = 0,
                ["pi"] = info?.Pi,
                ["cwd"] = info?.Cwd,   // 桥自己启动时的工作目录：只作参考
                ["models"] = new JsonArray(bridge.DeviceModels(name).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray())
            });
        }

        return list;
    }

    /// <summary>
    /// 一键连接：给出「本机怎么接上来」的现成脚本（内嵌当前地址与令牌）。
    /// 为什么要内嵌令牌：把号主的步骤从“改脚本里的令牌 + 改地址”压成“下载 → 双击”一件事。
    /// </summary>
    private async Task HandleAgentSetupAsync(HttpListenerContext context)
    {
        var os = (context.Request.QueryString["os"] ?? "win").Trim().ToLowerInvariant();
        var token = _settings.AgentToken?.Trim() ?? string.Empty;
        var host = context.Request.QueryString["host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            host = context.Request.Headers["Host"] ?? "127.0.0.1:8080";
        }

        var workdir = string.IsNullOrWhiteSpace(_settings.AgentWorkDir) ? "%USERPROFILE%" : _settings.AgentWorkDir;
        var deviceName = (context.Request.QueryString["name"] ?? string.Empty).Trim();
        var wsUrl = $"ws://{host}/agent-bridge";

        string script;
        string filename;
        if (os is "sh" or "linux" or "mac")
        {
            filename = "connect-pi-bridge.sh";
            script =
                "#!/bin/sh\n" +
                "# 一键连接：让这台机器接入 QQ 机器人的 Agent（由机器人的面板生成）\n" +
                "# 需要把 pi-bridge.py 放在同目录（面板里可下载）\n" +
                "set -e\n" +
                $"export PI_BRIDGE_URL='{wsUrl}'\n" +
                $"export PI_BRIDGE_TOKEN='{token}'\n" +
                (deviceName.Length > 0 ? $"export PI_BRIDGE_NAME='{deviceName}'\n" : string.Empty) +
                $"export PI_BRIDGE_WORKDIR='{workdir}'\n" +
                "export PI_BRIDGE_PI=${PI_BRIDGE_PI:-pi}\n" +
                "exec python3 pi-bridge.py\n";
        }
        else
        {
            // ── Windows：**整个文件必须是 ASCII**（注释也用英文）──
            // 为什么：cmd.exe 按“系统 ANSI 代码页”读 .cmd 脚本（中文 Windows 就是 GBK），
            // 而这里给的是 UTF-8 —— 中文注释在那时候会变成乱码，并且能**吃掉紧跟其后的那一行**：
            // 号主实测 `set PI_BRIDGE_TOKEN=…` 就被吃掉，脚本改成 “--token: expected one argument” 报错。
            // 同理不要在 .cmd 里用 --token %VAR% 那种转一手的形式：参数直接走环境变量，少一个坑。
            filename = "connect-pi-bridge.cmd";
            script =
                "@echo off\r\n" +
                "rem ==========================================================================\r\n" +
                "rem  Connect this PC to the QQ bot's Agent (generated by the bot panel).\r\n" +
                "rem  Put pi-bridge.py in the same folder (download it from the panel too).\r\n" +
                "rem  Keep this file ASCII-only: cmd.exe reads .cmd with the ANSI code page,\r\n" +
                "rem  so non-ASCII comments get garbled and can swallow the next line.\r\n" +
                "rem  Started on demand only - nothing is installed for auto-start.\r\n" +
                "rem ==========================================================================\r\n" +
                "setlocal\r\n" +
                "title pi-bridge (QQ agent)\r\n" +
                "cd /d \"%~dp0\"\r\n" +
                "if not exist \"pi-bridge.py\" (\r\n" +
                "  echo [X] pi-bridge.py not found next to this script.\r\n" +
                "  echo     Download it from the bot panel, then run me again.\r\n" +
                "  pause\r\n" +
                "  exit /b 1\r\n" +
                ")\r\n" +
                $"set \"PI_BRIDGE_URL={wsUrl}\"\r\n" +
                $"set \"PI_BRIDGE_TOKEN={token}\"\r\n" +
                (deviceName.Length > 0 ? $"set \"PI_BRIDGE_NAME={deviceName}\"\r\n" : string.Empty) +
                $"set \"PI_BRIDGE_WORKDIR={workdir}\"\r\n" +
                "python pi-bridge.py\r\n" +
                "echo.\r\n" +
                "echo pi-bridge exited - press any key to close.\r\n" +
                "pause >nul\r\n" +
                "endlocal\r\n";
        }

        var bytes = Encoding.UTF8.GetBytes(script);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{filename}\"");
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    /// <summary>
    /// Agent 会话管理（面板）：
    ///   GET  /api/agent/sessions?key=group:123   → 列该会话的 agent 会话（带当前标记）
    ///   GET  /api/agent/sessions                 → 不带 key：列出**所有**聊天的会话（面板总览用）
    ///   POST {key, action: new|use|delete|reset, id?, name?}
    /// 与群里的 //sessions / //new / //use / //del / //reset 是同一套存储，两边看到的一样。
    /// </summary>
    private async Task HandleAgentSessionsAsync(HttpListenerContext context, string method)
    {
        var key = context.Request.QueryString["key"];

        if (method != "POST")
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                var all = _agent.BuildAllAgentSessionsPayload();
                await WriteJsonAsync(context, 200, all);
                return;
            }

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["key"] = key,
                ["sessions"] = _agent.BuildAgentSessionsPayload(key!)
            });
            return;
        }

        var body = await ReadJsonAsync(context);
        var action = (body?["action"]?.GetValue<string>() ?? string.Empty).Trim().ToLowerInvariant();
        var chatKey = (body?["key"]?.GetValue<string>() ?? string.Empty).Trim();
        var sessionId = body?["id"]?.GetValue<string>();
        var sessionName = body?["name"]?.GetValue<string>();
        var backend = (body?["backend"]?.GetValue<string>() ?? "host").Trim();

        if (chatKey.Length == 0)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "缺少 key（聊天会话）" });
            return;
        }

        var ok = false;
        var message = string.Empty;
        switch (action)
        {
            case "new":
            {
                var created = _agent.CreateAgentSession(chatKey, backend, sessionName);
                ok = true;
                message = $"已新建会话「{ShownName(created.Name)}」";
                break;
            }

            case "use":
                ok = _agent.UseAgentSession(chatKey, sessionId ?? string.Empty);
                message = ok ? "已切换" : "没找到这个会话";
                break;

            case "delete":
                ok = _agent.DeleteAgentSession(chatKey, sessionId ?? string.Empty);
                message = ok ? "已删除（当前会话已补新的）" : "没找到这个会话";
                break;

            case "reset":
                ok = _agent.ResetAgentSession(chatKey, sessionId ?? string.Empty);
                message = ok ? "已清空历史" : "没找到这个会话";
                break;

            case "rename":
                ok = _agent.RenameAgentSession(chatKey, sessionId ?? string.Empty, body?["title"]?.GetValue<string>() ?? string.Empty);
                message = ok ? "已改名" : "改名失败（名字空或会话不存在）";
                break;

            case "import":
            {
                // 把设备上 pi 里的一个会话接过来用（新建一个指向它的会话）
                var piId = body?["piSession"]?.GetValue<string>() ?? string.Empty;
                var created = _agent.ImportPiSession(chatKey, piId, sessionName);
                ok = created is not null;
                message = ok ? $"已接用 pi 会话「{ShownName(created!.Name)}」" : "没认出那个 pi 会话";
                break;
            }

            default:
                await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "action 只支持 new/use/delete/reset" });
                return;
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = ok,
            ["message"] = message,
            ["sessions"] = _agent.BuildAgentSessionsPayload(chatKey)
        });
    }

    /// <summary>
    /// 面板回显的会话名/设备上 pi 会话的标题：脱敏开关打开时遮一下（与群里 //sessions 同一套规则 —— 面板也要能直接截图）。
    /// 注意：面板列表的 **显示** 用脱敏值，但改名输入框要用 nameRaw，否则一改名就把“群友A”这种占位符写回去。
    /// </summary>
    private string ShownName(string name)
        => _settings.AgentMaskSensitive ? AgentMask.Text(name) : name;

    /// <summary>设备上报的 pi 会话条目：只遮 title（id/cwd 是机器字段，面板还要拿来接用）。</summary>
    private JsonObject MaskPiSession(JsonObject raw)
    {
        var node = (JsonObject)raw.DeepClone();
        if (_settings.AgentMaskSensitive && node["title"] is JsonNode t && t.GetValueKind() == JsonValueKind.String)
        {
            node["title"] = ShownName(t.GetValue<string>());
        }

        return node;
    }

    /// <summary>本机 agent 的状态（面板卡片 / 群里的 //status）。</summary>
    private JsonObject BuildAgentStatusPayload()
    {
        var bridge = _agentBridge;
        var current = bridge?.Current;
        var target = _settings.AgentTarget;
        // “指定设备”时按名字解析目录 —— 设备离线也要显示它已配的那个目录，
        // 否则面板显示全局默认，而任务实际跑在设备专属目录（两处又对不上）。
        var targetDevice = string.IsNullOrWhiteSpace(target) || target is "auto" or "host" or "server"
            ? null
            : target.Trim();
        var selected = targetDevice is null ? bridge?.AnyBridge : bridge?.DeviceInfo(targetDevice);
        return new JsonObject
        {
            ["enabled"] = _settings.EnableAgentBridge,
            ["prefix"] = _settings.AgentPrefix,
            ["allowedUsers"] = _settings.AgentAllowedUsers,
            ["tokenConfigured"] = !string.IsNullOrWhiteSpace(_settings.AgentToken),
            ["connected"] = bridge?.Connected ?? false,
            ["host"] = selected?.Name ?? targetDevice,
            ["cwd"] = _settings.ResolveAgentWorkDir(targetDevice ?? selected?.Name, selected?.Cwd),
            ["hostCwd"] = selected?.Cwd,
            // 面板要拿来标注“这个目录是哪来的”：设备专属 / 全局默认 / 桥自报
            ["globalWorkdir"] = _settings.AgentWorkDir ?? string.Empty,
            ["pi"] = selected?.Pi,
            ["devices"] = new JsonArray(bridge?.BridgeNames.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray() ?? Array.Empty<JsonNode>()),
            ["serverAgent"] = _settings.EnableServerAgent,
            ["hostAgent"] = _settings.EnableHostAgent,
            ["target"] = _settings.AgentTarget,
            ["agentModel"] = _settings.AgentModel,
            ["serverBaseUrl"] = _settings.AgentServerBaseUrl,
            ["serverModel"] = _settings.AgentServerModel,
            ["serverKeyConfigured"] = !string.IsNullOrWhiteSpace(_settings.AgentServerApiKey),
        ["serverKeySource"] = !string.IsNullOrWhiteSpace(_settings.AgentServerApiKeyOverride)
            ? "panel"
            : (string.IsNullOrWhiteSpace(_settings.AgentServerApiKey) ? "none" : "env"),
            ["deviceModels"] = new JsonArray((bridge?.DeviceModels() ?? Array.Empty<string>())
                .Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["deviceList"] = BuildDeviceListPayload(),
            ["queued"] = bridge?.QueuedCount ?? 0,
            ["summary"] = bridge?.Describe() ?? "未启用",
            ["current"] = current is null
                ? null
                : new JsonObject
                {
                    ["id"] = current.Id,
                    ["prompt"] = current.Prompt,
                    ["elapsedMs"] = (long)(DateTimeOffset.Now - current.StartedAt).TotalMilliseconds,
                    ["note"] = current.LastNote
                }
        };
    }

    /// <summary>面板里的“试一条”：不经过 QQ，直接把一句提示词送到本机 pi，看能不能跑通。</summary>
    private async Task HandleAgentTestAsync(HttpListenerContext context)
    {
        var bridge = _agentBridge;
        if (bridge is null || !_settings.EnableAgentBridge)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "本机 Agent 没启用（面板里打开开关）" });
            return;
        }

        var body = await ReadJsonAsync(context);
        var prompt = body?["prompt"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "prompt 不能为空" });
            return;
        }

        var timeout = Math.Clamp(body?["timeoutSec"]?.GetValue<int>() ?? 180, 10, 900);
        var target = (body?["target"]?.GetValue<string>() ?? "host").Trim();

        // 面板里能分别试两边：服务器内置 agent 直接在容器里跑工具循环（不用经过外部设备）
        if (target.Equals("server", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("服务器", StringComparison.OrdinalIgnoreCase))
        {
            if (!_settings.EnableServerAgent)
            {
                await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "服务器内置 agent 没开" });
                return;
            }

            var serverTask = await _agent.RunServerAgentDirectAsync(prompt, timeout);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = serverTask.Ok,
                ["id"] = serverTask.Id,
                ["text"] = serverTask.Ok ? serverTask.Text : serverTask.Error,
                ["durationMs"] = serverTask.DurationMs,
                ["toolCalls"] = serverTask.ToolCalls,
                ["target"] = "server"
            });
            return;
        }

        if (!bridge.Connected)
        {
            await WriteJsonAsync(context, 409, new JsonObject
            {
                ["error"] = "外部 agent 设备没连上",
                ["hint"] = "在本机跑 start-pi-bridge.cmd（那个窗口要开着），或者把 target 改成 server 试服务器内置 agent"
            });
            return;
        }

        var task = await bridge.RunDirectAsync(prompt, TimeSpan.FromSeconds(timeout), CancellationToken.None);
        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = task.Ok,
            ["id"] = task.Id,
            ["text"] = task.Ok ? task.Text : task.Error,
            ["durationMs"] = task.DurationMs,
            ["toolCalls"] = task.ToolCalls,
            ["target"] = "host"
        });
    }

    private async Task HandleSettingsSaveAsync(HttpListenerContext context)
    {
        var body = await ReadJsonAsync(context);
        if (body is null)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "invalid json" });
            return;
        }

        // 模型端点：先校验再应用 —— 无效 URL 直接 400，不然一次手滑就把配置写坏、连不上模型。
        var newBaseUrl = body["modelBaseUrl"] is JsonNode mbu ? (mbu.GetValue<string>() ?? string.Empty).Trim() : null;
        if (newBaseUrl is { Length: > 0 } &&
            (!Uri.TryCreate(newBaseUrl, UriKind.Absolute, out var parsedBaseUrl) ||
             (parsedBaseUrl.Scheme != Uri.UriSchemeHttp && parsedBaseUrl.Scheme != Uri.UriSchemeHttps)))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "Base URL 要形如 http://host:port/v1（http/https 开头的完整地址）" });
            return;
        }

        // 只接受运行时可改的字段（协议端地址、QQ 号这些仍属于容器环境变量职责）
        _agent.ApplyRuntimeSettings(s =>
        {
            if (body["botPersona"] is JsonNode persona) s.BotPersona = persona.GetValue<string>() ?? string.Empty;
            if (body["messageWhitelist"] is JsonNode wl) s.MessageWhitelist = wl.GetValue<string>() ?? string.Empty;
        if (body["whitelistGroups"] is JsonNode wlg) s.WhitelistGroups = wlg.GetValue<string>() ?? string.Empty;
        if (body["whitelistPrivates"] is JsonNode wlp) s.WhitelistPrivates = wlp.GetValue<string>() ?? string.Empty;
            if (body["aiDesire"] is JsonNode desire) s.AiDesire = Math.Clamp(desire.GetValue<int>(), 0, 100);
            if (body["suitabilityThreshold"] is JsonNode th) s.SuitabilityThreshold = Math.Clamp(th.GetValue<int>(), 0, 100);
            if (body["aiModeEnabled"] is JsonNode ai) s.AiModeEnabled = ai.GetValue<bool>();
            if (body["maxTokens"] is JsonNode mt) s.MaxTokens = Math.Clamp(mt.GetValue<int>(), 64, 32000);
            if (body["groupCooldownSeconds"] is JsonNode gc) s.GroupCooldownSeconds = Math.Max(0, gc.GetValue<int>());
            if (body["privateCooldownSeconds"] is JsonNode pc) s.PrivateCooldownSeconds = Math.Max(0, pc.GetValue<int>());
            if (body["idleFallbackSeconds"] is JsonNode fb) s.IdleFallbackSeconds = Math.Max(0, fb.GetValue<int>());
            if (body["splitReplies"] is JsonNode sp) s.SplitReplies = sp.GetValue<bool>();
            if (body["enableProactive"] is JsonNode pv) s.EnableProactive = pv.GetValue<bool>();
            if (body["proactiveCooldownSeconds"] is JsonNode pcd) s.ProactiveCooldownSeconds = Math.Clamp(pcd.GetValue<int>(), 60, 86400);
            if (body["proactiveQuietSeconds"] is JsonNode pq) s.ProactiveQuietSeconds = Math.Clamp(pq.GetValue<int>(), 1, 3600);
            if (body["ignoreBracketMessages"] is JsonNode ibm) s.IgnoreBracketMessages = ibm.GetValue<bool>();
            if (body["segmentDelayMs"] is JsonNode sd) s.SegmentDelayMs = Math.Max(0, sd.GetValue<int>());
            if (body["maxContextMessages"] is JsonNode mc) s.MaxContextMessages = Math.Clamp(mc.GetValue<int>(), 10, 1000);
            if (body["profileLookupCount"] is JsonNode pl) s.ProfileLookupCount = Math.Clamp(pl.GetValue<int>(), 0, 50);
            if (body["profileSummaryLines"] is JsonNode psl) s.ProfileSummaryLines = Math.Clamp(psl.GetValue<int>(), 0, 50);
            if (body["maxProfileChars"] is JsonNode mpc) s.MaxProfileChars = Math.Clamp(mpc.GetValue<int>(), 0, 20000);
            if (body["maxMessagesPerConversation"] is JsonNode mm) s.MaxMessagesPerConversation = Math.Clamp(mm.GetValue<int>(), 20, 100000);
            if (body["maxConcurrentReplies"] is JsonNode mcr) s.MaxConcurrentReplies = Math.Clamp(mcr.GetValue<int>(), 1, 16);
            if (body["enableProfileSummary"] is JsonNode eps) s.EnableProfileSummary = eps.GetValue<bool>();
            if (body["profileSummaryThreshold"] is JsonNode pst) s.ProfileSummaryThreshold = Math.Clamp(pst.GetValue<int>(), 5, 500);
            if (body["profileSummaryMaxChars"] is JsonNode psm) s.ProfileSummaryMaxChars = Math.Clamp(psm.GetValue<int>(), 40, 2000);
            if (body["profileSummaryIntervalSeconds"] is JsonNode psi) s.ProfileSummaryIntervalSeconds = Math.Clamp(psi.GetValue<int>(), 0, 86400);

            // ---- 表情包 ----
            if (body["enableVoice"] is JsonNode ev) s.EnableVoice = ev.GetValue<bool>();
        if (body["voiceName"] is JsonNode vn) s.VoiceName = vn.GetValue<string>().Trim();
        if (body["voiceSpeed"] is JsonNode vs) s.VoiceSpeed = Math.Clamp(vs.GetValue<int>(), 50, 200);
        if (body["voiceMaxChars"] is JsonNode vmc) s.VoiceMaxChars = Math.Clamp(vmc.GetValue<int>(), 10, 300);
        if (body["ttsServiceUrl"] is JsonNode tts) s.TtsServiceUrl = tts.GetValue<string>().Trim();
        if (body["enableWebSearch"] is JsonNode ws) s.EnableWebSearch = ws.GetValue<bool>();
        if (body["webSearchUseModelSearch"] is JsonNode wsm) s.WebSearchUseModelSearch = wsm.GetValue<bool>();
        if (body["webSearchSources"] is JsonNode wss) s.WebSearchSources = wss.GetValue<string>().Trim();
        if (body["webSearchMaxResults"] is JsonNode wsr) s.WebSearchMaxResults = Math.Clamp(wsr.GetValue<int>(), 1, 10);
        if (body["webSearchCooldownSeconds"] is JsonNode wsc) s.WebSearchCooldownSeconds = Math.Clamp(wsc.GetValue<int>(), 0, 86400);
        if (body["webSearchTimeoutSeconds"] is JsonNode wst) s.WebSearchTimeoutSeconds = Math.Clamp(wst.GetValue<int>(), 5, 60);
        if (body["enableLinkPreview"] is JsonNode elp) s.EnableLinkPreview = elp.GetValue<bool>();
        if (body["linkPreviewTimeoutSeconds"] is JsonNode lpt) s.LinkPreviewTimeoutSeconds = Math.Clamp(lpt.GetValue<int>(), 2, 30);
        if (body["linkPreviewMax"] is JsonNode lpm) s.LinkPreviewMax = Math.Clamp(lpm.GetValue<int>(), 0, 5);
        if (body["enableMusic"] is JsonNode em) s.EnableMusic = em.GetValue<bool>();
        if (body["neteaseBaseUrl"] is JsonNode nbu) s.NeteaseBaseUrl = nbu.GetValue<string>().Trim();
        if (body["musicSources"] is JsonNode ms) s.MusicSources = ms.GetValue<string>();
        if (body["musicBitrate"] is JsonNode mb) s.MusicBitrate = Math.Clamp(mb.GetValue<int>(), 32, 320);
        if (body["musicMaxDownloadMb"] is JsonNode mmd) s.MusicMaxDownloadMb = Math.Clamp(mmd.GetValue<int>(), 1, 64);
        if (body["musicMaxAnalysisSeconds"] is JsonNode mma) s.MusicMaxAnalysisSeconds = Math.Clamp(mma.GetValue<int>(), 20, 600);
        if (body["musicLibraryMax"] is JsonNode mml) s.MusicLibraryMax = Math.Clamp(mml.GetValue<int>(), 10, 5000);
        if (body["musicNoteTtlDays"] is JsonNode mnt) s.MusicNoteTtlDays = Math.Clamp(mnt.GetValue<int>(), 1, 365);
        if (body["musicListenCooldownSeconds"] is JsonNode mlc) s.MusicListenCooldownSeconds = Math.Clamp(mlc.GetValue<int>(), 0, 86400);
        if (body["musicUnderstandModel"] is JsonNode mum) s.MusicUnderstandModel = mum.GetValue<string>().Trim();
        if (body["musicSendAudioToModel"] is JsonNode msa) s.MusicSendAudioToModel = msa.GetValue<bool>();
        if (body["musicKeepAudio"] is JsonNode mka) s.MusicKeepAudio = mka.GetValue<bool>();

        // ---- 本机 Agent（// 命令）----
        if (body["enableAgentBridge"] is JsonNode eab) s.EnableAgentBridge = eab.GetValue<bool>();
        if (body["agentPrefix"] is JsonNode apx) s.AgentPrefix = (apx.GetValue<string>() ?? "//").Trim();
        if (body["agentAllowedUsers"] is JsonNode aau) s.AgentAllowedUsers = aau.GetValue<string>() ?? string.Empty;
        if (body["agentWorkDir"] is JsonNode awd) s.AgentWorkDir = (awd.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentModel"] is JsonNode amd) s.AgentModel = (amd.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentTools"] is JsonNode atl) s.AgentTools = (atl.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentTimeoutSeconds"] is JsonNode ats) s.AgentTimeoutSeconds = Math.Clamp(ats.GetValue<int>(), 30, 7200);
        if (body["agentReplyMaxChars"] is JsonNode arc) s.AgentReplyMaxChars = Math.Clamp(arc.GetValue<int>(), 200, 3000);
        if (body["agentProgressSeconds"] is JsonNode aps) s.AgentProgressSeconds = Math.Clamp(aps.GetValue<int>(), 0, 3600);
        if (body["agentMaxQueued"] is JsonNode amq) s.AgentMaxQueued = Math.Clamp(amq.GetValue<int>(), 1, 20);

        // ---- agent 路由与服务器内置 agent ----
        if (body["agentTarget"] is JsonNode atg) s.AgentTarget = (atg.GetValue<string>() ?? "auto").Trim();
        if (body["enableServerAgent"] is JsonNode esa) s.EnableServerAgent = esa.GetValue<bool>();
        if (body["enableHostAgent"] is JsonNode eha) s.EnableHostAgent = eha.GetValue<bool>();
        if (body["agentServerTools"] is JsonNode ast) s.AgentServerTools = (ast.GetValue<string>() ?? string.Empty).Trim();

        // QQ 动作：只认已存在的动作名（别名也翻成规范名），写错的直接忽略 ——
        // 下次读回设置时面板上看到的就是“真正生效的那几个”，不会拿一个拼错的名字骗自己。
        if (body["agentServerQqActions"] is JsonNode asqa)
        {
            var raw = (asqa.GetValue<string>() ?? string.Empty).Trim();
            if (raw.Length == 0 || raw is "all" or "*" or "全部" or "所有")
            {
                s.AgentServerQqActions = raw;
            }
            else
            {
                var names = new List<string>();
                foreach (var piece in raw.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var canonical = QqActionCatalog.Canonical(piece.Trim());
                    if (canonical is not null && !names.Contains(canonical))
                    {
                        names.Add(canonical);
                    }
                }

                s.AgentServerQqActions = string.Join(",", names);
            }
        }
        if (body["agentServerModel"] is JsonNode asm) s.AgentServerModel = (asm.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentDevices"] is JsonNode ad) s.AgentDevices = ad.GetValue<string>() ?? string.Empty;
        if (body["enableAgentMask"] is JsonNode eam) s.AgentMaskSensitive = eam.GetValue<bool>();
        if (body["agentPrompt"] is JsonNode ap)
        {
            // 附加提示词：空 = 明确不带（与“没这个字段”不同）；长度设上限，免得一屏文本被贴进每一轮请求
            s.AgentPrompt = (ap.ToString() ?? string.Empty).Trim();
            if (s.AgentPrompt.Length > 8000)
            {
                s.AgentPrompt = s.AgentPrompt[..8000];
            }
        }
        if (body["agentModel"] is JsonNode am2) s.AgentModel = (am2.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentServerBaseUrl"] is JsonNode asbu)
        {
            var v = (asbu.GetValue<string>() ?? string.Empty).Trim();
            // 允许空（= 用聊天那个）；填了就必须是 http(s) 完整地址，不然一次手滑就调不通
            if (v.Length > 0 && (!Uri.TryCreate(v, UriKind.Absolute, out var parsed) ||
                                 (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            {
                FileLog.Warn("Web", $"服务器 agent 的接口地址无效，已忽略：{v}");
            }
            else
            {
                s.AgentServerBaseUrl = v;
            }
        }
        // 服务器 agent 自己的密钥：与聊天那把同规矩 —— 存 secrets 表、不回显明文、留空 = 不改
        // （要清就去点“清除密钥”，那边有二次确认）
        if (body["agentServerKey"] is JsonValue agentKeyValue && agentKeyValue.TryGetValue<string>(out var rawAgentKey))
        {
            var newAgentKey = (rawAgentKey ?? string.Empty).Trim();
            if (newAgentKey.Length == 0)
            {
                SecretsStore.SaveAgentServerKey(null);
                s.AgentServerApiKeyOverride = null;
                s.AgentServerApiKey = (Environment.GetEnvironmentVariable("QQCHAT_AGENT_SERVER_KEY") ?? string.Empty).Trim();
            }
            else
            {
                SecretsStore.SaveAgentServerKey(newAgentKey);
                s.AgentServerApiKeyOverride = newAgentKey;
                s.AgentServerApiKey = newAgentKey;
            }

            // 密钥只记“变了”，绝不回显明文（日志会被贴出来排障）
            FileLog.Write("Web", newAgentKey.Length == 0
                ? "面板清空了服务器 agent 的密钥（回退环境变量）"
                : "面板更新了服务器 agent 的密钥（已掩码保存）");
        }
        if (body["agentServerWorkDir"] is JsonNode asw) s.AgentServerWorkDir = (asw.GetValue<string>() ?? "/data").Trim();
        if (body["agentServerKeepContext"] is JsonNode askc) s.AgentServerKeepContext = askc.GetValue<bool>();
        if (body["agentServerDocker"] is JsonNode asdk) s.AgentServerDocker = asdk.GetValue<bool>();
        if (body["panelDeployEnabled"] is JsonNode pde) s.PanelDeployEnabled = pde.GetValue<bool>();
        if (body["panelDeployUrl"] is JsonNode pdu) s.PanelDeployUrl = (pdu.GetValue<string>() ?? string.Empty).Trim();
        if (body["agentServerMaxSteps"] is JsonNode ass) s.AgentServerMaxSteps = Math.Clamp(ass.GetValue<int>(), 1, 30);
        if (body["agentServerCommandTimeoutSeconds"] is JsonNode asct) s.AgentServerCommandTimeoutSeconds = Math.Clamp(asct.GetValue<int>(), 5, 300);
            // ---- 服务器健康日报（定时私聊推送）----
            if (body["healthReportEnabled"] is JsonNode hre) s.HealthReportEnabled = hre.GetValue<bool>();
            if (body["healthReportTime"] is JsonNode hrt)
            {
                // 只接受能解析成 HH:mm 的值（"18：00"/"1800" 也认）；解析不出来就保持原值，
                // 免得一次手滑把推送时间静默改成 18:00（用户以为改了 07:30）
                var typed = (hrt.ToString() ?? string.Empty).Trim();
                if (typed.Length > 0)
                {
                    var (hour, minute) = AppSettings.ParseHealthReportClock(typed);
                    var normalized = $"{hour:00}:{minute:00}";
                    var looksValid = typed.Replace('：', ':').Contains(':') || typed.Length == 4;
                    if (looksValid)
                    {
                        s.HealthReportTime = normalized;
                    }
                    else
                    {
                        FileLog.Warn("Web", $"健康日报时刻格式不对，已忽略：{typed}");
                    }
                }
            }
            if (body["healthReportTargets"] is JsonNode hrtg) s.HealthReportTargets = (hrtg.ToString() ?? string.Empty).Trim();

        if (body["enableStickers"] is JsonNode es) s.EnableStickers = es.GetValue<bool>();
            if (body["stickerLibraryMax"] is JsonNode slm) s.StickerLibraryMax = Math.Clamp(slm.GetValue<int>(), 0, 2000);
            if (body["stickerCandidates"] is JsonNode sc) s.StickerCandidates = Math.Clamp(sc.GetValue<int>(), 0, 20);
            if (body["stickerCurateIntervalSeconds"] is JsonNode sci) s.StickerCurateIntervalSeconds = Math.Clamp(sci.GetValue<int>(), 0, 86400);
            if (body["stickerCooldownSeconds"] is JsonNode scl) s.StickerCooldownSeconds = Math.Clamp(scl.GetValue<int>(), 0, 86400);
        if (body["enablePoke"] is JsonNode ep) s.EnablePoke = ep.GetValue<bool>();
        if (body["pokeCooldownSeconds"] is JsonNode pcl) s.PokeCooldownSeconds = Math.Clamp(pcl.GetValue<int>(), 0, 86400);
        if (body["moodTtlSeconds"] is JsonNode mtt) s.MoodTtlSeconds = Math.Clamp(mtt.GetValue<int>(), 0, 86400 * 7);

        // ---- 模型接口（面板可改；留空 = 回退到环境变量）----
        if (newBaseUrl is not null)
        {
            s.ModelBaseUrlOverride = newBaseUrl.Length == 0 ? null : newBaseUrl;
            s.ModelBaseUrl = newBaseUrl.Length > 0
                ? newBaseUrl
                : Environment.GetEnvironmentVariable("QQCHAT_BASE_URL") is { Length: > 0 } envUrl
                    ? envUrl.Trim()
                    : "https://api.openai.com/v1";
        }

        if (body["model"] is JsonNode modelNode)
        {
            var modelName = (modelNode.GetValue<string>() ?? string.Empty).Trim();
            s.ModelOverride = modelName.Length == 0 ? null : modelName;
            s.Model = modelName.Length > 0
                ? modelName
                : Environment.GetEnvironmentVariable("QQCHAT_MODEL") is { Length: > 0 } envModel
                    ? envModel.Trim()
                    : "gpt-4o-mini";
        }

        // 密钥：存 data/secrets.json（权限 600），**不写 settings.json**；留空 = 删掉、回退环境变量
        if (body["apiKey"] is JsonValue keyValue && keyValue.TryGetValue<string>(out var rawKey))
        {
            var newKey = (rawKey ?? string.Empty).Trim();
            if (newKey.Length == 0)
            {
                SecretsStore.SaveApiKey(null);
                s.ApiKeyOverride = null;
                s.ApiKey = (Environment.GetEnvironmentVariable("QQCHAT_API_KEY") ??
                            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty).Trim();
            }
            else
            {
                SecretsStore.SaveApiKey(newKey);
                s.ApiKeyOverride = newKey;
                s.ApiKey = newKey;
            }

            // 密钥只记“变了”，绝不回显明文（日志会被贴出来排障）
            FileLog.Write("Web", newKey.Length == 0 ? "面板清空了模型 API Key（回退环境变量）" : "面板更新了模型 API Key（已掩码保存）");
        }

        // 心情是机器人状态（不是行为配置），单独走 agent：留空 = 交回自动描述
        if (body["mood"] is JsonValue moodValue && moodValue.TryGetValue<string>(out var moodText))
        {
            _agent.SetMood(moodText);
        }
        });

        // 模型配置改完要让客户端也看到（同一个 AppSettings 实例，这里只是显式同步一次）
        _agent.SyncModelSettings();

        // 定时类功能：开关/时刻/收件人变了一定要重排定时器，否则“改了不生效”（要重启才变）
        _healthReports?.Reapply();

        await WriteJsonAsync(context, 200, BuildSettingsPayload());
    }

    // ══════════════ SSE ══════════════

    private async Task HandleSseAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.SendChunked = true;

        var client = new SseClient(response.OutputStream);
        lock (_clientsGate)
        {
            _clients.Add(client);
        }

        try
        {
            // 首帧：完整状态，前端据此渲染
            await client.SendAsync("state", BuildState().ToJsonString(Json));

            // 心跳，避免代理/浏览器掐断空闲连接
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(15000, ct);
                await client.SendCommentAsync("ping");
            }
        }
        catch
        {
            // 客户端断开
        }
        finally
        {
            lock (_clientsGate)
            {
                _clients.Remove(client);
            }

            try
            {
                response.Close();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private void Broadcast(string @event, JsonNode payload)
    {
        var json = payload.ToJsonString(Json);
        List<SseClient> targets;
        lock (_clientsGate)
        {
            targets = _clients.ToList();
        }

        foreach (var client in targets)
        {
            _ = client.SendAsync(@event, json).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    lock (_clientsGate)
                    {
                        _clients.Remove(client);
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    private void OnMessageAdded(string key, ChatMessage message)
        => Broadcast("message", new JsonObject
        {
            ["key"] = key,
            ["message"] = ToMessageDto(message)
        });

    private void OnConversationsChanged()
    {
        // 每条消息都会触发这个事件，而它要重建**全部**会话的 JSON。
        // 忙群（每秒几条）下会把 CPU 和 SSE 带宽吃掉 —— 所以合并：
        // 窗口内只广播一次，窗口结束后若还有新变更再补一次，保证最后状态不会丢。
        Interlocked.Increment(ref _convBroadcastVersion);

        if (Interlocked.Exchange(ref _convBroadcastPending, 1) == 1)
        {
            return;
        }

        _ = BroadcastConversationsLoopAsync();
    }

    private async Task BroadcastConversationsLoopAsync()
    {
        try
        {
            while (true)
            {
                var version = Interlocked.Read(ref _convBroadcastVersion);

                await Task.Delay(250);

                Broadcast("conversations", new JsonObject
                {
                    ["conversations"] = new JsonArray(BuildConversations().Select(c => (JsonNode)c).ToArray())
                });

                Interlocked.Exchange(ref _convBroadcastPending, 0);

                if (Interlocked.Read(ref _convBroadcastVersion) == version)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _convBroadcastPending, 1) == 1)
                {
                    return;
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _convBroadcastPending, 0);
        }
    }

    private void OnStateChanged()
        => Broadcast("state", BuildState());

    private void OnThinkingChanged(string key)
    {
        var conversation = _agent.Find(key);
        Broadcast("thinking", new JsonObject
        {
            ["key"] = key,
            ["thinking"] = conversation?.Thinking ?? false
        });
    }

    /// <summary>实时日志：与 /api/logs 回填的历史是同一份文本（含 [标签]）。</summary>
    private void OnFileLogLine(long time, string text)
        => Broadcast("log", new JsonObject
        {
            ["time"] = time,
            ["text"] = text
        });

    // ══════════════ 数据组装 ══════════════

    private JsonObject BuildStatus() => new()
    {
        ["status"] = "running",
        ["uptimeSeconds"] = (int)(DateTimeOffset.Now - _startedAt).TotalSeconds,
        ["startedAt"] = _startedAt.ToString("O"),
        ["onebot"] = new JsonObject
        {
            ["connected"] = _gateway.IsConnected,
            ["protocol"] = _settings.OneBotProtocol,
            ["address"] = _settings.OneBotAddress
        },
        ["account"] = new JsonObject
        {
            ["uin"] = _settings.NormalizedUin,
            ["selfId"] = _agent.SelfId,
            // true/false = 已探明；null = 未知（协议端未实现 get_status）
            // 注意：这是“QQ 账号在不在线”，与上面的 onebot.connected 不是一回事
            ["online"] = _agent.AccountOnline
        },
        ["login"] = new JsonObject
        {
            // 面板靠这两项决定「扫码卡片」里是显示二维码还是显示原因
            ["qrAvailable"] = _loginQr.Configured,
            ["napcatWebUi"] = _loginQr.WebUiDisplay
        },
        ["agent"] = new JsonObject
        {
            ["enabled"] = _settings.AiModeEnabled,
            ["model"] = _settings.Model,
            ["desire"] = _settings.AiDesire,
            ["personaConfigured"] = !string.IsNullOrWhiteSpace(_settings.BotPersona)
        },
        ["conversations"] = _agent.Conversations.Count,
        ["inFlightReplies"] = _agent.InFlightReplies,
        ["queuedReplies"] = _agent.QueuedReplies,
        ["profileSummaries"] = _agent.SummaryDoneCount,
        ["profileSummaryFailures"] = _agent.SummaryFailCount,
        ["stickers"] = _agent.Stickers.Count,
        ["stickersDescribed"] = _agent.Stickers.DescribedCount,
        ["stickersPending"] = _agent.StickerPendingDescribe
    };

    private JsonObject BuildState() => new()
    {
        ["status"] = BuildStatus(),
        ["aiMode"] = _settings.AiModeEnabled,
        ["conversations"] = new JsonArray(BuildConversations().Select(c => (JsonNode)c).ToArray()),
        ["serverTime"] = DateTimeOffset.Now.ToUnixTimeMilliseconds()
    };

    private List<JsonObject> BuildConversations()
    {
        var list = new List<JsonObject>();
        foreach (var c in _agent.Conversations)
        {
            var (isGroup, id) = c.Target;
            list.Add(new JsonObject
            {
                ["key"] = c.SourceKey,
                ["kind"] = isGroup ? "Group" : "Private",
                ["name"] = c.Name,
                ["id"] = id,
                ["avatarUrl"] = BuildAvatarUrl(isGroup, id),
                ["avatarText"] = FirstChar(c.Name),
                ["unread"] = c.Unread,
                ["thinking"] = c.Thinking,
                ["preview"] = c.Preview,
                ["messageCount"] = c.MessageCount,
                ["lastTime"] = c.LastTime.ToUnixTimeMilliseconds()
            });
        }

        return list;
    }

    private static JsonObject ToMessageDto(ChatMessage m) => new()
    {
        ["seq"] = m.Seq,
        ["role"] = m.Role switch
        {
            MessageRole.Self => "Self",
            MessageRole.System => "System",
            _ => "Peer"
        },
        ["text"] = m.Text,
        ["senderName"] = m.SenderName,
        ["senderId"] = m.SenderId,
        ["time"] = m.Timestamp.ToUnixTimeMilliseconds(),
        ["images"] = m.ImageUrls is { Count: > 0 }
            ? new JsonArray(m.ImageUrls.Select(u => (JsonNode)u!).ToArray())
            : null,
        ["qqMessageId"] = m.QqMessageId,
        // 已撤回的消息：面板把它划掉并注明“模型看到的是 [已撤回]”
        ["recalled"] = m.Recalled ? true : null
    };

    /// <summary>QQ 头像：群 p.qlogo.cn/gh/{群号}/{群号}/0；用户 q1.qlogo.cn/g?b=qq&amp;nk={QQ}&amp;s=640。</summary>
    private static string BuildAvatarUrl(bool isGroup, long id) => isGroup
        ? $"https://p.qlogo.cn/gh/{id}/{id}/0"
        : $"https://q1.qlogo.cn/g?b=qq&nk={id}&s=640";

    private static string FirstChar(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? "?" : trimmed[..1];
    }

    private JsonObject BuildSettingsPayload()
    {
        var s = _settings;

        return new JsonObject
        {
            ["runtime"] = new JsonObject
            {
                ["botPersona"] = s.BotPersona,
                ["messageWhitelist"] = s.MessageWhitelist,
        ["whitelistGroups"] = s.WhitelistGroups,
        ["whitelistPrivates"] = s.WhitelistPrivates,
        // 留空的那一边会回落到旧的共用名单 —— 面板要如实告知，不然号主会以为新框填了没生效
        ["whitelistGroupsFromLegacy"] = string.IsNullOrWhiteSpace(s.WhitelistGroups) && s.MessageWhitelist.Length > 0,
        ["whitelistPrivatesFromLegacy"] = string.IsNullOrWhiteSpace(s.WhitelistPrivates) && s.MessageWhitelist.Length > 0,
                ["aiDesire"] = s.AiDesire,
                ["suitabilityThreshold"] = s.SuitabilityThreshold,
                ["aiModeEnabled"] = s.AiModeEnabled,
                ["maxTokens"] = s.MaxTokens,
                ["groupCooldownSeconds"] = s.GroupCooldownSeconds,
                ["privateCooldownSeconds"] = s.PrivateCooldownSeconds,
                ["idleFallbackSeconds"] = s.IdleFallbackSeconds,
                ["splitReplies"] = s.SplitReplies,
                ["enableProactive"] = s.EnableProactive,
                ["proactiveCooldownSeconds"] = s.ProactiveCooldownSeconds,
                ["proactiveQuietSeconds"] = s.ProactiveQuietSeconds,
                ["ignoreBracketMessages"] = s.IgnoreBracketMessages,
                ["segmentDelayMs"] = s.SegmentDelayMs,
                ["maxContextMessages"] = s.MaxContextMessages,
                ["profileLookupCount"] = s.ProfileLookupCount,
                ["profileSummaryLines"] = s.ProfileSummaryLines,
                ["maxProfileChars"] = s.MaxProfileChars,
                ["maxMessagesPerConversation"] = s.MaxMessagesPerConversation,
                ["maxConcurrentReplies"] = s.MaxConcurrentReplies,
                ["enableProfileSummary"] = s.EnableProfileSummary,
                ["profileSummaryThreshold"] = s.ProfileSummaryThreshold,
                ["profileSummaryMaxChars"] = s.ProfileSummaryMaxChars,
                ["profileSummaryIntervalSeconds"] = s.ProfileSummaryIntervalSeconds,
                ["enableVoice"] = s.EnableVoice,
        ["voiceName"] = s.VoiceName,
        ["voiceSpeed"] = s.VoiceSpeed,
        ["voiceMaxChars"] = s.VoiceMaxChars,
        ["ttsServiceUrl"] = s.TtsServiceUrl,
         ["enableWebSearch"] = s.EnableWebSearch,
         ["webSearchUseModelSearch"] = s.WebSearchUseModelSearch,
         ["webSearchSources"] = s.WebSearchSources,
         ["webSearchMaxResults"] = s.WebSearchMaxResults,
         ["webSearchCooldownSeconds"] = s.WebSearchCooldownSeconds,
         ["webSearchTimeoutSeconds"] = s.WebSearchTimeoutSeconds,
        ["enableLinkPreview"] = s.EnableLinkPreview,
        ["linkPreviewTimeoutSeconds"] = s.LinkPreviewTimeoutSeconds,
        ["linkPreviewMax"] = s.LinkPreviewMax,
        ["enableMusic"] = s.EnableMusic,
        ["neteaseBaseUrl"] = s.NeteaseBaseUrl,
        ["musicSources"] = s.MusicSources,
        ["musicBitrate"] = s.MusicBitrate,
        ["musicMaxDownloadMb"] = s.MusicMaxDownloadMb,
        ["musicMaxAnalysisSeconds"] = s.MusicMaxAnalysisSeconds,
        ["musicLibraryMax"] = s.MusicLibraryMax,
        ["musicNoteTtlDays"] = s.MusicNoteTtlDays,
        ["musicListenCooldownSeconds"] = s.MusicListenCooldownSeconds,
        ["musicUnderstandModel"] = s.MusicUnderstandModel,
        ["musicSendAudioToModel"] = s.MusicSendAudioToModel,
        ["musicAudioToModelMaxKb"] = s.MusicAudioToModelMaxKb,
        ["musicKeepAudio"] = s.MusicKeepAudio,
        ["enableAgentBridge"] = s.EnableAgentBridge,
        ["agentPrefix"] = s.AgentPrefix,
        ["agentAllowedUsers"] = s.AgentAllowedUsers,
        ["agentWorkDir"] = s.AgentWorkDir,
        ["agentModel"] = s.AgentModel,
        ["agentTools"] = s.AgentTools,
        ["agentTimeoutSeconds"] = s.AgentTimeoutSeconds,
        ["agentReplyMaxChars"] = s.AgentReplyMaxChars,
        ["agentProgressSeconds"] = s.AgentProgressSeconds,
        ["agentMaxQueued"] = s.AgentMaxQueued,
        ["agentTarget"] = s.AgentTarget,
        ["enableServerAgent"] = s.EnableServerAgent,
        ["enableHostAgent"] = s.EnableHostAgent,
        ["hostAgent"] = s.EnableHostAgent,
        ["agentServerTools"] = s.AgentServerTools,
        ["agentServerQqActions"] = s.AgentServerQqActions,
        ["agentServerQqActionsEffective"] = QqActionCatalog.Summarize(QqActionCatalog.ParseAllowed(s.AgentServerQqActions)),
        ["agentServerModel"] = s.AgentServerModel,
        ["agentModel"] = s.AgentModel,
        ["agentServerBaseUrl"] = s.AgentServerBaseUrl,
        // 服务器 agent 的密钥：只给“设没设 / 掩码 / 来源”，不回显明文（与聊天那把 key 同样的规矩）
        ["agentServerKeySet"] = !string.IsNullOrWhiteSpace(s.AgentServerApiKey),
        ["agentServerKeyMasked"] = Mask(s.AgentServerApiKey),
        ["agentServerKeySource"] = !string.IsNullOrWhiteSpace(s.AgentServerApiKeyOverride)
            ? "panel"
            : (string.IsNullOrWhiteSpace(s.AgentServerApiKey) ? "none" : "env"),
        ["agentDevices"] = s.AgentDevices,
        ["enableAgentMask"] = s.AgentMaskSensitive,
        ["agentPrompt"] = s.AgentPrompt,
        // 面板「恢复默认」按钮用：默认那份写在 AppSettings.DefaultAgentPrompt（只有一处真源）
        ["agentPromptDefault"] = AppSettings.DefaultAgentPrompt,
        ["agentServerWorkDir"] = s.AgentServerWorkDir,
        ["agentServerKeepContext"] = s.AgentServerKeepContext,
        ["agentServerDocker"] = s.AgentServerDocker,
        ["panelDeployEnabled"] = s.PanelDeployEnabled,
        ["panelDeployUrl"] = s.PanelDeployUrl,
        ["agentServerMaxSteps"] = s.AgentServerMaxSteps,
        ["agentServerCommandTimeoutSeconds"] = s.AgentServerCommandTimeoutSeconds,
        ["neteaseCookieSet"] = !string.IsNullOrWhiteSpace(s.NeteaseCookie),
        ["enableStickers"] = s.EnableStickers,
                ["stickerLibraryMax"] = s.StickerLibraryMax,
                ["stickerCandidates"] = s.StickerCandidates,
                ["stickerCurateIntervalSeconds"] = s.StickerCurateIntervalSeconds,
                ["stickerCooldownSeconds"] = s.StickerCooldownSeconds,
        ["enablePoke"] = s.EnablePoke,
        ["pokeCooldownSeconds"] = s.PokeCooldownSeconds,
        ["moodTtlSeconds"] = s.MoodTtlSeconds,
        // ---- 服务器健康日报（定时私聊推送）----
        ["healthReportEnabled"] = s.HealthReportEnabled,
        ["healthReportTime"] = s.HealthReportTime,
        ["healthReportTargets"] = s.HealthReportTargets,
        // 当前心情（可手改；空 = 由代码按被戳次数自动描述）
        ["mood"] = _agent.MoodText,
        ["moodSummary"] = _agent.MoodSummary
            },
            // 只读：容器环境变量职责，改这里无效
            ["env"] = new JsonObject
            {
                ["modelBaseUrl"] = s.ModelBaseUrl,
                ["modelBaseUrlSource"] = string.IsNullOrWhiteSpace(s.ModelBaseUrlOverride) ? "env" : "panel",
                ["model"] = s.Model,
                ["modelSource"] = string.IsNullOrWhiteSpace(s.ModelOverride) ? "env" : "panel",
                ["maxTokens"] = s.MaxTokens,
                ["apiKeyMasked"] = Mask(s.ApiKey),
                ["apiKeySet"] = !string.IsNullOrWhiteSpace(s.ApiKey),
                ["apiKeySource"] = !string.IsNullOrWhiteSpace(s.ApiKeyOverride) ? "panel" : (string.IsNullOrWhiteSpace(s.ApiKey) ? "none" : "env"),
                ["oneBotProtocol"] = s.OneBotProtocol,
                ["oneBotAddress"] = s.OneBotAddress,
                ["oneBotTokenMasked"] = Mask(s.OneBotToken),
                ["uin"] = s.NormalizedUin,
                ["dataDir"] = AppPaths.RuntimeRoot,
                ["healthPort"] = s.HealthPort,
                ["tz"] = Environment.GetEnvironmentVariable("TZ") ?? TimeZoneInfo.Local.Id
            },
            ["settingsFile"] = SettingsStore.FilePath
        };
    }

    private static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        return secret.Length <= 4 ? "****" : secret[..4] + "****";
    }

    // ══════════════ 基础 IO ══════════════

    /// <summary>
    /// 读取某会话的归档（已溢出滚动窗口的旧消息）。
    /// 现在归档在 SQLite 里（messages 表 archived=1），不再是 archive/*.jsonl 文件；
    /// 返回给面板的字段保持与老版一致（t/role/sender/uid/mid/text），前端不用改。
    /// </summary>
    private JsonObject ReadArchive(string sourceKey, int limit)
    {
        var result = new JsonObject
        {
            ["key"] = sourceKey,
            ["messages"] = new JsonArray(),
            ["totalLines"] = 0
        };

        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            result["error"] = "缺少 key";
            return result;
        }

        try
        {
            var rows = _agent.ReadArchive(sourceKey, limit);
            var array = new JsonArray();
            foreach (var m in rows)
            {
                array.Add(new JsonObject
                {
                    ["t"] = m.TimeUnix,
                    ["role"] = m.Role,
                    ["sender"] = m.SenderName,
                    ["uid"] = m.SenderId,
                    ["mid"] = m.QqMessageId,
                    ["text"] = m.Text
                });
            }

            result["messages"] = array;
            result["totalLines"] = rows.Count;
            if (rows.Count == 0)
            {
                result["error"] = "该会话尚无归档";
            }
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
        }

        return result;
    }

    // ══════════════ 表情包库 ══════════════

    /// <summary>
    /// 表情包库接口。库是全库共用一份（不分会话）：
    ///   GET  /api/stickers                 列表（含 id、说明、关键词、用过几次）
    ///   GET  /api/stickers/{id}/img        图片本体（面板缩略图用）
    ///   POST /api/stickers/{id}/delete     删除一张（面板手动）
    ///   POST /api/stickers/curate          立即让机器人巡检一遍（自己决定删哪些）
    ///   POST /api/stickers/import          从 QQ 收藏表情导入（机器人自己“添加”）
    /// </summary>
    private async Task HandleStickersAsync(HttpListenerContext context, string path, string method)
    {
        var store = _agent.Stickers;
        var rest = path.Length > "/api/stickers".Length ? path["/api/stickers/".Length..] : string.Empty;

        if (rest.Length == 0)
        {
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["enabled"] = _settings.EnableStickers,
                ["max"] = _settings.StickerLibraryMax,
                ["candidates"] = _settings.StickerCandidates,
                ["curateIntervalSeconds"] = _settings.StickerCurateIntervalSeconds,
                ["count"] = store.Count,
                ["described"] = store.DescribedCount,
                ["pendingDescribe"] = _agent.StickerPendingDescribe,
                ["describeDone"] = _agent.StickerDescribeDone,
                ["items"] = new JsonArray(store.Snapshot()
                    .OrderByDescending(s => s.AddedAt)
                    .Select(s => (JsonNode)new JsonObject
                    {
                        ["id"] = s.Id,
                        ["desc"] = s.Desc,
                        ["tags"] = new JsonArray(s.Tags.Select(t => (JsonNode)t).ToArray()),
                        ["uses"] = s.Uses,
                        ["bytes"] = s.Bytes,
                        ["ext"] = s.Ext,
                        ["addedAt"] = s.AddedAt * 1000,
                        ["lastUsedAt"] = s.LastUsedAt * 1000,
                        ["fromUid"] = s.FromUid,
                        ["fromGroup"] = s.FromGroup,
                        ["described"] = s.Described,
                        // true/false = 模型审过“是不是表情包”；null = 还没审（未审的不会被发出去）
                        ["isSticker"] = s.IsSticker
                    })
                    .ToArray())
            });
            return;
        }

        var parts = rest.Split('/', 2);
        var id = Uri.UnescapeDataString(parts[0]);

        // 图片本体：<img> 不能带自定义请求头，所以只能靠 ?token=（与 SSE 同一套约定）
        if (parts.Length == 2 && parts[1].Equals("img", StringComparison.OrdinalIgnoreCase))
        {
            var record = store.Find(id);
            if (record is null || !File.Exists(record.AbsolutePath))
            {
                await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "sticker not found" });
                return;
            }

            var bytes = await File.ReadAllBytesAsync(record.AbsolutePath);
            var type = record.Ext switch
            {
                "jpg" => "image/jpeg",
                "gif" => "image/gif",
                "webp" => "image/webp",
                _ => "image/png"
            };
            context.Response.Headers["Cache-Control"] = "no-store";
            await WriteBytesAsync(context, 200, type, bytes);
            return;
        }

        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        // 两段形式：/api/stickers/{id}/delete
        // 注意：单段形式（/api/stickers/curate）下 parts 只有一个元素，
        // 直接读 parts[1] 会抛 IndexOutOfRange（之前就是这么把 curate/import 打挂的）。
        if (parts.Length == 2 && parts[1].Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            var ok = store.Remove(id, "面板手动删除");
            await WriteJsonAsync(context, ok ? 200 : 404, new JsonObject
            {
                ["ok"] = ok,
                ["count"] = store.Count
            });
            return;
        }

        // 单段形式：/api/stickers/curate 或 /api/stickers/import
        if (parts.Length == 1)
        {
            switch (id.ToLowerInvariant())
            {
                case "curate":
                    // 别让面板等模型：先回一句，跑完往面板推日志
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            FileLog.Write("Sticker", await _agent.CurateStickersAsync(force: true));
                        }
                        catch (Exception ex)
                        {
                            FileLog.Warn("Sticker", $"手动巡检失败：{ex.Message}");
                        }
                    });
                    await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["started"] = true });
                    return;

                case "import":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            FileLog.Write("Sticker", await _agent.ImportStickersFromAlbumAsync());
                        }
                        catch (Exception ex)
                        {
                            FileLog.Warn("Sticker", $"导入收藏表情失败：{ex.Message}");
                        }
                    });
                    await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["started"] = true });
                    return;
            }
        }

        await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "not found", ["path"] = path });
    }

    private static bool IsTruthy(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.Equals("1", StringComparison.Ordinal) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>读取嵌入的静态资源（wwwroot/*，LogicalName = web/文件名）。</summary>
    private static async Task WriteAssetAsync(HttpListenerContext context, string fileName, string contentType)
    {
        // 面板是会随版本迭代的，禁止浏览器缓存，否则改了界面看不到
        context.Response.Headers["Cache-Control"] = "no-store, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";

        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase) ||
                                 n.Equals("web/" + fileName, StringComparison.OrdinalIgnoreCase));

        if (resource is null)
        {
            var message = Encoding.UTF8.GetBytes($"asset not found: {fileName}");
            await WriteBytesAsync(context, 404, "text/plain; charset=utf-8", message);
            return;
        }

        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        await WriteBytesAsync(context, 200, contentType, buffer.ToArray());
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, JsonNode payload)
        => await WriteBytesAsync(context, status, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(payload.ToJsonString(Json)));

    private static async Task WriteBytesAsync(HttpListenerContext context, int status, string contentType, byte[] bytes)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        if (bytes.Length > 0)
        {
            await context.Response.OutputStream.WriteAsync(bytes);
        }
    }

    /// <summary>
    /// 面板内的网易云扫码登录（代理到自建 API 的 /login/qr/*）。
    /// 为什么放在面板里：手机号/密码登录要暴露账号密码，扫码最干净；
    /// 而且登录态是存在自建 API 那边的，面板只是把二维码拿过来展示、帮忙轮询。
    /// </summary>
    private async Task HandleNeteaseQrAsync(HttpListenerContext context, string path, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        var baseUrl = _settings.NeteaseBaseUrl.TrimEnd('/');
        var stamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        try
        {
            if (path.EndsWith("/check", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadJsonAsync(context);
                var key = body?["key"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "缺少 key" });
                    return;
                }

                var check = await GetJsonFromAsync($"{baseUrl}/login/qr/check?key={Uri.EscapeDataString(key)}&timestamp={stamp}");

                // 扫码成功（803）时把登录态存下来：上游会在这条响应里给 cookie。
                // 为什么必须存：cookie 本来只活在自建 API 容器的进程内存里 ——
                // 容器一重建（升级镜像 / compose up 重创）就得重新扫码，号主反馈的“老是掉登录”就是这个。
                // 存进库（secrets 表，权限 600）之后，每轮请求直接带 cookie（见 NeteaseMusicClient），
                // 与那个容器活着不活着无关；重启机器人也不会丢。
                if (TryReadInt(check?["code"]) == 803 &&
                    check?["cookie"] is JsonValue cookieValue && cookieValue.TryGetValue<string>(out var freshCookie) &&
                    !string.IsNullOrWhiteSpace(freshCookie))
                {
                    var saved = SecretsStore.SaveNeteaseCookie(freshCookie);
                    _settings.NeteaseCookie = freshCookie;   // 立即生效（音乐客户端每轮现读）
                    check["saved"] = saved;
                    FileLog.Write("Music", saved
                        ? $"网易云扫码登录成功，登录态已存进库里（{freshCookie.Length} 字，重启/重建容器都不丢）"
                        : "网易云扫码登录成功，但登录态落盘失败（仍会用在本次进程内）");
                }

                await WriteJsonAsync(context, 200, check ?? new JsonObject { ["error"] = "上游无响应" });
                return;
            }

            /// <summary>宽容地读一个整数（上游有时给字符串 "803"，不确定就别让它把整条链路弄挂）。</summary>
            static int? TryReadInt(JsonNode? node)
            {
                if (node is not JsonValue value)
                {
                    return null;
                }

                if (value.TryGetValue<int>(out var number))
                {
                    return number;
                }

                return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
            }

            // 两步：先拿 key，再让上游生成二维码（qrimg=true 直接回 base64 图）
            var keyJson = await GetJsonFromAsync($"{baseUrl}/login/qr/key?timestamp={stamp}");
            var unikey = keyJson?["data"]?["unikey"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(unikey))
            {
                await WriteJsonAsync(context, 502, new JsonObject
                {
                    ["error"] = "拿不到二维码 key（自建网易云接口不可用？）",
                    ["detail"] = keyJson?.ToJsonString() ?? "(无响应)"
                });
                return;
            }

            var qrJson = await GetJsonFromAsync($"{baseUrl}/login/qr/create?key={Uri.EscapeDataString(unikey)}&qrimg=true&timestamp={stamp}");
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["key"] = unikey,
                ["qrimg"] = qrJson?["data"]?["qrimg"]?.GetValue<string>() ?? string.Empty,
                ["qrurl"] = qrJson?["data"]?["qrurl"]?.GetValue<string>() ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = ex.Message });
        }
    }

    /// <summary>面板登录流程用的 HttpClient（打自建网易云接口；超时短一点，别拖住面板）。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>向自建网易云接口发一个 GET 并解析 JSON（仅面板登录流程用）。</summary>
    private static async Task<JsonNode?> GetJsonFromAsync(string url)
    {
        using var resp = await Http.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        try
        {
            return JsonNode.Parse(text);
        }
        catch (Exception)
        {
            return new JsonObject { ["raw"] = text.Length > 300 ? text[..300] : text };
        }
    }

    /// <summary>/api/music/test：把“听音乐”链路真跑一遍（搜歌 → 歌词 → 低码率音源 → 波形分析）。</summary>
    private async Task HandleMusicTestAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        JsonNode? body;
        try
        {
            body = await ReadJsonAsync(context);
        }
        catch (Exception)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请求体不是合法 JSON" });
            return;
        }

        var song = body?["song"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(song) || song.Length > 60)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请填 song（歌名，可带歌手，≤ 60 字）" });
            return;
        }

        try
        {
            var note = await _agent.TestMusicAsync(song, CancellationToken.None);
            var song2 = _agent.LastMusicTestHeader;
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = note is not null,
                ["song"] = song2,
                ["note"] = note ?? "没搜到，或这首歌没拿到音源且没有歌词"
            });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = ex.Message });
        }
    }

    /// <summary>
    /// /api/voice/test：真合成一句语音（走 TTS 容器 /speak），直接把 wav 字节还给浏览器播。
    /// 为什么返回二进制而不是 JSON+base64：浏览器直接 Blob 播放最省事，也不白扛 33% 的 base64 开销。
    /// 音色/语速可以带参数（面板改了还没保存也能试听）；服务地址一律用已保存的设置 ——
    /// 面板不足以成为“拿任意 URL 去访问”的入口（跟白名单/密码一个道理）。
    /// </summary>
    private async Task HandleVoiceTestAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        JsonNode? body;
        try
        {
            body = await ReadJsonAsync(context);
        }
        catch (Exception)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请求体不是合法 JSON" });
            return;
        }

        var text = body?["text"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请填 text（要说的一句话）" });
            return;
        }

        if (text.Length > 300)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = $"文本太长（{text.Length} > 300），长文本请改用文字" });
            return;
        }

        var voice = body?["voice"]?.GetValue<string>()?.Trim();
        var speed = body?["speed"] is JsonNode sp && int.TryParse(sp.ToString(), out var parsedSpeed) ? parsedSpeed : (int?)null;

        var (data, error) = await _agent.TestVoiceAsync(text, voice, speed, CancellationToken.None);
        if (data is null)
        {
            await WriteJsonAsync(context, 502, new JsonObject { ["error"] = error ?? "合成失败" });
            return;
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        await WriteBytesAsync(context, 200, "audio/wav", data);
    }

    // ══════════════ 服务器健康日报（定时私聊推送） ══════════════

    /// <summary>
    /// GET /api/health-report：面板卡片要的一切（开关/时刻/收件人/下一次推送/上次结果）。
    /// 这里**不做探针**（不碰模型接口、不碰 TTS）—— 打开面板就慢 6 秒不可接受；
    /// 「预览这次会发什么」是用户主动点按钮才走 <see cref="HandleHealthReportAsync" />。
    /// </summary>
    private JsonObject BuildHealthReportPayload()
    {
        var (hour, minute) = AppSettings.ParseHealthReportClock(_settings.HealthReportTime);
        var next = _healthReports?.NextRunAt;
        return new JsonObject
        {
            ["enabled"] = _settings.HealthReportEnabled,
            ["time"] = $"{hour:00}:{minute:00}",
            ["targets"] = _settings.HealthReportTargets ?? string.Empty,
            ["targetList"] = new JsonArray(HealthReportService.ParseTargets(_settings.HealthReportTargets)
                .Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
            ["nextRunAt"] = next?.ToString("O"),
            ["lastSentAt"] = _healthReports?.LastSentAt?.ToString("O"),
            ["lastError"] = _healthReports?.LastError,
            ["sentCount"] = _healthReports?.SentCount ?? 0,
            ["serverTime"] = HealthReportService.NowBeijing().ToString("O")
        };
    }

    /// <summary>
    /// POST /api/health-report：<c>{"mode":"preview"}</c> 只生成（不碰 QQ）、
    /// <c>{"mode":"send"}</c>（默认）现在真发一条到私聊。
    /// 面板两个按钮走这里；两个都要能被当成“当场验收”，所以返回正文与失败原因。
    /// </summary>
    private async Task HandleHealthReportAsync(HttpListenerContext context)
    {
        if (_healthReports is null)
        {
            await WriteJsonAsync(context, 503, new JsonObject { ["ok"] = false, ["error"] = "健康日报服务未初始化" });
            return;
        }

        JsonNode? body = null;
        try
        {
            body = await ReadJsonAsync(context);
        }
        catch (Exception)
        {
            // 没带请求体 = 默认“发一条”
        }

        var mode = (body?["mode"]?.ToString() ?? "send").Trim().ToLowerInvariant();
        if (mode == "preview")
        {
            var preview = await _healthReports.PreviewAsync();
            await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["mode"] = "preview", ["text"] = preview });
            return;
        }

        var (ok, text, error) = await _healthReports.SendNowAsync("面板手动");
        await WriteJsonAsync(context, ok ? 200 : 502, new JsonObject
        {
            ["ok"] = ok,
            ["mode"] = "send",
            ["text"] = text,
            ["error"] = error,
            ["targets"] = new JsonArray(HealthReportService.ParseTargets(_settings.HealthReportTargets)
                .Select(id => (JsonNode)JsonValue.Create(id)).ToArray())
        });
    }

    /// <summary>/api/voice/health：把 TTS 服务自己的 /health 透传给面板（活着吗、有哪些音色）。</summary>
    private async Task HandleVoiceHealthAsync(HttpListenerContext context)
    {
        var voice = _agent.Voice;
        if (voice is null)
        {
            await WriteJsonAsync(context, 503, new JsonObject { ["ok"] = false, ["error"] = "语音服务还没初始化" });
            return;
        }

        var (ok, payload, error) = await voice.HealthAsync(CancellationToken.None);
        await WriteJsonAsync(context, ok ? 200 : 502, new JsonObject
        {
            ["ok"] = ok,
            ["url"] = voice.BaseUrl,
            ["currentVoice"] = voice.VoiceName,
            ["voices"] = payload?["voices"]?.DeepClone() ?? new JsonArray(),
            ["default"] = payload?["default"]?.ToString(),
            ["error"] = error
        });
    }

    /// <summary>
    /// /api/search/test：跑一次真实联网搜索（模型自带搜索优先，否则走搜索源模板）。
    /// 为什么要这个入口：搜索能不能用跟“服务器 IP、代理支不支持工具”强相关，
    /// 面板上当场跑一次，比在群里碰运气强。
    /// </summary>
    private async Task HandleSearchTestAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        JsonNode? body;
        try
        {
            body = await ReadJsonAsync(context);
        }
        catch (Exception)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请求体不是合法 JSON" });
            return;
        }

        var query = body?["query"]?.GetValue<string>()?.Trim();
        var url = body?["url"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(url))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请填 query（搜索词）或 url（要读的页面）" });
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                var (text, error) = await _agent.TestReadPageAsync(url!, CancellationToken.None);
                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["ok"] = text is not null,
                    ["mode"] = "read",
                    ["text"] = text,
                    ["error"] = error
                });
                return;
            }

            var result = await _agent.TestSearchAsync(query!, CancellationToken.None);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = result.HasContent,
                ["mode"] = "search",
                ["provider"] = result.Provider,
                ["answer"] = result.Answer,
                ["hits"] = new JsonArray(result.Hits
                    .Select(h => (JsonNode)new JsonObject
                    {
                        ["title"] = h.Title,
                        ["url"] = h.Url,
                        ["snippet"] = h.Snippet
                    })
                    .ToArray()),
                ["note"] = result.Describe(),
                ["error"] = result.Error
            });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = ex.Message });
        }
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _agent.MessageAdded -= OnMessageAdded;
        _agent.ConversationsChanged -= OnConversationsChanged;
        _agent.StateChanged -= OnStateChanged;
        _agent.ThinkingChanged -= OnThinkingChanged;
        FileLog.LineWritten -= OnFileLogLine;

        _cts.Cancel();
        lock (_clientsGate)
        {
            foreach (var c in _clients)
            {
                c.Dispose();
            }

            _clients.Clear();
        }

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // 忽略
        }

        _ = _loop;
    }

    /// <summary>一个 SSE 订阅者。</summary>
    private sealed class SseClient : IDisposable
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public SseClient(Stream stream) => _stream = stream;

        public async Task SendAsync(string @event, string data)
            => await WriteRawAsync($"event: {@event}\ndata: {data}\n\n");

        public async Task SendCommentAsync(string comment) => await WriteRawAsync($": {comment}\n\n");

        private async Task WriteRawAsync(string payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _lock.WaitAsync();
            try
            {
                await _stream.WriteAsync(bytes);
                await _stream.FlushAsync();
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Dispose()
        {
            try
            {
                _stream.Dispose();
            }
            catch
            {
                // 忽略
            }

            _lock.Dispose();
        }
    }
}

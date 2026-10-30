using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Evaluation;
using BotAgent.Services.Music;
using BotAgent.Services.NapCat;
using BotAgent.Services.Net;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Participation;
using BotAgent.Services.Local;
using BotAgent.Services.Permissions;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Settings;
using BotAgent.Services.Stickers;
using BotAgent.Services.Platforms;
using BotAgent.Services.Voice;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Ports;
using BotAgent.Panel;
using BotAgent.Services.Ports;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// Web 面板 + 健康检查服务（复用 in-box HttpListener，不引入 ASP.NET，保持镜像体积）。
///
///   面板      旧内置页面已移除（S6）：前端只有 src/web 的 Vue 面板，由 Caddy 以 /web/ 提供；
///             宿主只留 API 与健康检查，GET / 不再返回页面。
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
///
/// 依赖口径（批次 5）：面板**不再经过 BotAgentHost 这层 façade** 去够组件 —— 要什么就注什么
/// （配置问 <see cref="SettingsHotReload" /> / <see cref="SettingsBox" />，事件订阅 <see cref="PanelNotifier" />，
/// 会话问注册表，表情包/心情/语音/音乐/搜索问各自的用例）。剩下的 <c>_agent</c> 只有一处：
/// 「以机器人身份代发一条」—— 那件事真要会话 + 协议端 + 记账 + 存档，不是转发（见 <see cref="BotAgentHost.SendAsBotAsync" />）。
/// </summary>
public sealed partial class WebUiServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // ⚠ 容器是 **trimmed 发布**：反射被裁掉之后，没显式指定 resolver 的 options 会在**第一次用**时
        // 抛 “JsonSerializerOptions instance must specify a TypeInfoResolver setting before being marked
        //  as read-only”，而且只在 Web 请求里炸（日志里就是那句“请求处理异常”，线上断续复现很久 ✗）。
        // 2026-09-21 补上 —— 与 SettingsStore 那处是同一个坑。
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private readonly int _port;
    private readonly string _bind;
    // 配置读取入口：指向**当前发布版**（热更新是换引用，见 SettingsBox）——不要改成缓存实例。
    private AppSettings _settings => _box.Current;

    private readonly SettingsBox _box;
    private readonly PanelPasswordStore _panelPassword;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _panelSessions = new();
    private readonly object _loginGate = new();
    private readonly Dictionary<string, LoginFailureState> _loginFailuresByClient = new(StringComparer.Ordinal);
    private int _globalLoginFailures;
    private DateTimeOffset _globalLoginFailureWindowStarted;
    private DateTimeOffset _globalLoginBlockedUntil;
    private readonly OneBotGateway _gateway;

    /// <summary>上行通道（多通道部署时是聚合器）：面板只拿它当通道台账用（见 <see cref="BuildChannelStatus" />）。</summary>
    private readonly IQqChatSource _source;

    /// <summary>代发那一路仍然要它：会话 + 协议端 + 记账 + 存档都在里面（其余成员面板一律直连组件）。</summary>
    private readonly BotAgentHost _agent;
    private readonly LoginQrService _loginQr;
    private readonly SettingsHotReload _settingsHotReload;

    /// <summary>DTO 映射 + 显示层脱敏（散在各 handler 里的那几处都收在它里面，见 <see cref="PanelDto" />）。</summary>
    private readonly PanelDto _dto;

    /// <summary>面板事件聚合：实时推送与"日志/状态已变"的通知都从它来（面板直接订阅，不再经 BotAgentHost 转发）。</summary>
    private readonly PanelNotifier _ui;

    private readonly StickerService _stickers;
    private readonly MoodStore _mood;
    private readonly VoiceUseCase _voice;
    private readonly MusicUseCase _music;
    private readonly ResearchUseCase _research;
    private readonly ConversationRegistry _registry;
    private readonly MemberProfileStore _profiles;
    private readonly ISecretsRepository _secrets;
    private readonly ISettingsRepository _settingsRepo;
    private readonly BotIdentity _identity;
    private readonly BotScheduler _scheduler;
    private readonly ReplyPipeline _reply;
    private readonly ParticipationUseCase _participation;
    private readonly AgentCommandService _agentCmds;
    private readonly ITenantQuotaLedger _quotas;
    private readonly IJargonRepository _jargons;
    private readonly Domain.Plugins.IPluginRegistry? _plugins;
    private readonly BackgroundEvaluatorQueue? _background;
    private readonly DateTimeOffset _startedAt = Clock.Now;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _clientsGate = new();
    private readonly List<SseClient> _clients = new();

    private HttpListener? _listener;
    private Task? _loop;
    private int _convBroadcastPending;
    private long _convBroadcastVersion;
    private ZbaEndpoint? _zba;

    /// <summary>由组合根在管理面建好后挂上；ZBA 自带 Bearer 认证，不复用旧面板的 Cookie 会话。</summary>
    public void AttachZba(ZbaEndpoint endpoint) => _zba = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    public WebUiServer(
        int port,
        SettingsBox box,
        OneBotGateway gateway,
        IQqChatSource source,
        BotAgentHost agent,
        LoginQrService loginQr,
        IHttpFetcher modelProbeHttp,
        IHttpFetcher neteaseHttp,
        SettingsHotReload settingsHotReload,
        PanelNotifier ui,
        StickerService stickers,
        MoodStore mood,
        VoiceUseCase voice,
        MusicUseCase music,
        ResearchUseCase research,
        ConversationRegistry registry,
        MemberProfileStore profiles,
        ISecretsRepository secrets,
        ISettingsRepository settingsRepo,
        BotIdentity identity,
        BotScheduler scheduler,
        ReplyPipeline reply,
        ParticipationUseCase participation,
        AgentCommandService agentCmds,
        ITenantQuotaLedger quotas,
        HealthReportService? healthReports = null,
        SessionPolicyLedger? sessionPolicies = null,
        TurnTraceStore? traces = null,
        IHostFacts? hostFacts = null,
         IAuditChain? audit = null,
        ApprovalUseCase? approvals = null,
        LocalChannelSource? localChannel = null,
        Action? onRestart = null,
        Func<IReadOnlyList<Domain.Ops.CircuitStatusSnapshot>>? circuitStatusProvider = null,
        IPlatformRegistry? platformRegistry = null,
        Platforms.Feishu.FeishuBotGateway? feishuGateway = null,
         PlatformPolicyResolver? platformPolicies = null,
        IJargonRepository? jargons = null,
        Domain.Plugins.IPluginRegistry? plugins = null,
        BackgroundEvaluatorQueue? background = null)
    {
        _port = port;
        _box = box;
        _panelPassword = new PanelPasswordStore(Path.Combine(AppPaths.DataDir, "panel-password"));
        _gateway = gateway;
        _source = source;
        _agent = agent;
        _loginQr = loginQr;
        _modelProbeHttp = modelProbeHttp;
        _neteaseHttp = neteaseHttp;
        _settingsHotReload = settingsHotReload;
        _dto = new PanelDto(box, registry);
        _ui = ui;
        _stickers = stickers;
        _mood = mood;
        _voice = voice;
        _music = music;
        _research = research;
        _registry = registry;
        _profiles = profiles;
        _secrets = secrets;
        _settingsRepo = settingsRepo;
        _identity = identity;
        _scheduler = scheduler;
        _reply = reply;
        _participation = participation;
        _agentCmds = agentCmds;
        _quotas = quotas;
        _jargons = jargons ?? new JargonStore();
        _healthReports = healthReports;
        _sessionPolicies = sessionPolicies;
        _traces = traces;
        _traceArchive = traces?.Archive;
        _auditChain = audit;
        _hostFacts = hostFacts;
        _approvals = approvals;
        _localChannel = localChannel;
        _onRestart = onRestart;
        _circuitStatusProvider = circuitStatusProvider;
        _platformRegistry = platformRegistry;
        _feishuGateway = feishuGateway;
        _platformPolicies = platformPolicies;
        _plugins = plugins;
        _background = background;
        // 启动时把密钥库里那份 TTS 密钥重新写给 tts 容器（容器可能刚被重建、
        // 或者上次写文件前我们就重启了）——否则面板里存着 key，语音却发不出去。
        WriteTtsConfToHost();
        _bind = !_panelPassword.IsConfigured
            ? "127.0.0.1"
            : EnvironmentAliases.Get("BOTAGENT_HEALTH_BIND", "QQCHAT_HEALTH_BIND")?.Trim() is { Length: > 0 } custom
                ? custom
                : "+";
    }

    /// <summary>面板自己的两条出网：模型列表探测（20s）与自建网易云登录（15s）。
    /// socket 由装配点造（见 <see cref="IHttpFetcher" />），面板里不再自己 new。</summary>
    private readonly IHttpFetcher _modelProbeHttp;

    private readonly IHttpFetcher _neteaseHttp;

    /// <summary>服务器健康日报（管理员 2026-09-18：定时私聊推送；不经过外部设备 agent）。</summary>
    private readonly HealthReportService? _healthReports;

    /// <summary>会话级权限元数据（批次 B）：只读观测用；测试环境里可以是 null（那时面板只报 available=false）。</summary>
    private readonly SessionPolicyLedger? _sessionPolicies;

    /// <summary>决策轨迹（批次 C）：只读观测用；测试环境里可以是 null（那时 /api/traces 报 available=false）。</summary>
    private readonly TurnTraceStore? _traces;
    private readonly ITraceArchive? _traceArchive;
    private readonly IAuditChain? _auditChain;

    /// <summary>宿主事实（批次 J 的仪表盘要内存上限与负载）：只读端口，测试环境里可以是 null。</summary>
    private readonly IHostFacts? _hostFacts;

    /// <summary>审批用例（批次 I 的面板审批卡）：测试环境里可以是 null（那时端点报 available=false）。</summary>
    private readonly ApprovalUseCase? _approvals;

    /// <summary>本地通道（批次 F）：没开时为 null（那时两个端点都报 available=false / 403）。</summary>
    private readonly LocalChannelSource? _localChannel;

    /// <summary>
    /// 面板「一键重启」：改完需要重启才生效的设置（官方通道 appid/secret、容器级的挂载与端口…）
    /// 不用再开 SSH。实现是**退出进程**——容器带着 <c>restart: unless-stopped</c>，Docker 会把它拉起来；
    /// 这比从面板直接摸 docker.sock 重启容器安全得多（那种做法等于把 root 交给面板）。
    /// 为 null 时（例如集成测试里）只记一条日志，不真的退。
    /// </summary>
    private readonly Action? _onRestart;
    private readonly Func<IReadOnlyList<Domain.Ops.CircuitStatusSnapshot>>? _circuitStatusProvider;
    private readonly IPlatformRegistry? _platformRegistry;
    private readonly Platforms.Feishu.FeishuBotGateway? _feishuGateway;
    private readonly PlatformPolicyResolver? _platformPolicies;
    private readonly SemaphoreSlim _feishuWebhookSlots = new(16, 16);

    /// <summary>实际监听的前缀（启动失败为 null）。</summary>
    public string? ListeningOn { get; private set; }

    public void Start()
    {
        // A listener must never open before the unique configuration coordinator is bound.
        _ = _settingsHotReload.ConfigurationAuthorization;

        // 订阅面板事件（订阅写法一字未改：事件本体本来就在 PanelNotifier 上，以前只是 BotAgentHost 转发了一层）
        _ui.MessageAdded += OnMessageAdded;
        _ui.ConversationsChanged += OnConversationsChanged;
        _ui.StateChanged += OnStateChanged;
        _ui.ThinkingChanged += OnThinkingChanged;
        // 注意：**不订阅** _ui.LogLine —— 那一份日志已经由 FileLog.LineWritten 推了
        // （EmitLog → FileLog.Write）。两边都订就会让面板里每条 Agent 日志重两遍。
        // 面板日志页的数据源是 FileLog：它包含所有组件（Agent/OneBot/Voice/Store…）的行，
        // 而且整个进程只有这一个漏斗 —— 实时推流与历史回填用同一份文本，不会两套格式。
        FileLog.LineWritten += OnFileLogLine;

        if (!_panelPassword.IsConfigured)
        {
            var tokenConfigured = !string.IsNullOrWhiteSpace(_settings.PanelToken);
            FileLog.Warn("Web", tokenConfigured
                ? $"当前面板未设置密码；已配置面板令牌，API 将要求 X-Panel-Token，绑定限制为 127.0.0.1:{_port}。"
                : $"[安全警告] 当前面板未设置密码，已自动启用免密模式并强制限制绑定 127.0.0.1:{_port}（仅限本机访问）。如需开放公网，请在设置中配置面板密码（至少 8 位）。");
        }

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

            // ZBA 是独立的 Bearer 边界：不走旧面板的 Cookie/同源判定，也不继承旧面板的免密回环规则。
            if (_zba is not null && path.StartsWith("/zba/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await ZbaHttpListenerAdapter.HandleAsync(context, _zba, _zba.MaxBodyBytes, _cts.Token);
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch
                    {
                        // 客户端已断开
                    }
                }

                return;
            }

            var isFeishuWebhook = path.Equals("/api/webhooks/feishu", StringComparison.OrdinalIgnoreCase);
            var publicPath = isFeishuWebhook ||
                path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/readyz", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/metrics", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                (method == "GET" && path == "/favicon.ico");
            if (!publicPath && !(path.Equals("/api/auth/change-password", StringComparison.OrdinalIgnoreCase) && HasPendingSession(context)))
            {
                if (!IsAuthorized(context))
                {
                    await WriteJsonAsync(context, 401, new JsonObject
                    {
                        ["error"] = "unauthorized",
                        ["mustChangePassword"] = HasPendingSession(context)
                    });
                    context.Response.Close();
                    return;
                }
            }

            if (!isFeishuWebhook && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && !IsSameOrigin(context))
            {
                await WriteJsonAsync(context, 403, new JsonObject { ["error"] = "cross-origin request rejected" });
                context.Response.Close();
                return;
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

    private bool IsAuthorized(HttpListenerContext context)
    {
        // 免密只对“既没密码、也没配置令牌”的本机面板成立。
        // 配了令牌而没配密码时必须校验令牌，否则隧道把远端请求转成回环后就能绕过令牌（Issue #78）。
        if (!_panelPassword.IsConfigured)
            return string.IsNullOrWhiteSpace(_settings.PanelToken) || IsLegacyAuthorized(context);
        var now = Clock.Now;
        if (IsLegacyAuthorized(context)) return true;
        return TryGetValidPanelSession(context, now) && !_panelPassword.MustChange;
    }

    private bool IsLegacyAuthorized(HttpListenerContext context)
    {
        var expected = _settings.PanelToken?.Trim();
        var provided = context.Request.Headers["X-Panel-Token"] ?? context.Request.QueryString["token"];
        return !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(provided) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
    }

    private bool HasPendingSession(HttpListenerContext context)
    {
        return TryGetValidPanelSession(context, Clock.Now) && _panelPassword.MustChange;
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

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0)
        {
            return false;
        }

        var request = context.Request.Url;
        if (request is null || string.IsNullOrWhiteSpace(request.Host))
        {
            return false;
        }

        // A TLS-terminating reverse proxy is the only caller allowed to describe the
        // browser-facing scheme; trusting this header from a public peer would let any
        // client choose its own origin.  The panel is reachable through Caddy on the
        // same host, where the source address is loopback.
        var scheme = request.Scheme;
        var forwarded = context.Request.Headers["X-Forwarded-Proto"]?.Trim();
        if (context.Request.IsLocal &&
            (string.Equals(forwarded, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(forwarded, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            scheme = forwarded!;
        }

        if (!string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase) ||
            !SameHost(uri, request))
        {
            return false;
        }

        // HttpListener reports its own listening port when the Host header has no
        // port.  Behind Caddy that is the internal port, while the browser's origin
        // is the public default port for the forwarded scheme.
        var hostHeader = context.Request.Headers["Host"];
        var port = HostHeaderHasPort(hostHeader) || string.IsNullOrWhiteSpace(hostHeader)
            ? request.Port
            : DefaultPort(scheme);

        return uri.Port == port;
    }

    private static int DefaultPort(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    private static bool HostHeaderHasPort(string? host)
    {
        host = host?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (host[0] == '[')
        {
            var end = host.IndexOf(']');
            return end > 0 && end + 1 < host.Length && host[end + 1] == ':';
        }

        var colon = host.IndexOf(':');
        return colon > 0 && colon == host.LastIndexOf(':') && colon + 1 < host.Length;
    }

    private static bool SameHost(Uri origin, Uri request)
    {
        if (string.Equals(origin.IdnHost.TrimEnd('.'), request.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // localhost, 127.0.0.1 and [::1] are the same machine for a loopback panel,
        // but that alias never weakens the port or scheme checks above.
        return origin.IsLoopback && request.IsLoopback;
    }

    /// <summary>QQ 登录二维码信息（面板内扫码）。</summary>
    private async Task HandleQqLoginAsync(PanelRequest r)
    {
        var refresh = IsTruthy(r.Context.Request.QueryString["refresh"]);
        var snapshot = await _loginQr.SnapshotAsync(refresh, _cts.Token);
        await WriteJsonAsync(r.Context, 200, new JsonObject
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
    }

    /// <summary>二维码图片本体（&lt;img&gt; 不能带自定义头，所以路由也支持 ?token=）。</summary>
    private async Task HandleQqLoginSvgAsync(PanelRequest r)
    {
        var refresh = IsTruthy(r.Context.Request.QueryString["refresh"]);
        var snapshot = await _loginQr.SnapshotAsync(refresh, _cts.Token);
        var svg = snapshot.Ok ? _loginQr.Svg() : null;
        if (svg is null)
        {
            await WriteJsonAsync(r.Context, 503, new JsonObject
            {
                ["error"] = snapshot.Error ?? "暂无二维码",
                ["configured"] = snapshot.Configured
            });
            return;
        }

        // 图随二维码变，缓存会让用户扫到已失效的旧图
        r.Context.Response.Headers["Cache-Control"] = "no-store, must-revalidate";
        await WriteBytesAsync(r.Context, 200, "image/svg+xml; charset=utf-8", Encoding.UTF8.GetBytes(svg));
    }

    /// <summary>面板日志页首屏：最近的运行日志（内存环形缓冲，含启动时从日志文件回填的历史）。
    /// 以前日志只活在浏览器内存里 —— 一刷新页面就"被清空"，只有之后的新行（管理员反馈）。</summary>
    private async Task HandleLogsAsync(PanelRequest r)
    {
        var raw = r.Context.Request.QueryString["limit"];
        var limit = int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, 1, FileLog.RecentCapacity) : 300;
        var lines = new JsonArray();
        foreach (var (time, text) in FileLog.RecentLines(limit))
        {
            lines.Add(new JsonObject { ["time"] = time, ["text"] = text });
        }

        await WriteJsonAsync(r.Context, 200, new JsonObject { ["lines"] = lines });
    }

    /// <summary>参与状态（只读快照 + 当前生效的上限）。</summary>
    private async Task HandleParticipationAsync(PanelRequest r)
    {
        // 用例只给结构化字段与**原始 key**；显示层脱敏在这里做（口径与 BotAgentHost 时期一致：
        // 开关开着才遮、key 本身保持原样 —— 面板按钮还要拿它去调接口）。
        var (policy, rows) = _participation.Snapshot();
        var payload = new JsonObject
        {
            ["policy"] = policy,
            ["gating"] = "off（只观测：状态机不改“发不发”的判定）",
            ["tracked"] = rows.Count,
            ["sessions"] = new JsonArray(rows
                .Select(row => (JsonNode)new JsonObject
                {
                    ["key"] = _dto.Mask(row.Key),
                    ["state"] = row.State,
                    ["reason"] = row.Reason,
                    ["counters"] = row.Counters,
                    ["lastTransition"] = row.Age,
                }).ToArray()),
        };

        await WriteJsonAsync(r.Context, 200, payload);
    }

    /// <summary>AI 总开关（面板顶部那个按钮）。</summary>
    private async Task HandleAiModeAsync(PanelRequest r)
    {
        var body = await ReadJsonAsync(r.Context);
        var enabled = body?["enabled"]?.GetValue<bool>() ?? !_settings.AiModeEnabled;

        // 写路径与 BotAgentHost 时期逐字一致：**值没变就什么都不做** → 走发布点换引用（不是就地改共享对象，
        // review-findings #4）→ 记一条用户看得懂的日志 → 通知面板刷新。
        if (_settings.AiModeEnabled != enabled)
        {
            _box.Apply(s => s.AiModeEnabled = enabled);
            _ui.EmitLog(enabled ? "AI 模式已开启" : "AI 模式已关闭");
            _ui.NotifyStateChanged();
        }

        await WriteJsonAsync(r.Context, 200, new JsonObject { ["aiMode"] = _settings.AiModeEnabled });
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

        SseClient client;
        lock (_loginGate)
        {
            client = new SseClient(response.OutputStream, SseAuthorization(context), _loginGate);
            lock (_clientsGate) _clients.Add(client);
        }

        try
        {
            // 首帧：完整状态，前端据此渲染
            await client.SendAsync("state", BuildState().ToJsonString(Json));

            // 心跳，避免代理/浏览器掐断空闲连接
            var ticks = 0;
            while (!ct.IsCancellationRequested && client.IsAuthorized)
            {
                await Task.Delay(1000, ct);
                if (++ticks % 15 == 0) await client.SendCommentAsync("ping");
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
            client.Dispose();

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
                    client.Dispose();
                }
            }, TaskScheduler.Default);
        }
    }

    private void OnMessageAdded(string key, ChatMessage message)
        => Broadcast("message", new JsonObject
        {
            ["key"] = key,
            ["message"] = PanelDto.Message(message)
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
        var conversation = _registry.Find(key);
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

    // ══════════════ 基础 IO ══════════════

    // ══════════════ 表情包库 ══════════════

    private static bool IsTruthy(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.Equals("1", StringComparison.Ordinal) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, JsonNode payload)
        => await WriteBytesAsync(context, status, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(payload.ToJsonString(Json)));

    private static async Task WriteBytesAsync(HttpListenerContext context, int status, string contentType, byte[] bytes)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;

        // 网站传输性能优化：支持 GZip 压缩 (传输体积锐减 70% 以上)
        if (bytes.Length > 512 && ShouldGzip(context.Request, contentType))
        {
            context.Response.Headers["Content-Encoding"] = "gzip";
            using var ms = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(bytes, 0, bytes.Length);
            }
            var compressed = ms.ToArray();
            context.Response.ContentLength64 = compressed.Length;
            if (compressed.Length > 0)
            {
                await context.Response.OutputStream.WriteAsync(compressed);
            }
            return;
        }

        context.Response.ContentLength64 = bytes.Length;
        if (bytes.Length > 0)
        {
            await context.Response.OutputStream.WriteAsync(bytes);
        }
    }

    private static bool ShouldGzip(HttpListenerRequest request, string contentType)
    {
        var accept = request.Headers["Accept-Encoding"];
        if (string.IsNullOrEmpty(accept) || !accept.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            return false;

        return contentType.StartsWith("application/javascript", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("text/css", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
               contentType.StartsWith("image/svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════ 服务器健康日报（定时私聊推送） ══════════════

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
        _ui.MessageAdded -= OnMessageAdded;
        _ui.ConversationsChanged -= OnConversationsChanged;
        _ui.StateChanged -= OnStateChanged;
        _ui.ThinkingChanged -= OnThinkingChanged;
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

        _feishuWebhookSlots.Dispose();
        _ = _loop;
    }

}

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Services.Ports;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Local;
using BotAgent.Services.Settings;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Plugins.WebUi;

/// <summary>
/// Web 控制台独立管理插件：以原生 HttpListener 提供全参数读写、诊断看板与静态页面。
/// </summary>
public sealed class WebUiPlugin : IBotPlugin
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private IPluginContext? _context;
    private SettingsBox? _settingsBox;
    private SettingsStore? _settingsStore;
    private ConversationRegistry? _registry;
    private IQqMessageSender? _plainSender;
    private IPluginRegistry? _pluginRegistry;
    private IDiagnosticEventBus? _diagnostics;
    private ISecretsRepository? _secrets;
    private LocalChannelSource? _localChannel;
    private SettingsHotReload? _hotReload;
    private IPlatformRegistry? _platformRegistry;
    private IHostFacts? _hostFacts;
    private OneBotGateway? _gateway;
    private int _port = 18245;

    /// <summary>
    /// 面板鉴权：口令校验、会话签发、失败限流。
    /// 没有它，/api/* 对任何能连上端口的人全开放（读全部配置、改全部配置、代发消息）。
    /// </summary>
    private PanelSessionStore? _auth;

    /// <summary>
    /// 日志增量游标：只读游标之后的新增字节来补算总行数，再反向定位尾部若干行。
    /// 整文件扫描的代价随日志增长线性上升，2.5 秒一次的轮询撑不住。
    /// </summary>
    private readonly object _logCursorLock = new();
    private long _logCursorBytes;
    private long _logCursorLines;

    public string Id => "preset.management.webui";
    public string Name => "Web 控制台与全参数管理面板";
    public string Version => "1.0.0";
    public string Description => "基于原生 HTTP 服务的全参数管理控制台、插件健康诊断分析与本地演练场。";
    public PluginCategory Category => PluginCategory.Management;
    public bool IsPreset => true;

    public Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        _settingsBox = context.GetService<SettingsBox>();
        _settingsStore = context.GetService<SettingsStore>();
        _registry = context.GetService<ConversationRegistry>();
        _plainSender = context.GetService<IQqMessageSender>();
        _pluginRegistry = context.GetService<IPluginRegistry>();
        _diagnostics = context.GetService<IDiagnosticEventBus>();
        _secrets = context.GetService<ISecretsRepository>();
        _localChannel = context.GetService<LocalChannelSource>();
        _hotReload = context.GetService<SettingsHotReload>();
        _platformRegistry = context.GetService<IPlatformRegistry>();
        _hostFacts = context.GetService<IHostFacts>();
        _gateway = context.GetService<OneBotGateway>();

        // 端口读取优先级：环境变量 HEALTH_PORT / QQCHAT_HEALTH_PORT -> 18245
        var envPort = Environment.GetEnvironmentVariable("HEALTH_PORT")
                      ?? Environment.GetEnvironmentVariable("QQCHAT_HEALTH_PORT")
                      ?? Environment.GetEnvironmentVariable("BOTAGENT_HEALTH_PORT");
        if (int.TryParse(envPort, out var p) && p > 0)
        {
            _port = p;
        }

        // 注册版本化能力探针
        context.TryRegisterCapability(
            new CapabilityDescriptor("management.webui", "全参数 Web 管理控制台", 1, 1, "Management"),
            out _);

        // 本地通道探针：面板据此决定是否渲染「演练沙箱」入口。
        // 没有本地通道就没有注入能力，那个页签不该出现 —— 入口不能靠前端写死。
        if (_localChannel != null)
        {
            context.TryRegisterCapability(
                new CapabilityDescriptor("local.channel", "本地通道端到端注入", 1, 1, "Channel"),
                out _);
        }

        // 面板鉴权：口令来自环境变量 BOTAGENT_PANEL_TOKEN / QQCHAT_PANEL_TOKEN。
        // 都没配时自动生成一个随机口令并落到 data/panel-credential.txt（权限 600）：
        // 绝不因为"忘了配环境变量"而让面板裸奔。
        var envToken = Environment.GetEnvironmentVariable("BOTAGENT_PANEL_TOKEN")
            ?? Environment.GetEnvironmentVariable("QQCHAT_PANEL_TOKEN");

        var credentialFile = Path.Combine(AppPaths.DataDir, "panel-credential.txt");
        var reusedFromFile = false;
        if (string.IsNullOrWhiteSpace(envToken) && File.Exists(credentialFile))
        {
            // 上一轮生成的口令还在文件里：沿用它，否则用户手里的口令会莫名其妙失效
            try
            {
                // 与写入侧口径一致：不带 BOM 的 UTF8。
                // ReadAllText 自身也会跳过 BOM，这里显式声明是为了读写两侧对称，
                // 避免日后一侧改动导致静默分歧。
                var saved = File.ReadAllText(credentialFile, new UTF8Encoding(false)).Trim();
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    envToken = saved;
                    reusedFromFile = true;
                }
            }
            catch (Exception ex)
            {
                _context?.Log($"[!] 面板口令文件读取失败: {ex.Message}（将重新生成）");
            }
        }

        _auth = new PanelSessionStore(envToken, msg => _context?.Log(msg));
        if (_auth.PasswordAutoGenerated && !reusedFromFile)
        {
            PersistPanelCredential(credentialFile);
        }

        StartServer();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 自动生成的口令落到 data/panel-credential.txt（仅本用户可读）。
    /// 环境变量给了口令、或文件里已有口令时都不写 —— 那种情况下口令另有出处，不需要副本。
    /// </summary>
    private void PersistPanelCredential(string path)
    {
        if (_auth == null)
        {
            return;
        }

        try
        {
            // 必须用不带 BOM 的 UTF8：带 BOM 写出去后，用户 cat 复制到的口令首部会多一个
            // U+FEFF —— 肉眼看不见，但 SHA-256 不同，登录永远提示「口令不正确」。
            // 末尾补一个换行：cat 时口令与下一条提示符分行，便于选区复制。
            File.WriteAllText(path, _auth.Password + "\n", new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    // 只给自己读写：同机其它用户看不到口令
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // 文件系统不支持时忽略：文件已落盘，至少能找回
                }
            }

            _context?.Log($"[Web] 未配置面板口令，已自动生成并写入 {path}（首次登录用，可随时改环境变量覆盖）");
        }
        catch (Exception ex)
        {
            _context?.Log($"[!] 面板口令文件写入失败: {ex.Message}");
        }
    }

    private void StartServer()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();

            _cts = new CancellationTokenSource();
            _context?.Log($"Web 控制台已启动，监听地址: http://127.0.0.1:{_port}/");

            _ = Task.Run(() => ListenLoopAsync(_listener, _cts.Token));
        }
        catch (Exception ex)
        {
            _context?.Log($"[!] Web 控制台启动失败: {ex.Message}");
        }
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx));
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                _context?.Log($"[Web] 处理请求循环异常: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";

        // 安全响应头：禁止 MIME 嗅探、禁止被嵌入 iframe、不外泄 Referer。
        // 原来的通配 CORS 已去掉 —— 面板是同源访问，不需要跨域。
        resp.Headers["X-Content-Type-Options"] = "nosniff";
        resp.Headers["X-Frame-Options"] = "DENY";
        resp.Headers["Referrer-Policy"] = "no-referrer";

        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        try
        {
            // 免鉴权白名单：登录端点 + 健康探针。
            // 健康探针必须免鉴权，否则容器 HEALTHCHECK 会因 401 判定进程已死。
            if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
                || path == "/healthz"
                || path == "/readyz")
            {
                await HandleAuthAsync(ctx, path);
                return;
            }

            if (!IsAuthenticated(req))
            {
                if (path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    // 接口未登录：明确告诉前端"要登录"，前端据此弹出登录卡片
                    await WriteJsonAsync(resp, 401, new { ok = false, unauthenticated = true, error = "未登录或会话已失效" });
                }
                else
                {
                    // 页面与静态资源照常返回，由前端检测到"无会话"后清 Cookie 并弹卡片，
                    // **不做页面跳转** —— 用户要求登录卡片是内嵌的。
                    await HandleStaticFileAsync(ctx, path);
                }
                return;
            }

            if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                await HandleApiAsync(ctx, path);
            }
            else
            {
                await HandleStaticFileAsync(ctx, path);
            }
        }
        catch (Exception ex)
        {
            _context?.Log($"[Web] API 路由异常 ({path}): {ex.Message}");
            await WriteJsonAsync(resp, 500, new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// 从 Cookie 头里取会话标识并校验。
    /// 只认 HttpOnly 会话 Cookie —— 不放行任何"URL 带令牌"的写法，
    /// 那种写法会把口令漏进浏览器历史、反向代理日志与 Referer。
    /// </summary>
    private bool IsAuthenticated(HttpListenerRequest req)
        => _auth != null && _auth.IsValid(ReadSessionId(req));

    /// <summary>取当前请求的会话标识（登出用）。</summary>
    private static string? ReadSessionId(HttpListenerRequest req)
    {
        var cookieHeader = req.Headers["Cookie"];
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        foreach (var part in cookieHeader.Split(';'))
        {
            var kv = part.Trim();
            var eq = kv.IndexOf('=');
            if (eq > 0 && kv[..eq].Trim() == PanelSessionStore.CookieName)
            {
                return kv[(eq + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// 登录失败限流的来源键：优先 X-Forwarded-For 首段（面板常挂反向代理），否则用直连地址。
    /// </summary>
    private static string ClientKey(HttpListenerRequest req)
    {
        var forwarded = req.Headers["X-Forwarded-For"];
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }

        return req.RemoteEndPoint?.Address.ToString() ?? "unknown";
    }

    /// <summary>
    /// 鉴权端点：登录 / 会话探测 / 登出 / 健康探针。
    /// 这几个是"还没登录"时唯一能用的接口，必须在统一鉴权之前处理。
    /// </summary>
    private async Task HandleAuthAsync(HttpListenerContext ctx, string path)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        // 健康探针：容器 HEALTHCHECK 用，不涉密
        if (path == "/healthz" && req.HttpMethod == "GET")
        {
            await WriteJsonAsync(resp, 200, new { status = "ok" });
            return;
        }

        if (path == "/readyz" && req.HttpMethod == "GET")
        {
            var connected = _gateway?.IsConnected ?? false;
            await WriteJsonAsync(resp, connected ? 200 : 503, new { status = connected ? "ready" : "not-ready" });
            return;
        }

        // 会话探测：前端进站第一件事就问它。authenticated=true 才关掉登录卡片。
        if (path == "/api/auth/status" && req.HttpMethod == "GET")
        {
            await WriteJsonAsync(resp, 200, new { ok = true, authenticated = IsAuthenticated(req) });
            return;
        }

        // 登出：撤销服务端会话，并让浏览器立刻丢弃 Cookie
        if (path == "/api/auth/logout" && req.HttpMethod == "POST")
        {
            _auth?.Revoke(ReadSessionId(req));
            resp.Headers["Set-Cookie"] =
                $"{PanelSessionStore.CookieName}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict";
            await WriteJsonAsync(resp, 200, new { ok = true });
            return;
        }

        if (path != "/api/auth/login" || req.HttpMethod != "POST")
        {
            await WriteJsonAsync(resp, 404, new { ok = false, error = "Not Found" });
            return;
        }

        if (_auth == null)
        {
            await WriteJsonAsync(resp, 500, new { ok = false, error = "鉴权未初始化" });
            return;
        }

        var client = ClientKey(req);
        if (_auth.IsRateLimited(client))
        {
            _context?.Log($"[Web] 登录尝试过于频繁，已暂时拒绝: {client}");
            await WriteJsonAsync(resp, 429, new { ok = false, error = "尝试过于频繁，请稍后再试" });
            return;
        }

        string? candidate = null;
        try
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            var node = JsonNode.Parse(json);
            // 字段名用短键 "pw"：不把口令字样写进请求体键名，也避免日志/代理把它当敏感字段名抓取
            candidate = node?["pw"]?.GetValue<string>();
        }
        catch
        {
            // 载荷不是 JSON 就按"口令为空"处理，走下面的失败分支
        }

        if (!_auth.VerifyPassword(candidate))
        {
            var count = _auth.RecordFailure(client);
            _context?.Log($"[Web] 面板登录失败（来源 {client}，本窗口第 {count} 次）");
            await WriteJsonAsync(resp, 401, new { ok = false, error = "口令不正确" });
            return;
        }

        _auth.ClearFailures(client);
        var sid = _auth.CreateSession();

        // HttpOnly：JS 读不到，XSS 偷不走；SameSite=Strict：跨站请求不携带。
        // 不加 Secure —— 面板常跑在 http 内网，加了 Secure 浏览器会直接丢弃该 Cookie。
        resp.Headers["Set-Cookie"] =
            $"{PanelSessionStore.CookieName}={sid}; Path=/; Max-Age=43200; HttpOnly; SameSite=Strict";

        _context?.Log($"[Web] 面板登录成功（来源 {client}）");
        await WriteJsonAsync(resp, 200, new { ok = true });
    }

    private async Task HandleApiAsync(HttpListenerContext ctx, string path)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        // 1. 系统状态概览
        if (path == "/api/status" && req.HttpMethod == "GET")
        {
            var settings = _settingsBox?.Current;
            var plugins = _pluginRegistry?.GetAll();
            var sessions = _registry?.Snapshot();

            var data = new
            {
                uptime_seconds = (int)(Environment.TickCount64 / 1000),
                memory_mb = (int)(GC.GetTotalMemory(false) / (1024 * 1024)),
                memory_limit_mb = _hostFacts?.MemoryLimitBytes() is long l ? (int)(l / (1024 * 1024)) : (int?)null,
                load_average = _hostFacts?.LoadAverage(),
                active_sessions = sessions?.Count ?? 0,
                loaded_plugins = plugins?.Count ?? 0,
                model = settings?.Model ?? "--",
                channels = BuildChannelFacts(settings)
            };
            await WriteJsonAsync(resp, 200, data);
            return;
        }

        // 2. 全量参数读取与保存
        if (path == "/api/settings" && req.HttpMethod == "GET")
        {
            var settings = _settingsBox?.Current;
            var data = BuildSettingsPayload(settings);
            await WriteJsonAsync(resp, 200, new { ok = true, data });
            return;
        }

        if (path == "/api/settings" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            // 大小写不敏感：前端回传的是驼峰字段，直接按大小写敏感解析会让未编辑的字段回落到默认值
            var updated = JsonSerializer.Deserialize<AppSettings>(json, RequestJsonOptions);
            if (updated == null || _settingsBox == null)
            {
                await WriteJsonAsync(resp, 400, new { ok = false, error = "无效的配置载荷" });
                return;
            }

            // 必须走 SettingsHotReload：它除了落盘，还会重建白名单/参与上限/并发闸门/
            // 会话窗口/定时器这些派生状态。绕过它就会出现"保存成功但什么都没变"。
            if (_hotReload != null)
            {
                _hotReload.ApplyRuntimeSettings(target =>
                {
                    MergePersistable(target, updated);

                    // 面板保存过 = 用户的最新意图。把端点与模型名标成「面板覆盖」状态，
                    // 否则下次启动时 .env / start.sh 里的种子值会把它们盖回去 ——
                    // 症状就是「面板改成 deepseek，一重启又变回 openai 默认值」。
                    if (!string.IsNullOrWhiteSpace(updated.ModelBaseUrl))
                    {
                        target.ModelBaseUrlOverride = updated.ModelBaseUrl.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(updated.Model))
                    {
                        target.ModelOverride = updated.Model.Trim();
                    }
                });
                _context?.Log("[Web] 全局参数已通过控制台更新并触发全量热重载。");
                await WriteJsonAsync(resp, 200, new { ok = true, degraded = false });
            }
            else
            {
                _settingsBox.ApplyPersisted(
                    target => MergePersistable(target, updated),
                    next => _settingsStore?.Save(next));
                _context?.Log("[Web] 全局参数已保存（热重载通道不可用，仅部分生效）。");
                await WriteJsonAsync(resp, 200, new { ok = true, degraded = true });
            }
            return;
        }

        // 2.1 密钥独立写入（不进 settings、不落 settings.json）
        if (path == "/api/settings/secrets" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            var node = JsonNode.Parse(json);
            var name = node?["name"]?.GetValue<string>() ?? "";
            var value = node?["value"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(name) || _secrets == null)
            {
                await WriteJsonAsync(resp, 400, new { ok = false, error = "无效的密钥名称" });
                return;
            }

            var saved = name switch
            {
                "apiKey" => _secrets.SaveApiKey(value),
                "officialAppSecret" => _secrets.SaveOfficialSecret(value),
                "feishuAppSecret" => _secrets.SaveFeishuSecret(value),
                "feishuEncryptKey" => _secrets.SaveFeishuEncryptKey(value),
                "neteaseCookie" => _secrets.SaveNeteaseCookie(value),
                "ttsKey" => _secrets.SaveTtsKey(value),
                _ => false
            };

            if (saved)
            {
                // 密钥在 secrets 表里，不进 settings.json；但内存里的覆盖值要同步，
                // 否则本轮运行仍用旧密钥，得重启才生效。
                // 注意：ttsKey 由 VoiceService 每次直读 secrets 表，AppSettings 上没有对应字段，跳过。
                if (_hotReload != null && !string.IsNullOrWhiteSpace(value))
                {
                    _hotReload.PatchSettings(s =>
                    {
                        switch (name)
                        {
                            case "apiKey": s.ApiKeyOverride = value; break;
                            case "officialAppSecret": s.OfficialAppSecret = value; break;
                            case "feishuAppSecret": s.FeishuAppSecret = value; break;
                            case "feishuEncryptKey": s.FeishuEncryptKey = value; break;
                            case "neteaseCookie": s.NeteaseCookie = value; break;
                        }
                    });
                }

                _context?.Log($"[Web] 密钥已更新: {name} -> {(string.IsNullOrWhiteSpace(value) ? "已清除" : "已配置")}");
                await WriteJsonAsync(resp, 200, new { ok = true });
            }
            else
            {
                await WriteJsonAsync(resp, 400, new { ok = false, error = "密钥写入失败或名称不受支持" });
            }
            return;
        }

        // 2.1 配置分组清单（动态）：只返回**贡献者插件当前启用**的分组 + 核心内置分组。
        // 插件被禁用或不存在时，它那一组不会出现在这里，前端也就不会渲染 —— 面板只挂真实存在的功能。
        if (path == "/api/settings/schema" && req.HttpMethod == "GET")
        {
            var enabledPlugins = (_pluginRegistry?.GetAll() ?? Array.Empty<PluginInfo>())
                .Where(p => p.IsEnabled)
                .Select(p => p.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pluginSections = (_context?.GetSettingsSections() ?? Array.Empty<SettingsSection>())
                .Where(s => enabledPlugins.Contains(s.PluginId))
                .Select(s => new
                {
                    section_id = s.SectionId,
                    title = s.Title,
                    plugin_id = s.PluginId,
                    source = "plugin",
                    fields = s.Fields.Select(f => new
                    {
                        key = f.Key,
                        label = f.Label,
                        type = f.Type,
                        desc = f.Desc,
                        step = f.Step,
                        options = f.Options,
                        secret_name = f.SecretName,
                        secret_flag = f.SecretFlag
                    })
                })
                .ToList();

            await WriteJsonAsync(resp, 200, new
            {
                ok = true,
                sections = CoreSettingsSections().Concat(pluginSections).ToList()
            });
            return;
        }

        // 2.2 能力探针清单：面板据此决定渲染哪些页签/入口
        if (path == "/api/capabilities" && req.HttpMethod == "GET")
        {
            var caps = (_context?.GetCapabilities() ?? Array.Empty<CapabilityDescriptor>())
                .Select(c => new
                {
                    capability_id = c.CapabilityId,
                    display_name = c.DisplayName,
                    contract_version = c.ContractVersion,
                    min_ui_version = c.MinSupportedUiVersion,
                    category = c.Category,
                    is_available = c.IsAvailable
                })
                .ToList();
            await WriteJsonAsync(resp, 200, new { ok = true, items = caps });
            return;
        }

        // 3. 插件清单
        if (path == "/api/plugins" && req.HttpMethod == "GET")
        {
            var list = _pluginRegistry?.GetAll();
            await WriteJsonAsync(resp, 200, new { ok = true, items = list });
            return;
        }

        // 3.1 切换插件状态
        if (path == "/api/plugins/toggle" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            var node = JsonNode.Parse(json);
            var id = node?["id"]?.GetValue<string>() ?? "";
            var enabled = node?["enabled"]?.GetValue<bool>() ?? true;

            if (!string.IsNullOrEmpty(id) && _pluginRegistry != null)
            {
                // TrySetEnabled 会区分「已落配置」「仅内存」「服务端硬拒绝」三种结果，
                // 面板据此把回执如实展示给用户，不再一律回 ok=true。
                var result = _pluginRegistry.TrySetEnabled(id, enabled);
                if (result.Ok)
                {
                    _context?.Log($"[Web] 插件状态更新: {id} -> {(enabled ? "已启用" : "已禁用")}"
                        + (result.Persisted ? "（已落配置）" : "（仅内存，重启失效）"));
                }
                else
                {
                    _context?.Log($"[Web] 插件状态更新被拒绝: {id} -> {result.Error}");
                }

                await WriteJsonAsync(resp, 200, new
                {
                    ok = result.Ok,
                    persisted = result.Persisted,
                    rejected = result.Rejected,
                    error = result.Error
                });
                return;
            }
            await WriteJsonAsync(resp, 400, new { ok = false, persisted = false, rejected = false, error = "无效的插件 ID" });
            return;
        }

        // 4. 插件诊断报告
        if (path == "/api/plugins/diagnostics" && req.HttpMethod == "GET")
        {
            var reports = _diagnostics?.GetReports();
            await WriteJsonAsync(resp, 200, new { ok = true, reports = reports });
            return;
        }

        // 5. 会话列表
        if (path == "/api/conversations" && req.HttpMethod == "GET")
        {
            var sessions = _registry?.Snapshot();
            var items = sessions?.Select(s => new
            {
                key = s.SourceKey,
                title = string.IsNullOrEmpty(s.Name) ? s.SourceKey : s.Name,
                message_count = s.MessageCount
            });
            await WriteJsonAsync(resp, 200, new { ok = true, items = items });
            return;
        }

        // 6. 会话历史消息
        if (path == "/api/conversations/messages" && req.HttpMethod == "GET")
        {
            var key = req.QueryString["key"] ?? "";
            var conv = _registry?.Find(key);
            var messages = conv?.Messages.TakeLast(30).Select(m => new
            {
                sender = m.SenderName,
                text = m.Text,
                time = m.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                isBot = m.Role == MessageRole.Self
            });
            await WriteJsonAsync(resp, 200, new { ok = true, messages = messages });
            return;
        }

        // 7. 代发消息
        if (path == "/api/messages/send" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            var node = JsonNode.Parse(json);
            var key = node?["key"]?.GetValue<string>() ?? "";
            var text = node?["text"]?.GetValue<string>() ?? "";

            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(text) && _plainSender != null)
            {
                var conv = _registry?.Find(key);
                if (conv != null)
                {
                    await _plainSender.SendPlainAsync(conv, text);
                    _context?.Log($"[Web] 代发消息至 {key}: {text}");
                    await WriteJsonAsync(resp, 200, new { ok = true });
                    return;
                }
            }
            await WriteJsonAsync(resp, 400, new { ok = false, error = "无效的会话 key 或目标" });
            return;
        }

        // 8. 演练场注入（走本地通道真实链路）
        if (path == "/api/playground/inject" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            var node = JsonNode.Parse(json);
            var text = node?["text"]?.GetValue<string>() ?? "";
            var sender = node?["sender"]?.GetValue<string>() ?? "演练测试员";
            var mode = node?["mode"]?.GetValue<string>() ?? "at";

            if (string.IsNullOrWhiteSpace(text))
            {
                await WriteJsonAsync(resp, 400, new { ok = false, error = "演练内容不能为空" });
                return;
            }

            if (_localChannel == null)
            {
                await WriteJsonAsync(resp, 400, new { ok = false, error = "本地通道未启用：请在参数设置中配置本地通道名单后再试" });
                return;
            }

            var configuredId = 1L;
            var settings = _settingsBox?.Current;
            var rawIds = (settings?.LocalChannelIds ?? string.Empty)
                .Split(new[] { ',', ' ', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawIds.Length > 0 && long.TryParse(rawIds[0].Trim(), out var firstId))
            {
                configuredId = firstId;
            }

            var internalId = Channels.LocalTarget(configuredId);
            if (internalId == 0)
            {
                await WriteJsonAsync(resp, 400, new { ok = false, error = "本地通道配置的 id 超出允许范围" });
                return;
            }

            var outboxBefore = _localChannel.Outbox.Count;
            var started = DateTimeOffset.UtcNow;

            _localChannel.Inject(
                isGroup: true,
                id: internalId,
                senderName: sender,
                text: text,
                mentioned: string.Equals(mode, "at", StringComparison.OrdinalIgnoreCase));

            // 等待回复链把结果写进出箱（最多 30 秒，每 200ms 探一次）
            string? replyText = null;
            for (var i = 0; i < 150; i++)
            {
                await Task.Delay(200);
                var outbox = _localChannel.Outbox;
                if (outbox.Count > outboxBefore)
                {
                    replyText = outbox[outbox.Count - 1].Text;
                    break;
                }
            }

            var elapsed = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            var key = Channels.Key(Channels.Local, true, internalId);

            if (replyText == null)
            {
                await WriteJsonAsync(resp, 200, new
                {
                    ok = true,
                    elapsed_ms = elapsed,
                    decision = "模型选择静默或未在时限内产生回复",
                    reply = "",
                    trace = new { step = "NoOutbound", source_key = key }
                });
                return;
            }

            await WriteJsonAsync(resp, 200, new
            {
                ok = true,
                elapsed_ms = elapsed,
                decision = "回复已产生",
                reply = replyText,
                trace = new { step = "OutboxDelivered", source_key = key }
            });
            return;
        }

        // 9. 实时日志
        if (path == "/api/logs" && req.HttpMethod == "GET")
        {
            var take = 100;
            if (int.TryParse(req.QueryString["lines"], out var requested))
            {
                take = Math.Clamp(requested, 1, 500);
            }

            var logPath = Path.Combine(AppPaths.RuntimeRoot, "logs", "bot-agent.log");
            var (lines, totalLines) = ReadLogTail(logPath, take);
            // totalLines 让前端能判断日志文件是否被清空/截断，从而重置滚动游标
            await WriteJsonAsync(resp, 200, new { ok = true, lines = lines, totalLines = totalLines });
            return;
        }

        await WriteJsonAsync(resp, 404, new { ok = false, error = "Not Found" });
    }

    /// <summary>
    /// 前端静态资源根目录。
    /// 首选插件构建产物旁的 wwwroot：它随插件一起发布、被版本库跟踪、可重现；
    /// 回退 runtime/webroot 只为兼容旧部署 —— 那个目录不在版本控制内，随时可能被清理掉。
    /// </summary>
    private static string ResolveWebRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(WebUiPlugin).Assembly.Location);
        if (!string.IsNullOrEmpty(asmDir))
        {
            var bundled = Path.GetFullPath(Path.Combine(asmDir, "wwwroot"));
            if (Directory.Exists(bundled))
            {
                return bundled;
            }
        }

        return Path.GetFullPath(Path.Combine(AppPaths.RuntimeRoot, "webroot"));
    }

    private static async Task HandleStaticFileAsync(HttpListenerContext ctx, string path)
    {
        var resp = ctx.Response;
        var rootDir = ResolveWebRoot();
        var requested = path == "/" ? "index.html" : path.TrimStart('/');
        var filePath = Path.GetFullPath(Path.Combine(rootDir, requested));

        // 路径穿越防护：归一化后必须仍在 webroot 目录之内
        if (!filePath.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(filePath, rootDir, StringComparison.Ordinal))
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        if (!File.Exists(filePath))
        {
            resp.StatusCode = 404;
            resp.Close();
            return;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        resp.ContentType = ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            _ => "application/octet-stream"
        };

        var bytes = await File.ReadAllBytesAsync(filePath);
        // 前端资源必须每次回源校验：面板一更新，用户浏览器里的旧 app.js 会让新字段/新逻辑全部失效，
        // 表现就是「改了没反应、保存不了」。no-cache = 可以缓存但每次都要验证。
        resp.Headers["Cache-Control"] = "no-cache, must-revalidate";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    /// <summary>
    /// 构造面板可读的配置载荷：公开字段全量返回，密钥类字段只回显「是否已配置」掩码。
    /// </summary>
    private object? BuildSettingsPayload(AppSettings? settings)
    {
        if (settings == null)
        {
            return null;
        }

        var node = JsonSerializer.SerializeToNode(settings, JsonOptions) as JsonObject ?? new JsonObject();

        node["_secretApiKeyConfigured"] = !string.IsNullOrWhiteSpace(_secrets?.LoadApiKey());
        node["_secretOfficialConfigured"] = !string.IsNullOrWhiteSpace(_secrets?.LoadOfficialSecret());
        node["_secretFeishuConfigured"] = !string.IsNullOrWhiteSpace(_secrets?.LoadFeishuSecret());
        node["_secretFeishuEncryptConfigured"] = !string.IsNullOrWhiteSpace(_secrets?.LoadFeishuEncryptKey());
        node["_secretNeteaseConfigured"] = !string.IsNullOrWhiteSpace(_secrets?.LoadNeteaseCookie());
        node["_secretTtsConfigured"] = !string.IsNullOrWhiteSpace(_secrets?.LoadTtsKey());

        return node;
    }

    /// <summary>
    /// 只把可持久化的公开字段合并进目标配置；标注 JsonIgnore 的密钥与派生属性一律跳过，
    /// 避免「保存一次就把 secrets 表里的密钥覆盖成空」。
    /// </summary>
    private static void MergePersistable(AppSettings target, AppSettings source)
    {
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (prop.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
            {
                continue;
            }

            var value = prop.GetValue(source);
            if (value == null)
            {
                continue;
            }

            prop.SetValue(target, value);
        }
    }

    /// <summary>
    /// 构造通道事实：**只列已连接的协议端**。
    ///
    /// 为什么不列未连接/未配置的：这张表是「现在有哪些路是通的」，
    /// 把「没有这个通道」和「有但连不上」混在同一张表里，看的人分不清是没配还是掉线。
    /// 没连上时这张表就是空的，面板会显示「暂无已连接的协议端」。
    /// </summary>
    private object[] BuildChannelFacts(AppSettings? settings)
    {
        var list = new List<object>();

        var snapshots = _platformRegistry?.GetSnapshots();
        if (snapshots != null)
        {
            foreach (var s in snapshots)
            {
                if (!s.Connected)
                {
                    continue;
                }

                list.Add(new
                {
                    platform_id = s.PlatformId,
                    name = s.DisplayName,
                    enabled = s.Enabled,
                    connected = s.Connected,
                    last_error_code = s.LastErrorCode,
                    address = s.Tag
                });
            }
        }

        // 私域通道若未出现在快照里（理论上不会），用网关直连状态兜底 —— 同样只收在线的
        if (list.Count == 0 && (_gateway?.IsConnected ?? false))
        {
            list.Add(new
            {
                platform_id = "qq-private",
                name = "QQ 私域 OneBot 通道",
                enabled = true,
                connected = true,
                last_error_code = (string?)null,
                address = settings?.OneBotAddress ?? ""
            });
        }

        return list.ToArray();
    }

    /// <summary>
    /// 核心内置配置分组：这些字段属于无头核心本体（模型、通道、人设、名单、限流、记忆窗口、审批），
    /// 不依赖任何插件，因此无插件时也在。插件供给的分组由 /api/settings/schema 追加在其后。
    ///
    /// 命名一律不带序号：面板是"可热拔插模块"的容器，不是章节列表。
    /// </summary>
    private static List<object> CoreSettingsSections() => new()
    {
        new
        {
            section_id = "sec-model",
            title = "推理引擎",
            plugin_id = "core",
            source = "core",
            fields = new object[]
            {
                new { key = "ModelBaseUrl", label = "模型接口基础地址 (Base URL)", type = "text", desc = "兼容 OpenAI 规范的 API 基础端点", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "Model", label = "主回复模型名称", type = "text", desc = "例如 deepseek-chat, gpt-4o 等", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "ApiKey", label = "模型 API 密钥", type = "secret", desc = "存入独立密钥库，面板只显示是否已配置", step = (string?)null, options = (string[]?)null, secret_name = "apiKey", secret_flag = "_secretApiKeyConfigured" },
                new { key = "FastReply", label = "启用快速模型回复档位", type = "boolean", desc = "开启后聊天回复走轻量快速模型", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "FastModel", label = "快速档模型名称", type = "text", desc = "快速档生效时的模型名，例如 deepseek-flash", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "MaxTokens", label = "最大输出 Token 上限", type = "number", desc = "单次请求模型的最大生成 token 数", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "ThinkingBudget", label = "思考深度预设档位", type = "select", desc = "控制推理模型的思考深度", step = (string?)null, options = new[] { "off", "low", "medium", "high", "xhigh", "custom" }, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "AdaptiveSamplingEnabled", label = "启用自适应动态采样", type = "boolean", desc = "理性问题降温提准确度，感性互动升温保灵动", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "RationalTemperature", label = "理性温度 (0.0 - 2.0)", type = "number", desc = "理性回答时的基准采样温度", step = "0.05", options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "EmotionalTemperature", label = "感性温度 (0.0 - 2.0)", type = "number", desc = "拟人情感互动时的采样温度", step = "0.05", options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null }
            }
        },
        new
        {
            section_id = "sec-channels",
            title = "接入协议",
            plugin_id = "core",
            source = "core",
            fields = new object[]
            {
                new { key = "OneBotProtocol", label = "OneBot 通信协议", type = "select", desc = "与 QQ 协议端通信方式", step = (string?)null, options = new[] { "ForwardWebSocket", "ReverseWebSocket", "Http" }, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "OneBotAddress", label = "OneBot 连接端点", type = "text", desc = "正向 WS: ws://napcat:3001，反向 WS: http://0.0.0.0:3001", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "QuickLoginUin", label = "机器人登录 QQ 账号", type = "text", desc = "用于自我消息过滤与 @ 识别，留空自动识别", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "OfficialAppId", label = "QQ 官方开放平台 AppID", type = "text", desc = "官方机器人凭证，不使用留空", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "OfficialSecret", label = "QQ 官方开放平台 AppSecret", type = "secret", desc = "存入独立密钥库，不写入配置文件", step = (string?)null, options = (string[]?)null, secret_name = "officialAppSecret", secret_flag = "_secretOfficialConfigured" },
                new { key = "OfficialEnabled", label = "启用 QQ 官方开放平台通道", type = "boolean", desc = "打开后与私域通道并存；须与 AppID、AppSecret 同时齐备才真正建立连接", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "OfficialSandbox", label = "官方通道使用沙箱环境", type = "boolean", desc = "沙箱只接收开放平台「沙箱配置」里登记的沙箱群/沙箱单聊事件；正式群里 @ 机器人不会被推送，日志里也不会有任何行。联调用，正式上线请关闭", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "OfficialChatEnabled", label = "官方通道允许对话", type = "boolean", desc = "关闭后保留长连接但不再回复消息", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "OfficialWhitelistGroups", label = "官方通道群白名单", type = "text", desc = "填 group_openid，逗号分隔；留空 = 不响应任何群消息；填 * = 全量放行", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "OfficialWhitelistPrivates", label = "官方通道私聊白名单", type = "text", desc = "填 user_openid，逗号分隔；留空 = 不响应任何私聊；填 * = 全量放行", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "OfficialApiBase", label = "官方 REST 接口根地址", type = "text", desc = "留空 = 自动选：正式 https://api.bot.qq.com，沙箱 https://sandbox.api.sgroup.qq.com。官方改版时可在此覆盖，无需改代码", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "OfficialTokenUrl", label = "官方取 access_token 地址", type = "text", desc = "留空 = https://bots.qq.com/app/getAppAccessToken。官方换域名时可在此覆盖", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "FeishuAppId", label = "飞书机器人 App ID", type = "text", desc = "飞书开放平台应用 ID", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "FeishuSecret", label = "飞书机器人 App Secret", type = "secret", desc = "存入独立密钥库，不写入配置文件", step = (string?)null, options = (string[]?)null, secret_name = "feishuAppSecret", secret_flag = "_secretFeishuConfigured" },
                new { key = "FeishuEnabled", label = "启用飞书机器人通道", type = "boolean", desc = "打开后须与 App ID、App Secret 同时齐备才真正注册事件回调", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "FeishuVerificationToken", label = "飞书事件订阅 Verification Token", type = "text", desc = "飞书开放平台「事件订阅」页的校验令牌，用于回调验签", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "FeishuEncryptKey", label = "飞书事件加密 Encrypt Key", type = "secret", desc = "开启事件加密时填写，用于回调体解密；存入独立密钥库，不写入配置文件。留空表示未加密", step = (string?)null, options = (string[]?)null, secret_name = "feishuEncryptKey", secret_flag = "_secretFeishuEncryptConfigured" },
                new { key = "FeishuWhitelist", label = "飞书通道白名单", type = "text", desc = "填飞书 chat_id 或 open_id，逗号分隔；留空 = 不响应；填 * = 全量放行", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "FeishuApiBase", label = "飞书 REST 接口根地址", type = "text", desc = "留空 = https://open.feishu.cn。飞书改版或走自建代理时可在此覆盖", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null }
            }
        },
        new
        {
            section_id = "sec-persona",
            title = "人格配置",
            plugin_id = "core",
            source = "core",
            fields = new object[]
            {
                new { key = "AiModeEnabled", label = "全局 AI 自动回复总开关", type = "boolean", desc = "关闭后机器人停止自动研判与回复", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "BotPersona", label = "核心系统人设设定", type = "textarea", desc = "定义机器人的身份性格、说话语气与互动风格", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "AiDesire", label = "群聊发言欲望指数 (0 - 100)", type = "number", desc = "数值越高越主动插话参与群聊，默认 50", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "SuitabilityThreshold", label = "发言适合度门限 (0 - 100)", type = "number", desc = "模型综合评估分低于此门限时选择沉默，默认 10", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "ScenarioPreset", label = "场景策略预设", type = "select", desc = "留空跟随通用开关，其余预设收紧能力", step = (string?)null, options = new[] { "", "on-demand", "research", "social" }, secret_name = (string?)null, secret_flag = (string?)null }
            }
        },
        new
        {
            section_id = "sec-whitelist",
            title = "准入名单",
            plugin_id = "core",
            source = "core",
            fields = new object[]
            {
                new { key = "WhitelistGroups", label = "群聊白名单 (* 代表全放行)", type = "textarea", desc = "每行或逗号分隔一个群号，空代表严格忽略", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "WhitelistPrivates", label = "私聊白名单 (* 代表全放行)", type = "textarea", desc = "每行或逗号分隔一个 QQ 号，空代表忽略私聊", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null }
            }
        },
        new
        {
            section_id = "sec-cooldown",
            title = "节奏阀门",
            plugin_id = "core",
            source = "core",
            fields = new object[]
            {
                new { key = "GroupCooldownSeconds", label = "群聊回复冷却 (秒)", type = "number", desc = "同一群聊两次回复之间的最小间隔", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "PrivateCooldownSeconds", label = "私聊回复冷却 (秒)", type = "number", desc = "同一私聊会话的回复冷却间隔", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "SplitReplies", label = "启用长句切分模拟真人打字", type = "boolean", desc = "按标点符号切分长句并分批次发送", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "SegmentDelayMs", label = "分句发送间隔基准 (毫秒)", type = "number", desc = "模拟打字各分句发送时的延迟基准", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "FilterActionNarration", label = "过滤回复夹带动作/心理描写", type = "boolean", desc = "自动剥除（笑）、（叹气）等舞台旁白动作", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "EnableProactive", label = "允许安静时主动开口陪伴", type = "boolean", desc = "群内沉寂且有契机时适时发表话题", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null }
            }
        },
        new
        {
            section_id = "sec-memory",
            title = "记忆窗口",
            plugin_id = "core",
            source = "core",
            fields = new object[]
            {
                new { key = "MaxContextMessages", label = "上下文喂给模型的最大消息数", type = "number", desc = "历史滑动窗口长度", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "MaxProfileChars", label = "画像注入最大字符预算", type = "number", desc = "向提示词注入人物档案时的总字数上限", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null }
            }
        },
        new
        {
            section_id = "sec-approval",
            title = "审批阀门",
            plugin_id = "core",
            source = "core",
            fields = new object[]
            {
                new { key = "EnableApprovals", label = "启用人在回路操作审批", type = "boolean", desc = "敏感动作需管理员回复命令确认后执行", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null },
                new { key = "ApprovalApprovers", label = "指定特权审批人名单 (QQ号)", type = "text", desc = "逗号分隔，留空仅限群主与管理员", step = (string?)null, options = (string[]?)null, secret_name = (string?)null, secret_flag = (string?)null }
            }
        }
    };

    /// <summary>
    /// 增量读取日志尾部：从上次读到的字节位置往后只扫新增部分来累计总行数，
    /// 再反向 seek 从文件末尾回退定位最后 <paramref name="take"/> 行。
    /// 避免每 2.5 秒把整个日志文件逐行读一遍。
    /// </summary>
    private (List<string> Lines, long TotalLines) ReadLogTail(string logPath, int take)
    {
        var lines = new List<string>();
        if (!File.Exists(logPath))
        {
            lock (_logCursorLock)
            {
                _logCursorBytes = 0;
                _logCursorLines = 0;
            }
            return (lines, 0);
        }

        long length;
        long totalLines;
        lock (_logCursorLock)
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            length = fs.Length;

            // 文件被清空或轮转（变短）：游标归零，从头重新计数
            if (length < _logCursorBytes)
            {
                _logCursorBytes = 0;
                _logCursorLines = 0;
            }

            // 只扫游标之后新增的字节，数其中的换行符
            if (length > _logCursorBytes)
            {
                fs.Seek(_logCursorBytes, SeekOrigin.Begin);
                var buffer = new byte[8192];
                long scanned = _logCursorBytes;
                int read;
                while (scanned < length && (read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, length - scanned))) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        if (buffer[i] == (byte)'\n') _logCursorLines++;
                    }
                    scanned += read;
                }
                _logCursorBytes = length;
            }

            totalLines = _logCursorLines;
            if (totalLines == 0 && length > 0)
            {
                // 文件非空但没有换行符：至少算作 1 行
                totalLines = 1;
            }

            // 反向回退定位尾部 take 行：从末尾按块回读，数够 take+1 个换行符即停
            const int blockSize = 8192;
            long start = 0;
            var newlines = 0;
            var pos = length;
            while (pos > 0 && newlines <= take)
            {
                var size = (int)Math.Min(blockSize, pos);
                pos -= size;
                var chunk = new byte[size];
                fs.Seek(pos, SeekOrigin.Begin);
                var got = fs.Read(chunk, 0, size);
                for (var i = got - 1; i >= 0; i--)
                {
                    if (chunk[i] != (byte)'\n') continue;
                    newlines++;
                    if (newlines > take)
                    {
                        start = pos + i + 1;
                        break;
                    }
                }
            }

            if (start < length)
            {
                fs.Seek(start, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    lines.Add(line);
                    if (lines.Count > take) lines.RemoveAt(0);
                }
            }
        }

        return (lines, totalLines);
    }

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        // trimmed/AOT 发布下必须显式给 resolver，否则首次序列化就抛异常（OneBotModels 里同类问题已记录）
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int statusCode, object data)
    {
        resp.StatusCode = statusCode;
        // 面板的每个数字都靠这条链路刷新。没有 Cache-Control 时浏览器会自行决定怎么缓存 GET 响应，
        // 于是「保存成功但页面上看不到变化」「矩阵永远显示旧状态」这类问题会反复出现。
        resp.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        resp.Headers["Pragma"] = "no-cache";
        resp.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
        resp.Close();
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
            _context?.Log("[Web] Web 控制台已正常关闭。");
        }
        catch
        {
            // 忽略关闭异常
        }
        return Task.CompletedTask;
    }
}
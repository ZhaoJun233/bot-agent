using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Net.Codecrete.QrCodeGenerator;

namespace BotAgent.Services.NapCat;

/// <summary>二维码查询结果。失败也是一种结果 —— 面板要把「为什么看不到二维码」讲清楚。</summary>
public sealed record LoginQrSnapshot(
    bool Ok,
    bool Configured,
    string? Url,
    int AgeSeconds,
    string? Key,
    string? Error);

/// <summary>
/// NapCat WebUI 的最小客户端：只干一件事 —— 把「当前登录二维码」取出来，让面板能直接扫。
///
/// 为什么需要它：
///   二维码每约 2 分钟换一张，以前只能去 NapCat 自己的 WebUI（另一个域名 + 另一道 Basic 认证）
///   才看得到。用户打开机器人面板看不到二维码，就会卡在「没登录」上 ——
///   这正是远程部署那一轮卡了很久的原因。
///
/// 用的都是 NapCat WebUI 自己在用的公开接口，不是私有协议：
///   POST /api/auth/login            {"hash": SHA256(token + ".napcat")} → data.Credential
///   之后每个请求带                   Authorization: Bearer &lt;Credential&gt;
///   POST /api/QQLogin/GetQQLoginQrcode → data.qrcode（txz.qq.com 短链，字符串很短，适合直接编码）
///   POST /api/QQLogin/RefreshQrcode    → 强制换一张（旧图立刻失效，只在用户点“换一张”时调）
///
/// 线程模型：全部请求串行化（一个 SemaphoreSlim）+ 结果缓存。
///   缓存的意义：面板每 10 秒轮询一次，而二维码 2 分钟才换一张；
///   没有缓存就会把 NapCat 打爆，也会让正在扫的那张图闪来闪去。
/// </summary>
public sealed class LoginQrService : IDisposable
{
    /// <summary>凭据有效期。到期重新登录一次，避免长期复用同一个 Credential。</summary>
    private static readonly TimeSpan CredentialLifetime = TimeSpan.FromMinutes(20);

    /// <summary>
    /// 超过这个秒数没换过的二维码视为“NapCat 已经不再自动轮换”，下次查询时强制刷新。
    /// 取 100 秒：QQ 的登录二维码约 2 分钟失效，留出余量；
    /// 以前定 150 秒时，面板轮询间隔叠加之后很容易把已经过期的码展示给用户（实测扫出来就是“已过期”）。
    /// </summary>
    private const int StaleSeconds = 100;

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    private readonly IHttpFetcher _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _baseUrl;
    private readonly bool _configured;

    private string? _credential;
    private DateTimeOffset _credentialAt;

    private string? _qrcodeUrl;
    private DateTimeOffset _qrcodeAt;
    private string? _qrcodeKey;

    private string? _svg;
    private string? _svgFor;

    public LoginQrService(string webUiUrl, string? webUiToken, IHttpFetcher http)
    {
        _baseUrl = (webUiUrl ?? string.Empty).Trim().TrimEnd('/');
        var token = webUiToken?.Trim() ?? string.Empty;
        _configured = _baseUrl.Length > 0 && token.Length > 0;
        _authToken = token;

        _http = http;
    }

    private readonly string _authToken;

    /// <summary>给面板显示用（永远不含令牌）。</summary>
    public string WebUiDisplay => _baseUrl.Length > 0 ? _baseUrl : "（未配置）";

    /// <summary>是否配置齐全（地址 + 令牌）。</summary>
    public bool Configured => _configured;

    /// <summary>
    /// 取当前二维码。缓存还在保质期内就直接返回缓存；过期 / 强制刷新时才去打扰 NapCat。
    /// 任何异常都被折成 <see cref="LoginQrSnapshot.Error"/>，绝不抛出 —— 面板轮询不能因为 NapCat 挂了就炸。
    /// </summary>
    public async Task<LoginQrSnapshot> SnapshotAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!_configured)
        {
            return new LoginQrSnapshot(false, false, null, 0, null,
                "未配置 NapCat WebUI 令牌：给容器加上 QQCHAT_NAPCAT_WEBUI_TOKEN（值见 napcat/config/webui.json 的 token 字段）");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var age = AgeSeconds();
            if (_qrcodeUrl is not null && !forceRefresh && age <= StaleSeconds)
            {
                return Current(true, null);
            }

            return await FetchAsync(forceRefresh, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>当前二维码的 SVG（同一张图只生成一次）。没有二维码时返回 null。</summary>
    public string? Svg()
    {
        if (_qrcodeUrl is null)
        {
            return null;
        }

        if (_svg is not null && string.Equals(_svgFor, _qrcodeUrl, StringComparison.Ordinal))
        {
            return _svg;
        }

        // 二维码必须黑白分明 + 足够大的安静区，否则手机扫不出来。
        // ECC M 是扫码场景的常规选择；border=4 是标准安静区宽度。
        var qr = QrCode.EncodeText(_qrcodeUrl, QrCode.Ecc.Medium);
        _svg = qr.ToSvgString(4);
        _svgFor = _qrcodeUrl;
        return _svg;
    }

    private async Task<LoginQrSnapshot> FetchAsync(bool forceRefresh, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var relogin = attempt > 0;
            try
            {
                var credential = await EnsureCredentialAsync(relogin, ct).ConfigureAwait(false);
                if (credential is null)
                {
                    return new LoginQrSnapshot(false, true, null, 0, null,
                        "登录 NapCat WebUI 失败：令牌不对？（webui.json 里的 token）");
                }

                // 只有「强制换一张」或「NapCat 已经不再自动轮换」时才刷新，
                // 否则会把用户正在扫的那张图作废。
                if (forceRefresh || _qrcodeUrl is not null)
                {
                    var refreshed = await CallAsync("/api/QQLogin/RefreshQrcode", credential, ct).ConfigureAwait(false);
                    if (IsUnauthorized(refreshed))
                    {
                        _credential = null;
                        continue;
                    }

                    // 刷新失败不致命：继续尝试取当前这张
                }

                var response = await CallAsync("/api/QQLogin/GetQQLoginQrcode", credential, ct).ConfigureAwait(false);
                if (IsUnauthorized(response))
                {
                    _credential = null;
                    continue;
                }

                var url = response?["data"]?["qrcode"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(url))
                {
                    // 账号已登录时 NapCat 会直接拒绘二维码（code=-1 "QQ Is Logined"）。
                    // 这不是错误，但面板应该看到人话而不是原始 message。
                    if (IsLoggedIn(response))
                    {
                        _qrcodeUrl = null;
                        _qrcodeKey = null;
                        _svg = null;
                        _svgFor = null;
                        return new LoginQrSnapshot(false, true, null, 0, null, "QQ 账号已登录，无需扫码");
                    }

                    return new LoginQrSnapshot(false, true, null, 0, null,
                        $"NapCat 没有返回二维码：{Describe(response)}");
                }

                if (!string.Equals(_qrcodeUrl, url, StringComparison.Ordinal))
                {
                    _qrcodeUrl = url;
                    _qrcodeAt = Clock.UtcNow;
                    _qrcodeKey = KeyOf(url);
                    _svg = null;
                    _svgFor = null;
                    FileLog.Write("Login", "已取到新的登录二维码（面板可直接扫）");
                }

                // 注意：强制刷新后 NapCat 仍返回同一张时，**不要**重置 _qrcodeAt。
                // 否则“NapCat 卡死、一直是同一张过期码”这件事会被我们自己掩盖成“刚更新过”
                // —— 线上就是这么踩的：面板显示 age=0，手机上扫却提示“二维码已过期”。

                return Current(true, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 1)
                {
                    return new LoginQrSnapshot(false, true, _qrcodeUrl, AgeSeconds(), _qrcodeKey,
                        $"访问 NapCat WebUI 失败：{ex.Message}");
                }
            }
        }

        return new LoginQrSnapshot(false, true, null, 0, null, "NapCat WebUI 认证失败（令牌可能已更换）");
    }

    private LoginQrSnapshot Current(bool ok, string? error)
        => new(ok && _qrcodeUrl is not null, true, _qrcodeUrl, AgeSeconds(), _qrcodeKey, error);

    private int AgeSeconds()
        => _qrcodeAt == default ? 0 : (int)Math.Max(0, (Clock.UtcNow - _qrcodeAt).TotalSeconds);

    /// <summary>取（必要时重新登录）WebUI 凭据。</summary>
    private async Task<string?> EnsureCredentialAsync(bool force, CancellationToken ct)
    {
        if (!force && _credential is not null && Clock.UtcNow - _credentialAt < CredentialLifetime)
        {
            return _credential;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_authToken + ".napcat")))
            .ToLowerInvariant();

        var response = await CallAsync("/api/auth/login", null, ct, new JsonObject { ["hash"] = hash })
            .ConfigureAwait(false);

        var credential = response?["data"]?["Credential"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(credential))
        {
            FileLog.Warn("Login", $"NapCat WebUI 登录被拒：{Describe(response)}");
            return null;
        }

        _credential = credential;
        _credentialAt = Clock.UtcNow;
        return credential;
    }

    /// <summary>POST 一个 NapCat WebUI 接口。HTTP 层错误抛异常（由调用方折成提示）。</summary>
    private async Task<JsonNode?> CallAsync(string path, string? credential, CancellationToken ct, JsonObject? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
        {
            Content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json")
        };

        if (credential is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {path}");
        }

        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception)
        {
            throw new InvalidOperationException($"{path} 返回了非 JSON 内容");
        }
    }

    private static bool IsUnauthorized(JsonNode? response)
        => response?["code"]?.GetValue<int>() is int code && code != 0 &&
           (response?["message"]?.GetValue<string>() ?? string.Empty).Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);

    /// <summary>账号已经登录了（NapCat 拒绝再给二维码）。</summary>
    private static bool IsLoggedIn(JsonNode? response)
    {
        if (response?["code"]?.GetValue<int>() is not int code || code == 0)
        {
            return false;
        }

        var message = response["message"]?.GetValue<string>() ?? string.Empty;
        return message.Contains("Logined", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("已登录", StringComparison.Ordinal);
    }

    private static string Describe(JsonNode? response)
    {
        if (response is null)
        {
            return "（无响应）";
        }

        var code = response["code"]?.GetValue<int>();
        var message = response["message"]?.GetValue<string>();
        return code is null && message is null ? response.ToJsonString() : $"code={code} {message}";
    }

    /// <summary>二维码内容的短指纹：面板用它做缓存键，换了二维码才重新下载图。</summary>
    private static string KeyOf(string url)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()[..12];

    public void Dispose()
    {
        // 出网客户端由装配点持有并统一释放（见 HttpFetcher）；这里只收自己的锁
        _gate.Dispose();
    }
}

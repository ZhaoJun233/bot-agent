using System.Net;
using System.Text;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// 假的图片服务器：给“表情包自动收集”场景提供一个可下载的图片源。
///
/// 为什么需要它：机器人只接受 http(s) 图片地址（SSRF 防护默认拦回环/内网），
/// 所以测试要开 <c>QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1</c> 才能从 127.0.0.1 下载 ——
/// 这个开关是给“自建/测试”用的，生产部署不该打开。
///
/// 每张图的字节都不同（用 n 参数区分），这样表情包库才会按内容哈希存成不同条目。
/// </summary>
public sealed class MockImageHost : IDisposable
{
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly HttpListener _listener = new();
    private readonly int _port;
    private int _served;

    public MockImageHost(int port)
    {
        _port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    /// <summary>已经响应过的图片请求数。</summary>
    public int Served => Volatile.Read(ref _served);

    /// <summary>被 400 回绝的请求数（模拟 QQ 图片地址的 rkey 过期）。</summary>
    public int Rejected => Volatile.Read(ref _rejected);

    private int _rejected;

    /// <summary>
    /// 打开后：没带 <c>rkey=…</c> 的请求一律回 400（模拟 QQ 多媒体 CDN：图片地址带时效 rkey，过期即 400）。
    /// 默认关 —— 表情包场景还在用不带 rkey 的地址。
    /// </summary>
    public bool RequireFreshRkey { get; set; }

    /// <summary>“过期”的图片地址：带一个已被作废的 rkey（RequireFreshRkey 打开时会被 400）。</summary>
    public string StaleUrl(int n) => $"http://127.0.0.1:{_port}/img?n={n}&rkey=expired-{n}";

    /// <summary>“协议端重新签发”的图片地址：同一张图，rkey 是有效的。</summary>
    public string FreshUrl(int n) => $"http://127.0.0.1:{_port}/img?n={n}&rkey=fresh-{n}";

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>第 n 张图的地址（n 从 1 开始；不同 n = 不同内容 = 库里不同条目）。</summary>
    public string Url(int n) => $"http://127.0.0.1:{_port}/img?n={n}";

    /// <summary>第 n 张图的字节（测试侧用它算出表情包 id）。</summary>
    public static byte[] Bytes(int n)
    {
        // 只需要“看起来是 PNG”（头部魔数）且内容各不相同：
        // 机器人的下载器按魔数判类型，并不会真去解码图片。
        var data = new byte[256];
        Array.Copy(PngMagic, data, PngMagic.Length);
        data[16] = (byte)(n & 0xFF);
        data[17] = (byte)((n >> 8) & 0xFF);
        for (var i = 32; i < data.Length; i++)
        {
            data[i] = (byte)((n * 31 + i) & 0xFF);
        }

        return data;
    }

    /// <summary>对应的表情包 id（= sha256 前 8 位，与 StickerStore 的口径一致）。</summary>
    public static string StickerId(int n)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Bytes(n))).ToLowerInvariant();
        return hash[..8];
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        Interlocked.Increment(ref _served);

        var n = 1;
        var raw = context.Request.QueryString["n"];
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
        {
            n = parsed;
        }

        // 模拟“rkey 过期”：QQ 那侧对过期地址回 400，机器人应该去找协议端重新签发
        if (RequireFreshRkey && !(context.Request.QueryString["rkey"] ?? "").StartsWith("fresh-", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _rejected);
            var fail = Encoding.UTF8.GetBytes("{\"error\":\"rkey expired\"}");
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = fail.Length;
            await context.Response.OutputStream.WriteAsync(fail);
            context.Response.Close();
            return;
        }

        var bytes = Bytes(n);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // 忽略
        }
    }
}

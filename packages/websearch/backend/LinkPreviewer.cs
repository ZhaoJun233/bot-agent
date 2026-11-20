using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BotAgent.Plugins.Net;
using BotAgent.Services.Net;

namespace BotAgent.Services.Links;

/// <summary>一个链接的“看到什么”摘要。</summary>
/// <param name="Url">原始链接。</param>
/// <param name="Title">页面标题（og:title 优先）。</param>
/// <param name="Description">摘要（og:description / meta description）。</param>
/// <param name="SiteName">站点名（og:site_name），拿不到就写域名。</param>
public sealed record LinkPreview(string Url, string? Title, string? Description, string? SiteName)
{
    /// <summary>给模型看的一行。</summary>
    public string Describe()
    {
        var head = string.IsNullOrWhiteSpace(Title) ? Url : Title!;
        var site = string.IsNullOrWhiteSpace(SiteName) ? null : SiteName;
        var sb = new StringBuilder("- ").Append(head);
        if (site is not null)
        {
            sb.Append("（").Append(site).Append('）');
        }

        if (!string.IsNullOrWhiteSpace(Description))
        {
            sb.Append("：").Append(Description);
        }

        return sb.Append("  ").Append(Url).ToString();
    }
}

/// <summary>
/// 链接预览：真正打开一下群里发来的链接，取回标题与摘要。
///
/// 为什么要做：模型光看 URL 只能猜（短链更是完全看不出内容），
/// 有了标题/摘要它才谈得上“接得住”别人发的东西 —— 和听音乐是同一个思路：
/// 先去看一眼，再拿着事实说话。
///
/// 三条纪律：
///  ① SSRF：出站 URL 由宿主提供的 <see cref="IWebPageTransport"/> 校验；
///  ② 有界：只读前 256KB、单条超时（默认 5 秒）、总量上限也有限，绝不因为一个慢站点拖住机器人；
///  ③ 缓存：同一链接只取一次，失败的也记下来（别反复捅同一个坏链接）。
/// </summary>
public sealed partial class LinkPreviewer : ILinkPreviewer
{
    private const int ReadCap = 256 * 1024;

    [GeneratedRegex(@"<meta\s+([^>]+?)/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaRegex();

    [GeneratedRegex(@"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"(?<k>[a-zA-Z:]+)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')")]
    private static partial Regex AttrRegex();

    [GeneratedRegex(@"charset\s*=\s*[""']?\s*(?<c>[\w\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();

    private readonly IWebPageTransport _pages;
    private readonly Func<ResearchOptions> _settings;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, LinkPreview?> _cache = new();

    public LinkPreviewer(IWebPageTransport pages, Func<ResearchOptions> settings, Action<string> log)
    {
        _pages = pages;
        _settings = settings;
        _log = log;
    }

    /// <summary>把一批链接的标题/摘要拼成给模型看的说明；全都拿不到时返回 null。</summary>
    public async Task<string?> DescribeAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var url in urls)
        {
            var preview = await PreviewAsync(url, ct);
            if (preview is not null && (!string.IsNullOrWhiteSpace(preview.Title) || !string.IsNullOrWhiteSpace(preview.Description)))
            {
                lines.Add(preview.Describe());
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        return "群里消息里的链接（机器人已经打开看过）：\n" + string.Join("\n", lines);
    }

    /// <summary>取一个链接的标题/摘要（失败返回 null，且会被缓存住不再重试）。</summary>
    public async Task<LinkPreview?> PreviewAsync(string url, CancellationToken ct)
    {
        var settings = _settings();
        if (!settings.EnableLinkPreview)
        {
            return null;
        }

        if (_cache.TryGetValue(url, out var cached))
        {
            return cached;
        }

        if (!_pages.TryValidate(url, out var uri, out var reason))
        {
            _log($"[Link] 跳过 {Shorten(url)}（{reason}）");
            _cache[url] = null;
            return null;
        }

        LinkPreview? result = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.LinkPreviewTimeoutSeconds, 2, 30)));

            var resp = await _pages.GetAsync(new WebPageRequest(uri, ReadCap,
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/122 Safari/537.36", "zh-CN,zh;q=0.9,en;q=0.8"), cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _log($"[Link] {Shorten(url)} 返回 {(int)resp.StatusCode}");
                _cache[url] = null;
                return null;
            }

            result = Parse(resp.Body, resp.Url);
            if (result is null)
            {
                _log($"[Link] {Shorten(url)} 没解析出标题");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log($"[Link] 取 {Shorten(url)} 失败: {ex.GetType().Name} {ex.Message}");
        }

        // 缓存成功的与失败的都记（失败也记 = 不反复重试）
        if (_cache.Count > 500)
        {
            _cache.Clear();
        }

        _cache[url] = result;
        return result;
    }

    /// <summary>从 HTML 里取标题/摘要/站点名。取不到标题时返回 null。</summary>
    public static LinkPreview? Parse(byte[] body, Uri uri)
    {
        var html = Decode(body);
        string? title = null, description = null, siteName = null;

        foreach (Match meta in MetaRegex().Matches(html))
        {
            var attrs = ReadAttrs(meta.Groups[1].Value);
            var key = (attrs.GetValueOrDefault("property") ?? attrs.GetValueOrDefault("name") ?? string.Empty).ToLowerInvariant();
            var content = attrs.GetValueOrDefault("content");
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            switch (key)
            {
                case "og:title":
                    title ??= content;
                    break;
                case "og:description":
                    description ??= content;
                    break;
                case "description":
                    description ??= content;
                    break;
                case "og:site_name":
                    siteName ??= content;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            var m = TitleRegex().Match(html);
            if (m.Success)
            {
                title = m.Groups["t"].Value;
            }
        }

        title = Clean(title, 120);
        description = Clean(description, 200);
        siteName = Clean(siteName, 40) ?? uri.Host;

        return title is null && description is null ? null : new LinkPreview(uri.ToString(), title, description, siteName);
    }

    private static Dictionary<string, string> ReadAttrs(string tag)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRegex().Matches(tag))
        {
            attrs[m.Groups["k"].Value] = m.Groups["v"].Value;
        }

        return attrs;
    }

    private static string? Clean(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(raw);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length > max ? text[..max] + "…" : text;
    }

    /// <summary>
    /// 按页面自己声明的编码解码（没有 GBK 编解码provider 时退化 UTF-8 —— 现代站点基本都是 UTF-8）。
    /// </summary>
    private static string Decode(byte[] body)
    {
        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 2048));
        var charset = CharsetRegex().Match(head).Groups["c"].Value;
        var encoding = Encoding.UTF8;
        if (charset.Length > 0 && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) &&
            !charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (Exception)
            {
                encoding = Encoding.UTF8;
            }
        }

        return encoding.GetString(body);
    }

    private static string Shorten(string url) => url.Length > 60 ? url[..60] + "…" : url;
}

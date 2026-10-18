using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Links;
using BotAgent.Services.Net;

// Never bootstrap the application, load production data, or use public HTTP/DNS.
FileLog.WriteToFile = false;
FileLog.Verbose = false;
var originalOverride = Environment.GetEnvironmentVariable("QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS");
Environment.SetEnvironmentVariable("QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS", null);
var passed = 0;
var failures = 0;
void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    if (condition) passed++; else failures++;
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) { Check(false, name + ": " + ex.GetType().Name + ": " + ex.Message); }
}
try
{
    await Test("URL/address policy", () =>
    {
        foreach (var url in new[] { "", "file:///tmp/image", "ftp://example.com/image", "http://localhost/image", "http://service.local./image", "http://service.internal/image", "http://a.localhost/image" })
            Check(!SafeUrl.TryValidate(url, false, out _, out _), "unsafe URL rejected: " + url);
        // Documentation addresses are synthetic public-shaped candidates, never connected.
        foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "172.16.0.1", "192.168.0.1", "169.254.1.1", "100.100.100.100", "0.0.0.0", "224.0.0.1", "[::ffff:127.0.0.1]", "[::ffff:10.0.0.1]", "[fc00::1]", "[fd00::1]", "[fe80::1]", "[::1]", "[::]" })
            Check(!SafeUrl.TryValidate("https://" + address + "/image", false, out _, out _), "private literal rejected: " + address);
        foreach (var address in new[] { "203.0.113.7", "[2001:db8::7]", "[::ffff:203.0.113.7]" })
            Check(SafeUrl.TryValidate("https://" + address + "/image", false, out _, out _), "public-shaped literal passes URL check: " + address);
        var mixed = new[] { "127.0.0.1", "203.0.113.7", "::ffff:10.0.0.1", "2001:db8::7", "fd00::1" };
        Check(mixed.Where(ip => !IsBlocked(ip)).SequenceEqual(new[] { "203.0.113.7", "2001:db8::7" }),
            "mixed DNS concept filters private candidates and keeps public candidates");
        Check(new[] { "127.0.0.1", "::1" }.All(IsBlocked), "all-loopback DNS candidate set is blocked");
        return Task.CompletedTask;
    });

    await Test("production socket boundary", async () =>
    {
        using var secure = new HttpFetcher(TimeSpan.FromSeconds(3), rejectPrivateDestinations: true);
        var handler = Handler(secure);
        Check(!handler.UseProxy && !handler.AllowAutoRedirect && handler.ConnectCallback is not null,
            "secure client disables proxy/auto-redirect and installs connection policy");
        // OS localhost resolution only. Private destinations must fail before a socket connect.
        Check((await Dns.GetHostAddressesAsync("localhost")).All(ip => IsBlocked(ip.ToString())), "localhost DNS resolves only to denied candidates");
        foreach (var host in new[] { "localhost", "127.0.0.1", "[::1]" })
        {
            var denied = false;
            try { using var response = await secure.GetAsync("http://" + host + ":9/"); }
            catch (HttpRequestException ex) { denied = ExceptionChain(ex).Any(e => e.Message.Contains("禁止的私有", StringComparison.Ordinal)); }
            Check(denied, "real secure connection callback rejects " + host);
        }
        using var trusted = new HttpFetcher(TimeSpan.FromSeconds(3));
        var trustedHandler = Handler(trusted);
        Check(trustedHandler.UseProxy && trustedHandler.AllowAutoRedirect && trustedHandler.ConnectCallback is null,
            "trusted configured client retains internal endpoint/proxy behavior");
        using var testOverride = new HttpFetcher(TimeSpan.FromSeconds(3), allowAutoRedirect: false);
        Check(Handler(testOverride).ConnectCallback is null && !Handler(testOverride).AllowAutoRedirect,
            "test override permits private sockets but still uses explicit redirects");
    });

    await Test("redirect helper", async () =>
    {
        foreach (var status in new[] { HttpStatusCode.Moved, HttpStatusCode.Redirect, HttpStatusCode.SeeOther, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect })
        {
            var contents = new List<TrackedContent>();
            var http = new SyntheticHttp(request =>
            {
                var content = new TrackedContent();
                contents.Add(content);
                return request.RequestUri!.AbsolutePath == "/start"
                    ? SyntheticHttp.Redirect("/final", status, content)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });
            var response = await Follow(http);
            Check(http.Urls.Count == 2 && http.Urls[1].AbsolutePath == "/final" && contents[0].Disposed && !contents[1].Disposed,
                "relative/public hop validated; intermediate disposed; final owned by caller: " + (int)status);
            response.Dispose();
            Check(contents[1].Disposed, "caller disposes final response: " + (int)status);
        }
        foreach (var location in new[] { "http://127.0.0.1/private", "https://[fd00::1]/private", "file:///tmp/image" })
        {
            var content = new TrackedContent();
            var http = new SyntheticHttp(_ => SyntheticHttp.Redirect(location, content: content));
            var rejected = false;
            try { using var response = await Follow(http); }
            catch (HttpRequestException) { rejected = true; }
            Check(rejected && http.Urls.Count == 1 && content.Disposed, "unsafe redirect never sent; response disposed: " + location);
        }
        var loop = new SyntheticHttp(_ => SyntheticHttp.Redirect("/start"));
        var loopRejected = false;
        try { using var response = await Follow(loop); }
        catch (HttpRequestException) { loopRejected = true; }
        Check(loopRejected && loop.Urls.Count == 6, "redirect loop bounded at five hops");
        var none = new SyntheticHttp(_ => SyntheticHttp.Image());
        var invalidStart = false;
        try { using var response = await Follow(none, "http://127.0.0.1/image"); }
        catch (HttpRequestException) { invalidStart = true; }
        Check(invalidStart && none.Urls.Count == 0, "invalid starting URL rejected before transport");
    });

    await Test("image downloader", async () =>
    {
        var imageHttp = new SyntheticHttp(request => request.RequestUri!.AbsolutePath == "/start"
            ? SyntheticHttp.Redirect("https://cdn.example.com/image") : SyntheticHttp.Image());
        var downloader = ImageDownloader(imageHttp);
        var result = await downloader.DownloadBytesAsync("https://example.com/start", default);
        Check(result is { Mime: "image/png", Ext: "png" } && result.Value.Data.SequenceEqual(SyntheticHttp.Png) && imageHttp.Urls.Count == 2,
            "public image redirect downloads final bytes");
        var cached = await downloader.DownloadBytesAsync("https://example.com/start", default);
        Check(cached is not null && cached.Value.Data.SequenceEqual(SyntheticHttp.Png) && imageHttp.Urls.Count == 2 && downloader.CacheHits == 1,
            "redirected image cache behavior unchanged");
        var privateRedirect = new SyntheticHttp(_ => SyntheticHttp.Redirect("http://127.0.0.1/private"));
        Check(await ImageDownloader(privateRedirect).DownloadBytesAsync("https://example.com/start", default) is null && privateRedirect.Urls.Count == 1,
            "image private redirect rejected before second request");
        var loop = new SyntheticHttp(_ => SyntheticHttp.Redirect("/start"));
        Check(await ImageDownloader(loop).DownloadBytesAsync("https://example.com/start", default) is null && loop.Urls.Count == 6,
            "image redirect loop bounded");
        var oversized = new SyntheticHttp(_ =>
        {
            var response = SyntheticHttp.Image();
            response.Content.Headers.ContentLength = 6L * 1024 * 1024 + 1;
            return response;
        });
        Check(await ImageDownloader(oversized).DownloadBytesAsync("https://example.com/large", default) is null, "image declared size cap retained");
        var refreshedHttp = new SyntheticHttp(request => request.RequestUri!.AbsolutePath == "/stale"
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : request.RequestUri.AbsolutePath == "/fresh" ? SyntheticHttp.Redirect("/image") : SyntheticHttp.Image());
        var refreshed = ImageDownloader(refreshedHttp);
        Func<long, CancellationToken, Task<IReadOnlyList<string>>> refresh = (_, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "https://example.com/fresh" });
        refreshed.RefreshUrls = refresh;
        Check(await refreshed.DownloadBytesAsync("https://example.com/stale", default, 10001) is not null && refreshedHttp.Urls.Count == 3 && refreshed.RefreshedCount == 1,
            "refreshed image URL also follows validated public hops");
        var canceled = new SyntheticHttp(_ => SyntheticHttp.Image());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancellationPropagated = false;
        try { await ImageDownloader(canceled).DownloadBytesAsync("https://example.com/cancel", cts.Token); }
        catch (OperationCanceledException) { cancellationPropagated = true; }
        Check(cancellationPropagated && canceled.Urls.Count == 0, "image cancellation still propagates");
        var slowHop = new SyntheticHttp(_ => SyntheticHttp.Redirect("/start"))
        {
            Timeout = TimeSpan.FromMilliseconds(50), HeaderDelay = TimeSpan.FromMilliseconds(30)
        };
        var timedOut = false;
        try { await ImageDownloader(slowHop).DownloadBytesAsync("https://example.com/start", default); }
        catch (OperationCanceledException) { timedOut = true; }
        Check(timedOut && slowHop.Urls.Count is >= 1 and <= 2, "image redirect chain shares client header timeout budget across hops");
    });

    await Test("link/page vs model boundaries", async () =>
    {
        var pageBody = "<html><title>Synthetic</title><p>" + new string('x', 80) + "</p></html>";
        var pages = new SyntheticHttp(request => request.RequestUri!.AbsolutePath == "/start"
            ? SyntheticHttp.Redirect("https://cdn.example.com/final")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(pageBody) });
        var settings = new AppSettings { EnableLinkPreview = true, ModelBaseUrl = "http://localhost:9/v1", Model = "synthetic-model", ApiKey = "synthetic-key", WebSearchUseModelSearch = true };
        var preview = await new LinkPreviewer(pages, () => settings, _ => { }).PreviewAsync("https://example.com/start", default);
        Check(preview is { Title: "Synthetic" } && preview.Url == "https://cdn.example.com/final" && pages.Urls.Count == 2,
            "link preview follows public hop and reports final URL");
        var models = new SyntheticHttp(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"synthetic answer\"}]}}]}")
        });
        var search = new WebSearchService(models, () => settings, _ => { }, pageHttp: pages);
        var read = await search.ReadPageAsync("https://example.com/start", default);
        Check(read.Text is not null && read.Error is null && pages.Urls.Count == 4 && models.Urls.Count == 0,
            "untrusted page reads use dedicated page transport");
        var modelResult = await search.SearchAsync("synthetic query", default);
        Check(modelResult is not null && modelResult.Answer == "synthetic answer"
                && models.Urls.Single().Host == "localhost" && models.Authorization.Single() == "Bearer synthetic-key",
            "trusted configured internal model search remains available");
        Check(pages.Authorization.All(auth => auth is null), "page hops do not forward model authorization");
        var deniedPages = new SyntheticHttp(_ => SyntheticHttp.Redirect("http://127.0.0.1/private"));
        var deniedSearch = new WebSearchService(models, () => settings, _ => { }, pageHttp: deniedPages);
        Check((await deniedSearch.ReadPageAsync("https://example.com/start", default)).Text is null && deniedPages.Urls.Count == 1,
            "page private redirect is blocked");
        var deniedLink = new SyntheticHttp(_ => SyntheticHttp.Redirect("http://127.0.0.1/private"));
        Check(await new LinkPreviewer(deniedLink, () => settings, _ => { }).PreviewAsync("https://example.com/start", default) is null && deniedLink.Urls.Count == 1,
            "link private redirect is blocked");
    });

    await Test("private override", async () =>
    {
        Environment.SetEnvironmentVariable("QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS", "1");
        var privateHttp = new SyntheticHttp(request => request.RequestUri!.AbsolutePath == "/start"
            ? SyntheticHttp.Redirect("http://localhost/final") : SyntheticHttp.Image());
        Check(await ImageDownloader(privateHttp).DownloadBytesAsync("http://127.0.0.1/start", default) is not null && privateHttp.Urls.Count == 2,
            "runtime private override permits private HTTP image redirects");
        foreach (var url in new[] { "file:///tmp/image", "ftp://example.com/image" })
            Check(await ImageDownloader(privateHttp).DownloadBytesAsync(url, default) is null && privateHttp.Urls.Count == 2,
                "override still rejects non-HTTP(S) input: " + url);
        var invalidHop = new SyntheticHttp(_ => SyntheticHttp.Redirect("file:///tmp/image"));
        Check(await ImageDownloader(invalidHop).DownloadBytesAsync("http://127.0.0.1/start", default) is null && invalidHop.Urls.Count == 1,
            "override still rejects non-HTTP(S) redirect");
    });

    await Test("production composition/pinning contracts", () =>
    {
        var root = FindRoot();
        var composition = File.ReadAllText(Path.Combine(root, "src/BotAgent.Headless/Host/CompositionRoot.cs"));
        var fetcher = File.ReadAllText(Path.Combine(root, "src/BotAgent.Headless/Adapters/Net/HttpFetcher.cs"));
        foreach (var name in new[] { "imageHttp", "linkHttp", "researchPageHttp" })
        {
            var block = Regex.Match(composition, @"var " + name + @" = new HttpFetcher\([\s\S]*?;").Value;
            Check(block.Contains("allowAutoRedirect: false") && block.Contains("rejectPrivateDestinations: !allowPrivateOutbound"),
                "production composition secure untrusted client: " + name);
        }
        foreach (var name in new[] { "modelAuxHttp", "modelChatHttp", "researchHttp", "mediaHttp", "voiceHttp" })
        {
            var block = Regex.Match(composition, @"var " + name + @" = new HttpFetcher\([\s\S]*?;").Value;
            Check(block.Length > 0 && !block.Contains("rejectPrivateDestinations"), "production composition retains trusted configured client: " + name);
        }
        Check(composition.Contains("new ImageDownloader(imageHttp)") && composition.Contains("new LinkPreviewer(linkHttp") && composition.Contains("searchBreaker, researchPageHttp)"),
            "production secure clients are wired to image/link/page consumers");
        Check(fetcher.Contains("OutboundAddressPolicy.IsBlocked(address)") && fetcher.Contains("socket.ConnectAsync(address, context.DnsEndPoint.Port, ct)") &&
              !fetcher.Contains("socket.ConnectAsync(context.DnsEndPoint"),
            "production pinning contract connects validated IP literal, never DNS endpoint");
        return Task.CompletedTask;
    });
}
finally { Environment.SetEnvironmentVariable("QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS", originalOverride); }
Console.WriteLine($"Passed {passed}; failed {failures}");
return failures == 0 ? 0 : 1;

// Internal production types are invoked only in the probe; no visibility/interface expansion.
static IImageDownloader ImageDownloader(IHttpFetcher http) => (IImageDownloader)Activator.CreateInstance(
    typeof(SafeUrl).Assembly.GetType("BotAgent.Adapters.Model.ImageDownloader")!, http)!;
static bool IsBlocked(string text) => (bool)typeof(SafeUrl).Assembly.GetType("BotAgent.Services.Net.OutboundAddressPolicy")!
    .GetMethod("IsBlocked", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new object[] { IPAddress.Parse(text) })!;
static SocketsHttpHandler Handler(HttpFetcher fetcher)
{
    var client = (HttpClient)typeof(HttpFetcher).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fetcher)!;
    return (SocketsHttpHandler)typeof(HttpMessageInvoker).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(field => field.FieldType == typeof(HttpMessageHandler)).GetValue(client)!;
}
static IEnumerable<Exception> ExceptionChain(Exception ex)
{
    for (Exception? current = ex; current is not null; current = current.InnerException) yield return current;
}
static Task<HttpResponseMessage> Follow(IHttpFetcher http, string start = "https://example.com/start")
    => SafeUrl.SendFollowingRedirectsAsync(http, new Uri(start), target => new HttpRequestMessage(HttpMethod.Get, target), false, default);
static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src/BotAgent.Headless/Host/CompositionRoot.cs"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Cannot locate source root for composition contracts");
}

sealed class TrackedContent : ByteArrayContent
{
    public TrackedContent() : base(Array.Empty<byte>()) { }
    public bool Disposed { get; private set; }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}

sealed class SyntheticHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpFetcher
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan HeaderDelay { get; init; }
    public List<Uri> Urls { get; } = new();
    public List<string?> Authorization { get; } = new();
    public static readonly byte[] Png = { 0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10 };
    public static HttpResponseMessage Image() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Redirect, HttpContent? content = null)
    {
        var response = new HttpResponseMessage(status) { Content = content };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Urls.Add(request.RequestUri!);
        Authorization.Add(request.Headers.Authorization?.ToString());
        if (HeaderDelay > TimeSpan.Zero) await Task.Delay(HeaderDelay, ct);
        var response = respond(request);
        response.RequestMessage = request;
        return response;
    }
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), option, ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => GetAsync(new Uri(url), ct);
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(new Uri(url), option, ct);
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        => throw new NotSupportedException("Synthetic probe has no POST convenience transport");
}

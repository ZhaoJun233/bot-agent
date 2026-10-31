using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Plugins;

var root = Path.Combine(Path.GetTempPath(), "plugin-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var http = new FakeHttp();
var context = new PluginContext(new EmptyServices());
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
async Task Reject(Func<Task> operation)
{
    try { await operation(); } catch (InvalidDataException) { checks++; return; }
    throw new Exception("Expected rejection");
}
byte[] Zip(string path = "probe.dll", int attributes = 0, bool large = false)
{
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
    {
        var entry = zip.CreateEntry(path);
        entry.ExternalAttributes = attributes;
        using var stream = entry.Open();
        if (large) stream.Write(new byte[33 * 1024 * 1024]);
        else stream.Write(File.ReadAllBytes(typeof(SyntheticPlugin).Assembly.Location));
    }
    return bytes.ToArray();
}
PluginPackageManifest Manifest(byte[] bytes, string id = "test.plugin", string version = "1.0.0", string type = "SyntheticPlugin") =>
    new(id, version, "probe.dll", type, Convert.ToHexString(SHA256.HashData(bytes)), "https://example.com/plugin.zip");
PluginPackageService Service() => new(root, http);
try
{
    var good = Zip();
    var manifest = Manifest(good);
    Check(PluginPackageValidation.ParseManifest(JsonSerializer.Serialize(manifest)).Id == manifest.Id, "parse");
    foreach (var version in new[] { "latest", "1x0x0", "1.*", "../1.0.0" })
        await Reject(() => Task.Run(() => PluginPackageValidation.Validate(manifest with { Version = version })));
    await Reject(() => Task.Run(() => PluginPackageValidation.Validate(manifest with { Id = "preset.music" })));
    // 宿主升级到 net10.0 后仍必须接受已发布的 net8.0 插件；其它框架（含未来版本）一律拒绝。
    foreach (var framework in new[] { "net8.0", "net10.0" })
        PluginPackageValidation.Validate(manifest with { TargetFramework = framework });
    checks++;
    foreach (var framework in new[] { "net6.0", "net9.0", "net11.0", "", "net10" })
        await Reject(() => Task.Run(() => PluginPackageValidation.Validate(manifest with { TargetFramework = framework })));
    http.Bytes = good;
    await Reject(() => Service().StageInstallAsync(manifest with { Sha256 = new string('0', 64) }));
    foreach (var path in new[] { "../escape.dll", "/absolute.dll", "a/../../escape.dll", "C:/escape.dll", "a\\escape.dll", "CON.dll" })
    {
        http.Bytes = Zip(path);
        await Reject(() => Service().StageInstallAsync(Manifest(http.Bytes)));
    }
    http.Bytes = Zip("probe.dll", unchecked((int)0xa1ff0000));
    await Reject(() => Service().StageInstallAsync(Manifest(http.Bytes)));
    http.Bytes = Zip(large: true);
    await Reject(() => Service().StageInstallAsync(Manifest(http.Bytes)));
    http.Bytes = good;
    var staged = await Service().StageInstallAsync(manifest);
    Check(staged.Active == null && staged.Pending != null, "install stages");
    var disabled = await Service().LoadAtStartupAsync(context);
    Check(disabled.Loaded.Count == 0 && (await Service().GetInstalledAsync())[0].Pending != null, "default disabled");
    var first = await Service().LoadAtStartupAsync(context, isEnabled: _ => true);
    Check(first.Loaded.Count == 1 && first.Failures.Count == 0, "real DLL startup");
    var configFile = Path.Combine(root, "test.plugin", "config", "marker.txt");
    Check(File.Exists(configFile), "PluginContext config path");
    await first.Loaded[0].InitializeAsync(CancellationToken.None);
    Check(await File.ReadAllTextAsync(configFile) == "1", "no double initialization");
    await first.Loaded[0].ShutdownAsync(CancellationToken.None);
    var second = manifest with { Version = "2.0.0", EntryType = "SyntheticPluginV2" };
    await Service().StageInstallAsync(second);
    Check((await Service().GetInstalledAsync())[0].Active?.Version == "1.0.0", "update deferred");
    var update = await Service().LoadAtStartupAsync(context, isEnabled: _ => true);
    Check(update.Loaded.Single().Version == "2.0.0", "update activated");
    Check((await Service().GetInstalledAsync())[0].Previous?.Version == "1.0.0", "previous retained");
    await update.Loaded[0].ShutdownAsync(CancellationToken.None);
    await Service().StageInstallAsync(manifest with { Version = "3.0.0", EntryType = "BrokenPlugin" });
    var fallback = await Service().LoadAtStartupAsync(context, isEnabled: _ => true);
    Check(fallback.Loaded.Single().Version == "2.0.0" && fallback.Failures.Count == 1, "failed update fallback");
    await fallback.Loaded[0].ShutdownAsync(CancellationToken.None);
    await Service().StageInstallAsync(Manifest(good, "test.broken", type: "NotAType"));
    var isolated = await Service().LoadAtStartupAsync(context, isEnabled: _ => true);
    Check(isolated.Loaded.Count == 1 && isolated.Failures.Count == 2, "independent failure");
    await isolated.Loaded[0].ShutdownAsync(CancellationToken.None);
    var collision = await Service().LoadAtStartupAsync(context, _ => true, _ => true);
    Check(collision.Loaded.Count == 0 && collision.Failures.Count == 2, "registry collision");
    await Service().StageUninstallAsync("test.plugin");
    Check((await Service().GetInstalledAsync()).Single(x => x.Id == "test.plugin").Active != null, "uninstall staged");
    await Service().LoadAtStartupAsync(context);
    var removed = (await Service().GetInstalledAsync()).Single(x => x.Id == "test.plugin");
    Check(removed.Active == null && removed.Previous != null && File.Exists(configFile), "uninstall preserves version and config");
    Console.WriteLine($"PASS plugin package probe: {checks} checks");
}
finally
{
    // Collect collectible contexts so Windows releases mapped fixture assemblies.
    // Best effort: a still-loaded collectible context can keep probe.dll mapped, so retry briefly
    // and never fail the probe for a temp-directory cleanup problem.
    for (var attempt = 0; attempt < 5; attempt++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        try { Directory.Delete(root, true); break; }
        catch (Exception) when (attempt < 4) { Thread.Sleep(200); }
        catch (Exception) { }
    }
}

public class SyntheticPlugin : IBotPlugin
{
    public string Id => "test.plugin";
    public string Name => "Synthetic";
    public virtual string Version => "1.0.0";
    public string Description => "Synthetic fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public virtual async Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        var path = Path.Combine(context.EnvironmentInfo["pluginConfigDirectory"], "marker.txt");
        var previous = File.Exists(path) ? int.Parse(await File.ReadAllTextAsync(path, ct)) : 0;
        await File.WriteAllTextAsync(path, (previous + 1).ToString(), ct);
    }
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
public sealed class SyntheticPluginV2 : SyntheticPlugin { public override string Version => "2.0.0"; }
public sealed class BrokenPlugin : SyntheticPlugin
{
    public override string Version => "3.0.0";
    public override Task InitializeAsync(PluginContext context, CancellationToken ct) => throw new InvalidOperationException("synthetic failure");
}
sealed class EmptyServices : IServiceProvider { public object? GetService(Type type) => null; }
sealed class FakeHttp : IHttpFetcher
{
    public byte[] Bytes { get; set; } = [];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => SendAsync(new(HttpMethod.Get, url), ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => GetAsync(url, ct);
}

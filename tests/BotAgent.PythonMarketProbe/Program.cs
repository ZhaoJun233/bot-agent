using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Mcp;
using BotAgent.Adapters.Net;
using BotAgent.Panel;
using BotAgent.Services.Net;
using BotAgent.Services.Plugins;

// Native product package lifecycle, synthetic files/HTTP only; no real sessions or networking.
if (args.Length is not (1 or 3)) throw new ArgumentException("Usage: probe <absolute-python> OR <fixture.json> <artifact-directory> <absolute-python>");
var root = Path.Combine(Path.GetTempPath(), "python-market-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
if (args.Length == 1) args = ProbeFixture.Prepare(root, args[0]);
Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
Environment.SetEnvironmentVariable("BOTAGENT_FILE_LOG", "0");
Environment.SetEnvironmentVariable("BOTAGENT_PLUGIN_CATALOG_URL", "https://example.com/index.json");
Environment.SetEnvironmentVariable("BOTAGENT_PYTHON_EXECUTABLE", args[2]);
var count = 0;
void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
var host = typeof(PluginPackageService).Assembly;
var validate = host.GetType("BotAgent.Adapters.Plugins.PluginPackageValidation")!.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic, new[] { typeof(PluginPackageManifest), typeof(bool) })!;
bool Accept(JsonNode node)
{
    try
    {
        var manifest = node.Deserialize<PluginPackageManifest>()!;
        validate.Invoke(null, new object?[] { manifest, false });
        return true;
    }
    catch (Exception ex) when (ex is TargetInvocationException or JsonException) { return false; }
}
var fixture = JsonNode.Parse(await File.ReadAllTextAsync(args[0]))!;
var baseManifest = fixture["manifest"]!;
foreach (var item in fixture["runtimeCases"]!.AsArray())
{
    var modified = baseManifest.DeepClone();
    modified["Runtime"]![item!["field"]!.GetValue<string>()] = item["value"]?.DeepClone();
    Check(Accept(modified) == item["accepted"]!.GetValue<bool>(), "shared runtime fixture " + item["name"]);
}
foreach (var field in new[] { "EntryAssembly", "EntryType", "TargetFramework" })
{
    var mixed = baseManifest.DeepClone(); mixed[field] = null;
    Check(!Accept(mixed), "managed null field rejected " + field);
}
var raw = JsonSerializer.Serialize(baseManifest.Deserialize<PluginPackageManifest>());
Check(!raw.Contains("EntryAssembly") && raw.Contains("Runtime"), "runtime-specific manifest projection");
var parseFeed = host.GetType("BotAgent.Adapters.Plugins.PluginPythonRepository")!.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
var validFeed = new JsonObject { ["SchemaVersion"] = 2, ["Plugins"] = new JsonArray(new JsonObject
    { ["Id"] = baseManifest["Id"]!.DeepClone(), ["Versions"] = new JsonArray(baseManifest.DeepClone()) }) };
Check(((IReadOnlyList<PluginPackageManifest>)parseFeed.Invoke(null, new object[] { Encoding.UTF8.GetBytes(validFeed.ToJsonString()) })!).Count == 1, "grouped Python feed parsed");
var manifestPath = Path.Combine(args[1], "manifest.json");
var package = JsonSerializer.Deserialize<PluginPackageManifest>(await File.ReadAllTextAsync(manifestPath))!;
var zip = await File.ReadAllBytesAsync(Path.Combine(args[1], package.Id + "-" + package.Version + ".zip"));
var fetch = new SyntheticHttp();
fetch.Bytes[package.DownloadUrl] = zip;
fetch.Bytes["https://example.com/index.json"] = Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"Plugins\":[]}");
fetch.Bytes["https://example.com/python-repository.json"] = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 2, Plugins = new[] { new { package.Id, Versions = new[] { package } } } });
var service = new PluginPackageService(Path.Combine(root, "packages"), fetch);
var registry = new PluginManager(_ => { });
var context = new PluginContext(new EmptyServices());
var desired = new Dictionary<string, bool> { [package.Id] = false };
async Task Reload(string? id, CancellationToken ct)
{
    if (id is not null) await service.ApplyAsync(id, context, registry, desired.GetValueOrDefault(id), ct, _ => "{\"prefix\":\"synthetic: \"}");
}
var market = new PluginMarketService(service, new PluginSettingsStore(), new PluginCatalogSource(fetch),
    () => DateTimeOffset.UtcNow, Path.Combine(root, "receipts.json"), () => desired, reload: Reload, registrySnapshot: registry.GetAll);
try
{
    AppDatabase.Initialize();
    var catalog = await market.ReadCatalogAsync(null, null, default);
    Check(catalog.Plugins.Count == 1 && catalog.Plugins[0].IsCompatible, "Python catalog compatibility");
    Environment.SetEnvironmentVariable("BOTAGENT_PYTHON_EXECUTABLE", "");
    var unavailable = await market.ReadCatalogAsync(null, null, default);
    Check(!unavailable.Plugins[0].IsCompatible, "missing interpreter not installable");
    var blocked = await market.PreviewAsync(new(Origin: "json", Manifest: package, ExpectedRevision: (await market.ReadInstalledAsync(default)).Revision), "synthetic-admin", default);
    Check(!blocked.CanInstall && blocked.Checks.Any(c => c.Code == "python-runtime" && c.Status == "blocked"), "fresh preview interpreter check");
    Environment.SetEnvironmentVariable("BOTAGENT_PYTHON_EXECUTABLE", args[2]);
    var preview = await market.PreviewAsync(new(Origin: "json", Manifest: package, ExpectedRevision: (await market.ReadInstalledAsync(default)).Revision), "synthetic-admin", default);
    Check(preview.CanInstall, "valid Python package preview");
    var installed = await market.InstallAsync(new(package, preview.Revision, Guid.NewGuid().ToString("N"), true) { PreviewId = preview.PreviewId }, "synthetic-admin", default, _ => Task.CompletedTask);
    Check(installed.Operation.Status != "failed", "package install receipt");
    Check(!service.PythonSources.Enabled && (await service.GetInstalledAsync()).Single().Active?.ApiVersion == 2, "installed disabled executes no Python");
    desired[package.Id] = true; await Reload(package.Id, default);
    var useCase = new McpToolUseCase(service.PythonSources);
    await useCase.RefreshAsync(default);
    var tool = (await service.PythonSources.DiscoverAsync(default)).Single();
    var lease = useCase.CaptureLocalLeases(new[] { tool.QualifiedName }).Single();
    Check(tool.IsLocalProcess && tool.SourceGeneration!.Contains(':'), "opaque owner-generation discovery lease");
    Check(!(await useCase.ExecuteAsync("synthetic-session", tool.QualifiedName, new JsonObject { ["text"] = "hello" }, 1, default)).Ok, "ordinary call lacks approved task lease");
    Check((await useCase.ExecuteWithLeaseAsync("synthetic-session", lease, new JsonObject { ["text"] = "hello" }, 1, default)).Ok, "governed package call");
    desired[package.Id] = false; await Reload(package.Id, default);
    Check(!service.PythonSources.Enabled && !(await useCase.ExecuteWithLeaseAsync("synthetic-session", lease, new JsonObject(), 1, default)).Ok, "disable revokes admitted source");
    desired[package.Id] = true; await Reload(package.Id, default); await useCase.RefreshAsync(default);
    Check(!(await useCase.ExecuteWithLeaseAsync("synthetic-session", lease, new JsonObject { ["text"] = "hello" }, 1, default)).Ok, "reenable same generation counter different owner rejected");
    var update = package with { Version = "1.0.1", DownloadUrl = package.DownloadUrl.Replace("1.0.0", "1.0.1", StringComparison.Ordinal) };
    fetch.Bytes[update.DownloadUrl] = zip;
    var updatePreview = await market.PreviewLifecycleAsync(new("update", package.Id, (await market.ReadInstalledAsync(default)).Revision, new("json", Manifest: update)), "synthetic-admin");
    if (!updatePreview.CanApply) Console.WriteLine(JsonSerializer.Serialize(updatePreview.Checks));
    Check(updatePreview.CanApply, "Python update preview");
    var updateResult = await market.UpdateAsync(new(update, updatePreview.Revision, Guid.NewGuid().ToString("N"), true) { PreviewId = updatePreview.PreviewId }, "synthetic-admin", default);
    Check(updateResult.Operation.Status != "failed" && (await service.GetInstalledAsync()).Single().Active?.Version == "1.0.1", "Python update hot applied");
    var rollbackPreview = await market.PreviewLifecycleAsync(new("rollback", package.Id, (await market.ReadInstalledAsync(default)).Revision), "synthetic-admin");
    Check(rollbackPreview.CanApply && rollbackPreview.TargetManifest?.Version == "1.0.0", "Python rollback preview");
    var rollback = await market.RollbackAsync(new(package, rollbackPreview.Revision, Guid.NewGuid().ToString("N"), true) { PreviewId = rollbackPreview.PreviewId }, "synthetic-admin", default);
    Check(rollback.Operation.Status != "failed" && (await service.GetInstalledAsync()).Single().Active?.Version == "1.0.0", "Python rollback restores previous artifact");
    var uninstallPreview = await market.PreviewLifecycleAsync(new("uninstall", package.Id, (await market.ReadInstalledAsync(default)).Revision), "synthetic-admin");
    Check(uninstallPreview.CanApply, "Python uninstall preview");
    var uninstall = await market.UninstallAsync(package.Id, new(uninstallPreview.Revision, Guid.NewGuid().ToString("N")) { PreviewId = uninstallPreview.PreviewId, TrustAcknowledged = true }, "synthetic-admin", default);
    Check(uninstall.Operation.Status != "failed", "Python uninstall receipt");
    Check(!service.PythonSources.Enabled && (await service.GetInstalledAsync()).Single().Active is null, "uninstall removes Python source");
    Console.WriteLine(JsonSerializer.Serialize(new { passed = count, failed = 0, synthetic = true }));
}
finally
{
    try { await registry.RemoveAsync(package.Id); } catch { }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, true); } catch { }
}

sealed class EmptyServices : IServiceProvider { public object? GetService(Type type) => null; }
sealed class SyntheticHttp : IHttpFetcher
{
    public Dictionary<string, byte[]> Bytes { get; } = new(StringComparer.Ordinal);
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default) => GetAsync(request.RequestUri!, ct);
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => GetAsync(new Uri(url), ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => GetAsync(url, ct);
    public Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ok = Bytes.TryGetValue(uri.AbsoluteUri, out var data);
        return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            { Content = new ByteArrayContent(data ?? []), RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri) });
    }
}

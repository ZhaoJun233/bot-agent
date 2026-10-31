using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;
using BotAgent.Services.Net;

// 合成市场探针：索引、包与回执全部本地构造，不连 GitHub、不读真实 data/。
var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "botagent-plugin-market-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
Environment.SetEnvironmentVariable("BOTAGENT_FILE_LOG", "0");
Environment.SetEnvironmentVariable("QQCHAT_FILE_LOG", "0");
Environment.SetEnvironmentVariable("BOTAGENT_PLUGIN_CATALOG_URL", "https://example.com/market/index.json");

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    checks++;
}
async Task ExpectCode(Func<Task> action, string code, string label)
{
    try { await action(); }
    catch (PluginCatalogException ex) when (ex.Code == code) { checks++; return; }
    throw new InvalidOperationException(label);
}

static byte[] Zip(string name, byte[] payload)
{
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(payload);
    }
    return bytes.ToArray();
}

PluginPackageManifest Manifest(byte[] package, string id = "probe.market", string version = "1.0.0",
    IReadOnlyList<PluginSettingDefinition>? settings = null) => new(id, version, "probe.dll", "SyntheticMarketPlugin",
    Convert.ToHexString(SHA256.HashData(package)),
    "https://github.com/example-org/example-plugins/releases/download/" + id + "-" + version + "/" + id + "-" + version + ".zip",
    1, "net10.0", "1.0.0", Array.Empty<string>(), Array.Empty<PluginPackageDependency>(),
    settings ?? Array.Empty<PluginSettingDefinition>());

string Index(params PluginPackageManifest[] manifests)
    => JsonSerializer.Serialize(new
    {
        SchemaVersion = 1,
        Plugins = manifests.Select(manifest => new
        {
            Id = manifest.Id,
            Version = manifest.Version,
            EntryAssembly = manifest.EntryAssembly,
            EntryType = manifest.EntryType,
            Sha256 = manifest.Sha256,
            DownloadUrl = manifest.DownloadUrl,
            ApiVersion = manifest.ApiVersion,
            TargetFramework = manifest.TargetFramework,
            MinimumHostVersion = manifest.MinimumHostVersion,
            Permissions = manifest.Permissions,
            Dependencies = manifest.Dependencies,
            Settings = manifest.Settings
        }).ToArray()
    });

try
{
    AppDatabase.Initialize();
    var package = Zip("probe.dll", Encoding.UTF8.GetBytes("synthetic"));
    var settings = new[]
    {
        new PluginSettingDefinition("greeting", "问候语", "string", "hello"),
        new PluginSettingDefinition("retries", "重试次数", "number", "3"),
        new PluginSettingDefinition("verbose", "详细日志", "boolean", "false"),
        new PluginSettingDefinition("token", "接口密钥", "secret", Required: true)
    };
    var manifest = Manifest(package, settings: settings);
    var http = new ScriptedHttp();
    var catalog = new PluginCatalogSource(http, _ => { });
    var packages = new PluginPackageService(Path.Combine(root, "packages"), http);
    var store = new PluginSettingsStore();
    var receiptPath = Path.Combine(root, "operations.json");
    var market = new PluginMarketService(packages, store, catalog, () => DateTimeOffset.UnixEpoch, receiptPath,
        () => new Dictionary<string, bool>(StringComparer.Ordinal) { [manifest.Id] = true });

    // ── 索引解析 ──
    http.Text = Index(manifest);
    var parsed = await catalog.ReadAsync("https://example.com/market/index.json", CancellationToken.None);
    Check(parsed.Count == 1 && parsed[0].Id == manifest.Id, "catalog_parses_pascal_case_index");
    await ExpectCode(() => catalog.ReadAsync("http://example.com/market/index.json", CancellationToken.None),
        "plugin_catalog_source_invalid", "catalog_rejects_http");
    await ExpectCode(() => catalog.ReadAsync("https://127.0.0.1/market/index.json", CancellationToken.None),
        "plugin_catalog_source_invalid", "catalog_rejects_loopback");
    http.Text = "{\"SchemaVersion\":1,\"Plugins\":[],\"Extra\":1}";
    await ExpectCode(() => catalog.ReadAsync("https://example.com/market/index.json", CancellationToken.None),
        "plugin_catalog_malformed", "catalog_rejects_unknown_field");
    http.Text = "{\"SchemaVersion\":2,\"Plugins\":[]}";
    await ExpectCode(() => catalog.ReadAsync("https://example.com/market/index.json", CancellationToken.None),
        "plugin_catalog_malformed", "catalog_rejects_schema_version");
    http.Text = Index(manifest, manifest with { Version = "1.0.1" });
    await ExpectCode(() => catalog.ReadAsync("https://example.com/market/index.json", CancellationToken.None),
        "plugin_catalog_malformed", "catalog_rejects_duplicate_id");
    http.Text = Index(manifest with { Version = "latest" });
    await ExpectCode(() => catalog.ReadAsync("https://example.com/market/index.json", CancellationToken.None),
        "plugin_catalog_malformed", "catalog_rejects_floating_version");
    http.Status = HttpStatusCode.NotFound;
    await ExpectCode(() => catalog.ReadAsync("https://example.com/market/index.json", CancellationToken.None),
        "plugin_catalog_unavailable", "catalog_reports_unavailable");
    http.Status = HttpStatusCode.OK;

    // ── 目录 / 安装态投影 ──
    http.Text = Index(manifest);
    var empty = await market.ReadCatalogAsync(null, null, CancellationToken.None);
    Check(empty.Plugins.Count == 1 && empty.Plugins[0].Status == PluginMarketService.StatusNotInstalled,
        "catalog_entry_not_installed");
    Check(empty.TrustNotice.Contains("沙箱", StringComparison.Ordinal), "catalog_states_no_sandbox");
    var filtered = await market.ReadCatalogAsync("nomatch", null, CancellationToken.None);
    Check(filtered.Plugins.Count == 0, "catalog_query_filters");
    var installedEmpty = await market.ReadInstalledAsync(CancellationToken.None);
    Check(installedEmpty.Plugins.Count == 0, "installed_empty_before_install");

    // ── 安装：暂存 + 回执 + 幂等 ──
    http.Bytes = package;
    http.Text = Index(manifest);
    var revision = (await market.ReadCatalogAsync(null, null, CancellationToken.None)).Revision;
    var install = await market.InstallAsync(new ZbaPluginMutationRequest(manifest, revision, "plugin:install:probe-1", true),
        "probe", CancellationToken.None);
    Check(install.Operation.Status == PluginMarketService.StatusRestartRequired
        && install.Operation.RestartRequired, "install_stages_restart_required");
    var replay = await market.InstallAsync(new ZbaPluginMutationRequest(manifest, "stale", "plugin:install:probe-1", true),
        "probe", CancellationToken.None);
    Check(replay.Operation.Id == install.Operation.Id && replay.Revision == install.Revision,
        "install_replay_returns_same_receipt_before_revision_check");
    Check(market.FindOperation("plugin:install:probe-1").Operation.Id == install.Operation.Id, "receipt_findable");
    // 回执必须跨进程可核对（重启后 POST 响应丢了仍能证明“已受理”），且文件里没有清单正文与密钥。
    var restarted = new PluginMarketService(packages, store, catalog, () => DateTimeOffset.UnixEpoch, receiptPath);
    Check(restarted.FindOperation("plugin:install:probe-1").Operation.Id == install.Operation.Id,
        "receipt_survives_restart");
    var receiptFile = await File.ReadAllTextAsync(receiptPath);
    Check(!receiptFile.Contains(manifest.DownloadUrl, StringComparison.Ordinal)
        && !receiptFile.Contains(manifest.Sha256, StringComparison.Ordinal), "receipt_stores_no_manifest_body");
    try { market.FindOperation("plugin:install:unknown"); throw new InvalidOperationException("expected 404"); }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_operation_not_found") { checks++; }
    var afterInstall = await market.ReadInstalledAsync(CancellationToken.None);
    Check(afterInstall.Plugins.Single().Status == PluginMarketService.StatusRestartRequired,
        "installed_shows_pending_restart");
    Check((await market.ReadCatalogAsync(null, null, CancellationToken.None)).Revision != revision,
        "revision_changes_after_staging");

    // ── 代际冲突与失败回执 ──
    try
    {
        await market.InstallAsync(new ZbaPluginMutationRequest(manifest, "stale-revision", "plugin:install:probe-2", true),
            "probe", CancellationToken.None);
        throw new InvalidOperationException("expected revision conflict");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_revision_conflict") { checks++; }

    http.Bytes = Zip("probe.dll", Encoding.UTF8.GetBytes("different"));
    var badManifest = manifest with { Sha256 = new string('b', 64) };
    var currentRevision = (await market.ReadCatalogAsync(null, null, CancellationToken.None)).Revision;
    var failed = await market.InstallAsync(new ZbaPluginMutationRequest(badManifest, currentRevision,
        "plugin:install:probe-3", true), "probe", CancellationToken.None);
    Check(failed.Operation.Status == PluginMarketService.StatusFailed
        && failed.Operation.ErrorCode == "plugin_package_invalid", "hash_mismatch_yields_failed_receipt");

    // ── 声明式设置：读取、保存、秘密不回显 ──
    var read = await market.ReadSettingsAsync(manifest.Id, CancellationToken.None);
    Check(read.Schema.Count == 4 && !read.Values.ContainsKey("token"), "settings_hide_secrets");
    Check(read.ConfiguredSecrets["token"] == false, "settings_secret_unconfigured");
    try
    {
        await market.SaveSettingsAsync(manifest.Id,
            new ZbaPluginSettingsSaveRequest(read.Revision, "plugin:settings:probe-1",
                new[] { new ZbaPluginSettingEdit("token", 0, null) }), "probe", CancellationToken.None);
        throw new InvalidOperationException("expected required secret failure");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == PluginSettingsContract.RequiredMissing) { checks++; }

    var saved = await market.SaveSettingsAsync(manifest.Id,
        new ZbaPluginSettingsSaveRequest(read.Revision, "plugin:settings:probe-2", new[]
        {
            new ZbaPluginSettingEdit("greeting", 1, "hi"),
            new ZbaPluginSettingEdit("retries", 1, "5"),
            new ZbaPluginSettingEdit("verbose", 1, "true"),
            new ZbaPluginSettingEdit("token", 1, "synthetic-token")
        }), "probe", CancellationToken.None);
    Check(saved.Values["greeting"] == "hi" && saved.Values["retries"] == "5" && saved.Values["verbose"] == "true",
        "settings_saved_typed_values");
    Check(saved.ConfiguredSecrets["token"] && !saved.Values.ContainsKey("token"), "settings_secret_configured_not_echoed");
    Check(saved.RestartRequired, "settings_save_requires_restart");
    try
    {
        await market.SaveSettingsAsync(manifest.Id,
            new ZbaPluginSettingsSaveRequest(read.Revision, "plugin:settings:probe-3",
                new[] { new ZbaPluginSettingEdit("greeting", 1, "stale") }), "probe", CancellationToken.None);
        throw new InvalidOperationException("expected settings revision conflict");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_settings_revision_conflict") { checks++; }
    try
    {
        await market.SaveSettingsAsync(manifest.Id,
            new ZbaPluginSettingsSaveRequest(saved.Revision, "plugin:settings:probe-4",
                new[] { new ZbaPluginSettingEdit("nope", 1, "x") }), "probe", CancellationToken.None);
        throw new InvalidOperationException("expected unknown key rejection");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "invalid_plugin_setting_key") { checks++; }
    try
    {
        await market.SaveSettingsAsync(manifest.Id,
            new ZbaPluginSettingsSaveRequest(saved.Revision, "plugin:settings:probe-5",
                new[] { new ZbaPluginSettingEdit("greeting", 2, string.Empty) }), "probe", CancellationToken.None);
        throw new InvalidOperationException("expected non-secret delete rejection");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_setting_delete_unsupported") { checks++; }
    try
    {
        await market.SaveSettingsAsync(manifest.Id,
            new ZbaPluginSettingsSaveRequest(saved.Revision, "plugin:settings:probe-6",
                new[] { new ZbaPluginSettingEdit("retries", 1, "not-a-number") }), "probe", CancellationToken.None);
        throw new InvalidOperationException("expected number rejection");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "invalid_plugin_setting_value") { checks++; }

    // ── 来源解析：只认固定 release tag ──
    var releaseUrl = "https://github.com/example-org/example-plugins/releases/tag/" + manifest.Id + "-" + manifest.Version;
    http.Text = Index(manifest);
    var resolved = await market.ResolveSourceAsync(releaseUrl, CancellationToken.None);
    Check(resolved.Manifest.Id == manifest.Id, "resolve_pinned_release");
    // 仓库主页只在索引里恰好一个插件时才可用；多插件必须给固定 tag，不许猜。
    var single = await market.ResolveSourceAsync("https://github.com/example-org/example-plugins", CancellationToken.None);
    Check(single.Manifest.Id == manifest.Id, "resolve_repo_root_single_plugin");
    http.Text = Index(manifest, manifest with { Id = "probe.market.two" });
    try
    {
        await market.ResolveSourceAsync("https://github.com/example-org/example-plugins", CancellationToken.None);
        throw new InvalidOperationException("expected ambiguous repo root rejection");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_source_ambiguous") { checks++; }
    try
    {
        await market.ResolveSourceAsync("https://github.com/example-org", CancellationToken.None);
        throw new InvalidOperationException("expected owner-only rejection");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_source_invalid") { checks++; }
    try
    {
        await market.ResolveSourceAsync("https://evil.example/example-org/example-plugins/releases/tag/x",
            CancellationToken.None);
        throw new InvalidOperationException("expected host rejection");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_source_invalid") { checks++; }
    http.Text = Index(manifest);
    try
    {
        await market.ResolveSourceAsync("https://github.com/example-org/example-plugins/releases/tag/other-9.9.9",
            CancellationToken.None);
        throw new InvalidOperationException("expected not found");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_source_not_found") { checks++; }

    // ── 卸载 ──
    var uninstallRevision = (await market.ReadCatalogAsync(null, null, CancellationToken.None)).Revision;
    var uninstall = await market.UninstallAsync(manifest.Id,
        new ZbaPluginUninstallRequest(uninstallRevision, "plugin:uninstall:probe-1"), "probe", CancellationToken.None);
    Check(uninstall.Operation.Kind == "uninstall" && uninstall.Operation.RestartRequired, "uninstall_stages_restart");
    var pendingRemoval = await market.ReadInstalledAsync(CancellationToken.None);
    Check(pendingRemoval.Plugins.Single().Status == PluginMarketService.StatusPendingUninstall,
        "uninstall_visible_before_restart");
    try
    {
        await market.UninstallAsync("probe.absent",
            new ZbaPluginUninstallRequest(pendingRemoval.Revision, "plugin:uninstall:probe-2"), "probe",
            CancellationToken.None);
        throw new InvalidOperationException("expected not installed");
    }
    catch (BotAgent.Panel.ZbaBackendException ex) when (ex.Code == "plugin_not_installed") { checks++; }

    checks += await Program.RunReviewPluginRegressionAsync(root);
    Console.WriteLine($"PASS plugin market probe: {checks} checks");
}
finally
{
    try { Directory.Delete(root, true); } catch (Exception) { }
}

internal sealed class SyntheticMarketPlugin : IBotPlugin
{
    public string Id => "probe.market";
    public string Name => "Synthetic market plugin";
    public string Version => "1.0.0";
    public string Description => "Synthetic fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>可编排的假 HTTP：按顺序返回预设状态码与正文，绝不发真实请求。</summary>
internal sealed class ScriptedHttp : IHttpFetcher
{
    public string Text { get; set; } = string.Empty;
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public int Calls { get; private set; }
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option,
        CancellationToken ct = default)
    {
        Calls++;
        // 市场索引与包下载走同一个假客户端：按 URL 区分，绝不把 ZIP 当成索引解析。
        var isDownload = request.RequestUri?.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal) == true;
        var content = new ByteArrayContent(isDownload && Bytes.Length > 0 ? Bytes : Encoding.UTF8.GetBytes(Text));
        return Task.FromResult(new HttpResponseMessage(Status) { Content = content });
    }

    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default)
        => GetAsync(url.ToString(), ct);
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default)
        => GetAsync(url, ct);
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        => GetAsync(url, ct);
}

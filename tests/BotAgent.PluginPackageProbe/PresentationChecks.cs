using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal static class PresentationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "presentation-probe-" + Guid.NewGuid().ToString("N"));
        var oldUrl = Environment.GetEnvironmentVariable("BOTAGENT_PLUGIN_CATALOG_URL");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("BOTAGENT_PLUGIN_CATALOG_URL", "https://example.com/index.json");
            var manifest = new PluginPackageManifest("test.community", "1.0.0", "fixture.dll", "Fixture",
                new string('a', 64), "https://example.com/releases/v1/fixture.zip");
            var http = new PresentationHttp { Index = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Plugins = new[] { manifest } }) };
            IReadOnlyList<PluginInfo> registry = [new("preset.feature.synthetic", "合成内置功能", "1.0.0", "离线注册表说明",
                PluginCategory.Feature, true, false), new("external.plugin", "外部", "1.0.0", "非内置", PluginCategory.Feature, false, true)];
            var packages = new PluginPackageService(root, http);
            var market = new PluginMarketService(packages, new PluginSettingsStore(), new PluginCatalogSource(http),
                () => DateTimeOffset.UtcNow, Path.Combine(root, "receipts.json"), () => new Dictionary<string, bool>(),
                registrySnapshot: () => registry);
            var installed = await market.ReadInstalledAsync(default);
            check(installed.BuiltinPlugins.Count == 1 && installed.Plugins.Count == 0 && http.Requests == 0,
                "installed builtins come from registry offline, not package manifests");
            check(installed.BuiltinPlugins[0].Metadata.Review is { Status: "unreviewed", Sha256: null }, "bundled is not reviewed");
            var legacy = await market.ReadCatalogAsync(null, null, default);
            check(legacy.Plugins.Count == 1 && legacy.Plugins[0].Metadata is null && legacy.MetadataNotice is not null,
                "missing sidecar preserves legacy catalog");
            var doc = Document(manifest);
            http.Sidecar = Bytes(doc);
            var rich = await market.ReadCatalogAsync(null, null, default);
            check(rich.MetadataNotice is null && rich.BuiltinPlugins.Count == 1 && rich.Plugins[0].Metadata?.Name == "合成示例包", "sidecar joins valid packages and registered builtins");
            check(rich.BuiltinPlugins[0].PluginId == "preset.feature.synthetic" && !rich.BuiltinPlugins[0].Enabled, "registry controls identity and enabled state");
            check(rich.Categories.Contains("合成分类"), "metadata contributes categories");
            check((await market.ReadCatalogAsync("搜索标签", null, default)).Plugins.Count == 1, "search tags");
            check((await market.ReadCatalogAsync("合成示例", "合成分类", default)).Plugins.Count == 1, "search localized name and category");
            check((await market.ReadCatalogAsync(null, "不存在", default)).BuiltinPlugins.Count == 0, "category filters builtins");
            var metadata = doc["Entries"]![0]!["Metadata"]!;
            var review = metadata["Review"]!;
            review["Status"] = "reviewed"; review["Reviewer"] = "Synthetic reviewer";
            review["ReviewedAt"] = "2026-01-01T00:00:00Z"; review["SourceRevision"] = new string('b', 40);
            http.Sidecar = Bytes(doc);
            check((await market.ReadCatalogAsync(null, null, default)).Plugins[0].Metadata?.Review.Status == "reviewed", "exact artifact review binds");
            foreach (var (revision, status) in new[] { (new string('B', 64), "reviewed"), (new string('b', 41), "unreviewed"), (new string('B', 40), "reviewed"), (new string('b', 40) + "\n", "unreviewed") })
            {
                review["SourceRevision"] = revision; http.Sidecar = Bytes(doc);
                check((await market.ReadCatalogAsync(null, null, default)).Plugins[0].Metadata?.Review.Status == status, "review source revision exact hash length");
            }
            foreach (var timestamp in new[] { "2026/01/01T00:00:00Z", "2026-01-01T00:00:00", "2026-02-30T00:00:00Z", "2026-01-01T00:00:00Z\n" })
            {
                review["ReviewedAt"] = timestamp; http.Sidecar = Bytes(doc);
                check((await market.ReadCatalogAsync(null, null, default)).Plugins[0].Metadata?.Review.Status == "unreviewed", "review timestamp must be real RFC3339");
            }
            review["ReviewedAt"] = "2026-01-01T00:00:00Z";
            review["Version"] = "2.0.0"; http.Sidecar = Bytes(doc);
            check((await market.ReadCatalogAsync(null, null, default)).Plugins[0].Metadata?.Review.Status == "unreviewed", "wrong review version downgrades");
            review["Version"] = "1.0.0"; review["Sha256"] = new string('c', 64); http.Sidecar = Bytes(doc);
            check((await market.ReadCatalogAsync(null, null, default)).Plugins[0].Metadata?.Review.Status == "unreviewed", "wrong review hash downgrades");
            review["Sha256"] = manifest.Sha256; review["SourceRevision"] = "main"; http.Sidecar = Bytes(doc);
            check((await market.ReadCatalogAsync(null, null, default)).Plugins[0].Metadata?.Review.Status == "unreviewed", "mutable review source downgrades");
            doc["Entries"]![0]!["PackageSha256"] = new string('d', 64); http.Sidecar = Bytes(doc);
            check((await market.ReadCatalogAsync(null, null, default)).Plugins[0].Metadata is null, "wrong outer artifact hash omits metadata");
            foreach (var change in InvalidChanges())
            {
                var invalid = Document(manifest); change(invalid); http.Sidecar = Bytes(invalid);
                var result = await market.ReadCatalogAsync(null, null, default);
                check(result.MetadataNotice is not null && result.Plugins.Count == 1 && result.Plugins[0].Metadata is null,
                    "invalid sidecar degrades without losing manifest");
            }
            http.Sidecar = Encoding.UTF8.GetBytes("{bad json");
            check((await market.ReadCatalogAsync(null, null, default)).MetadataNotice is not null, "malformed JSON fallback");
            http.Sidecar = Encoding.UTF8.GetBytes("""{"SchemaVersion":1,"SchemaVersion":1,"Entries":[]}""");
            check((await market.ReadCatalogAsync(null, null, default)).MetadataNotice is not null, "duplicate properties rejected");
            http.Sidecar = new byte[2 * 1024 * 1024 + 1];
            check((await market.ReadCatalogAsync(null, null, default)).MetadataNotice is not null, "oversized sidecar rejected");
            http.Offline = true;
            var offline = await market.ReadCatalogAsync(null, null, default);
            check(offline.Plugins.Count == 0 && offline.BuiltinPlugins.Count == 1 && offline.MetadataNotice is not null, "offline index retains registered builtins");
            registry = [registry[0] with { IsEnabled = true }];
            check((await market.ReadInstalledAsync(default)).BuiltinPlugins[0].Enabled, "installed state reflects fresh registry");
            registry = [];
            try { await market.ReadCatalogAsync(null, null, default); throw new Exception("offline catalog unexpectedly succeeded without builtins"); }
            catch (ZbaBackendException ex) { check(ex.Status == 503, "offline legacy catalog without builtins preserves service unavailable"); }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await market.ReadCatalogAsync(null, null, cancellation.Token); throw new Exception("cancellation swallowed"); }
            catch (OperationCanceledException) { check(true, "caller cancellation is preserved"); }
            CheckContracts(check, rich);
        }
        finally { Environment.SetEnvironmentVariable("BOTAGENT_PLUGIN_CATALOG_URL", oldUrl); Directory.Delete(root, true); }
    }

    private static JsonObject Document(PluginPackageManifest manifest) => new()
    {
        ["SchemaVersion"] = 1,
        ["Entries"] = new JsonArray(Entry(manifest.Id, manifest.Sha256, false), Entry("preset.feature.synthetic", null, true),
            Entry("preset.not.registered", null, true))
    };
    private static JsonObject Entry(string id, string? hash, bool builtin)
    {
        var metadata = new JsonObject();
        foreach (var key in new[] { "Summary", "Description", "Author", "License", "Usage", "RiskNotice", "Configuration",
            "Compatibility", "Changelog", "Maintenance", "PermissionNotes" }) metadata[key] = "合成资料";
        metadata["Name"] = builtin ? "合成内置" : "合成示例包"; metadata["Category"] = "合成分类";
        metadata["Repository"] = "https://github.com/example/test-plugin"; metadata["Tags"] = new JsonArray("搜索标签");
        metadata["Runtime"] = builtin ? "builtin" : "dotnet"; metadata["SourceKind"] = builtin ? "builtin" : "community";
        metadata["Review"] = new JsonObject { ["Status"] = "unreviewed", ["Version"] = "1.0.0", ["Sha256"] = hash };
        return new JsonObject { ["Id"] = id, ["Version"] = "1.0.0", ["PackageSha256"] = hash, ["Metadata"] = metadata };
    }
    private static byte[] Bytes(JsonObject doc) => Encoding.UTF8.GetBytes(doc.ToJsonString());
    private static IEnumerable<Action<JsonObject>> InvalidChanges()
    {
        yield return d => d["SchemaVersion"] = 2;
        yield return d => d["Entries"]![1]!["Metadata"]!["Review"]!["Sha256"] = new string('a', 64);
        yield return d => d["Entries"]![1]!["Metadata"]!["Review"]!["Status"] = "reviewed";
        yield return d => d["Entries"]![0]!["Metadata"]!["Review"]!["Reviewer"] = new string('x', 201);
        yield return d => d["Entries"]![0]!["Metadata"]!["Review"]!["Reviewer"] = null;
        yield return d => d["Entries"]![0]!["Metadata"]!["Tags"] = new JsonArray(" ");
        yield return d => d["schemaVersion"] = 1;
        yield return d => d["Entries"]![0]!["Unknown"] = true;
        yield return d => d["Entries"]![0]!["Metadata"]!["Name"] = "";
        yield return d => d["Entries"]![0]!["Metadata"]!["Name"] = new string('x', 4001);
        yield return d => d["Entries"]![0]!["Metadata"]!["Tags"] = new JsonArray("duplicate", "duplicate");
        yield return d => d["Entries"]![0]!["Metadata"]!["Repository"] = "javascript:alert(1)";
        yield return d => d["Entries"]![0]!["Metadata"]!["Repository"] = "https://github.com/example/test?variant=synthetic";
        yield return d => d["Entries"]![0]!["Metadata"]!["Repository"] = "https://synthetic-user:" + "not-a-real-secret" + "@github.com/example/test";
        yield return d => d["Entries"]![0]!["Metadata"]!["SourceKind"] = "builtin";
        yield return d => d["Entries"]![1]!["PackageSha256"] = new string('a', 64);
        yield return d => ((JsonObject)d["Entries"]![0]!["Metadata"]!).Remove("Configuration");
        yield return d => ((JsonArray)d["Entries"]!).Add(d["Entries"]![0]!.DeepClone());
    }
    private static void CheckContracts(Action<bool, string> check, ZbaPluginCatalog catalog)
    {
        var wireType = typeof(ZbaPluginCatalog).Assembly.GetType("BotAgent.Panel.ZbaJson")!;
        var wire = (JsonSerializerOptions)wireType.GetField("Wire", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var json = JsonSerializer.SerializeToNode(catalog, wire)!;
        var builtinReview = json["builtinPlugins"]![0]!["metadata"]!["review"]!.AsObject();
        check(builtinReview.ContainsKey("sha256") && builtinReview["sha256"] is null, "actual wire preserves required nullable builtin review hash");
        check(!builtinReview.ContainsKey("reviewer"), "actual wire omits absent optional review fields");
        check(json["builtinPlugins"]![0]!["pluginId"] is not null && json["plugins"]![0]!["manifest"] is not null,
            "wire separates builtin descriptors from required package manifests");
        var legacy = JsonSerializer.Deserialize<ZbaInstalledPlugins>("""{"revision":"test","plugins":[]}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(legacy?.BuiltinPlugins.Count == 0, "legacy response deserializes with additive defaults");
        var openApiType = typeof(ZbaPluginCatalog).Assembly.GetType("BotAgent.Panel.ZbaOpenApi")!;
        var openApi = (JsonObject)openApiType.GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var schemas = openApi["components"]!["schemas"]!;
        check(schemas["ZbaPluginCatalog"]!["properties"]!["builtinPlugins"] is not null, "OpenAPI catalogs builtins");
        check(schemas["ZbaInstalledPlugins"]!["properties"]!["builtinPlugins"] is not null, "OpenAPI installed builtins");
        check(schemas["ZbaPluginMetadata"]!["properties"]!["runtime"]!["enum"]!.AsArray().Count == 3, "OpenAPI runtime vocabulary");
        check(schemas["ZbaPluginReview"]!["required"]!.AsArray().Any(n => n!.GetValue<string>() == "sha256"), "OpenAPI review hash required nullable");
    }
    private sealed class PresentationHttp : IHttpFetcher
    {
        internal byte[] Index = []; internal byte[]? Sidecar; internal bool Offline; internal int Requests;
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => SendAsync(new(HttpMethod.Get, url), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            Requests++; ct.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException("synthetic offline");
            var bytes = request.RequestUri!.AbsolutePath.EndsWith("presentation.json", StringComparison.Ordinal) ? Sidecar : Index;
            return Task.FromResult(new HttpResponseMessage(bytes is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes ?? []) });
        }
    }
}

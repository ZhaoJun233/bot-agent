using System.Net;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

internal partial class Program
{
    internal static async Task<int> RunImportRegressionChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        async Task Refuse(Func<Task> action, string code)
        {
            try { await action(); throw new InvalidOperationException("expected_" + code); }
            catch (ZbaBackendException ex) when (ex.Code == code) { checks++; }
        }
        var now = DateTimeOffset.UnixEpoch.AddDays(10);
        var bytes = ReviewZip("probe.dll", Encoding.UTF8.GetBytes("synthetic archive, never executed"));
        var manifest = ReviewManifest(bytes, "probe.import", "1.0.0", "probe.dll", "NeverLoaded");
        var http = new ImportHttp { Archive = bytes };
        string Descriptor(params PluginPackageManifest[] manifests) => JsonSerializer.Serialize(new { SchemaVersion = 1,
            Plugins = manifests.Select(m => new { m.Id, Versions = new[] { new { m.Version, Manifest = m } } }) });
        http.Descriptor = Descriptor(manifest);
        var packages = new PluginPackageService(Path.Combine(root, "import-checks"), http);
        var receipt = Path.Combine(root, "import-checks-operations.json");
        var market = new PluginMarketService(packages, new PluginSettingsStore(), new PluginCatalogSource(http), () => now, receipt);
        const string caller = "import-admin";
        const string source = "https://github.com/example/plugins";
        var revision = (await market.ReadInstalledAsync(default)).Revision;
        var discovered = await market.DiscoverSourceAsync(source, caller, default);
        Check(discovered.Candidates.Count == 1 && discovered.SourceRevision == http.Sha, "repository_immutable_discovery");
        Check(http.Paths.Any(p => p.Contains("/" + http.Sha + "/repository.json")), "raw_descriptor_pinned_to_commit");
        var preview = await market.PreviewAsync(new("repository", revision, discovered.DiscoveryId, manifest.Id, manifest.Version), caller, default);
        Check(preview.CanInstall && !preview.EnabledAfterInstall && preview.Checks.Any(c => c.Status == "pending"), "preview_default_disabled_hash_pending");
        Check(http.Downloads == 0 && !File.Exists(receipt) && packages.InstalledSnapshot.Count == 0, "discover_preview_have_no_durable_install_effects");
        var request = new ZbaPluginMutationRequest(manifest, revision, "import-confirm", true) { PreviewId = preview.PreviewId };
        var callbacks = 0;
        Task Configure(CancellationToken _) { callbacks++; return Task.CompletedTask; }
        await Refuse(() => market.InstallAsync(request with { PreviewId = null }, caller, default, Configure), "plugin_preview_required");
        await Refuse(() => market.InstallAsync(request with { Manifest = manifest with { Sha256 = new string('0', 64) } }, caller, default, Configure), "plugin_preview_refresh_required");
        await Refuse(() => market.InstallAsync(request, "other-admin", default, Configure), "plugin_preview_refresh_required");
        http.Sha = new string('b', 40);
        await Refuse(() => market.InstallAsync(request, caller, default, Configure), "plugin_preview_refresh_required");
        http.Sha = discovered.SourceRevision;
        now += TimeSpan.FromMinutes(11);
        await Refuse(() => market.InstallAsync(request, caller, default, Configure), "plugin_preview_refresh_required");
        Check(http.Downloads == 0 && callbacks == 0 && !File.Exists(receipt), "rejected_confirmations_never_download_configure_or_reserve");

        foreach (var malformed in new[] { manifest with { Version = "1.0.0\n" }, manifest with { Sha256 = manifest.Sha256 + "\n" },
            manifest with { Permissions = ["network\n"] }, manifest with { Id = manifest.Id + "\n" },
            manifest with { DownloadUrl = manifest.DownloadUrl + "\n" }, manifest with { Dependencies = [new("dependency\n", "1.0.0")] } })
            await Refuse(() => market.PreviewAsync(new("json", revision, Manifest: malformed), caller, default), "plugin_preview_invalid");
        foreach (var api in new[] { "[]", "null", "{}", "{\"sha\":12}", "{\"sha\":\"not-a-commit\"}" })
        {
            http.Api = api;
            await Refuse(() => market.DiscoverSourceAsync(source, caller, default), "plugin_source_unavailable");
        }
        http.Api = null;
        foreach (var invalid in new[] { "{}", http.Descriptor.Replace("SchemaVersion", "schemaVersion"),
            Descriptor(manifest, manifest), Descriptor(manifest with { Dependencies = [new(manifest.Id, manifest.Version)] }) })
        {
            http.Descriptor = invalid;
            await Refuse(() => market.DiscoverSourceAsync(source, caller, default), "plugin_source_unavailable");
        }
        Check(!http.Paths.Any(p => p.EndsWith("/index.json")), "invalid_present_descriptor_never_legacy_fallback");
        http.Descriptor = Descriptor(manifest);
        http.RedirectRepository = true;
        await Refuse(() => market.DiscoverSourceAsync(source, caller, default), "plugin_source_unavailable");
        http.RedirectRepository = false;
        var configured = Environment.GetEnvironmentVariable("BOTAGENT_PLUGIN_CATALOG_URL");
        try
        {
            Environment.SetEnvironmentVariable("BOTAGENT_PLUGIN_CATALOG_URL", "https://example.com/index.json");
            http.Index = JsonSerializer.Serialize(new { SchemaVersion = 1, Plugins = new[] { manifest } });
            var custom = await market.ReadCatalogAsync(null, null, default);
            Check(custom.Plugins.Count == 1, "custom_public_https_catalog_preserved");
            Check(http.Paths.Contains("/presentation.json"), "custom_catalog_presentation_requested");
        }
        finally { Environment.SetEnvironmentVariable("BOTAGENT_PLUGIN_CATALOG_URL", configured); }
        http.Paths.Clear();
        var tag = await market.DiscoverSourceAsync(source + "/releases/tag/v1.0.0", caller, default);
        Check(http.Paths.Any(p => p.EndsWith("/git/ref/tags/v1.0.0")) && tag.SourceRevision == http.Sha, "tag_resolution_never_branch_shadowed");
        var json = await market.PreviewAsync(new("json", revision, Manifest: manifest), caller, default);
        var confirmed = request with { PreviewId = json.PreviewId };
        var result = await market.InstallAsync(confirmed, caller, default, Configure);
        Check(result.Operation.Status != "failed" && callbacks == 1 && http.Downloads == 1, "confirmed_install_invokes_callback_once");
        now += TimeSpan.FromMinutes(11);
        var replay = await market.InstallAsync(confirmed, caller, default, Configure);
        Check(replay.Operation.Id == result.Operation.Id && callbacks == 1 && http.Downloads == 1, "completed_replay_precedes_expired_preview_guard");
        return checks;
    }

    private sealed class ImportHttp : IHttpFetcher
    {
        internal string Sha = new('a', 40);
        internal string? Api;
        internal string Descriptor = "{}";
        internal string Index = "{}";
        internal bool RedirectRepository;
        internal byte[] Archive = [];
        internal int Downloads;
        internal List<string> Paths = [];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.Contains("/releases/download/")) { Downloads++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) }); }
            if (path.EndsWith("/repository.json", StringComparison.Ordinal) && RedirectRepository)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://example.com/repository.json");
                return Task.FromResult(redirect);
            }
            if (path.EndsWith("/repository.json", StringComparison.Ordinal)) return Json(Descriptor);
            if (path.EndsWith("index.json")) return Json(Index);
            if (path.EndsWith("/presentation.json", StringComparison.Ordinal)) return Json("{\"SchemaVersion\":1,\"Entries\":[]}");
            if (path.Contains("/git/ref/tags/")) return Json(Api ?? JsonSerializer.Serialize(new { @object = new { type = "commit", sha = Sha } }));
            if (path.EndsWith("/commits/HEAD")) return Json(Api ?? JsonSerializer.Serialize(new { sha = Sha }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct) => SendAsync(new(HttpMethod.Get, url), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct) => SendAsync(new(HttpMethod.Get, url), option, ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct) => GetAsync(new Uri(url), option, ct);
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct) => throw new NotSupportedException();
        private static Task<HttpResponseMessage> Json(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
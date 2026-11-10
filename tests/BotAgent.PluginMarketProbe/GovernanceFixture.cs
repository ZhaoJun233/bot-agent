using System.Net;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;
using BotAgent.Services.Net;

internal partial class Program
{
    private sealed class GovernanceHttp : IHttpFetcher
    {
        internal byte[] Archive = ReviewZip("probe.dll", Encoding.UTF8.GetBytes("synthetic governance package"));
        internal string Feed = "";
        internal string Index = "";
        internal string Descriptor = "";
        internal bool Unavailable;
        internal int Calls;
        internal int Downloads;
        internal Action? Downloaded;
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
        {
            Calls++;
            if (url.EndsWith("/python-repository.json", StringComparison.Ordinal) ||
                url.EndsWith("/python-presentation.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (url.EndsWith("governance.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(Unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                    { Content = new StringContent(Feed) });
            if (url.EndsWith(".zip", StringComparison.Ordinal))
            {
                Downloads++; Downloaded?.Invoke();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) });
            }
            if (url.Contains("/commits/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"sha\":\"" + new string('a', 40) + "\"}") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                url.EndsWith("repository.json", StringComparison.Ordinal) ? Descriptor : Index) });
        }
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default) => GetAsync(request.RequestUri!.ToString(), ct);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => GetAsync(url, ct);
    }

    private sealed class GovernanceFixture
    {
        internal readonly GovernanceHttp Http = new();
        internal readonly PluginPackageService Packages;
        internal readonly PluginMarketService Market;
        internal readonly string PackagesRoot;
        internal readonly PluginPackageManifest Manifest;
        internal readonly Dictionary<string, bool> Desired = new();
        private int _tick;
        internal GovernanceFixture(string root, string id)
        {
            PackagesRoot = Path.Combine(root, id);
            Packages = new(PackagesRoot, Http);
            Manifest = ReviewManifest(Http.Archive, id, "1.0.0", "probe.dll", "SyntheticEntry");
            Advertise(Manifest); Feed();
            Market = new(Packages, new PluginSettingsStore(), new PluginCatalogSource(Http), () => DateTimeOffset.UtcNow,
                Path.Combine(PackagesRoot, "operations.json"), desiredStates: () => Desired,
                reload: (pluginId, ct) => LifecycleTestRequests.Settle(Packages, pluginId!));
        }
        internal void Advertise(PluginPackageManifest manifest)
        {
            Http.Index = JsonSerializer.Serialize(new { SchemaVersion = 1, Plugins = new[] { manifest } });
            Http.Descriptor = JsonSerializer.Serialize(new { SchemaVersion = 1, Plugins = new[] {
                new { manifest.Id, Versions = new[] { new { manifest.Version, Manifest = manifest } } } } });
        }
        internal void Feed(string? status = null, PluginPackageManifest? manifest = null)
        {
            var m = manifest ?? Manifest;
            var time = DateTimeOffset.UtcNow.AddSeconds(++_tick - 200).ToString("yyyy-MM-ddTHH:mm:ss'Z'");
            Http.Feed = JsonSerializer.Serialize(new { schemaVersion = 1, generatedAt = time,
                entries = status is null ? Array.Empty<object>() : new object[] {
                    new { id = m.Id, version = m.Version, sha256 = m.Sha256.ToLowerInvariant(), status,
                        reason = "Synthetic maintenance notice", updatedAt = time } } });
        }
        internal async Task Start()
        { await Packages.StageInstallAsync(Manifest); await LifecycleTestRequests.Settle(Packages, Manifest.Id); }
        internal async Task<ZbaPluginPreview> Preview(string origin = "json", PluginPackageManifest? manifest = null)
        {
            var m = manifest ?? Manifest;
            var revision = (await Market.ReadInstalledAsync(default)).Revision;
            if (origin == "repository")
            {
                var discovery = await Market.DiscoverSourceAsync("https://github.com/example/plugins", "governance", default);
                return await Market.PreviewAsync(new(origin, revision, discovery.DiscoveryId, m.Id, m.Version), "governance", default);
            }
            return await Market.PreviewAsync(new(origin, revision, Manifest: m), "governance", default);
        }
        internal Task Install(ZbaPluginPreview preview) => Market.InstallAsync(
            new(preview.Manifest, preview.Revision, "plugin:install:" + Guid.NewGuid().ToString("N"), true) { PreviewId = preview.PreviewId }, "governance", default);
        internal async Task<ZbaPluginLifecyclePreview> Lifecycle(string kind, PluginPackageManifest? target = null)
            => await Market.PreviewLifecycleAsync(new(kind, Manifest.Id, (await Market.ReadInstalledAsync(default)).Revision,
                target is null ? null : new("json", Manifest: target)), "governance", default);
    }
}

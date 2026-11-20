using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;
using BotAgent.Services.Net;
using BotAgent.Services.Plugins;

internal static partial class Program
{
    internal static async Task<int> RunD2DeliveryAndComboChecks(string parentRoot)
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("D2_CHECK_FAILED: " + label); checks++; }

        var root = Path.Combine(parentRoot, "d2-delivery-combo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // ── 1. A36 Manifest UI / Adoption / ExclusiveResources validation & approval binding ──
        var htmlV1 = Encoding.UTF8.GetBytes("<!doctype html><h1>Quota v1</h1>");
        var jsV1 = Encoding.UTF8.GetBytes("export const v = 1;");
        var htmlV2 = Encoding.UTF8.GetBytes("<!doctype html><h1>Quota v2</h1>");
        var dllBytes = await File.ReadAllBytesAsync(typeof(SyntheticMarketPlugin).Assembly.Location);

        static byte[] BuildZip(params (string Path, byte[] Content)[] entries)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in entries)
                {
                    var e = zip.CreateEntry(path);
                    using var s = e.Open();
                    s.Write(content);
                }
            }
            return ms.ToArray();
        }
        static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        var zipV1 = BuildZip(
            ("probe.dll", dllBytes),
            ("web/index.html", htmlV1),
            ("web/app.js", jsV1));
        var zipV2 = BuildZip(
            ("probe.dll", dllBytes),
            ("web/index.html", htmlV2),
            ("web/app.js", jsV1));

        var uiV1 = new PluginUiDeclaration(1, "web/index.html",
            [new("web/index.html", Sha(htmlV1), "text/html; charset=utf-8"),
             new("web/app.js", Sha(jsV1), "text/javascript; charset=utf-8")],
            ["settings.read", "dialog.close"], "dialog", "UI v1");
        var uiV2 = uiV1 with
        {
            Assets = [new("web/index.html", Sha(htmlV2), "text/html; charset=utf-8"),
                      new("web/app.js", Sha(jsV1), "text/javascript; charset=utf-8")],
            Title = "UI v2"
        };

        var manifestV1 = new PluginPackageManifest("community.d2.ui", "1.0.0", "probe.dll", "D2UiPluginV1",
            Sha(zipV1), "https://github.com/example-org/example-plugins/releases/download/d2-1.0.0/d2-1.0.0.zip",
            1, "net10.0", "1.0.0", [], [], []) { Ui = uiV1, Adoption = PluginAdoptionMode.Cold };
        var manifestV2 = manifestV1 with
        {
            Version = "1.0.1",
            EntryType = "D2UiPluginV2",
            Sha256 = Sha(zipV2),
            DownloadUrl = "https://github.com/example-org/example-plugins/releases/download/d2-1.0.1/d2-1.0.1.zip",
            Ui = uiV2
        };

        // Single-field mutations must alter canonical fingerprint (A36).
        var fpMethod = typeof(PluginPackageService).Assembly.GetType("BotAgent.Adapters.Plugins.PluginLifecycleManifest")!
            .GetMethod("Fingerprint", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Fingerprint(PluginPackageManifest m) => (string)fpMethod.Invoke(null, [m])!;
        var fp1 = Fingerprint(manifestV1);
        Check(fp1 != Fingerprint(manifestV1 with { Ui = uiV1 with { Title = "UI v1b" } }), "A36 ui.title changes fingerprint");
        Check(fp1 != Fingerprint(manifestV1 with { Ui = uiV1 with { AllowedActions = ["settings.read"] } }), "A36 ui.allowedActions changes fingerprint");
        Check(fp1 != Fingerprint(manifestV1 with { Adoption = null }), "A36 adoption changes fingerprint");
        Check(fp1 != Fingerprint(manifestV1 with { ExclusiveResources = ["platform.connection"] }), "A36 exclusiveResources changes fingerprint");
        // Asset order normalization keeps identical fingerprint.
        var reordered = uiV1 with { Assets = uiV1.Assets.Reverse().ToArray(), AllowedActions = ["dialog.close", "settings.read"] };
        Check(fp1 == Fingerprint(manifestV1 with { Ui = reordered }), "A36 ui asset/action order normalized");

        // Exclusive resource + online adoption must be rejected (A39).
        var invalidOnline = false;
        try
        {
            PluginPackageValidation.Validate(manifestV1 with { Adoption = PluginAdoptionMode.Online, ExclusiveResources = ["platform.connection"] });
        }
        catch (InvalidDataException) { invalidOnline = true; }
        Check(invalidOnline, "A39 exclusive resource rejects online adoption");

        // Tampered UI asset hash in ZIP rejected during StageInstallAsync archive validation.
        var badUiArchive = false;
        var badHttp = new MultiUrlHttp(new Dictionary<string, byte[]>(StringComparer.Ordinal) { [manifestV1.DownloadUrl] = zipV1 });
        var badPackages = new PluginPackageService(Path.Combine(root, "bad-ui"), badHttp);
        try
        {
            await badPackages.StageInstallAsync(manifestV1 with
            {
                Ui = uiV1 with
                {
                    Assets = [new("web/index.html", new string('0', 64), "text/html; charset=utf-8"),
                              new("web/app.js", Sha(jsV1), "text/javascript; charset=utf-8")]
                }
            });
        }
        catch (InvalidDataException) { badUiArchive = true; }
        Check(badUiArchive, "A14/A38 archive validation rejects tampered UI asset hash");

        // ── 2. A14/A38 Real runtime UI asset serving, CSP, 404 no-store, ticket scope, and independent UI upgrade ──
        var uiRoot = Path.Combine(root, "ui-packages");
        var http = new MultiUrlHttp(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [manifestV1.DownloadUrl] = zipV1,
            [manifestV2.DownloadUrl] = zipV2,
        });
        var packages = new PluginPackageService(uiRoot, http);
        var registry = new PluginManager(_ => { });
        var now = new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero);
        var assets = new PluginUiAssetService(packages, registry, () => now);

        await packages.StageInstallAsync(manifestV1);
        await packages.ApplyAtStartupAsync(manifestV1.Id, new PluginContext(new EmptyServices()), registry, true);

        // Verify reader 12 required for UI manifest.
        var stateJson = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(uiRoot, manifestV1.Id, "installed.json")))!;
        Check(stateJson["minimumReaderVersion"]!.GetValue<int>() == 12, "A37 UI manifest requires reader 12");

        var session1 = await assets.OpenSessionAsync(manifestV1.Id, default);
        Check(session1.Version == "1.0.0" && session1.BackendReady && session1.VersionMatched
            && session1.AdoptionMode == "cold" && registry.ActiveUiSessionCount(manifestV1.Id) == 1,
            "D2.1/D2.2 opens generation-bound UI session lease");

        var ticket1 = session1.EntryUrl.Split('/')[7];
        var indexRes = await assets.ReadAssetAsync(manifestV1.Id, manifestV1.Sha256, ticket1, "web/index.html", false, default);
        Check(indexRes.Status == 200 && indexRes.Bytes.SequenceEqual(htmlV1)
            && indexRes.ContentType == "text/html; charset=utf-8"
            && indexRes.ContentSecurityPolicy!.Contains("connect-src 'none'", StringComparison.Ordinal)
            && !indexRes.ContentSecurityPolicy!.Contains("unsafe-inline", StringComparison.Ordinal)
            && indexRes.CacheControl.StartsWith("private,", StringComparison.Ordinal)
            && indexRes.CacheControl.Contains("immutable", StringComparison.Ordinal),
            "A14/A38 serves verified HTML asset with strict CSP (no unsafe-inline) and private immutable cache");

        var missingRes = await assets.ReadAssetAsync(manifestV1.Id, manifestV1.Sha256, ticket1, "web/missing.js", false, default);
        Check(missingRes.Status == 404 && missingRes.ErrorCode == "plugin_ui_asset_not_found" && missingRes.CacheControl == "no-store",
            "A38 missing asset returns 404 no-store, never login/index HTML");

        var badTicket = await assets.ReadAssetAsync(manifestV1.Id, manifestV1.Sha256, "forged.9999999999.deadbeef", "web/index.html", false, default);
        Check(badTicket.Status == 401 && badTicket.CacheControl == "no-store", "A16 forged asset ticket rejected 401");

        // Upgrade plugin package 1.0.0 -> 1.0.1 (independent UI upgrade without rebuilding host frontend!).
        await packages.StageInstallAsync(manifestV2);
        await packages.ApplyAtStartupAsync(manifestV2.Id, new PluginContext(new EmptyServices()), registry, true);

        Check(!registry.IsUiSessionCurrent(session1.SessionId, manifestV1.Id, manifestV1.Sha256),
            "D2.2 old UI session lease invalidated when new generation activates");

        var session2 = await assets.OpenSessionAsync(manifestV2.Id, default);
        var ticket2 = session2.EntryUrl.Split('/')[7];
        var indexV2Res = await assets.ReadAssetAsync(manifestV2.Id, manifestV2.Sha256, ticket2, "web/index.html", false, default);
        Check(indexV2Res.Status == 200 && indexV2Res.Bytes.SequenceEqual(htmlV2),
            "A14 upgraded package serves new runtime UI bytes immediately without host web rebuild");

        // Previous release assets remain readable for in-flight recovery of old pages.
        var prevAssetRes = await assets.ReadAssetAsync(manifestV1.Id, manifestV1.Sha256, "auth", "web/index.html", true, default);
        Check(prevAssetRes.Status == 200 && prevAssetRes.Bytes.SequenceEqual(htmlV1),
            "A38 previous release assets retained read-only for recovery");

        var closed = await assets.CloseSessionAsync(manifestV2.Id, session2.SessionId, default);
        Check(closed.Closed && registry.ActiveUiSessionCount(manifestV2.Id) == 0, "D2.2 closing UI session releases lease");

        var revokedTicket = await assets.ReadAssetAsync(manifestV2.Id, manifestV2.Sha256, ticket2, "web/index.html", false, default);
        Check(revokedTicket.Status == 401 && revokedTicket.CacheControl == "no-store",
            "D2.2 closing UI session immediately revokes its asset ticket");

        // Non-quota plugin cannot declare quotas.* UI allowedActions (rejected during StageInstallAsync).
        var badQuotaUi = manifestV1 with
        {
            Ui = manifestV1.Ui! with { AllowedActions = ["quotas.list", "dialog.close"] }
        };
        var quotaActionRejected = false;
        try { await packages.StageInstallAsync(badQuotaUi); }
        catch (InvalidDataException ex) when (ex.Message == "plugin_ui_allowed_actions_invalid") { quotaActionRejected = true; }
        Check(quotaActionRejected, "Review regression: non-quota plugin cannot claim quotas.* allowedActions");

        // ZbaRules accepts valid Python (ApiVersion=2) manifests on ZBA HTTP routes.
        var zbaRulesType = typeof(ZbaEndpoint).Assembly.GetType("BotAgent.Panel.ZbaRules", throwOnError: true)!;
        var validateManifestMethod = zbaRulesType.GetMethod("ValidatePluginManifest", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var pythonManifest = new PluginPackageManifest(
            "community.py.demo", "1.0.0", null, null, new string('a', 64),
            "https://github.com/example-org/example-plugins/releases/download/v1.0.0/py.zip",
            2, null, "1.0.0", [], [], [],
            new PluginPythonRuntime("python", 1, "plugin.py", "3.11", "stdlib-only", ["demo.echo"]));
        Check(validateManifestMethod.Invoke(null, [pythonManifest]) is null,
            "Review regression: ZbaRules accepts valid ApiVersion=2 Python manifests");

        // ByRefLike contribution parameters are rejected upfront before DispatchProxy fails at runtime.
        var byRefRejected = false;
        try { registry.TryGetContribution<IInvalidSpanContribution>(manifestV2.Id, out _); }
        catch (InvalidOperationException ex) when (ex.Message == "plugin_contribution_contract_invalid") { byRefRejected = true; }
        Check(byRefRejected, "Review regression: ValidateContributionContract rejects ReadOnlySpan/IsByRefLike parameters");

        // ── 3. A39 Two-Phase Activation (Prepare before drain, Activate only after clean old shutdown) ──
        var twoPhaseEvents = new List<string>();
        var candidateReadyDuringOldShutdown = false;
        var pOldOk = new TwoPhaseTestPlugin("community.d2.exclusive", "1.0.0", twoPhaseEvents, failShutdown: false,
            onShutdown: () => candidateReadyDuringOldShutdown = registry.IsReady("community.d2.exclusive"));
        var pNewOk = new TwoPhaseTestPlugin("community.d2.exclusive", "2.0.0", twoPhaseEvents, failShutdown: false);
        await registry.ReplaceAsync(pOldOk, true);
        twoPhaseEvents.Clear();
        await registry.ReplaceAsync(pNewOk, true);
        Check(twoPhaseEvents.SequenceEqual(["shutdown:1.0.0", "activate:2.0.0"]) && registry.IsReady("community.d2.exclusive")
            && !candidateReadyDuringOldShutdown,
            "A39 new exclusive owner activates strictly after old generation drains and is NOT Ready before ActivateAsync");

        var pOldFail = new TwoPhaseTestPlugin("community.d2.exclusive.fail", "1.0.0", twoPhaseEvents, failShutdown: true);
        var pNewBlocked = new TwoPhaseTestPlugin("community.d2.exclusive.fail", "2.0.0", twoPhaseEvents, failShutdown: false);
        await registry.ReplaceAsync(pOldFail, true);
        twoPhaseEvents.Clear();
        await registry.ReplaceAsync(pNewBlocked, true);
        Check(!twoPhaseEvents.Contains("activate:2.0.0") && !registry.IsReady("community.d2.exclusive.fail"),
            "A08/A39 failed old shutdown blocks new exclusive activation (no dual online)");

        // ── 4. A34 Atomic Multi-Package Dependency Combo Upgrade (C1/P1 -> C2/P2) ──
        var comboRoot = Path.Combine(root, "combo-packages");
        var zipP1 = BuildZip(("p.dll", dllBytes));
        var zipP2 = BuildZip(("p.dll", dllBytes.Concat(new byte[] { 1 }).ToArray()));
        var zipC1 = BuildZip(("c.dll", dllBytes));
        var zipC2 = BuildZip(("c.dll", dllBytes.Concat(new byte[] { 2 }).ToArray()));

        var p1 = new PluginPackageManifest("community.d2.provider", "1.0.0", "p.dll", "SyntheticMarketPlugin",
            Sha(zipP1), "https://github.com/example-org/example-plugins/releases/download/p-1.0.0/p-1.0.0.zip", 1, "net10.0", "1.0.0", [], [], []);
        var p2 = p1 with { Version = "2.0.0", Sha256 = Sha(zipP2), DownloadUrl = "https://github.com/example-org/example-plugins/releases/download/p-2.0.0/p-2.0.0.zip" };
        var c1 = new PluginPackageManifest("community.d2.consumer", "1.0.0", "c.dll", "SyntheticMarketPlugin",
            Sha(zipC1), "https://github.com/example-org/example-plugins/releases/download/c-1.0.0/c-1.0.0.zip", 1, "net10.0", "1.0.0", [],
            [new PluginPackageDependency("community.d2.provider", "1.0.0")], []);
        var c2 = c1 with { Version = "2.0.0", Sha256 = Sha(zipC2), DownloadUrl = "https://github.com/example-org/example-plugins/releases/download/c-2.0.0/c-2.0.0.zip",
            Dependencies = [new PluginPackageDependency("community.d2.provider", "2.0.0")] };

        var comboHttp = new MultiUrlHttp(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [p1.DownloadUrl] = zipP1, [p2.DownloadUrl] = zipP2,
            [c1.DownloadUrl] = zipC1, [c2.DownloadUrl] = zipC2,
        });
        var comboPackages = new PluginPackageService(comboRoot, comboHttp);
        var comboRegistry = new PluginManager(_ => { });
        var comboMarket = new PluginMarketService(comboPackages, new PluginSettingsStore(),
            new PluginCatalogSource(comboHttp, _ => { }), () => now, Path.Combine(comboRoot, "operations.json"),
            registrySnapshot: comboRegistry.GetAll, canReload: () => false);

        // Seed Active C1 + P1.
        await comboPackages.StageInstallAsync(p1);
        await comboPackages.StageInstallAsync(c1);
        await LifecycleTestRequests.SaveState(comboPackages, new InstalledPluginPackage(p1.Id, Active: p1));
        await LifecycleTestRequests.SaveState(comboPackages, new InstalledPluginPackage(c1.Id, Active: c1));

        // Partial combo (P2 without C2) must be blocked by reverse dependency of C1!
        var blockedPartial = await comboMarket.PreviewBundleAsync(new([p2]), "admin", default);
        Check(!blockedPartial.CanApply && blockedPartial.Checks.Any(c => c.Code == "reverse_dependency" && c.Status == "blocked"),
            "A34 partial combo upgrading P2 without C2 blocked by reverse dependency");

        // Cycle combo must be blocked!
        var cycA = p2 with { Dependencies = [new(c2.Id, "2.0.0")] };
        var blockedCycle = await comboMarket.PreviewBundleAsync(new([cycA, c2]), "admin", default);
        Check(!blockedCycle.CanApply && blockedCycle.Checks.Any(c => c.Code == "cycle" && c.Status == "blocked"),
            "A34 dependency cycle in bundle preview is blocked");

        // Complete combo (P2 + C2) passes preview and stages atomically!
        var comboPreview = await comboMarket.PreviewBundleAsync(new([p2, c2]), "admin", default);
        Check(comboPreview.CanApply && comboPreview.AffectedIds.SequenceEqual([c2.Id, p2.Id]),
            "A34 complete C2+P2 bundle preview passes dependency closure");

        var comboApply = await comboMarket.ApplyBundleAsync(new([p2, c2], comboPreview.Revision,
            "plugin:bundle:c2-p2:key-0001", true, comboPreview.PreviewId), "admin", default);
        Check(comboApply.Operation.Kind == "bundle" && comboApply.Operation.Status == "restart_required",
            "A34 bundle apply commits atomic pending combo");

        // Idempotent replay by original key returns identical receipt without re-staging.
        var comboReplay = await comboMarket.ApplyBundleAsync(new([p2, c2], comboPreview.Revision,
            "plugin:bundle:c2-p2:key-0001", true, comboPreview.PreviewId), "admin", default);
        Check(comboReplay.Operation.Id == comboApply.Operation.Id && comboReplay.Operation.Status == comboApply.Operation.Status,
            "A34 bundle idempotent replay returns original receipt");

        var afterCombo = await comboPackages.GetInstalledAsync();
        Check(afterCombo.Single(x => x.Id == p2.Id).Pending?.Version == "2.0.0"
            && afterCombo.Single(x => x.Id == c2.Id).Pending?.Version == "2.0.0",
            "A34 both provider and consumer staged Pending 2.0.0 atomically");

        return checks;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class MultiUrlHttp(IReadOnlyDictionary<string, byte[]> payloads) : IHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (payloads.TryGetValue(url, out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => SendAsync(new(HttpMethod.Get, url), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => SendAsync(new(HttpMethod.Post, url) { Content = content }, ct);
    }

    private sealed class TwoPhaseTestPlugin(string id, string version, List<string> events, bool failShutdown, Action? onShutdown = null) : IBotPlugin, IPluginTwoPhaseLifecycle
    {
        public string Id => id;
        public string Name => id;
        public string Version => version;
        public string Description => "two-phase test";
        public PluginCategory Category => PluginCategory.Feature;
        public Task PrepareAsync(PluginContext context, CancellationToken ct) { events.Add("prepare:" + version); return Task.CompletedTask; }
        public Task ActivateAsync(CancellationToken ct) { events.Add("activate:" + version); return Task.CompletedTask; }
        public Task InitializeAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken ct)
        {
            onShutdown?.Invoke();
            events.Add("shutdown:" + version);
            if (failShutdown) throw new InvalidOperationException("shutdown_failed");
            return Task.CompletedTask;
        }
    }

    public interface IInvalidSpanContribution : IPluginContribution
    {
        bool CheckSpan(ReadOnlySpan<byte> data);
    }
}

internal sealed class D2UiPluginV1 : IBotPlugin
{
    public string Id => "community.d2.ui";
    public string Name => "D2 UI v1";
    public string Version => "1.0.0";
    public string Description => "D2 UI test plugin v1";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class D2UiPluginV2 : IBotPlugin
{
    public string Id => "community.d2.ui";
    public string Name => "D2 UI v2";
    public string Version => "1.0.1";
    public string Description => "D2 UI test plugin v2";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

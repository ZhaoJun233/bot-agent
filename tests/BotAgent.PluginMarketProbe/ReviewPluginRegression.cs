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
using BotAgent.Services.Plugins;

// Parent wiring: checks += await RunReviewPluginRegressionAsync(root), after synthetic DB initialization.
internal partial class Program
{
    internal static async Task<int> RunReviewPluginRegressionAsync(string syntheticRoot)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }
        async Task Expect(Func<Task> action, string code)
        {
            try { await action(); }
            catch (ZbaBackendException ex) when (ex.Code == code) { checks++; return; }
            throw new InvalidOperationException("review_expected_" + code);
        }
        var root = Path.Combine(syntheticRoot, "review-plugins");
        var http = new ReviewPackageHttp();
        var packages = new PluginPackageService(Path.Combine(root, "packages"), http);
        var now = DateTimeOffset.UnixEpoch;
        var receiptPath = Path.Combine(root, "operations.json");
        PluginMarketService Market() => new(packages, new PluginSettingsStore(),
            new PluginCatalogSource(http, _ => { }), () => now, receiptPath);
        var market = Market();
        var bytes = ReviewZip("probe.dll", Encoding.UTF8.GetBytes("synthetic-only"));
        http.Bytes = bytes;
        var manifest = ReviewManifest(bytes, "probe.review", "1.0.0", "probe.dll", "UnusedSyntheticEntry") with
        { Settings = new[] { new PluginSettingDefinition("greeting", "Greeting", "string", "hello") } };
        var revision = (await market.ReadInstalledAsync(default)).Revision;
        var request = new ZbaPluginMutationRequest(manifest, revision, "review-install", true);

        // Hold the first download; submit same-key retry through another market facade.
        http.HoldNext();
        var first = market.InstallAsync(request, "synthetic-admin", default);
        await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Expect(() => Task.FromResult(market.FindOperation(request.IdempotencyKey)), "plugin_operation_unknown");
        var second = Market().InstallAsync(request, "synthetic-admin", default);
        var divergent = Market().UpdateAsync(request, "synthetic-admin", default);
        http.Release.TrySetResult();
        var installed = await first;
        var replay = await second;
        Check(installed == replay && http.Calls == 1, "B10_same_key_concurrent_stage_once");
        await Expect(() => divergent, "idempotency_conflict");
        var restartedMarket = new PluginMarketService(
            new PluginPackageService(Path.Combine(root, "packages"), http), new PluginSettingsStore(),
            new PluginCatalogSource(http, _ => { }), () => now, receiptPath);
        Check((await restartedMarket.InstallAsync(request with { ExpectedRevision = "stale" },
            "synthetic-admin", default)) == installed, "B10_restart_exact_replay_before_CAS");
        Check((await Market().InstallAsync(request with { Manifest = manifest with
            { Sha256 = manifest.Sha256.ToLowerInvariant(), Permissions = Array.Empty<string>(),
                Dependencies = Array.Empty<PluginPackageDependency>() } }, "synthetic-admin", default)) == installed,
            "B10_normalized_manifest_replay");
        await Expect(() => market.UninstallAsync("probe.other", new(revision, request.IdempotencyKey),
            "synthetic-admin", default), "idempotency_conflict");
        await Expect(() => market.UpdateAsync(request, "synthetic-admin", default), "idempotency_conflict");
        await Expect(() => market.InstallAsync(request with { Manifest = manifest with { Version = "2.0.0" } },
            "synthetic-admin", default), "idempotency_conflict");
        await Expect(() => market.InstallAsync(request with { Manifest = manifest with { EntryType = "DifferentEntry" } },
            "synthetic-admin", default), "idempotency_conflict");
        await Expect(() => market.InstallAsync(request with { TrustAcknowledged = false },
            "synthetic-admin", default), "idempotency_conflict");
        await Expect(() => market.InstallAsync(request, "other-synthetic-admin", default), "idempotency_conflict");
        var receiptText = await File.ReadAllTextAsync(receiptPath);
        Check(!receiptText.Contains(manifest.DownloadUrl, StringComparison.Ordinal)
            && !receiptText.Contains(manifest.Sha256, StringComparison.Ordinal), "B10_receipt_has_no_manifest_body");

        // Two fresh operations share one expected revision. The stale loser cannot replace Pending.
        revision = (await market.ReadInstalledAsync(default)).Revision;
        var next = manifest with { Version = "1.1.0" };
        http.HoldNext();
        var winner = market.UpdateAsync(new(next, revision, "review-cas-winner", true), "synthetic-admin", default);
        await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loser = Market().UninstallAsync(manifest.Id, new(revision, "review-cas-loser"), "synthetic-admin", default);
        http.Release.TrySetResult();
        await winner;
        await Expect(() => loser, "plugin_revision_conflict");
        Check(packages.InstalledSnapshot.Single().Pending?.Version == "1.1.0"
            && !packages.InstalledSnapshot.Single().PendingUninstall, "B09_loser_does_not_replace_pending");

        // Caller-supplied original rN must reach store's receipt-before-CAS transaction.
        var settings = await market.ReadSettingsAsync(manifest.Id, default);
        var edit = new ZbaPluginSettingsSaveRequest(settings.Revision, "review-settings",
            new[] { new ZbaPluginSettingEdit("greeting", 1, "synthetic-new") });
        var saved = await market.SaveSettingsAsync(manifest.Id, edit, "synthetic-admin", default);
        var savedReplay = await market.SaveSettingsAsync(manifest.Id, edit, "synthetic-admin", default);
        Check(savedReplay.Revision == saved.Revision && savedReplay.Values["greeting"] == "synthetic-new",
            "B11_original_revision_replay_reaches_store");
        await Expect(() => market.SaveSettingsAsync(manifest.Id, edit with { IdempotencyKey = "review-settings-stale" },
            "synthetic-admin", default), "plugin_settings_revision_conflict");
        await Expect(() => market.SaveSettingsAsync(manifest.Id, edit with
            { Edits = new[] { new ZbaPluginSettingEdit("greeting", 1, "different") } },
            "synthetic-admin", default), "idempotency_conflict");
        await Expect(() => market.SaveSettingsAsync(manifest.Id, edit with { ExpectedRevision = "r-1" },
            "synthetic-admin", default), "plugin_settings_revision_conflict");

        // Package CAS itself, not just market serialization, owns revision validation.
        var casMethod = typeof(PluginPackageService).GetMethod("StageUninstallAsync", new[]
            { typeof(string), typeof(string), typeof(CancellationToken) });
        Check(casMethod is not null, "B09_package_compare_and_stage_entry_exists");
        try
        {
            await (Task)casMethod!.Invoke(packages, new object[] { manifest.Id, "stale", CancellationToken.None })!;
            throw new InvalidOperationException("B09_package_stale_CAS_was_accepted");
        }
        catch (ZbaBackendException ex) when (ex.Code == "plugin_revision_conflict") { checks++; }

        var directPackages = new PluginPackageService(Path.Combine(root, "direct-CAS"), http);
        var directMarket = new PluginMarketService(directPackages, new PluginSettingsStore(),
            new PluginCatalogSource(http, _ => { }), () => now, Path.Combine(root, "direct-operations.json"));
        var directRevision = (await directMarket.ReadInstalledAsync(default)).Revision;
        http.HoldNext();
        var directWinner = directPackages.StageInstallAsync(manifest, directRevision, default);
        await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var directLoser = directPackages.StageInstallAsync(manifest with { Version = "2.0.0" }, directRevision, default);
        http.Release.TrySetResult();
        await directWinner;
        await Expect(() => directLoser, "plugin_revision_conflict");
        Check(directPackages.InstalledSnapshot.Single().Pending?.Version == "1.0.0",
            "B09_direct_package_concurrent_compare_and_stage");

        // Cancellation after durable admission leaves uncertainty, never authorization to stage twice.
        var cancelRevision = (await directMarket.ReadInstalledAsync(default)).Revision;
        using var cancelled = new CancellationTokenSource();
        http.HoldNext();
        var cancelledRequest = new ZbaPluginMutationRequest(manifest with { Version = "3.0.0" },
            cancelRevision, "review-cancel-reservation", true);
        var cancelledStage = directMarket.InstallAsync(cancelledRequest, "synthetic-admin", cancelled.Token);
        await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        try { await cancelledStage; throw new InvalidOperationException("B10_cancel_not_observed"); }
        catch (OperationCanceledException) { checks++; }
        var callsAtCancellation = http.Calls;
        await Expect(() => directMarket.InstallAsync(cancelledRequest, "synthetic-admin", default), "plugin_operation_unknown");
        await Expect(() => Task.FromResult(directMarket.FindOperation(cancelledRequest.IdempotencyKey)), "plugin_operation_unknown");
        Check(http.Calls == callsAtCancellation, "B10_uncertain_reservation_replay_never_restages");

        // A legacy receipt is still readable, but lacks enough evidence to replay a mutation.
        var legacyPath = Path.Combine(root, "legacy-operations.json");
        var legacyStore = new PluginOperationReceiptStore(legacyPath, () => now);
        var legacyOperation = new ZbaPluginOperation("po-legacy", manifest.Id, "install", "restart_required", true, null);
        Check(legacyStore.TryAdd("legacy-key", legacyOperation, "legacy-revision", "synthetic-admin"),
            "B10_legacy_receipt_shape_writes");
        legacyStore = new PluginOperationReceiptStore(legacyPath, () => now);
        Check(legacyStore.Find("legacy-key")?.Operation == legacyOperation, "B10_legacy_receipt_query_compatible");
        await Expect(() => Task.FromResult(legacyStore.Find("legacy-key", new string('A', 64), "synthetic-admin")),
            "idempotency_conflict");
        var corruptPath = Path.Combine(root, "corrupt-operations.json");
        await File.WriteAllTextAsync(corruptPath, "{synthetic-malformed");
        var corruptStore = new PluginOperationReceiptStore(corruptPath, () => now);
        await Expect(() => Task.FromResult(corruptStore.Find("some-key")), "plugin_operation_unknown");
        Check(!corruptStore.TryAdd("some-key", legacyOperation, "p0", "synthetic-admin", new string('A', 64)),
            "B10_corrupt_receipts_cannot_be_overwritten");
        var oversizedPath = Path.Combine(root, "oversized-operations.json");
        await File.WriteAllTextAsync(oversizedPath, new string('x', 1_048_577));
        var oversizedStore = new PluginOperationReceiptStore(oversizedPath, () => now);
        await Expect(() => Task.FromResult(oversizedStore.Find("some-key")), "plugin_operation_unknown");
        Check(!oversizedStore.TryAdd("some-key", legacyOperation, "p0", "synthetic-admin", new string('A', 64)),
            "B10_oversized_receipts_fail_closed_without_overwrite");
        var blockedParent = Path.Combine(root, "blocked-receipt-parent");
        await File.WriteAllTextAsync(blockedParent, "synthetic file, not a directory");
        var blockedMarket = new PluginMarketService(packages, new PluginSettingsStore(),
            new PluginCatalogSource(http, _ => { }), () => now, Path.Combine(blockedParent, "operations.json"));
        var callsBeforeFailure = http.Calls;
        var beforeFailureRevision = (await market.ReadInstalledAsync(default)).Revision;
        await Expect(() => blockedMarket.InstallAsync(new(manifest,
            beforeFailureRevision, "review-receipt-failure", true),
            "synthetic-admin", default), "plugin_operation_unknown");
        Check(http.Calls == callsBeforeFailure, "B10_receipt_write_failure_prevents_stage");

        // Load this synthetic probe assembly as an actual package, then register and dispatch.
        var assembly = typeof(ReviewPolicyPlugin).Assembly;
        var loadedBytes = ReviewZip(Path.GetFileName(assembly.Location), await File.ReadAllBytesAsync(assembly.Location));
        http.Bytes = loadedBytes;
        var policyManifest = ReviewManifest(loadedBytes, "probe.review.policy", "1.0.0",
            Path.GetFileName(assembly.Location), typeof(ReviewPolicyPlugin).FullName!);
        var ordinaryManifest = policyManifest with { Id = "probe.review.ordinary", EntryType = typeof(ReviewOrdinaryPlugin).FullName! };
        var loadPackages = new PluginPackageService(Path.Combine(root, "load-packages"), http);
        await loadPackages.StageInstallAsync(policyManifest);
        await loadPackages.StageInstallAsync(ordinaryManifest);
        var result = await loadPackages.LoadAtStartupAsync(new PluginContext(new ReviewNoServices()), isEnabled: _ => true);
        Check(result.Failures.Count == 0 && result.Loaded.Count == 2, "B13_actual_packages_loaded");
        var policy = result.Loaded.Single(p => p.Id == policyManifest.Id);
        var ordinary = result.Loaded.Single(p => p.Id == ordinaryManifest.Id);
        Check(policy is IReplyPolicyPlugin && ordinary is not IReplyPolicyPlugin, "B13_wrapper_types_are_accurate");
        var registry = new PluginManager(_ => { });
        foreach (var plugin in result.Loaded) registry.Register(plugin);
        await registry.StartAllAsync(default);
        Check(new PluginReplyPolicies(registry).BlockReason(new("group:10001", false, true)) == "synthetic_block",
            "B13_real_registry_dispatch_forwards_policy");
        Check(((IReplyPolicyPlugin)policy).BlockReason(new("group:10001", true, false)) is null,
            "B13_reply_context_transparent");
        registry.SetEnabled(policy.Id, false);
        Check(new PluginReplyPolicies(registry).BlockReason(new("group:10001", false, true)) is null,
            "B13_disabled_policy_transparent");
        registry.SetEnabled(policy.Id, true);
        await registry.StopAllAsync(default);

        now += TimeSpan.FromDays(8);
        await Expect(() => Task.FromResult(Market().FindOperation(request.IdempotencyKey)), "plugin_operation_not_found");
        return checks;
    }

    private static byte[] ReviewZip(string name, byte[] payload)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(payload);
        }
        return bytes.ToArray();
    }

    private static PluginPackageManifest ReviewManifest(byte[] bytes, string id, string version, string assembly, string type)
        => new(id, version, assembly, type, Convert.ToHexString(SHA256.HashData(bytes)),
            "https://example.com/releases/download/" + id + "-" + version + "/package.zip");

    private sealed class ReviewNoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class ReviewPackageHttp : IHttpFetcher
    {
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public int Calls { get; private set; }
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hold;
        public void HoldNext()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _hold = true;
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option,
            CancellationToken ct = default)
        {
            Calls++;
            if (_hold)
            {
                _hold = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
        }
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
            => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default)
            => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), option, ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default)
            => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), option, ct);
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
            => GetAsync(url, ct);
    }
}

public sealed class ReviewPolicyPlugin : IReplyPolicyPlugin
{
    private int _initialized;
    public string Id => "probe.review.policy";
    public string Name => "Synthetic policy";
    public string Version => "1.0.0";
    public string Description => "Synthetic policy fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        if (++_initialized != 1) throw new InvalidOperationException("initialized_twice");
        return Task.CompletedTask;
    }
    public string? BlockReason(ReplyPolicyContext context)
        => _initialized == 1 && context.DirectMention ? "synthetic_block" : null;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class ReviewOrdinaryPlugin : IBotPlugin
{
    public string Id => "probe.review.ordinary";
    public string Name => "Synthetic ordinary plugin";
    public string Version => "1.0.0";
    public string Description => "Synthetic ordinary fixture";
    public PluginCategory Category => PluginCategory.Feature;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Platforms;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Management;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Model;
using BotAgent.Services.Music;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Poke;
using BotAgent.Services.Profiles;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;

internal static partial class Program
{
    internal static async Task<int> RunD3D4D5AcceptanceChecks(string parentRoot)
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("D3_D5_FAILED: " + label); checks++; }

        // Locate repo src/ for A31 zero-host-source-modification hash check
        var repoRoot = FindRepoRoot();
        var srcHashBefore = repoRoot is not null ? HashDirectory(Path.Combine(repoRoot, "src")) : null;

        // ── 1. Bare host loads 9 system.* packages, then all 13 official 1.1.0 feature presets load cleanly ──
        var bundleDir = Path.Combine(parentRoot, "d3-d4-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bundleDir);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var runtimeType = typeof(PluginPackageService).Assembly.GetType("BotAgent.Host.PluginRuntime")!;
        var logs = new List<string>();
        await using (var bareRuntime = (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
            .Invoke([bundleDir, (Action<string>)(msg => logs.Add(msg))]))
        {
            var barePackages = (PluginPackageService)runtimeType.GetProperty("Packages", hidden)!.GetValue(bareRuntime)!;
            await (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(bareRuntime, [CancellationToken.None])!;
            var bareInstalled = await barePackages.GetInstalledAsync();
            Check(bareInstalled.Count == 9 && bareInstalled.All(p => p.Id.StartsWith("system.", StringComparison.Ordinal)),
                "bare host starts with ONLY the 9 system.* packages");
        }

        OfficialMarketplaceTestHelper.StageAllOfficialFeatures(bundleDir, enableInStore: true);
        await using (var runtime = (IAsyncDisposable)runtimeType.GetConstructors(hidden).Single()
            .Invoke([bundleDir, (Action<string>)(msg => logs.Add(msg))]))
        {
            var registry = (PluginManager)runtimeType.GetProperty("Registry", hidden)!.GetValue(runtime)!;
            var packages = (PluginPackageService)runtimeType.GetProperty("Packages", hidden)!.GetValue(runtime)!;
            var market = (PluginMarketService)runtimeType.GetProperty("Market", hidden)!.GetValue(runtime)!;
            BindResearchRuntime(runtime);
            await (Task)runtimeType.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;

            var expectedIds = new[]
            {
                "preset.feature.token-quota", "preset.feature.websearch", "preset.feature.poke",
                "preset.feature.vibes", "preset.feature.stickers", "preset.feature.music",
                "preset.feature.voice", "preset.feature.profiles",
                "preset.feature.documents", "preset.feature.digest", "preset.feature.knowledge",
                "preset.feature.scheduler", "preset.feature.personal-todo",
                "system.health-reporting", "system.model-services", "system.agent",
                "system.mcp-tools", "system.conversation",
                "system.platform-qq-private", "system.platform-qq-official",
                "system.platform-feishu", "system.platform-local"
            };
            var installed = await packages.GetInstalledAsync();
            var missing = expectedIds.Where(id => !installed.Any(p => p.Id == id && p.Active is not null)).ToArray();
            if (missing.Length > 0)
            {
                var fails = (System.Collections.Immutable.ImmutableDictionary<string, string>)runtimeType.GetField("_failures", hidden)!.GetValue(runtime)!;
                var desc = fails.Select(kv => $"{kv.Key}={kv.Value}");
                var instDesc = installed.Where(p => missing.Contains(p.Id) || p.Id == "system.agent")
                    .Select(p => $"{p.Id}:active={p.Active?.Version},pending={p.Pending?.Version},metaErr={p.MetadataFailure?.Code}");
                Check(false, $"all 17 preset + system packages are bundled and adopted (missing=[{string.Join(",", missing)}], desc=[{string.Join("; ", desc)}], inst=[{string.Join("; ", instDesc)}])");
            }
            checks++;
            Check(AssemblyLoadContext.Default.Assemblies.All(a => !(a.GetName().Name ?? "").StartsWith("BotAgent.Package.", StringComparison.Ordinal)),
                "zero package implementation assemblies leak into AssemblyLoadContext.Default");

            // P04 Poke works even when P05 Vibes is disabled
            var now = DateTimeOffset.UtcNow;
            registry.SetEnabled("preset.feature.vibes", false);
            Check(!registry.IsReady("preset.feature.vibes") && registry.IsReady("preset.feature.poke"),
                "P04 poke remains ready when P05 vibes is disabled");
            var pokePolicy = registry.GetContribution<IPokePolicy>("preset.feature.poke");
            var firstPoke = pokePolicy.EvaluateInbound("group:10001", 20002, 60, now);
            var secondPoke = pokePolicy.EvaluateInbound("group:10001", 20002, 60, now.AddSeconds(5));
            Check(firstPoke.ShouldReply && !secondPoke.ShouldReply && pokePolicy.IsRecentPoker("group:10001", 20002),
                "P04 poke policy runs independently without P05 vibes and enforces cooldown");
            registry.SetEnabled("preset.feature.vibes", true);

            // P05 Vibes
            var vibes = registry.GetContribution<IVibesContribution>("preset.feature.vibes");
            vibes.Remember("group:10001", "低落", "测试气氛", now);
            Check(vibes.IsSober("group:10001", now) && vibes.Hint("group:10001", now) == "低落：测试气氛",
                "P05 vibes tracks sober state and hint from isolated package");

            // P03 Stickers + packaged UI asset delivery + DispatchProxy byte[] ValidatePayload regression
            var stickerPolicy = registry.GetContribution<IStickerPolicyContribution>("preset.feature.stickers");
            Check(stickerPolicy.Allow("group:10001", "s1", 30, now, out _), "P03 stickers allows initial send");
            stickerPolicy.NoteSent("group:10001", "s1", now);
            Check(!stickerPolicy.Allow("group:10001", "s1", 30, now.AddSeconds(5), out _),
                "P03 stickers enforces cooldown and anti-repeat");
            Check(stickerPolicy.ValidatePayload([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0], 1024).Valid
                && !stickerPolicy.ValidatePayload([0x00, 0x01, 0x02, 0x03, 0, 0, 0, 0, 0, 0, 0, 0], 1024).Valid,
                "Review regression: IStickerPolicyContribution.ValidatePayload(byte[], long) works through DispatchProxy in isolated ALC");
            var stickerUiSvc = new PluginUiAssetService(packages, registry, () => DateTimeOffset.UtcNow);
            var stickerUi = await stickerUiSvc.OpenSessionAsync("preset.feature.stickers", default);
            var stickerTicket = stickerUi.EntryUrl.Split('/')[7];
            var stickerHtml = await stickerUiSvc.ReadAssetAsync("preset.feature.stickers", stickerUi.ReleaseSha256, stickerTicket, "web/index.html", false, default);
            Check(stickerHtml.Status == 200 && (stickerHtml.ContentType ?? "").Contains("text/html"),
                "P03 stickers serves packaged runtime UI via D2.1 asset service");

            // P01 Music, P02 Voice, P06 Profiles
            var music = registry.GetContribution<IMusicPolicyContribution>("preset.feature.music");
            Check(music.TryBeginListen("group:10001", 30, now, out _) && !music.TryBeginListen("group:10001", 30, now.AddSeconds(5), out _),
                "P01 music enforces listen cooldown");
            var voice = registry.GetContribution<IVoicePolicyContribution>("preset.feature.voice");
            voice.MarkSent("group:10001", now);
            Check(!voice.Allow("group:10001", 8, now.AddSeconds(2), out _) && voice.Allow("group:10001", 8, now.AddSeconds(10), out _),
                "P02 voice enforces burst breaker");
            var profiles = registry.GetContribution<IProfilesPolicyContribution>("preset.feature.profiles");
            Check(profiles.AllowsProfileInjection(true, true) && !profiles.AllowsMemoryMutation(true, true, legacySummaryDisabled: true),
                "P06 profiles enforces authorized scope and never resurrects disabled legacy summary");

            // S05 HealthReporting, S02 ModelServices, S03 Agent+McpTools, S04 Conversation
            var health = registry.GetContribution<IHealthReportingContribution>("system.health-reporting");
            var report = health.FormatSummaryReport(new HealthMetricsSnapshot(3, 1, 2, 120, true, null, null), now);
            Check(report.Contains("在线") && report.Contains("会话：3"), "S05 health reporting formats summary gracefully without optional metrics");
            var models = registry.GetContribution<IModelServicesContribution>("system.model-services");
            var route = models.SelectRoute("primary", "synthetic-main", "synthetic-fast", fastReplyEnabled: true, 999);
            Check(route.IsFastReply && route.ModelName == "synthetic-fast" && route.TimeoutSeconds == 300,
                "S02 model services applies fast-reply tier and 300s timeout ceiling");
            var agent = registry.GetContribution<IAgentRuntimeContribution>("system.agent");
            Check(agent.EvaluateAdmission("web", false, false, true).Allowed && !agent.EvaluateAdmission("qq", false, false, true).Allowed,
                "S03 agent separates Web and QQ admission");
            Check(!agent.FilterAllowedTools(["bash", "docker"], dockerSwitchEnabled: true, dockerAvailable: false).Contains("docker"),
                "S03 agent enforces dual-lock Docker tool guard");
            var mcp = registry.GetContribution<IMcpToolsContribution>("system.mcp-tools");
            Check(mcp.MergeToolSources(["srv-a"], ["py_tool"]).Length == 2, "S03 mcp-tools merges MCP + Python tool sources");
            var conv = registry.GetContribution<IConversationContribution>("system.conversation");
            Check(conv.ComputeContextWindow(50, energySaving: true) == 12 && conv.ComputeContextWindow(50, energySaving: false) == 50,
                "S04 conversation enforces energy-saving context window budget");

            // S01 Platforms: all 4 exclusive-connection drivers declare cold adoption + exclusiveResources
            foreach (var pid in new[] { "system.platform-qq-private", "system.platform-qq-official", "system.platform-feishu", "system.platform-local" })
            {
                var pkg = installed.Single(p => p.Id == pid);
                var drv = registry.GetContribution<IPlatformDriverContribution>(pid);
                Check(pkg.Active!.Adoption == PluginAdoptionMode.Cold && pkg.Active.ExclusiveResources is { Count: > 0 } && drv.RequiresExclusiveConnection,
                    $"{pid} declares cold adoption and exclusive connection resource (A39)");
            }
        }

        // ── 2. A31 (D5.3): Two Non-Preinstalled Synthetic IDs with Zero Host Source Changes ──
        var a31Root = Path.Combine(parentRoot, "a31-non-preinstalled-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(a31Root);
        var dllBytes = await File.ReadAllBytesAsync(typeof(CommunityChatNotePluginV1).Assembly.Location);
        static byte[] MakeZip(params (string Path, byte[] Content)[] entries)
        {
            using var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (p, c) in entries) { using var s = z.CreateEntry(p).Open(); s.Write(c); }
            return ms.ToArray();
        }
        static string ShaHex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        var convContractBytes = await File.ReadAllBytesAsync(typeof(ITurnContextContributor).Assembly.Location);
        var convContract = new PluginSharedContract(
            "BotAgent.Conversation.Contracts",
            typeof(ITurnContextContributor).Assembly.GetName().Version!.ToString(),
            ShaHex(convContractBytes),
            "system.conversation");
        var noteZipV1 = MakeZip(("ext.dll", dllBytes), ("BotAgent.Conversation.Contracts.dll", convContractBytes));
        var noteZipV2 = MakeZip(("ext.dll", dllBytes), ("BotAgent.Conversation.Contracts.dll", convContractBytes), ("meta.txt", Encoding.UTF8.GetBytes("v2")));
        var panelHtmlV1 = Encoding.UTF8.GetBytes("<!doctype html><h1>Custom Panel v1</h1>");
        var panelHtmlV2 = Encoding.UTF8.GetBytes("<!doctype html><h1>Custom Panel v2</h1>");
        var panelJs = Encoding.UTF8.GetBytes("console.log('panel');");
        var panelZipV1 = MakeZip(("ext.dll", dllBytes), ("web/index.html", panelHtmlV1), ("web/app.js", panelJs));
        var panelZipV2 = MakeZip(("ext.dll", dllBytes), ("web/index.html", panelHtmlV2), ("web/app.js", panelJs));

        var noteUrlV1 = "https://github.com/example/ext/releases/download/note-1/note-1.zip";
        var noteUrlV2 = "https://github.com/example/ext/releases/download/note-2/note-2.zip";
        var panelUrlV1 = "https://github.com/example/ext/releases/download/panel-1/panel-1.zip";
        var panelUrlV2 = "https://github.com/example/ext/releases/download/panel-2/panel-2.zip";

        var noteManifestV1 = new PluginPackageManifest("community.ext.chat-note", "1.0.0", "ext.dll", typeof(CommunityChatNotePluginV1).FullName!,
            ShaHex(noteZipV1), noteUrlV1, 1, "net10.0", "1.0.0", [], [], []) { SharedContracts = [convContract], Adoption = PluginAdoptionMode.Cold };
        var noteManifestV2 = new PluginPackageManifest("community.ext.chat-note", "2.0.0", "ext.dll", typeof(CommunityChatNotePluginV2).FullName!,
            ShaHex(noteZipV2), noteUrlV2, 1, "net10.0", "1.0.0", [], [], []) { SharedContracts = [convContract], Adoption = PluginAdoptionMode.Cold };
        var panelUiV1 = new PluginUiDeclaration(1, "web/index.html",
            [new("web/index.html", ShaHex(panelHtmlV1), "text/html; charset=utf-8"), new("web/app.js", ShaHex(panelJs), "text/javascript; charset=utf-8")],
            ["dialog.close"], "dialog", "Custom Panel v1");
        var panelUiV2 = new PluginUiDeclaration(1, "web/index.html",
            [new("web/index.html", ShaHex(panelHtmlV2), "text/html; charset=utf-8"), new("web/app.js", ShaHex(panelJs), "text/javascript; charset=utf-8")],
            ["dialog.close"], "dialog", "Custom Panel v2");
        var panelManifestV1 = new PluginPackageManifest("community.ext.custom-panel", "1.0.0", "ext.dll", typeof(CommunityCustomPanelPlugin).FullName!,
            ShaHex(panelZipV1), panelUrlV1, 1, "net10.0", "1.0.0", [], [], []) { Ui = panelUiV1 };
        var panelManifestV2 = new PluginPackageManifest("community.ext.custom-panel", "2.0.0", "ext.dll", typeof(CommunityCustomPanelPluginV2).FullName!,
            ShaHex(panelZipV2), panelUrlV2, 1, "net10.0", "1.0.0", [], [], []) { Ui = panelUiV2 };

        var a31Http = new MultiUrlHttp(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [noteUrlV1] = noteZipV1, [noteUrlV2] = noteZipV2,
            [panelUrlV1] = panelZipV1, [panelUrlV2] = panelZipV2
        });
        var a31PkgSvc = new PluginPackageService(a31Root, a31Http);
        var a31Manager = new PluginManager(_ => { });
        var a31UiSvc = new PluginUiAssetService(a31PkgSvc, a31Manager, () => DateTimeOffset.UtcNow);

        // Install, execute, upgrade, uninstall Package #1 (chat context contributor, cold adoption with shared contract)
        await a31PkgSvc.StageInstallAsync(noteManifestV1, default);
        await a31PkgSvc.ApplyAtStartupAsync("community.ext.chat-note", new PluginContext(new EmptyServices()), a31Manager, true, default);
        var fragV1 = a31Manager.GetContribution<ITurnContextContributor>("community.ext.chat-note").Contribute("group:10001", DateTimeOffset.UtcNow);
        Check(fragV1?.Content == "chat-note-v1", "A31 package #1 (non-preinstalled ID) contributes chat turn context v1");
        await a31PkgSvc.StageInstallAsync(noteManifestV2, default);
        await a31PkgSvc.ApplyAtStartupAsync("community.ext.chat-note", new PluginContext(new EmptyServices()), a31Manager, true, default);
        var fragV2 = a31Manager.GetContribution<ITurnContextContributor>("community.ext.chat-note").Contribute("group:10001", DateTimeOffset.UtcNow);
        Check(fragV2?.Content == "chat-note-v2", "A31 package #1 upgrades cleanly to v2");
        await a31PkgSvc.StageUninstallAsync("community.ext.chat-note", default);
        await a31PkgSvc.ApplyAtStartupAsync("community.ext.chat-note", new PluginContext(new EmptyServices()), a31Manager, true, default);
        Check(!a31Manager.IsReady("community.ext.chat-note"), "A31 package #1 uninstalls cleanly");

        // Install, render UI, upgrade (invalidating old session), uninstall Package #2 (packaged UI bundle)
        await a31PkgSvc.StageInstallAsync(panelManifestV1, default);
        await a31PkgSvc.ApplyAsync("community.ext.custom-panel", new PluginContext(new EmptyServices()), a31Manager, true, default);
        var pSessV1 = await a31UiSvc.OpenSessionAsync("community.ext.custom-panel", default);
        var pTicketV1 = pSessV1.EntryUrl.Split('/')[7];
        var pAssetV1 = await a31UiSvc.ReadAssetAsync("community.ext.custom-panel", pSessV1.ReleaseSha256, pTicketV1, "web/index.html", false, default);
        Check(pAssetV1.Status == 200 && Encoding.UTF8.GetString(pAssetV1.Bytes).Contains("Custom Panel v1"),
            "A31 package #2 (non-preinstalled ID) serves packaged runtime UI v1");
        await a31PkgSvc.StageInstallAsync(panelManifestV2, default);
        await a31PkgSvc.ApplyAsync("community.ext.custom-panel", new PluginContext(new EmptyServices()), a31Manager, true, default);
        var pSessV2 = await a31UiSvc.OpenSessionAsync("community.ext.custom-panel", default);
        var pTicketV2 = pSessV2.EntryUrl.Split('/')[7];
        var crossReleaseTicket = await a31UiSvc.ReadAssetAsync("community.ext.custom-panel", pSessV2.ReleaseSha256, pTicketV1, "web/index.html", false, default);
        Check(crossReleaseTicket.Status == 401 && pSessV2.GenerationId != pSessV1.GenerationId && pSessV2.ReleaseSha256 != pSessV1.ReleaseSha256,
            "A31 package #2 upgrade rotates generation and rejects previous release ticket on new release");
        var pAssetV2 = await a31UiSvc.ReadAssetAsync("community.ext.custom-panel", pSessV2.ReleaseSha256, pTicketV2, "web/index.html", false, default);
        Check(pAssetV2.Status == 200 && Encoding.UTF8.GetString(pAssetV2.Bytes).Contains("Custom Panel v2"),
            "A31 package #2 serves upgraded UI v2 without host frontend rebuild");
        await a31PkgSvc.StageUninstallAsync("community.ext.custom-panel", default);
        await a31PkgSvc.ApplyAsync("community.ext.custom-panel", new PluginContext(new EmptyServices()), a31Manager, true, default);
        Check(!a31Manager.IsReady("community.ext.custom-panel"), "A31 package #2 uninstalls cleanly");

        if (srcHashBefore is not null && repoRoot is not null)
        {
            var srcHashAfter = HashDirectory(Path.Combine(repoRoot, "src"));
            Check(string.Equals(srcHashBefore, srcHashAfter, StringComparison.Ordinal),
                "A31 host src/ SHA-256 hash is 100% identical after installing, executing, upgrading, and uninstalling two non-preinstalled packages");
        }

        // ── 3. D0-VOICE-ERROR-10: VoiceService redacts upstream error body canary ──
        const string voiceCanary = "CANARY_VOICE_SECRET_445566";
        var voiceLogs = new List<string>();
        var voiceHttp = new ScriptedHttp { Status = HttpStatusCode.InternalServerError, Text = $"{{\"error\":\"{voiceCanary}\"}}" };
        var voiceSvc = new VoiceService(voiceHttp, () => new AppSettings { TtsServiceUrl = "http://127.0.0.1:5000" },
            new SecretsStore(), voiceLogs.Add);
        var synthRes = await voiceSvc.SynthesizeAsync("hello", null, null, default);
        var healthRes = await voiceSvc.HealthAsync(default);
        Check(synthRes.Data is null && synthRes.Error is not null && !synthRes.Error.Contains(voiceCanary, StringComparison.Ordinal)
            && voiceLogs.All(l => !l.Contains(voiceCanary, StringComparison.Ordinal))
            && !healthRes.Ok && healthRes.Error is not null && !healthRes.Error.Contains(voiceCanary, StringComparison.Ordinal),
            "D0-VOICE-ERROR-10: VoiceService never leaks upstream error body canary to logs or callers");

        // ── 4. D0-PLATFORM-LIFETIME-01~04: Drain + throwing cancel callback + ConnectionChanged drain ──
        var fakeSource = new SyntheticDrainSource();
        var adapter = new QqChatSourcePlatformAdapter(fakeSource,
            new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy),
            PlatformCapabilities.FeishuTextOnly, "QQ Synthetic", "qq-synthetic");
        var stopCalls = 0;
        var startCalls = 0;
        var reg = new PlatformDriverRegistration(adapter,
            start: _ => { Interlocked.Increment(ref startCalls); return Task.CompletedTask; },
            stop: _ => { Interlocked.Increment(ref stopCalls); return Task.CompletedTask; },
            initiallyRunning: true);
        var ops = new PlatformDriverOperations([reg]);
        reg.Lifetime.Register(() => throw new InvalidOperationException("synthetic_cancel_boom"));

        var connCbEntered = new ManualResetEventSlim(false);
        var connCbRelease = new ManualResetEventSlim(false);
        var connCbActive = 0;
        adapter.ConnectionChanged += _ =>
        {
            Interlocked.Exchange(ref connCbActive, 1);
            connCbEntered.Set();
            connCbRelease.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref connCbActive, 0);
        };
        var fireTask = Task.Run(() => fakeSource.RaiseConnectionChanged(true));
        Check(connCbEntered.Wait(TimeSpan.FromSeconds(3)), "D0-PLATFORM-LIFETIME-03: slow ConnectionChanged entered");
        var stopTask = ops.DispatchAsync(reg.Key, DriverCommand.Stop, default);
        await Task.Delay(60);
        Check(!stopTask.IsCompleted, "D0-PLATFORM-LIFETIME-03: Stop waits for in-flight ConnectionChanged callback");
        connCbRelease.Set();
        var stopRes = await stopTask;
        await fireTask;
        Check(Volatile.Read(ref connCbActive) == 0 && stopCalls == 1 && !string.IsNullOrEmpty(stopRes.ErrorCode),
            "D0-PLATFORM-LIFETIME-01/03/04: throwing cancel callback still drains ConnectionChanged, still invokes _stop, and surfaces error");
        var restartRes = await ops.DispatchAsync(reg.Key, DriverCommand.Start, default);
        Check(restartRes.Code == ManagedResultCode.Ok && startCalls == 1,
            "D0-PLATFORM-LIFETIME-01/02: _stopping resets cleanly after throwing cancel callback so restart succeeds");

        // ── 5. A03/A27: Removing deeply migrated packages leaves ZERO timers and fail-closed facades ──
        var emptyPlugins = new PluginManager(_ => { });
        var emptyBox = new BotAgent.Services.SettingsBox(new AppSettings
        {
            EnableMusic = true,
            EnableVoice = true,
            EnableStickers = true,
            EnablePoke = true,
            EnableProfileSummary = true,
            HealthReportEnabled = true,
            HealthReportTargets = "10001"
        });
        var musicFacade = new BotAgent.Services.Music.MusicUseCase(null, fakeSource, _ => { }, emptyPlugins);
        Check(!musicFacade.IsReady
            && !musicFacade.TryBeginListen("group:10001", 30, out var musicReason)
            && musicReason == "plugin_contribution_unavailable"
            && await musicFacade.ListenAsync("group:10001", "synthetic-song", "tester") is null
            && !(await musicFacade.ShareWithReceiptAsync("synthetic-song", true, 10001)).Ok,
            "A03/A27: removing preset.feature.music leaves fail-closed MusicUseCase with zero residual executor");

        var voiceFacade = new BotAgent.Services.Voice.VoiceUseCase(null, emptyBox, _ => { }, emptyPlugins);
        Check(!voiceFacade.Allow("group:10001", out var voiceReason)
            && voiceReason == "plugin_contribution_unavailable"
            && voiceFacade.BuildSpeakUrls(["hello"], null, null, null).Count == 0
            && (await voiceFacade.TestAsync("hello", null, null, default)).Data is null,
            "A03/A27: removing preset.feature.voice leaves fail-closed VoiceUseCase with zero residual executor");

        using var stickerFacade = new BotAgent.Services.Stickers.StickerService(null!, null!, fakeSource, emptyBox, _ => { }, emptyPlugins);
        stickerFacade.EnsureTimer(true);
        Check(!stickerFacade.Allow("group:10001", "synthetic-1", out var stickerReason)
            && stickerReason == "plugin_contribution_unavailable"
            && stickerFacade.PendingDescribe == 0,
            "A03/A27: removing preset.feature.stickers fails closed and creates zero timer");

        var vibesFacade = new BotAgent.Services.Conversations.VibeTracker(emptyPlugins);
        vibesFacade.Remember("group:10001", "低落", "synthetic");
        Check(vibesFacade.Current("group:10001") == string.Empty && vibesFacade.Hint("group:10001") is null && !vibesFacade.IsSober("group:10001"),
            "A03/A27: removing preset.feature.vibes leaves zero local vibe state");

        var healthFacade = new BotAgent.Services.Ops.HealthReportService(
            emptyBox, null!, null!, new BotAgent.Services.Qq.BotIdentity(), null!, voiceFacade, fakeSource, voiceHttp, null!, null, emptyPlugins);
        healthFacade.Start();
        var healthSend = await healthFacade.SendNowAsync("synthetic-removed");
        Check(healthFacade.NextRunAt is null && !healthSend.Ok && healthFacade.SentCount == 0,
            "A03/A27: removing system.health-reporting leaves zero report timer and fail-closed facade while /healthz stays host-owned");

        // ── Review Regressions: owner-scoped host ports & no split-brain toggle on uninitialized RuntimePrepared ──
        var hostServicesType = typeof(PluginPackageService).Assembly.GetType("BotAgent.Host.ResearchPluginServices", throwOnError: true)!;
        var hostServicesCtor = hostServicesType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length).First();
        var hostServices = (IServiceProvider)hostServicesCtor.Invoke([null, null, null, null, Array.Empty<IDisposable>()]);
        var registerForPlugin = hostServicesType.GetMethod("RegisterForPlugin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(IHealthReportHostPorts));
        var fakeHealthPorts = healthFacade.CreateHostPorts();
        registerForPlugin.Invoke(hostServices, ["system.health-reporting", fakeHealthPorts]);
        var baseContext = new PluginContext(hostServices);
        var scopeMethod = typeof(PluginPackageService).Assembly
            .GetType("BotAgent.Adapters.Plugins.PluginActivationDataStore", throwOnError: true)!
            .GetMethod("Scope", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var emptyDeps = new EmptyPluginDeps();
        var unapprovedScope = ((PluginContext)scopeMethod.Invoke(null, [baseContext, null, null, null, null, emptyDeps, null, "system.health-reporting", false])!).Services;
        var otherPluginScope = ((PluginContext)scopeMethod.Invoke(null, [baseContext, null, null, null, null, emptyDeps, null, "community.untrusted", true])!).Services;
        var approvedOwnerScope = ((PluginContext)scopeMethod.Invoke(null, [baseContext, null, null, null, null, emptyDeps, null, "system.health-reporting", true])!).Services;
        Check(hostServices.GetService(typeof(IHealthReportHostPorts)) is null
            && unapprovedScope.GetService(typeof(IHealthReportHostPorts)) is null
            && otherPluginScope.GetService(typeof(IHealthReportHostPorts)) is null
            && ReferenceEquals(approvedOwnerScope.GetService(typeof(IHealthReportHostPorts)), fakeHealthPorts),
            "Review regression: host ports are strictly owner-scoped and never leaked ambiently to other/unapproved plugins");

        var splitBrainMgr = new PluginManager(_ => { });
        var coldStub = new CommunityChatNotePluginV1();
        await splitBrainMgr.ReplaceWithBindingsAsync(coldStub, enabled: false, apiVersion: 1, bind: _ => { });
        Check(!splitBrainMgr.IsInitialized(coldStub.Id) && splitBrainMgr.IsRuntimePrepared(coldStub.Id) && !splitBrainMgr.IsOperational(coldStub.Id),
            "Review regression: disabled RuntimePrepared package starts uninitialized and non-operational");

        return checks;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BotAgent.slnx")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string HashDirectory(string root)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}test-results{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}playwright-report{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}.vite{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            sha.AppendData(Encoding.UTF8.GetBytes(rel + "\n"));
            sha.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class SyntheticDrainSource : IQqChatSource
    {
        public bool IsConnected => true;
        public string? LastErrorCode => null;
        public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged;
        public event Action<string>? LogMessage { add { } remove { } }
        public void RaiseConnectionChanged(bool connected) => ConnectionChanged?.Invoke(connected);
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool markAsAt = false)
            => Task.FromResult(new SendResult(true));
        public Task<SendResult> SendImageWithReceiptAsync(bool isGroup, long targetId, byte[] imageData, CancellationToken ct = default, long? replyToMessageId = null)
            => Task.FromResult(new SendResult(true));
        public Task<SendResult> SendVoiceWithReceiptAsync(bool isGroup, long targetId, string voiceUrl, CancellationToken ct = default)
            => Task.FromResult(new SendResult(true));
        public Task<SendResult> SendMusicWithReceiptAsync(bool isGroup, long targetId, string type, string songId, string? title = null, CancellationToken ct = default)
            => Task.FromResult(new SendResult(true));
        public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] imageData, CancellationToken ct = default, long? replyToMessageId = null) => Task.FromResult(true);
        public Task<bool> SendVoiceAsync(bool isGroup, long targetId, string voiceUrl, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendMusicAsync(bool isGroup, long targetId, string type, string songId, string? title = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RecallMessageAsync(long messageId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default) => Task.FromResult<(string?, long)>((null, 0));
        public Task<long> GetLoginIdAsync(CancellationToken ct = default) => Task.FromResult(10001L);
        public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetMemberNameAsync(long groupId, long userId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<List<string>> FetchCustomFacesAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private sealed class EmptyPluginDeps : IPluginDependencies
    {
        public T Get<T>(string providerId) where T : class, IPluginContribution
            => throw new InvalidOperationException("no_dependency:" + providerId);
    }
}

public sealed class CommunityChatNotePluginV1 : IBotPlugin, IPluginContributionSource
{
    private readonly Contributor _c = new("chat-note-v1");
    public string Id => "community.ext.chat-note";
    public string Name => "Community Chat Note";
    public string Version => "1.0.0";
    public string Description => "A31 non-preinstalled chat context contributor v1";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    public object? GetContribution(Type contract) => contract == typeof(ITurnContextContributor) ? _c : null;
    private sealed class Contributor(string content) : ITurnContextContributor
    {
        public TurnContextFragment? Contribute(string sourceKey, DateTimeOffset now) => new("CommunityNote", content);
    }
}

public sealed class CommunityChatNotePluginV2 : IBotPlugin, IPluginContributionSource
{
    private readonly Contributor _c = new("chat-note-v2");
    public string Id => "community.ext.chat-note";
    public string Name => "Community Chat Note";
    public string Version => "2.0.0";
    public string Description => "A31 non-preinstalled chat context contributor v2";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    public object? GetContribution(Type contract) => contract == typeof(ITurnContextContributor) ? _c : null;
    private sealed class Contributor(string content) : ITurnContextContributor
    {
        public TurnContextFragment? Contribute(string sourceKey, DateTimeOffset now) => new("CommunityNote", content);
    }
}

public sealed class CommunityCustomPanelPlugin : IBotPlugin
{
    public string Id => "community.ext.custom-panel";
    public string Name => "Community Custom Panel";
    public string Version => "1.0.0";
    public string Description => "A31 non-preinstalled packaged UI bundle v1";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class CommunityCustomPanelPluginV2 : IBotPlugin
{
    public string Id => "community.ext.custom-panel";
    public string Name => "Community Custom Panel";
    public string Version => "2.0.0";
    public string Description => "A31 non-preinstalled packaged UI bundle v2";
    public PluginCategory Category => PluginCategory.Feature;
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}

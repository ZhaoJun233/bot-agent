extern alias StorageModule;

using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Jargon;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Model;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Profiles;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Tools;
using BotAgent.Engine.Memory;
using BotAgent.Platforms.Common;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Documents;
using BotAgent.Services.Jargon;
using BotAgent.Services.Knowledge;
using BotAgent.Services.Memory;
using BotAgent.Services.Net;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Profiles;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Resilience;
using BotAgent.Services.Stickers;
using BotAgent.Services.Tools;
using KnowledgeStorageImpl = StorageModule::BotAgent.Storage.KnowledgeStore;

namespace BotAgent.ReviewRemediationProbe;

internal static class Phase3MemoryKnowledgeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await RunKnowledgeStoreAndPackageTestsAsync(check);
        await RunTaintExfiltrationDefenseTestsAsync(check);
        await RunCommunityMemoryIsolationAndPurgeTestsAsync(check);
        await RunAttestedProfileSummaryGateTestsAsync(check);
        RunPrivateInteractiveCardTests(check);
        await RunReviewRemediationRegressionTestsAsync(check);
    }

    private static async Task RunKnowledgeStoreAndPackageTestsAsync(Action<bool, string> check)
    {
        var userVersion = AppDatabase.Scalar<int>("PRAGMA user_version");
        check(userVersion == 15, $"P3 knowledge store keeps SQLite PRAGMA user_version == 15 (actual={userVersion})");

        var store = new KnowledgeStorageImpl();
        var options = new KnowledgeOptions(MaxDocsPerScope: 4, MaxChunksPerScope: 64, MaxChunkChars: 180, DefaultTopK: 4);
        var scopeA = "qq.private|legacy||group:66001";
        var scopeB = "qq.private|legacy||group:66002";

        var savedZh = store.SaveDocument(scopeA, "发布值班规范",
            "周五晚 20:00 执行灰度巡检与 Caddyfile 路由检查，异常时按回滚手册恢复。\n第二条：确认容器状态。", "note", options);
        var savedEn = store.SaveDocument(scopeA, "Deployment Checklist",
            "Verify release checksum and healthz readiness before cutting over traffic.", "note", options);
        var savedB = store.SaveDocument(scopeB, "B群机密备忘",
            "B 群独享口令 BETA_999 绝不跨群检索。", "note", options);

        check(savedZh.Ok && savedZh.Document is not null && savedEn.Ok && savedB.Ok,
            "P3.4 knowledge store persists Chinese and English documents cleanly");

        var zhHits = store.Search(scopeA, "灰度巡检", 4);
        check(zhHits.Length == 1 && zhHits[0].DocId == savedZh.Document!.DocId && zhHits[0].Snippet.Contains("灰度巡检"),
            "P3.4 BM25 retrieves matching Chinese document by bigram tokens");

        var enHits = store.Search(scopeA, "caddyfile", 4);
        check(enHits.Length == 1 && enHits[0].DocId == savedZh.Document!.DocId,
            "P3.4 BM25 retrieves lowercase unicode61 identifier case-insensitively");

        var crossHits = store.Search(scopeA, "BETA_999", 4);
        check(crossHits.Length == 0, "P3.4 knowledge search strictly isolates scopeA from scopeB");

        check(store.ForgetDocument(scopeA, savedZh.Document!.DocId)
            && store.Search(scopeA, "灰度巡检", 4).Length == 0
            && store.ListDocuments(scopeA).All(d => d.DocId != savedZh.Document!.DocId),
            "P3.4 forget removes document and evicts BM25 hits immediately");

        // Quota enforcement
        store.SaveDocument(scopeA, "Doc2", "第二篇配额文档", "note", options);
        store.SaveDocument(scopeA, "Doc3", "第三篇配额文档", "note", options);
        store.SaveDocument(scopeA, "Doc4", "第四篇配额文档", "note", options);
        var overQuota = store.SaveDocument(scopeA, "Doc5", "超限文档", "note", options);
        check(!overQuota.Ok && overQuota.ReasonCode == "doc_quota_exceeded",
            "P3.4 knowledge store enforces per-scope MaxDocsPerScope quota");
        store.ClearScope(scopeA);
        store.ClearScope(scopeB);

        // Plugin + HostPortsAdapter + attachment ingestion without external URLs
        var docPlugin = OfficialFeatureFactory.DocumentsPlugin();
        var docHttp = new DummyHttp();
        var docAdapter = new DocumentHostPortsAdapter(docHttp, () => false, () => null, _ => { });
        await docPlugin.InitializeAsync(new PluginContext(new ServiceMap(
            (typeof(IDocumentHostPorts), docAdapter),
            (typeof(Action<string>), (Action<string>)(_ => { })))), CancellationToken.None);

        var manager = new PluginManager(_ => { });
        await manager.ReplaceAsync(docPlugin, true);

        using var memory = CreateMemory(enabled: true);
        using var kbPorts = new KnowledgeHostPortsAdapter(store, () => null, _ => { }, docAdapter, manager, memory.Memory);
        var kbPlugin = OfficialFeatureFactory.KnowledgePlugin();
        await kbPlugin.InitializeAsync(new PluginContext(new ServiceMap(
            (typeof(IKnowledgeHostPorts), kbPorts),
            (typeof(Action<string>), (Action<string>)(_ => { })))), CancellationToken.None);
        await manager.ReplaceAsync(kbPlugin, true);

        var docxBytes = BuildZip(("word/document.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>架构评审核心决议：启用分域知识库。</w:t></w:r></w:p></w:body></w:document>"));
        docAdapter.StageInboundDocument("group:66010", "notes.docx", docxBytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        var urlDenied = await kbPorts.SaveAttachmentAsync("group:66010", "https://example.com/secret.txt", null, CancellationToken.None);
        check(!urlDenied.Ok && urlDenied.ReasonCode == "url_not_allowed_for_knowledge_attachment",
            "P3.4 knowledge attachment ingestion refuses outbound HTTP URLs");

        var docSaved = await kbPorts.SaveAttachmentAsync("group:66010", "notes.docx", "评审纪要", CancellationToken.None);
        check(docSaved.Ok && docSaved.Document is not null, "P3.4 knowledge ingests staged DOCX from same conversation");

        var tools = (IToolContribution)((IPluginContributionSource)kbPlugin).GetContribution(typeof(IToolContribution))!;
        var toolSearch = await tools.ExecuteAsync("group:66010", new("kb.search", new JsonObject { ["query"] = "分域知识库" }), CancellationToken.None);
        var toolOther = await tools.ExecuteAsync("group:66011", new("kb.search", new JsonObject { ["query"] = "分域知识库" }), CancellationToken.None);
        check(toolSearch.Ok && toolSearch.ModelText!.Contains("架构评审核心决议")
            && toolOther.Ok && toolOther.ModelText!.Contains("命中 0 段"),
            "P3.4 kb.search tool respects sourceKey isolation");

        kbPorts.ClearConversation("group:66010");
        await manager.StopAllAsync(CancellationToken.None);
    }

    private static async Task RunTaintExfiltrationDefenseTestsAsync(Action<bool, string> check)
    {
        using var fixture = CreateMemory(enabled: true);
        var noteReq = fixture.Memory.RequestRemember(
            fixture.PrivateCtx,
            new(new(UnifiedMemoryFactCategory.PersonalNote, "周三架构评审口令ALPHA_CANARY"), [], true)).Entry!;
        fixture.Memory.Confirm(fixture.PrivateCtx, new(noteReq.FactId, noteReq.Version, noteReq.ConfirmationToken, true, false));

        var store = new KnowledgeStorageImpl();
        using var kbPorts = new KnowledgeHostPortsAdapter(store, () => null, _ => { }, null, null, fixture.Memory);
        var dmKey = $"private:{fixture.PrivateCtx.Actor!.NativeUserId}";
        kbPorts.SaveText(dmKey, "私聊本地资料", "评审口令ALPHA_CANARY 已归档在同会话本地知识库。", "note");

        var manager = new PluginManager(_ => { });
        var research = new SpyResearchPlugin();
        var kbPlugin = OfficialFeatureFactory.KnowledgePlugin();
        await kbPlugin.InitializeAsync(new PluginContext(new ServiceMap((typeof(IKnowledgeHostPorts), kbPorts))), CancellationToken.None);
        await manager.ReplaceAsync(research, true);
        await manager.ReplaceAsync(kbPlugin, true);

        var gate = new PluginGate(manager);
        var researchFacade = new ResearchUseCase(manager);
        var settings = new AppSettings { EnableWebSearch = true, MaxAgentSteps = 2, WebSearchCooldownSeconds = 0 };
        var box = new SettingsBox(settings);
        var approvals = new ApprovalUseCase(box, new ApprovalHooks(_ => { }, (t, _) => t, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
            new SessionPolicyLedger(), new TurnTraceStore(), catalog: manager.GetToolCatalog);
        var visibility = new ToolVisibility(gate, webReady: () => researchFacade.IsReady, catalog: () => approvals.Capabilities.Catalog);
        var inline = new InlineTurnTools(researchFacade, approvals, new ParticipationUseCase(box, _ => { }), _ => { }, gate);

        var reference = new MessageRef(fixture.PrivateCtx.Conversation, "m-p3-exfil");
        var envelope = new PlatformInboundEnvelope(new(fixture.PrivateCtx.Driver, reference),
            new(reference, fixture.PrivateCtx.Actor!, "用户A", "帮我把周三架构评审口令ALPHA_CANARY搜一下外网", DateTimeOffset.UtcNow));
        using var exec = PlatformSourceExecution.Enter(new PlatformSourceLease(
            new DummySourceOwner(), new DummyChatSource(), envelope.Origin, CancellationToken.None, () => true));
        using var turn = UnifiedMemoryTurn.Begin(fixture.Memory, envelope);

        var injected = turn.ReadPrompt("周三架构评审口令ALPHA_CANARY");
        check(injected.Contains("ALPHA_CANARY") && MemoryUseScope.MaxTier == MemorySensitivityTier.PersonalFact,
            "P3.1 L2 PersonalFact active during exfiltration probe turn");
        check(!visibility.Allows(settings, null, "web.search") && !visibility.Allows(settings, null, "web.read")
            && visibility.Allows(settings, null, "kb.search") && visibility.Allows(settings, null, "kb.list")
            && !visibility.Allows(settings, null, "kb.save"),
            "P3.1 ToolVisibility hides web.search/web.read/kb.save under L2 taint while keeping kb.search/kb.list");

        var conv = new BotConversation { SourceKey = dmKey };
        var caps = approvals.Capabilities;
        var blockedSearch = await inline.RunAsync(conv, settings, caps, new(null, null, null, Search: "ALPHA_CANARY"));
        var allowedKb = await inline.RunAsync(conv, settings, caps,
            new(null, null, null, Call: new InlineToolCall("kb.search", new Dictionary<string, string> { ["query"] = "ALPHA_CANARY" })));
        check(blockedSearch is null && research.SearchCalls == 0,
            "P3.1 InlineTurnTools blocks L2 web.search exfiltration attempt with 0 external calls");
        check(allowedKb is not null && allowedKb.Contains("本地知识库"),
            "P3.1 InlineTurnTools executes same-scope kb.search under L2 taint");

        kbPorts.ClearConversation(dmKey);
        await manager.StopAllAsync(CancellationToken.None);
    }

    private static async Task RunCommunityMemoryIsolationAndPurgeTestsAsync(Action<bool, string> check)
    {
        var jargonRepo = new BotAgent.Adapters.Persistence.JargonStore();
        await jargonRepo.UpsertAsync(new JargonEntry
        {
            Scope = "global", Phrase = "GLOBAL_LEAK_TOKEN", Meaning = "must never leak",
            Status = JargonStatus.Confirmed, HitCount = 5, CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow
        });
        var scoped = await jargonRepo.ListConfirmedForPromptAsync("qq.private|legacy||group:66020");
        check(scoped.All(j => j.Phrase != "GLOBAL_LEAK_TOKEN"),
            "P3.3 JargonStore no longer leaks 'global' rows into conversation scopes");

        const string sourceKey = "group:66021";
        var episodeRepo = new BotAgent.Adapters.Persistence.EpisodeStore();
        var kbStore = new KnowledgeStorageImpl();
        using var fixture = CreateMemory(enabled: true);
        var box = new SettingsBox(new AppSettings
        {
            RuntimeConversationProfiles =
            [
                new ConversationProfile(
                    new ConversationProfileKey(fixture.GroupCtx.Driver, sourceKey),
                    "灰度测试群",
                    Capabilities: new ConversationCapabilityOverride(
                        [
                            new FeatureOverride(PlatformFeatureId.Episodes, true),
                            new FeatureOverride(PlatformFeatureId.Jargon, true)
                        ]))
            ]
        });
        var policyResolver = new StaticPolicyResolver();
        var jargonSvc = new JargonService(jargonRepo, box);

        using var community = new ScopedCommunityMemoryService(box, policyResolver, episodeRepo, jargonRepo, jargonSvc, fixture.Memory, _ => { });
        using var kbPorts = new KnowledgeHostPortsAdapter(kbStore, () => null, _ => { }, null, null, fixture.Memory);

        var compScope = ScopedCommunityMemoryService.BuildCompositeScope(fixture.GroupCtx.Driver, sourceKey);
        var msgRef = new MessageRef(fixture.GroupCtx.Conversation, "m-comm-1");
        var env = new PlatformInboundEnvelope(new(fixture.GroupCtx.Driver, msgRef),
            new(msgRef, fixture.GroupCtx.Actor!, "群友A", "hello", DateTimeOffset.UtcNow));
        var msg = new BotAgent.Services.OneBot.QqChatMessage(
            1, true, 10001, 66021, "群友A", "今天讨论部署流程", Clock.Now, false, Channel: Channels.Private)
        { Envelope = env };

        using (PlatformSourceExecution.Enter(new PlatformSourceLease(
            new DummySourceOwner(), new DummyChatSource(), env.Origin, CancellationToken.None, () => true)))
        {
            community.ObserveInbound(msg, sourceKey);
            kbPorts.ObserveSourceContext(msg, sourceKey);
        }

        await episodeRepo.InsertAsync(new EpisodeEntry
        {
            Scope = compScope, Title = "部署讨论", Summary = "讨论灰度与回滚", OccurredAt = Clock.UtcNow
        });
        await jargonRepo.UpsertAsync(new JargonEntry
        {
            Scope = compScope, Phrase = "灰度稳了", Meaning = "灰度通过", Status = JargonStatus.Confirmed, HitCount = 3
        });
        kbPorts.SaveText(sourceKey, "灰度笔记", "灰度笔记正文", "note");

        var fact = fixture.Memory.RequestRemember(fixture.GroupCtx,
            new(new(UnifiedMemoryFactCategory.ReplyLength, "brief"), [], true)).Entry!;
        fixture.Memory.Confirm(fixture.PrivateCtx, new(fact.FactId, fact.Version, fact.ConfirmationToken, true, true));

        check(community.Contribute(sourceKey, Clock.UtcNow)?.Content.Contains("灰度") == true
            && kbPorts.Search(sourceKey, "灰度笔记", 4).Length == 1,
            "P3.3 community memory and knowledge populated before Forget");

        check(fixture.Memory.Forget(fixture.PrivateCtx, fact.FactId).Succeeded, "Forget succeeds");
        await community.PurgeSuppressedScopesAsync();

        var remainingEps = await episodeRepo.ListRecentByScopeAsync(compScope, 10);
        var remainingJar = await jargonRepo.ListByScopeAsync(compScope);
        check(remainingEps.Count == 0 && remainingJar.Count == 0
            && kbPorts.List(sourceKey).Length == 0 && community.Contribute(sourceKey, Clock.UtcNow) is null,
            "P3.3/P3.4 Forget purges episodes, jargons, and knowledge for suppressed conversation");
    }

    private static async Task RunAttestedProfileSummaryGateTestsAsync(Action<bool, string> check)
    {
        var box = new SettingsBox(new AppSettings
        {
            ApiKey = "sk-mock", Model = "synthetic-model", ModelBaseUrl = "https://example.com/v1",
            EnableProfileSummary = true, ProfileSummaryThreshold = 1, ProfileSummaryIntervalSeconds = 3600,
            ProfileSummaryMaxChars = 160, EnableStickers = false
        });
        using var fixture = CreateMemory(enabled: true);
        var provenance = new AttestedProfileProvenanceRegistry();
        var allowAttested = false;
        var repo = new SpyProfileRepo();
        var http = new SpyModelHttp();
        var brain = new OpenAiClient(box, http, http, new NoImg(), new NoChat());
        var stickers = new StickerService(null!, brain, null!, box, _ => { });
        var scheduler = new BotScheduler(box, null!, repo, brain, null!, stickers, new PanelNotifier(),
            new BotSchedulerHooks(_ => { }, () => false), _ => true, fixture.Memory,
            allowAttestedSummary: () => allowAttested, profileProvenance: provenance);
        var coord = OfficialFeatureFactory.ProfilesCoordinator(scheduler.CreateProfilesHostPorts());
        try {
        scheduler.BindDirectProfiles(coord);

        var scopedUid = LegacyProfileScopeKey.TryCreate(
            fixture.GroupCtx.Driver, fixture.GroupCtx.Actor!, fixture.GroupCtx.Conversation)!;
        provenance.Record(scopedUid, fixture.GroupCtx, "group:66030");
        repo.Candidates =
        [
            new SummaryCandidate("10002", "群友A", "group:66030", 66030L, ["unscoped message"], "", 1, 0),
            new SummaryCandidate(scopedUid, "群友A", "group:66030", 66030L, ["attested msg 1", "attested msg 2"], "", 2, 0)
        ];

        // 1. Default off -> zero reads, zero HTTP
        await coord.RunSummarySweepAsync();
        check(repo.FindCalls == 0 && http.Calls == 0 && repo.Applied.Count == 0,
            "P3.3 allowAttestedSummary=false performs 0 reads and 0 HTTP calls");

        // 2. Enabled -> only attested candidate with provenance summarized + EvidenceJson stored
        allowAttested = true;
        await coord.RunSummarySweepAsync();
        check(repo.FindCalls == 1 && http.Calls == 1
            && repo.Applied.Count == 1 && repo.Applied[0].Uid == scopedUid
            && repo.EvidenceWritten.Contains("attested msg 2"),
            "P3.3 allowAttestedSummary=true summarizes attested scoped key and writes evidence");

        // 3. Mid-await Forget blocks ApplySummary and Evidence write
        var entry = fixture.Memory.RequestRemember(fixture.GroupCtx,
            new(new(UnifiedMemoryFactCategory.ReplyLength, "brief"), [], true)).Entry!;
        fixture.Memory.Confirm(fixture.PrivateCtx, new(entry.FactId, entry.Version, entry.ConfirmationToken, true, true));
        http.OnSend = () => fixture.Memory.Forget(fixture.PrivateCtx, entry.FactId);
        await coord.RunSummarySweepAsync();
        check(http.Calls == 2 && repo.Applied.Count == 1,
            "P3.3 Forget mid-await revokes receipt and blocks ApplySummary & Evidence write");

        scheduler.Stop();
        stickers.EnsureTimer(false);
        } finally { (coord as IDisposable)?.Dispose(); }
    }

    private static void RunPrivateInteractiveCardTests(Action<bool, string> check)
    {
        const string fid = "0123456789abcdef0123456789abcdef";
        const string tok = "fedcba9876543210fedcba9876543210";
        var prompt = $"""
            本人偏好：PersonalNote = 周三复盘
            事实 {fid} · 版本 1 · 待确认
            //memory confirm {fid} 1 {tok} allow-source private-only
            """;
        var cardText = MemoryPrivateCardHelper.TryBuildFeishuCardJson(prompt, out var cardJson)
            ? System.Text.RegularExpressions.Regex.Unescape(cardJson)
            : string.Empty;
        check(JsonNode.Parse(cardJson) is JsonObject
            && cardText.Contains("确认授权")
            && cardText.Contains("暂不启用")
            && cardText.Contains("遗忘删除")
            && cardText.Contains("memory_cmd"),
            "P3.2 Feishu private card builder generates confirm, reject, and forget callback buttons");

        var kbBuilt = MemoryPrivateCardHelper.TryBuildOfficialKeyboardJson(prompt, out var kb);
        var kbText = kbBuilt ? System.Text.RegularExpressions.Regex.Unescape(kb.ToJsonString()) : string.Empty;
        check(kbBuilt
            && kbText.Contains("确认授权") && kbText.Contains("暂不启用"),
            "P3.2 Official C2C keyboard builder generates confirm and reject callbacks");

        check(MemoryPrivateCardHelper.IsSafePrivateMemoryButtonCommand($"//memory confirm {fid} 1 {tok} allow-source private-only")
            && !MemoryPrivateCardHelper.IsSafePrivateMemoryButtonCommand("//agent do-evil"),
            "P3.2 private card callback validator accepts valid //memory command and rejects arbitrary command");

        check(!MemoryPrivateCardHelper.TryBuildFeishuCardJson("普通群聊回复内容", out _)
            && !MemoryPrivateCardHelper.TryBuildOfficialKeyboardJson("普通群聊回复内容", out _),
            "P3.2 private card helpers ignore ordinary messages");
    }

    private static async Task RunReviewRemediationRegressionTestsAsync(Action<bool, string> check)
    {
        // 1. MemoryTaintPolicy tool classification + ADR-0005 L3 full denial
        check(MemoryTaintPolicy.ClassifyTool("mcp.github.search", readOnly: true, origin: "Mcp") == ToolEgressClass.ExternalReadEgress
            && MemoryTaintPolicy.ClassifyTool("mcp.github.issue", readOnly: false, origin: "Mcp") == ToolEgressClass.ExternalSideEffect
            && MemoryTaintPolicy.ClassifyTool("chat.reply", readOnly: false) == ToolEgressClass.LocalPresentation
            && MemoryTaintPolicy.ClassifyTool("voice.speak", readOnly: false) == ToolEgressClass.LocalPresentation
            && MemoryTaintPolicy.ClassifyTool("sticker.send", readOnly: false) == ToolEgressClass.LocalPresentation
            && MemoryTaintPolicy.ClassifyTool("poke.send", readOnly: false) == ToolEgressClass.LocalPresentation
            && MemoryTaintPolicy.ClassifyTool("kb.save", readOnly: false) == ToolEgressClass.GlobalStateWrite
            && MemoryTaintPolicy.ClassifyTool("kb.forget", readOnly: false) == ToolEgressClass.GlobalStateWrite
            && MemoryTaintPolicy.ClassifyTool("music.share", readOnly: false) == ToolEgressClass.ExternalSideEffect,
            "P3 remediation: MemoryTaintPolicy classifies MCP, local presentation, kb.*, and music.share accurately");

        check(!MemoryTaintPolicy.Allows(MemorySensitivityTier.LegacyProfile, ToolEgressClass.LocalPresentation)
            && !MemoryTaintPolicy.Allows(MemorySensitivityTier.LegacyProfile, ToolEgressClass.SameScopeRead)
            && !MemoryTaintPolicy.Allows(MemorySensitivityTier.LegacyProfile, ToolEgressClass.ExternalReadEgress)
            && !MemoryTaintPolicy.Allows(MemorySensitivityTier.LegacyProfile, ToolEgressClass.ExternalSideEffect)
            && !MemoryTaintPolicy.Allows(MemorySensitivityTier.LegacyProfile, ToolEgressClass.GlobalStateWrite),
            "P3 remediation: L3 LegacyProfile denies all five egress classes");

        // 2. UnifiedMemory Edit caps/duplicates, PendingCount, GetSuppressedScopes, and NoteRelevanceScore "keynote" false-positive fix
        using (var fixture = CreateMemory(enabled: true))
        {
            var pendingProp = fixture.Memory.RequestRemember(fixture.PrivateCtx,
                new(new(UnifiedMemoryFactCategory.Timezone, "Asia/Tokyo"), [], true)).Entry!;
            var pendingProp2 = fixture.Memory.RequestRemember(fixture.PrivateCtx,
                new(new(UnifiedMemoryFactCategory.ReplyLength, "brief"), [], true)).Entry!;
            var beforeReject = fixture.Memory.GetGovernanceSummary().PendingCount;
            fixture.Memory.Reject(fixture.PrivateCtx, pendingProp2.FactId);
            var afterReject = fixture.Memory.GetGovernanceSummary().PendingCount;
            check(beforeReject == 2 && afterReject == 1,
                $"P3 remediation: GetGovernanceSummary().PendingCount excludes rejected proposals (before={beforeReject}, after={afterReject})");

            fixture.Memory.Confirm(fixture.PrivateCtx,
                new(pendingProp.FactId, pendingProp.Version, pendingProp.ConfirmationToken, true, false));

            UnifiedMemoryEntry? firstNote = null;
            for (var i = 0; i < 64; i++)
            {
                var added = fixture.Memory.RequestRemember(fixture.PrivateCtx,
                    new(new(UnifiedMemoryFactCategory.PersonalNote, $"alpha note_{i:D2}"), [], true));
                check(added.Succeeded, $"P3 remediation: note #{i} within 64-cap added");
                firstNote ??= added.Entry;
            }
            var overCap = fixture.Memory.RequestRemember(fixture.PrivateCtx,
                new(new(UnifiedMemoryFactCategory.PersonalNote, "alpha note_64"), [], true));
            var dupEdit = fixture.Memory.Edit(fixture.PrivateCtx, firstNote!.FactId, firstNote.Version,
                new(new(UnifiedMemoryFactCategory.PersonalNote, "alpha note_01"), [], true));
            var editCatConflict = fixture.Memory.Edit(fixture.PrivateCtx, pendingProp2.FactId, pendingProp2.Version,
                new(new(UnifiedMemoryFactCategory.Timezone, "UTC"), [], true));
            check(!overCap.Succeeded && !dupEdit.Succeeded && !editCatConflict.Succeeded,
                "P3 remediation: UnifiedMemory enforces 64-note cap, Edit duplicate rejection, and single-valued category conflict");

            // Test whole-token "note" matching in NoteRelevanceScore without substring false-positive on "keynote"
            using var keynoteFix = CreateMemory(enabled: true);
            for (var i = 0; i < 7; i++)
            {
                var n = keynoteFix.Memory.RequestRemember(keynoteFix.PrivateCtx,
                    new(new(UnifiedMemoryFactCategory.PersonalNote, $"苹果记录{i}"), [], true)).Entry!;
                keynoteFix.Memory.Confirm(keynoteFix.PrivateCtx, new(n.FactId, n.Version, n.ConfirmationToken, true, false));
            }
            var keynoteEntry = keynoteFix.Memory.RequestRemember(keynoteFix.PrivateCtx,
                new(new(UnifiedMemoryFactCategory.PersonalNote, "下周 keynote 会议议程"), [], true)).Entry!;
            keynoteFix.Memory.Confirm(keynoteFix.PrivateCtx, new(keynoteEntry.FactId, keynoteEntry.Version, keynoteEntry.ConfirmationToken, true, false));
            var readKeynote = keynoteFix.Memory.Read(keynoteFix.PrivateCtx, "keynote", 7);
            check(readKeynote.Entries.Any(e => e.Fact.Value.Contains("keynote")),
                "P3 remediation: querying 'keynote' ranks actual keynote note above unrelated notes without substring false-positive");

            var grpPref = fixture.Memory.RequestRemember(fixture.GroupCtx,
                new(new(UnifiedMemoryFactCategory.ReplyLanguage, "zh"), [], true)).Entry!;
            fixture.Memory.Confirm(fixture.PrivateCtx, new(grpPref.FactId, grpPref.Version, grpPref.ConfirmationToken, true, true));
            fixture.Memory.Forget(fixture.PrivateCtx, grpPref.FactId);
            var suppressed = fixture.Memory.GetSuppressedScopes();
            check(suppressed.Contains(new UnifiedMemoryScope(fixture.GroupCtx.Driver, fixture.GroupCtx.Conversation)),
                "P3 remediation: GetSuppressedScopes returns suppressed group scope after Forget");
        }

        // 3. KnowledgeStore 1-char CJK, BuildCompositeScope %7C escaping, KnowledgeCommandService role gate & multiline table
        var ks = new KnowledgeStorageImpl();
        var kbOpts = new KnowledgeOptions(MaxDocsPerScope: 8, MaxChunksPerScope: 64, MaxChunkChars: 180, DefaultTopK: 4);
        var scopePipe1 = KnowledgeHostPortsAdapter.BuildCompositeScope(new DriverKey("qq.private", "a|b", ""), "group:1");
        var scopePipe2 = KnowledgeHostPortsAdapter.BuildCompositeScope(new DriverKey("qq.private", "a", "b|group"), "1");
        check(!string.Equals(scopePipe1, scopePipe2, StringComparison.Ordinal) && scopePipe1.Contains("%7C"),
            $"P3 remediation: BuildCompositeScope escapes '|' delimiters ({scopePipe1} vs {scopePipe2})");

        var oneCharScope = $"qq.private|legacy||group:66099_{Guid.NewGuid():N}";
        ks.SaveDocument(oneCharScope, "养宠备忘", "我家里养了一只橘猫", "note", kbOpts);
        var oneCharHit = ks.Search(oneCharScope, "猫", 4);
        check(oneCharHit.Length == 1 && oneCharHit[0].Snippet.Contains("橘猫"),
            "P3 remediation: 1-char CJK BM25 search ('猫') finds matching document");

        using (var cmdKbPorts = new KnowledgeHostPortsAdapter(ks, () => null, _ => { }))
        {
            var manager = new PluginManager(_ => { });
            var kbPlugin = OfficialFeatureFactory.KnowledgePlugin();
            await kbPlugin.InitializeAsync(new PluginContext(new ServiceMap((typeof(IKnowledgeHostPorts), cmdKbPorts))), CancellationToken.None);
            await manager.ReplaceAsync(kbPlugin, true);
            var gate = new PluginGate(manager);
            var chatSource = new CapturingChatSource();
            var cmdBox = new SettingsBox(new AppSettings { AiModeEnabled = true, ApprovalApprovers = "user:30003" });
            var cmdSvc = new KnowledgeCommandService(chatSource, cmdBox, new StaticPolicyResolver(), manager, gate, cmdKbPorts);

            const long kbGroup = 66098;
            var tableCmd = "//kb save 巡检表\n| 列1 | 列2 |\n| a | b |";
            var memberMsg = new BotAgent.Services.OneBot.QqChatMessage(
                901, true, 30001, kbGroup, "群员M", tableCmd, Clock.Now, false, Channel: Channels.Private, SenderRole: "member")
            { AllowsCommands = true };
            await cmdSvc.HandleAsync(memberMsg);
            check(chatSource.SentTexts.Count == 1 && chatSource.SentTexts[^1].Contains("仅限群主、群管理员或已配置审批人"),
                $"P3 remediation: group member //kb save denied (reply={chatSource.SentTexts.LastOrDefault()})");

            var ownerMsg = new BotAgent.Services.OneBot.QqChatMessage(
                902, true, 30002, kbGroup, "群主O", tableCmd, Clock.Now, false, Channel: Channels.Private, SenderRole: "owner")
            { AllowsCommands = true };
            await cmdSvc.HandleAsync(ownerMsg);
            check(chatSource.SentTexts.Count == 2 && chatSource.SentTexts[^1].Contains("已入库") && chatSource.SentTexts[^1].Contains("《巡检表》"),
                $"P3 remediation: group owner multiline markdown table //kb save succeeds with intact title (reply={chatSource.SentTexts.LastOrDefault()})");

            var docsInGroup = cmdKbPorts.List($"group:{kbGroup}");
            check(docsInGroup.Length == 1 && docsInGroup[0].Title == "巡检表",
                "P3 remediation: multiline markdown table saved under title '巡检表'");

            var approverForget = new BotAgent.Services.OneBot.QqChatMessage(
                903, true, 30003, kbGroup, "审批人A", $"//kb forget {docsInGroup[0].DocId}", Clock.Now, false, Channel: Channels.Private, SenderRole: "member")
            { AllowsCommands = true };
            await cmdSvc.HandleAsync(approverForget);
            check(chatSource.SentTexts[^1].Contains("已遗忘"),
                $"P3 remediation: configured approver allowed to //kb forget (reply={chatSource.SentTexts.LastOrDefault()})");
            await manager.StopAllAsync(CancellationToken.None);
        }

        // 4. FeishuBotGateway P2P _knownChats tracking, fail-closed isPrivate, header.token, and safe JSON parsing
        var feishuBox = new SettingsBox(new AppSettings
        {
            FeishuEnabled = true,
            FeishuAppId = "app-feishu-p3",
            FeishuAppSecret = "sec-feishu-p3",
            FeishuVerificationToken = "tok-p3-verify",
            FeishuWhitelist = "*"
        });
        using var feishuGw = new FeishuBotGateway(feishuBox, new FeishuDummyHttp(), log: null, ids: null, dedupStore: new FeishuWebhookDedupStore());
        var feishuInbound = new List<InboundMessage>();
        feishuGw.InboundReceived += feishuInbound.Add;

        var p2pInboundBody = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["event_id"] = "evt-feishu-p2p-1",
                ["event_type"] = "im.message.receive_v1",
                ["token"] = "tok-p3-verify"
            },
            ["event"] = new JsonObject
            {
                ["sender"] = new JsonObject
                {
                    ["sender_type"] = "user",
                    ["sender_id"] = new JsonObject { ["open_id"] = "ou_p2p_user" }
                },
                ["message"] = new JsonObject
                {
                    ["message_id"] = "om_p2p_1",
                    ["chat_type"] = "p2p",
                    ["chat_id"] = "oc_p2p_tracked",
                    ["message_type"] = "text",
                    ["content"] = "{\"text\":\"hello\"}"
                }
            }
        }.ToJsonString();
        var p2pRes = await feishuGw.HandleWebhookAsync(p2pInboundBody, null, null, null);
        check(p2pRes.Handled && p2pRes.StatusCode == 200 && feishuInbound.Count == 1,
            $"P3 remediation: Feishu header.token + p2p inbound tracked (status={p2pRes.StatusCode}, body={p2pRes.ResponseBody})");

        const string safeMemCmd = "//memory confirm 0123456789abcdef0123456789abcdef 1 fedcba9876543210fedcba9876543210 allow-source private-only";
        var p2pCardNoChatType = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["event_id"] = "evt-feishu-card-p2p",
                ["event_type"] = "card.action.trigger",
                ["token"] = "tok-p3-verify"
            },
            ["event"] = new JsonObject
            {
                ["operator"] = new JsonObject { ["open_id"] = "ou_p2p_user" },
                ["context"] = new JsonObject { ["open_chat_id"] = "oc_p2p_tracked" },
                ["action"] = new JsonObject { ["value"] = new JsonObject { ["memory_cmd"] = safeMemCmd } }
            }
        }.ToJsonString();
        var p2pCardRes = await feishuGw.HandleWebhookAsync(p2pCardNoChatType, null, null, null);
        check(p2pCardRes.Handled && p2pCardRes.StatusCode == 200 && feishuInbound.Count == 2,
            "P3 remediation: Feishu P2P card callback with oc_ chat_id and omitted chat_type succeeds via _knownChats");

        var unknownCardNoChatType = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["event_id"] = "evt-feishu-card-unk",
                ["event_type"] = "card.action.trigger",
                ["token"] = "tok-p3-verify"
            },
            ["event"] = new JsonObject
            {
                ["operator"] = new JsonObject { ["open_id"] = "ou_p2p_user" },
                ["context"] = new JsonObject { ["open_chat_id"] = "oc_unknown_group" },
                ["action"] = new JsonObject { ["value"] = new JsonObject { ["memory_cmd"] = safeMemCmd } }
            }
        }.ToJsonString();
        var unknownCardRes = await feishuGw.HandleWebhookAsync(unknownCardNoChatType, null, null, null);
        check(unknownCardRes.StatusCode == 200 && unknownCardRes.ResponseBody.Contains("仅限本人私聊") && feishuInbound.Count == 2,
            "P3 remediation: Feishu card callback for unknown oc_ chat fails closed without dispatching command");

        var badJsonTypeCard = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["event_id"] = "evt-feishu-card-badtype",
                ["event_type"] = "card.action.trigger",
                ["token"] = "tok-p3-verify"
            },
            ["event"] = new JsonObject
            {
                ["operator"] = new JsonObject { ["open_id"] = "ou_p2p_user" },
                ["context"] = new JsonObject { ["open_chat_id"] = "oc_p2p_tracked" },
                ["action"] = new JsonObject { ["value"] = new JsonObject { ["memory_cmd"] = 12345 } }
            }
        }.ToJsonString();
        var badTypeRes = await feishuGw.HandleWebhookAsync(badJsonTypeCard, null, null, null);
        check(!badTypeRes.Handled && badTypeRes.StatusCode == 400 && badTypeRes.ResponseBody.Contains("invalid_payload"),
            $"P3 remediation: Feishu non-string memory_cmd returns safe 400 without exception (status={badTypeRes.StatusCode})");
    }

    private sealed class CapturingChatSource : IQqChatSource
    {
        public List<string> SentTexts { get; } = [];
        public bool IsConnected => true;
        public event Action<BotAgent.Services.OneBot.QqChatMessage>? MessageReceived { add { } remove { } }
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
        {
            SentTexts.Add(text);
            return Task.FromResult(new SendResult(true));
        }
        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default) => Task.FromResult<(string?, long)>((null, 0));
        public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null) => Task.FromResult(false);
        public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class FeishuDummyHttp : BotAgent.Platforms.Net.IPlatformHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class MemoryFixture : IDisposable
    {
        public readonly UnifiedMemory Memory;
        public readonly UnifiedMemoryContext PrivateCtx;
        public readonly UnifiedMemoryContext GroupCtx;
        public MemoryFixture(bool enabled)
        {
            var store = new MemStore();
            Memory = new UnifiedMemory(store);
            if (enabled) Memory.SetEnabled(true);
            var driver = new DriverKey(PlatformId.QqPrivate, AccountScope.Legacy, string.Empty);
            var actor = new ParticipantId(PlatformId.QqPrivate, AccountScope.Legacy, "10002");
            PrivateCtx = new(actor, driver, new(PlatformId.QqPrivate, AccountScope.Legacy, ConversationKind.PrivateChat, "10002"), true);
            GroupCtx = new(actor, driver, new(PlatformId.QqPrivate, AccountScope.Legacy, ConversationKind.GroupChat, "66021"), true);
        }
        public void Dispose() { }
        private sealed class MemStore : IUnifiedMemoryPersistence
        {
            private UnifiedMemoryState _state = UnifiedMemoryState.Empty;
            public UnifiedMemoryState Load() => _state;
            public void Save(long rev, UnifiedMemoryState next) => _state = next;
        }
    }

    private static MemoryFixture CreateMemory(bool enabled) => new(enabled);

    private sealed class ServiceMap(params (Type ServiceType, object Instance)[] map) : IServiceProvider
    {
        public object? GetService(Type serviceType) => map.FirstOrDefault(x => x.ServiceType == serviceType).Instance;
    }

    private sealed class SpyResearchPlugin : IBotPlugin, IPluginContributionSource, IToolContribution, IResearchUseCase
    {
        public int SearchCalls;
        public string Id => "preset.feature.websearch";
        public string Version => "1.0.0";
        public string Name => "Spy Research";
        public string Description => "Spy";
        public PluginCategory Category => PluginCategory.Feature;
        public bool IsReady => true;
        public bool TryBeginSearch(string sourceKey, int cooldownSeconds, out string reason) { reason = string.Empty; return true; }
        public Task<WebSearchResult?> SearchAsync(string sourceKey, string query, CancellationToken ct = default) { Interlocked.Increment(ref SearchCalls); return Task.FromResult<WebSearchResult?>(null); }
        public Task<string?> ReadPageAsync(string sourceKey, string url, CancellationToken ct = default) { Interlocked.Increment(ref SearchCalls); return Task.FromResult<string?>("READ"); }
        public void AddNote(string sourceKey, string note) { }
        public void KeepNote(string sourceKey, string? note, string why) { }
        public string? TakeNote(string sourceKey) => null;
        public ToolDefinition[] DescribeTools() =>
        [
            new(new("web.search", ToolCategory.WebRead, "Search", true, "research.actions", "query", ToolDefaultPolicy.FollowSwitch, FeatureKey: ToolFeature.WebSearch, Origin: ToolOrigin.Plugin),
                [new("query", Required: true, MinLength: 1, MaxLength: 120)]),
            new(new("web.read", ToolCategory.WebRead, "Read", true, "research.actions", "url", ToolDefaultPolicy.FollowSwitch, FeatureKey: ToolFeature.WebSearch, Origin: ToolOrigin.Plugin),
                [new("url", Required: true, MinLength: 1, MaxLength: 500)], HttpUrlField: "url")
        ];
        public async Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct)
        {
            Interlocked.Increment(ref SearchCalls);
            return ToolOutcome.Success(await Task.FromResult("EXFIL_OK"), "hits=1");
        }
        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
        public object? GetContribution(Type contract)
            => contract == typeof(IResearchUseCase) ? this : contract == typeof(IToolContribution) ? this : null;
    }

    private sealed class SpyProfileRepo : IProfileRepository
    {
        public int FindCalls;
        public List<SummaryCandidate> Candidates = [];
        public List<(string Uid, string Text)> Applied = [];
        public string EvidenceWritten = string.Empty;
        public List<SummaryCandidate> FindSummarizable(int min, int max) { FindCalls++; return Candidates; }
        public void ApplySummary(string uid, string scope, string text, long throughSeq, int foldedCount) => Applied.Add((uid, text));
        public void SetEvidence(string uid, string scope, string evidenceJson) => EvidenceWritten = evidenceJson;
        public void Append(string uid, string name, string text, DateTimeOffset time, string? groupName, long groupId = 0, long seq = 0) { }
        public string GetProfileSummary(string uid, long scopeGroupId = 0, int limit = 12, long beforeSeq = long.MaxValue, long beforeUnix = long.MaxValue, bool allScopes = false) => "";
        public void SetProfileOverride(string uid, string scope, string? overrideText) { }
        public (string AutoSummary, string OverrideText, string EvidenceJson) GetDetailedSummary(string uid, string scope) => ("", "", EvidenceWritten);
    }

    private sealed class SpyModelHttp : IHttpFetcher
    {
        public int Calls;
        public Action? OnSend;
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            Calls++;
            OnSend?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"SYNTHETIC_SUMMARY\"}}]}", Encoding.UTF8, "application/json")
            });
        }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            return await SendAsync(req, ct);
        }
    }

    private sealed class StaticPolicyResolver : IPlatformPolicyResolver
    {
        private static readonly EffectivePlatformPolicy Policy = new(
            new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy), true, true, true, true,
            PlatformCapabilities.QqOneBot, new HashSet<string>(), new Dictionary<string, bool>(), []);
        public EffectivePlatformPolicy Resolve(PlatformContext context) => Policy;
        public EffectivePlatformPolicy ResolveForChannel(string? channel) => Policy;
        public bool IsChatEnabled(string? channel) => true;
    }

    private sealed class DummyHttp : IHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class DummySourceOwner : IPlatformSourceOwner
    {
        public string? ConversationKey(BotAgent.Services.OneBot.QqChatMessage message, Func<string, bool> exists) => null;
    }

    private sealed class DummyChatSource : IQqChatSource
    {
        public bool IsConnected => true;
        public event Action<BotAgent.Services.OneBot.QqChatMessage>? MessageReceived { add { } remove { } }
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false) => Task.FromResult(new SendResult(true));
        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default) => Task.FromResult<(string?, long)>((null, 0));
        public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null) => Task.FromResult(false);
        public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class NoImg : IImageDownloader
    {
        public Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshUrls { get; set; }
        public int CacheHits => 0;
        public int RefreshedCount => 0;
        public Task<(byte[] Data, string Mime, string Ext)?> DownloadBytesAsync(string url, CancellationToken ct, long? messageId = null)
            => throw new InvalidOperationException("synthetic unexpected image");
    }

    private sealed class NoChat : IModelTransport
    {
        public Task<BuiltRequest> BuildAsync(IReadOnlyList<ChatMessage> window, string systemContent, IReadOnlyCollection<long> quotableIds,
            CancellationToken ct, BotAgent.Domain.Reply.SamplingProfile? sampling = null, string? promptCacheScope = null)
            => throw new InvalidOperationException("synthetic unexpected chat build");
        public Task<SendOutcome> SendAsync(JsonObject payload, int attachedImages, IReadOnlyList<long> attachedImageIds, CancellationToken ct)
            => throw new InvalidOperationException("synthetic unexpected chat send");
    }

    private static byte[] BuildZip(params (string Path, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (path, content) in entries)
            {
                using var s = zip.CreateEntry(path).Open();
                using var w = new StreamWriter(s, Encoding.UTF8);
                w.Write(content);
            }
        }
        return ms.ToArray();
    }
}

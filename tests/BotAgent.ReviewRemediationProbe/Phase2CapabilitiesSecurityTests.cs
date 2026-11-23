using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Profiles;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Tools;
using BotAgent.Packages.Digest;
using BotAgent.Packages.Documents;
using BotAgent.Packages.Voice;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Digest;
using BotAgent.Services.Documents;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Resilience;
using BotAgent.Services.Tools;
using BotAgent.Services.Voice;

namespace BotAgent.ReviewRemediationProbe;

internal static class Phase2CapabilitiesSecurityTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await RunDocumentExtractorAndSecurityTestsAsync(check);
        await RunDigestPrivacyAndScopeTestsAsync(check);
        await RunCloudSttSecurityTestsAsync(check);
        await RunAllowsCommandsPrivilegeBoundaryTestsAsync(check);
        await RunInlineToolCallParserTestsAsync(check);
    }

    private static async Task RunDocumentExtractorAndSecurityTestsAsync(Action<bool, string> check)
    {
        var plugin = new DocumentsPackagePlugin();
        var http = new SyntheticDocHttp();
        var logs = new List<string>();
        var adapter = new DocumentHostPortsAdapter(http, () => false, () => null, logs.Add);
        var services = new SinglePortProvider(
            (typeof(IDocumentHostPorts), adapter),
            (typeof(Action<string>), (Action<string>)logs.Add));
        await plugin.InitializeAsync(new PluginContext(services), CancellationToken.None);
        var contrib = (IDocumentContribution)plugin.GetContribution(typeof(IDocumentContribution))!;
        var tools = (IToolContribution)plugin.GetContribution(typeof(IToolContribution))!;

        // 1. Plain/HTML, DOCX, XLSX, PPTX, PDF positive extraction
        var htmlRes = contrib.ExtractBytes("guide.html", Encoding.UTF8.GetBytes("<html><body><h1>部署手册</h1><script>evil()</script><p>第一步：配置环境变量</p></body></html>"));
        check(htmlRes.Ok && htmlRes.Text.Contains("部署手册") && htmlRes.Text.Contains("第一步") && !htmlRes.Text.Contains("evil"),
            "documents: HTML extracts clean text and strips script tags");

        var docxBytes = BuildZip(
            ("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>第一段合同条款</w:t></w:r></w:p><w:p><w:r><w:t>第二段交付节点</w:t></w:r></w:p></w:body></w:document>"));
        var docxRes = contrib.ExtractBytes("contract.docx", docxBytes);
        check(docxRes.Ok && docxRes.Format == "docx" && docxRes.Text.Contains("第一段合同条款") && docxRes.Text.Contains("第二段交付节点"),
            "documents: DOCX extracts paragraphs cleanly");

        var xlsxBytes = BuildZip(
            ("xl/sharedStrings.xml", "<?xml version=\"1.0\"?><sst><si><t>项目名</t></si><si><t>预算额</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\"?><worksheet><sheetData><row><c t=\"s\"><v>0</v></c><c t=\"s\"><v>1</v></c></row><row><c><v>42</v></c><c><v>99</v></c></row></sheetData></worksheet>"));
        var xlsxRes = contrib.ExtractBytes("budget.xlsx", xlsxBytes);
        check(xlsxRes.Ok && xlsxRes.Format == "xlsx" && xlsxRes.Text.Contains("项目名") && xlsxRes.Text.Contains("预算额") && xlsxRes.Text.Contains("99"),
            "documents: XLSX resolves sharedStrings and sheet cells");

        var pptxBytes = BuildZip(
            ("ppt/slides/slide1.xml", "<?xml version=\"1.0\"?><p:sld xmlns:p=\"urn:p\" xmlns:a=\"urn:a\"><a:t>架构总览页</a:t></p:sld>"),
            ("ppt/slides/slide2.xml", "<?xml version=\"1.0\"?><p:sld xmlns:p=\"urn:p\" xmlns:a=\"urn:a\"><a:t>里程碑排期页</a:t></p:sld>"));
        var pptxSlide2 = contrib.ExtractBytes("roadmap.pptx", pptxBytes, section: "2");
        check(pptxSlide2.Ok && pptxSlide2.Format == "pptx" && pptxSlide2.TotalUnits == 2 && pptxSlide2.Text.Contains("里程碑排期页"),
            "documents: PPTX supports slide section pagination");

        var pdfStream = "BT /F1 12 Tf (Hello PDF World) Tj [(Phase ) -10 (2 Spec)] TJ ET";
        var pdfBytes = Encoding.ASCII.GetBytes($"%PDF-1.4\n1 0 obj\n<< /Length {pdfStream.Length} >>\nstream\n{pdfStream}\nendstream\nendobj\n%%EOF");
        var pdfRes = contrib.ExtractBytes("spec.pdf", pdfBytes);
        check(pdfRes.Ok && pdfRes.Format == "pdf" && pdfRes.Text.Contains("Hello PDF World") && pdfRes.Text.Contains("Phase 2 Spec"),
            "documents: PDF extracts Tj and TJ text operators");

        // 2. Negative Security Matrix: PE/ELF, ZIP bomb, XXE DTD, Encrypted/Scan PDF, SSRF, >10MiB, Cross-Conversation Isolation
        var peDisguised = contrib.ExtractBytes("readme.txt", new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 });
        var elfDisguised = contrib.ExtractBytes("notes.md", new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1 });
        check(!peDisguised.Ok && peDisguised.ReasonCode == "executable_rejected"
            && !elfDisguised.Ok && elfDisguised.ReasonCode == "executable_rejected",
            "documents security: PE and ELF magic headers disguised as text are blocked");

        var bombPayload = new string('A', 600_000); // compresses > 100x in Deflate -> exceeds MaxInflationRatio=20!
        var zipBomb = BuildZip(("word/document.xml", $"<w:document xmlns:w=\"urn:w\"><w:t>{bombPayload}</w:t></w:document>"));
        var bombRes = contrib.ExtractBytes("bomb.docx", zipBomb);
        check(!bombRes.Ok && bombRes.ReasonCode.Contains("zip", StringComparison.OrdinalIgnoreCase),
            $"documents security: high-ratio ZIP bomb blocked ({bombRes.ReasonCode})");

        var xxeDocx = BuildZip(("word/document.xml", "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><w:document xmlns:w=\"urn:w\"><w:t>&xxe;</w:t></w:document>"));
        var xxeRes = contrib.ExtractBytes("xxe.docx", xxeDocx);
        check(!xxeRes.Ok && !xxeRes.Text.Contains("root:"),
            "documents security: XXE DTD inside OOXML is strictly prohibited");

        var encryptedPdf = contrib.ExtractBytes("secret.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Encrypt 2 0 R >> endobj\n%%EOF"));
        var scannedPdf = contrib.ExtractBytes("scan.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Subtype /Image >> endobj\n%%EOF"));
        check(!encryptedPdf.Ok && encryptedPdf.ReasonCode == "pdf_encrypted"
            && !scannedPdf.Ok && scannedPdf.ReasonCode == "pdf_no_text_layer",
            "documents security: encrypted PDF and image-only scanned PDF return explicit safe reason codes");

        var ssrfLocal = await adapter.FetchAsync("group:10001", "http://127.0.0.1:8080/internal.pdf", CancellationToken.None);
        var ssrfFile = await adapter.FetchAsync("group:10001", "file:///etc/passwd", CancellationToken.None);
        http.RedirectTarget = "http://127.0.0.1:9000/private.txt";
        var ssrfRedirect = await adapter.FetchAsync("group:10001", "https://docs.example.com/jump", CancellationToken.None);
        http.RedirectTarget = null;
        http.DeclaredContentLength = 12 * 1024 * 1024;
        var oversized = await adapter.FetchAsync("group:10001", "https://docs.example.com/huge.pdf", CancellationToken.None);
        check(!ssrfLocal.Ok && ssrfLocal.ReasonCode == "ssrf_blocked"
            && !ssrfFile.Ok && ssrfFile.ReasonCode == "unsupported_url_scheme"
            && !ssrfRedirect.Ok && ssrfRedirect.ReasonCode == "ssrf_redirect_blocked"
            && !oversized.Ok && oversized.ReasonCode == "file_too_large",
            "documents security: loopback SSRF, file:// scheme, private redirect and >10MiB stream are blocked");

        adapter.StageInboundDocument("group:10001", "private_spec.md", Encoding.UTF8.GetBytes("绝密甲群文档CANA_DOC_10001"));
        var okSameConv = await tools.ExecuteAsync("group:10001", new ToolCall("doc.read", new JsonObject { ["source"] = "latest" }), CancellationToken.None);
        var deniedOtherConv = await tools.ExecuteAsync("group:10002", new ToolCall("doc.read", new JsonObject { ["source"] = "latest" }), CancellationToken.None);
        check(okSameConv.Ok && (okSameConv.ModelText ?? string.Empty).Contains("绝密甲群文档")
            && !deniedOtherConv.Ok && !(deniedOtherConv.ModelText ?? string.Empty).Contains("绝密甲群文档"),
            "documents security: staged inbound attachments are strictly isolated by conversation sourceKey");
        check(logs.All(l => !l.Contains("绝密甲群文档CANA_DOC_10001", StringComparison.Ordinal)),
            "documents privacy: logs never contain extracted document text");

        adapter.EvictConversation("group:10001");
        var evictedRead = await tools.ExecuteAsync("group:10001", new ToolCall("doc.read", new JsonObject { ["source"] = "latest" }), CancellationToken.None);
        check(!evictedRead.Ok,
            "documents cleanup: EvictConversation purges staged inbound attachments on session deletion");
    }

    private static async Task RunDigestPrivacyAndScopeTestsAsync(Action<bool, string> check)
    {
        var box = new SettingsBox(new AppSettings { AgentMaskSensitive = false });
        var registry = new ConversationRegistry(new EmptyConversationRepo(), box, new MinimalChatSource(), _ => true, _ => { });
        var convA = registry.GetOrCreate(new QqChatMessage(1, true, 20001, 10001, "群友A", "init", DateTimeOffset.UtcNow, true))!;
        var convB = registry.GetOrCreate(new QqChatMessage(2, true, 20003, 10002, "群友C", "initB", DateTimeOffset.UtcNow, true))!;

        var now = DateTimeOffset.UtcNow;
        convA.Append(new ChatMessage { Role = MessageRole.Peer, SenderName = "群友A", SenderId = 20001, Text = "保留的有效讨论：周五发版", Timestamp = now.AddMinutes(-5) });
        convA.Append(new ChatMessage { Role = MessageRole.Peer, SenderName = "群友B", SenderId = 20002, Text = "SECRET_RECALLED_CANARY_99", Timestamp = now.AddMinutes(-4), Recalled = true });
        convA.Append(new ChatMessage { Role = MessageRole.Peer, SenderName = "群友B", SenderId = 20002, Text = "[已撤回] OLD_RECALLED_CANARY_88", Timestamp = now.AddMinutes(-3) });
        convB.Append(new ChatMessage { Role = MessageRole.Peer, SenderName = "群友C", SenderId = 20003, Text = "OTHER_GROUP_SECRET_10002", Timestamp = now.AddMinutes(-2) });

        var capturedPrompts = new List<string>();
        var ports = new RealWindowDigestPorts(registry, box, capturedPrompts);
        var clock = new AdvanceableClock(now);
        var plugin = new DigestPackagePlugin();
        await plugin.InitializeAsync(
            new PluginContext(new SinglePortProvider(
                (typeof(IDigestHostPorts), ports),
                (typeof(IClock), clock))),
            CancellationToken.None);
        var tools = (IToolContribution)plugin.GetContribution(typeof(IToolContribution))!;

        var res = await tools.ExecuteAsync("group:10001",
            new ToolCall("chat.digest", new JsonObject { ["count"] = "30", ["focus"] = "发版" }),
            CancellationToken.None);

        check(res.Ok && (res.ModelText ?? string.Empty).Contains("合成纪要结论")
            && capturedPrompts.Count == 1
            && capturedPrompts[0].Contains("保留的有效讨论：周五发版")
            && !capturedPrompts[0].Contains("SECRET_RECALLED_CANARY_99")
            && !capturedPrompts[0].Contains("OLD_RECALLED_CANARY_88")
            && !capturedPrompts[0].Contains("OTHER_GROUP_SECRET_10002"),
            "digest: DigestHostPortsAdapter + DigestPackagePlugin summarizes same-conversation messages while strictly excluding recalled and cross-group messages");

        var cooldownHit = await tools.ExecuteAsync("group:10001",
            new ToolCall("chat.digest", new JsonObject { ["count"] = "30" }),
            CancellationToken.None);
        check(!cooldownHit.Ok && cooldownHit.ReasonCode == "digest_cooldown",
            "digest: per-conversation cooldown blocks rapid repeated summarization");

        var digestContrib = (IDigestContribution)plugin.GetContribution(typeof(IDigestContribution))!;
        digestContrib.ClearConversation("group:10001");
        convA.Append(new ChatMessage { Role = MessageRole.System, SenderName = "系统", SenderId = 0, Text = "SYSTEM_PROMPT_CANARY_44", Timestamp = now.AddMinutes(-1) });
        ports.ReturnNullOnce = true;
        var failedDigest = await tools.ExecuteAsync("group:10001",
            new ToolCall("chat.digest", new JsonObject { ["count"] = "30" }),
            CancellationToken.None);
        var immediateRetry = await tools.ExecuteAsync("group:10001",
            new ToolCall("chat.digest", new JsonObject { ["count"] = "30" }),
            CancellationToken.None);
        check(!failedDigest.Ok && immediateRetry.Ok
            && !capturedPrompts[^1].Contains("SYSTEM_PROMPT_CANARY_44", StringComparison.Ordinal),
            "digest: failed digest does not consume cooldown and excludes System messages");
    }

    private static async Task RunCloudSttSecurityTestsAsync(Action<bool, string> check)
    {
        var logs = new List<string>();
        var http = new SyntheticSttHttp();
        var settingsDoc = JsonDocument.Parse("{\"sttEnabled\":true,\"sttEndpointUrl\":\"https://stt.example.com/v1/audio/transcriptions\",\"sttModel\":\"whisper-1\"}");
        var dict = settingsDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var stt = new CloudSttClient(
            http,
            allowPrivateOutbound: () => false,
            readAppSettings: () => new AppSettings { ApiKey = "sk-synthetic-stt" },
            readPluginSettings: () => dict,
            log: logs.Add,
            circuit: new ToolCircuitBreaker("stt-test"));

        var box = new SettingsBox(new AppSettings());
        var voiceUseCase = new VoiceUseCase(null, box, logs.Add, stt: stt);
        var coord = new VoiceCoordinator(new VoicePolicyEngine(), voiceUseCase.CreateHostPorts());
        voiceUseCase.BindDirect(coord);

        var ok = await voiceUseCase.TranscribeAsync([1, 2, 3, 4], "clip.mp3", CancellationToken.None);
        check(ok.Transcript is not null && ok.Transcript.Contains("今天下午三点开会")
            && logs.All(l => !l.Contains("今天下午三点开会", StringComparison.Ordinal)),
            "stt: transcribes audio and never logs transcript content");

        http.ReturnErrorCanary = true;
        var err = await voiceUseCase.TranscribeAsync([1, 2, 3, 4], "clip.mp3", CancellationToken.None);
        check(err.Transcript is null && logs.All(l => !l.Contains("UPSTREAM_SECRET_CANARY_99", StringComparison.Ordinal)),
            "stt: upstream HTTP failure never leaks error body canary to logs");

        var ssrfAudio = await voiceUseCase.TranscribeFromUrlAsync("http://127.0.0.1:8080/voice.wav", "voice.wav", CancellationToken.None);
        var oversized = await voiceUseCase.TranscribeAsync(new byte[8 * 1024 * 1024 + 1], "big.mp3", CancellationToken.None);
        check(ssrfAudio.ReasonCode == "audio_url_ssrf_blocked" && oversized.ReasonCode == "audio_invalid_or_too_large",
            "stt: blocks loopback SSRF audio URLs and >8MiB payloads");

        // Cross-origin custom sttEndpointUrl must NOT leak primary fallback API key; same-origin attaches it; 502s trip circuit breaker
        var crossHttp = new SyntheticSttHttp();
        var crossDict = JsonDocument.Parse("{\"sttEnabled\":true,\"sttEndpointUrl\":\"https://thirdparty-stt.example.com/v1/audio/transcriptions\",\"sttModel\":\"whisper-1\"}")
            .RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var crossCircuit = new ToolCircuitBreaker("stt-cross-test");
        var crossStt = new CloudSttClient(
            crossHttp,
            () => false,
            () => new AppSettings { ApiKey = "sk-primary-secret", ModelBaseUrl = "https://primary.example.com/v1" },
            () => crossDict,
            logs.Add,
            circuit: crossCircuit,
            resolveFallbackApiKey: () => "sk-primary-secret");
        _ = await crossStt.TranscribeAsync([1, 2, 3, 4], "clip.mp3", CancellationToken.None);
        check(crossHttp.LastAuthHeader is null,
            "stt security: cross-origin sttEndpointUrl never receives fallback primary ApiKey");

        crossDict["sttEndpointUrl"] = JsonDocument.Parse("\"https://primary.example.com/v1/audio/transcriptions\"").RootElement.Clone();
        _ = await crossStt.TranscribeAsync([1, 2, 3, 4], "clip.mp3", CancellationToken.None);
        check(crossHttp.LastAuthHeader == "Bearer sk-primary-secret",
            "stt security: same-origin sttEndpointUrl keeps fallback ApiKey");

        crossHttp.ReturnErrorCanary = true;
        for (var i = 0; i < 6; i++)
            _ = await crossStt.TranscribeAsync([1, 2, 3, 4], "clip.mp3", CancellationToken.None);
        var hitsWhenOpen = crossHttp.Hits;
        var openRes = await crossStt.TranscribeAsync([1, 2, 3, 4], "clip.mp3", CancellationToken.None);
        check(!openRes.Ok && crossHttp.Hits == hitsWhenOpen,
            "stt resilience: repeated 502s trip ToolCircuitBreaker and short-circuit HTTP");
    }

    private static async Task RunAllowsCommandsPrivilegeBoundaryTestsAsync(Action<bool, string> check)
    {
        var box = new SettingsBox(new AppSettings
        {
            AiModeEnabled = true,
            WhitelistGroups = "*",
            EnableApprovals = true,
            ApprovalApprovers = "20001",
            EnableNaturalAgent = true,
            EnableAgentBridge = true,
            AgentAllowedUsers = "20001"
        });
        var traces = new TurnTraceStore();
        var approvals = new ApprovalUseCase(
            box,
            new ApprovalHooks(
                Log: _ => { },
                Mask: (s, _) => s,
                SendPlainAsync: (_, _) => Task.CompletedTask,
                SendApprovalReplyAsync: (_, _) => Task.CompletedTask),
            new SessionPolicyLedger(),
            traces);

        var conv = new BotConversation { SourceKey = "group:10001", Name = "测试群" };
        var opened = await approvals.OpenApprovalForTool(conv, "demo.echo", 101, approvals.Capabilities);
        check(opened && approvals.PendingApprovals().Count == 1, "setup: created pending approval for demo.echo");
        var code = approvals.PendingApprovals()[0].RequestId;

        // Surface 1: ApprovalUseCase.HandleCommand + ReplyInboundGates block AllowsCommands = false
        var derivedApprove = new QqChatMessage(201, true, 20001, 10001, "群友A", $"同意 {code}", DateTimeOffset.UtcNow, true, SenderRole: "owner")
        {
            AllowsCommands = false
        };
        var handledDerived = approvals.HandleCommand(derivedApprove);
        check(!handledDerived && approvals.PendingApprovals().Count == 1,
            "security surface 1: AllowsCommands=false message cannot consume pending approval");

        var genuineApprove = derivedApprove with { MessageId = 202, AllowsCommands = true };
        var handledGenuine = approvals.HandleCommand(genuineApprove);
        check(handledGenuine && approvals.PendingApprovals().Count == 0,
            "security surface 1 control: genuine AllowsCommands=true message from owner approves normally");

        // Surface 2 & 3: Explicit // command and NaturalAgentTrigger on ReplyPipeline
        var spyAgent = new SpyAgentCommandService();
        var source = new MinimalChatSource();
        var registry = new ConversationRegistry(new EmptyConversationRepo(), box, source, _ => true, _ => { });
        var seedConv = registry.GetOrCreate(new QqChatMessage(300, true, 20001, 10001, "群友A", "seed", DateTimeOffset.UtcNow, false))!;
        seedConv.HistoryLoaded = true;
        var pm = new PluginManager(_ => { });
        pm.Register(new SyntheticPresetRegistration("preset.feature.profiles", "synthetic profiles"), defaultEnabled: false);
        var policies = new EnabledPlatformPolicies();
        var pipeline = new ReplyPipeline(
            box, source, null!, new EmptyProfileRepo(), registry, new NullPanelUi(), new WhitelistGate(box, policies), approvals,
            new ParticipationUseCase(box, _ => { }), null!, new VibeTracker(), null!,
            null!, spyAgent, null!, null!, new VoiceUseCase(null, box, _ => { }),
            null!, null!, null, null!,
            new ReplyHooks(_ => { }, () => 10001, () => false), traces, platformPolicies: policies, plugins: new PluginGate(pm));

        box.Current.GroupInterjectionProbability = 0;
        var memoryCmdHits = 0;
        var memoryPipeline = new ReplyPipeline(
            box, source, null!, new EmptyProfileRepo(), registry, new NullPanelUi(), new WhitelistGate(box, policies), approvals,
            new ParticipationUseCase(box, _ => { }), null!, new VibeTracker(), null!,
            null!, spyAgent, null!, null!, new VoiceUseCase(null, box, _ => { }),
            null!, null!, null, null!,
            new ReplyHooks(_ => { }, () => 10001, () => false), traces, platformPolicies: policies, plugins: new PluginGate(pm),
            memoryCommand: m =>
            {
                if (!m.Text.StartsWith("/remember", StringComparison.Ordinal)) return false;
                Interlocked.Increment(ref memoryCmdHits);
                return true;
            });

        memoryPipeline.HandleInbound(new QqChatMessage(301, true, 20001, 10001, "群友A", "//status", DateTimeOffset.UtcNow, false)
        {
            AllowsCommands = false
        });
        memoryPipeline.HandleInbound(new QqChatMessage(304, true, 20001, 10001, "群友A", "/remember secret", DateTimeOffset.UtcNow, false)
        {
            AllowsCommands = false
        });
        var liveConv = registry.Find("group:10001")!;
        var appendedDerived = liveConv.Messages.Single(m => m.QqMessageId == 301);
        check(spyAgent.ExplicitCalls == 0 && memoryCmdHits == 0 && !appendedDerived.AllowsCommands,
            "security surface 2: AllowsCommands=false blocks explicit // command and memoryCommand and persists AllowsCommands=false on ChatMessage");

        memoryPipeline.HandleInbound(new QqChatMessage(305, true, 20001, 10001, "群友A", "//status", DateTimeOffset.UtcNow, false)
        {
            AllowsCommands = true
        });
        check(spyAgent.ExplicitCalls == 1,
            "security surface 2 control: genuine AllowsCommands=true message invokes explicit // command");

        var naturalMethod = typeof(ReplyPipeline).GetMethod("NaturalAgentTrigger", BindingFlags.Instance | BindingFlags.NonPublic)!;
        liveConv.Append(new ChatMessage
        {
            Role = MessageRole.Peer,
            SenderId = 20001,
            SenderName = "群友A",
            Text = "帮我看下服务器负载",
            QqMessageId = 302,
            DirectToBot = true,
            MentionedBot = true,
            AllowsCommands = false
        });
        liveConv.Append(new ChatMessage
        {
            Role = MessageRole.Peer,
            SenderId = 20001,
            SenderName = "群友A",
            Text = "帮我看下服务器负载",
            QqMessageId = 303,
            DirectToBot = true,
            MentionedBot = true,
            AllowsCommands = true
        });
        var blockedTrigger = naturalMethod.Invoke(pipeline, [liveConv, 302L, false, true, true]);
        var allowedTrigger = naturalMethod.Invoke(pipeline, [liveConv, 303L, false, true, true]);
        check(blockedTrigger is null && allowedTrigger is ChatMessage,
            "security surface 3: NaturalAgentTrigger rejects AllowsCommands=false and allows genuine AllowsCommands=true");

        // Persisted derived transcript record restores AllowsCommands=false across reload
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var persistedDerived = BotConversation.FromRecord(new ConversationRecord
        {
            SourceKey = "group:10099",
            Name = "重放群",
            Messages =
            [
                new MessageRecord { Role = "Peer", SenderId = 20001, SenderName = "群友A", Text = "[语音转写（外部不可信内容）：//status]", TimeUnix = nowUnix },
                new MessageRecord { Role = "Peer", SenderId = 20001, SenderName = "群友A", Text = "正常群聊消息", TimeUnix = nowUnix }
            ]
        });
        check(!persistedDerived.Messages[0].AllowsCommands && persistedDerived.Messages[1].AllowsCommands,
            "security surface 4: BotConversation.FromRecord restores AllowsCommands=false on persisted derived content");
    }

    private static Task RunInlineToolCallParserTestsAsync(Action<bool, string> check)
    {
        var okParsed = ModelOutputParser.Parse("""{"suitability": 92, "reply": "", "call": {"tool": "doc.read", "args": {"source": "latest", "section": "2"}}}""", false).Result;
        check(okParsed.Call is { ToolId: "doc.read" } c && c.Arguments["source"] == "latest" && c.Arguments["section"] == "2",
            "inline call parser: parses valid call object with string/scalar arguments");

        var evilToolId = ModelOutputParser.Parse("""{"suitability": 92, "reply": "hi", "call": {"tool": "../bash;rm", "args": {}}}""", false).Result;
        check(evilToolId.Call is null,
            "inline call parser: rejects non-allowlisted tool id characters");
        return Task.CompletedTask;
    }

    private static byte[] BuildZip(params (string Path, string Xml)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, xml) in entries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var s = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(xml);
                s.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private sealed class SinglePortProvider(params (Type Contract, object Instance)[] pairs) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => pairs.FirstOrDefault(p => p.Contract == serviceType).Instance;
    }

    private sealed class AdvanceableClock(DateTimeOffset initial) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = initial;
        public DateTimeOffset Now => UtcNow;
        public DateTime LocalDateTime => UtcNow.LocalDateTime;
        public long TickCount => 1000;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RealWindowDigestPorts(ConversationRegistry registry, SettingsBox box, List<string> capturedPrompts) : IDigestHostPorts
    {
        private readonly DigestHostPortsAdapter _inner = new(registry, null!, () => box.Current, () => null);
        public bool ReturnNullOnce { get; set; }
        public DigestOptions ReadOptions() => _inner.ReadOptions();
        public IReadOnlyList<DigestMessageItem> ReadConversationWindow(string sourceKey, long? sinceUnix, int maxMessages, string? focusKeyword)
            => _inner.ReadConversationWindow(sourceKey, sinceUnix, maxMessages, focusKeyword);
        public Task<string?> CompleteDigestAsync(string systemPrompt, string transcriptBlock, CancellationToken ct)
        {
            capturedPrompts.Add(transcriptBlock);
            if (ReturnNullOnce)
            {
                ReturnNullOnce = false;
                return Task.FromResult<string?>(null);
            }
            return Task.FromResult<string?>("• 合成纪要结论：周五下午灰度发版");
        }
    }

    private sealed class SyntheticDocHttp : IHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public string? RedirectTarget { get; set; }
        public long? DeclaredContentLength { get; set; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct = default)
        {
            if (RedirectTarget is not null)
            {
                var redir = new HttpResponseMessage(HttpStatusCode.Found);
                redir.Headers.Location = new Uri(RedirectTarget);
                return Task.FromResult(redir);
            }
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("synthetic doc"))
            };
            if (DeclaredContentLength is { } len)
                resp.Content.Headers.ContentLength = len;
            return Task.FromResult(resp);
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class SyntheticSttHttp : IHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public bool ReturnErrorCanary { get; set; }
        public int Hits { get; private set; }
        public string? LastAuthHeader { get; private set; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            Hits++;
            LastAuthHeader = request.Headers.Authorization?.ToString();
            if (ReturnErrorCanary)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("{\"error\":\"UPSTREAM_SECRET_CANARY_99\"}")
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"今天下午三点开会\"}", Encoding.UTF8, "application/json")
            });
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default)
            => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class MinimalChatSource : IQqChatSource
    {
        public bool IsConnected => true;
        public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
        public event Action<QqPokeEvent>? Poked { add { } remove { } }
        public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
        public event Action<bool>? ConnectionChanged { add { } remove { } }
        public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
            => Task.FromResult(new SendResult(true, 1));
        public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
            => Task.FromResult<(string?, long)>((null, 0));
        public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
            => Task.FromResult(false);
        public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class EnabledPlatformPolicies : IPlatformPolicyResolver
    {
        private static readonly EffectivePlatformPolicy Policy = new(
            new PlatformContext(PlatformId.QqPrivate), true, true, true, true,
            PlatformCapabilities.QqOneBot, new HashSet<string>(), new Dictionary<string, bool>(), Array.Empty<string>());
        public EffectivePlatformPolicy Resolve(PlatformContext context) => Policy;
        public EffectivePlatformPolicy ResolveForChannel(string? channel) => Policy;
        public bool IsChatEnabled(string? channel) => true;
    }

    private sealed class EmptyProfileRepo : IProfileRepository
    {
        public List<SummaryCandidate> FindSummarizable(int minNewMessages, int maxCandidates) => [];
        public void ApplySummary(string uid, string scope, string text, long throughSeq, int foldedCount) { }
        public void Append(string uid, string name, string text, DateTimeOffset time, string? groupName, long groupId = 0, long seq = 0) { }
        public string GetProfileSummary(string uid, long scopeGroupId = 0, int limit = 12, long beforeSeq = long.MaxValue, long beforeUnix = long.MaxValue, bool allScopes = false) => string.Empty;
        public void SetProfileOverride(string uid, string scope, string? overrideText) { }
        public void SetEvidence(string uid, string scope, string evidenceJson) { }
        public (string AutoSummary, string OverrideText, string EvidenceJson) GetDetailedSummary(string uid, string scope) => ("", "", "[]");
    }

    private sealed class EmptyConversationRepo : IConversationRepository
    {
        public List<ConversationRecord> LoadAsync() => [];
        public void RequestSave(IEnumerable<ConversationRecord> snapshot) { }
        public void DeleteConversation(string sourceKey) { }
        public void AppendArchive(string sourceKey, IReadOnlyList<ChatMessage> dropped) { }
        public List<ArchivedMessage> ReadArchive(string sourceKey, int limit = 100) => [];
        public long ArchiveCount(string sourceKey) => 0;
        public void DeleteMessages(string sourceKey) { }
    }

    private sealed class NullPanelUi : IPanelNotifier
    {
        public void EmitLog(string message) { }
        public void SetThinking(BotConversation conversation, bool thinking) { }
        public void NotifyMessageAdded(string sourceKey, ChatMessage message) { }
        public void NotifyConversationsChanged() { }
        public void NotifyStateChanged() { }
    }

    private sealed class SpyAgentCommandService : IAgentCommandService
    {
        public int ExplicitCalls;
        public bool TryParseCommand(string rawText, out string payload)
        {
            if (rawText.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                payload = rawText.TrimStart()[2..];
                return true;
            }
            payload = string.Empty;
            return false;
        }
        public Task RunCommandSafeAsync(BotConversation conversation, QqChatMessage message, string payload)
        {
            Interlocked.Increment(ref ExplicitCalls);
            return Task.CompletedTask;
        }
        public bool CanRunNatural(BotConversation conversation, long senderId) => senderId == 20001;
        public Task RunNaturalSafeAsync(BotConversation conversation, ChatMessage trigger, string task, string? ack)
            => Task.CompletedTask;
    }
}

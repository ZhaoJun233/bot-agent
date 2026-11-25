using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Tools;
using BotAgent.Engine.Runtime;
using BotAgent.Platforms.Common;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Mcp;
using BotAgent.Services.Memory;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Permissions;
using BotAgent.Services.Qq;
using BotAgent.Services.Tools;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void Phase4SafetyTests()
    {
        Section("Phase 4 · 提示词解耦 / ApprovableTools / 卡片伪造防护 / 冻结参数 / ReadOnly MCP 污点 / Task 审批恢复");
        BotAgent.Adapters.Time.ClockBindings.InitializeEngine();

        // 1. ServerAgentPromptBuilder: GeneralAssistantMode vs ServerOpsMode
        var builderType = typeof(ServerAgentRunner).Assembly.GetType("BotAgent.Services.Agent.ServerAgentPromptBuilder", throwOnError: true)!;
        var buildMethod = builderType.GetMethod("BuildSystemPrompt", BindingFlags.Public | BindingFlags.Static)
                          ?? throw new InvalidOperationException("BuildSystemPrompt not found");
        var baseSettings = new AppSettings();
        var generalPrompt = (string)buildMethod.Invoke(null, new object?[]
        {
            baseSettings, null, "/tmp/work",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read", "fetch" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null, null, null
        })!;
        var opsPrompt = (string)buildMethod.Invoke(null, new object?[]
        {
            baseSettings, null, "/opt/qqchat/workspace",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bash", "docker", "read" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null, null, null
        })!;

        Check("ServerAgentPromptBuilder GeneralAssistantMode 不含 docker 与 /opt/qqchat 运维约束",
            !generalPrompt.Contains("/opt/qqchat", StringComparison.Ordinal)
            && !generalPrompt.Contains("docker", StringComparison.OrdinalIgnoreCase),
            generalPrompt[..Math.Min(120, generalPrompt.Length)]);
        Check("ServerAgentPromptBuilder ServerOpsMode 保留宿主运维提示",
            opsPrompt.Contains("docker", StringComparison.OrdinalIgnoreCase),
            opsPrompt[..Math.Min(120, opsPrompt.Length)]);

        // 2. ToolPolicy.ApprovableTools overrides DefaultRequiresApproval but NEVER AlwaysDenied (FileOrShell / RemoteAgent)
        var reg = new ToolRegistry();
        reg.Register(new ToolSpec("mcp.demo.ticket", ToolCategory.SendMessage, "Create ticket", false, "mcp.client", "title:string", ToolDefaultPolicy.FollowSwitch, FeatureKey: ToolFeature.Approval, Origin: ToolOrigin.Mcp));
        reg.Register(new ToolSpec("danger.shell", ToolCategory.FileOrShell, "Run shell", false, "server", "cmd:string", ToolDefaultPolicy.AllOnWhenEmpty));
        reg.Register(new ToolSpec("danger.remote", ToolCategory.RemoteAgent, "Remote ops", false, "server", "prompt:string", ToolDefaultPolicy.AllOnWhenEmpty));

        var policyWithApprovable = new ToolPolicy(
            new HashSet<string>(StringComparer.Ordinal) { "mcp.demo.ticket", "danger.shell", "danger.remote" },
            MaxCallsPerRun: 3,
            ApprovableCategories: new HashSet<ToolCategory>(),
            PolicyVersion: 1,
            ApprovalRequiredCategories: new HashSet<ToolCategory>(),
            ApprovalRequiredTools: new HashSet<string>(StringComparer.Ordinal) { "mcp.demo.ticket" },
            ApprovableTools: new HashSet<string>(StringComparer.Ordinal) { "mcp.demo.ticket", "danger.shell", "danger.remote" });

        var noTicket = ToolGate.Evaluate(reg, policyWithApprovable, new ToolRequest("mcp.demo.ticket", "group:10001"));
        Check("ToolPolicy.ApprovableTools 允许 DefaultRequiresApproval 工具进入 approval_required",
            !noTicket.Allow && noTicket.ReasonCode == "approval_required", noTicket.Describe());

        var ticket = IssueTicket("mcp.demo.ticket", "group:10001", DateTimeOffset.UtcNow, "AAAAAA");
        var withTicket = ToolGate.Evaluate(reg, policyWithApprovable, new ToolRequest("mcp.demo.ticket", "group:10001"), ticket);
        Check("ToolPolicy.ApprovableTools 持有匹配一次性票据时放行真实工具",
            withTicket.Allow && withTicket.ReasonCode == "allowed", withTicket.Describe());

        var shellDenied = ToolGate.Evaluate(reg, policyWithApprovable, new ToolRequest("danger.shell", "group:10001"),
            IssueTicket("danger.shell", "group:10001", DateTimeOffset.UtcNow, "BBBBBB"));
        var remoteDenied = ToolGate.Evaluate(reg, policyWithApprovable, new ToolRequest("danger.remote", "group:10001"),
            IssueTicket("danger.remote", "group:10001", DateTimeOffset.UtcNow, "CCCCCC"));
        Check("ToolPolicy.ApprovableTools 绝不解禁 AlwaysDenied (FileOrShell / RemoteAgent)",
            !shellDenied.Allow && shellDenied.ReasonCode == "category_denied"
            && !remoteDenied.Allow && remoteDenied.ReasonCode == "category_denied",
            $"shell={shellDenied.ReasonCode}; remote={remoteDenied.ReasonCode}");

        // 3. ApprovalCardHelper + ApprovalOutboundScope guard
        const string validAnnouncement = "【需要确认】测试工具｜编号 ABCDEF\n有效期 180 秒。";
        Check("聊天模型伪造相似公告文案在无 ApprovalOutboundScope 时绝不发出 Feishu 审批卡片",
            !ApprovalCardHelper.TryBuildFeishuCardJson(validAnnouncement, out _)
            && !ApprovalCardHelper.TryBuildOfficialKeyboardJson(validAnnouncement, out _));

        string builtFeishu;
        JsonObject builtOfficial;
        bool feishuOk;
        bool officialOk;
        using (ApprovalOutboundScope.EnterSystemAnnouncement())
        {
            feishuOk = ApprovalCardHelper.TryBuildFeishuCardJson(validAnnouncement, out builtFeishu);
            officialOk = ApprovalCardHelper.TryBuildOfficialKeyboardJson(validAnnouncement, out builtOfficial);
        }
        var offJson = officialOk ? builtOfficial.ToJsonString() : string.Empty;
        Check("仅在 ApprovalOutboundScope.EnterSystemAnnouncement 内生成 Schema 2.0 飞书卡与 QQ 官方按钮",
            feishuOk && builtFeishu.Contains("\"schema\":\"2.0\"", StringComparison.Ordinal)
            && builtFeishu.Contains("\"approval_cmd\"", StringComparison.Ordinal)
            && builtFeishu.Contains("ABCDEF", StringComparison.Ordinal)
            && officialOk
            && offJson.Contains("/approve ABCDEF", StringComparison.Ordinal)
            && offJson.Contains("/deny ABCDEF", StringComparison.Ordinal));

        // 4. OpenApprovalForRealToolAsync + PendingToolInvocationStore frozen args + single consume
        var executedArgs = new List<string>();
        var realSpec = new ToolSpec(
            "mcp.demo.ticket",
            ToolCategory.WebRead,
            "Create remote ticket",
            ReadOnly: false,
            Executor: "mcp.client",
            Parameters: "title:string",
            Default: ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.Approval,
            Origin: ToolOrigin.Mcp);
        var realCatalog = new ChatToolCatalog([new CaptureRealBinding(realSpec, executedArgs)]);
        var approvalSettings = new AppSettings
        {
            EnableApprovals = true,
            ApprovalApprovers = "20001",
            WhitelistGroups = "10001",
            AgentAllowedUsers = "20001"
        };
        var sentAnnouncements = new List<string>();
        var approvalUseCase = new ApprovalUseCase(
            new Services.SettingsBox(approvalSettings),
            new ApprovalHooks(
                _ => { },
                (text, _) => text,
                (_, text) => { sentAnnouncements.Add(text); return Task.CompletedTask; },
                (_, text) => { sentAnnouncements.Add(text); return Task.CompletedTask; }),
            new SessionPolicyLedger(),
            new TurnTraceStore(),
            catalog: () => realCatalog);
        var conv = new BotConversation { SourceKey = "group:10001", Name = "group:10001", Kind = ConversationKind.GroupChat };
        var callerArgs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["title"] = JsonValue.Create("frozen-before-mutation"),
            ["nested"] = new JsonObject { ["severity"] = "p1" }
        };
        var opened = approvalUseCase.OpenApprovalForRealToolAsync(
            conv, "mcp.demo.ticket", callerArgs, 9001L, approvalUseCase.Capabilities).GetAwaiter().GetResult();

        // Mutate callerArgs after freezing!
        callerArgs["title"] = JsonValue.Create("MUTATED-AFTER-FREEZE");
        ((JsonObject)callerArgs["nested"]!)["severity"] = "p9-tampered";

        var reqMatch = sentAnnouncements.Select(s => System.Text.RegularExpressions.Regex.Match(s, @"编号 ([23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{6})"))
            .FirstOrDefault(m => m.Success);
        Check("OpenApprovalForRealToolAsync 成功冻结参数并产出合法编号", opened && reqMatch is { Success: true }, string.Join(" | ", sentAnnouncements));

        if (reqMatch is { Success: true })
        {
            var reqId = reqMatch.Groups[1].Value;
            var approveMsg = new QqChatMessage(9002L, true, 20001L, 10001L, "owner", $"同意 {reqId}", DateTimeOffset.UtcNow, MentionedSelf: true);
            var firstHandled = approvalUseCase.HandleCommand(approveMsg);
            var waitUntil = DateTime.UtcNow.AddSeconds(3);
            while (executedArgs.Count == 0 && DateTime.UtcNow < waitUntil) Thread.Sleep(15);
            var secondHandled = approvalUseCase.HandleCommand(approveMsg with { MessageId = 9003L });
            Thread.Sleep(50);

            Check("真实工具审批仅执行一次且严格使用冻结拷贝入参（防御 TOCTOU 与重放）",
                firstHandled
                && secondHandled
                && executedArgs.Count == 1
                && executedArgs[0].Contains("frozen-before-mutation", StringComparison.Ordinal)
                && !executedArgs[0].Contains("MUTATED-AFTER-FREEZE", StringComparison.Ordinal)
                && !executedArgs[0].Contains("p9-tampered", StringComparison.Ordinal)
                && !sentAnnouncements.Any(s => s.Contains("demo.echo", StringComparison.Ordinal) || s.Contains("没有任何真实副作用", StringComparison.Ordinal)),
                $"first={firstHandled} second={secondHandled} count={executedArgs.Count} args={string.Join(",", executedArgs)}");
        }

        // 4b. Live RBAC revocation, Guest fail-closed over native SenderRole, live ConversationProfile gate & PendingToolInvocationStore capacity eviction
        {
            var rbacArgs = new List<string>();
            var rbacCatalog = new ChatToolCatalog([new CaptureRealBinding(realSpec, rbacArgs)]);
            var liveConv = BotAgent.Services.Platforms.ConversationProfileResolver.Resolve(
                approvalSettings,
                new EffectivePlatformPolicy(
                    new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy), true, true, true, true,
                    PlatformCapabilities.QqOneBot, new HashSet<string>(), new Dictionary<string, bool>(), Array.Empty<string>()),
                (ConversationProfile?)null);
            var rbacReplies = new List<string>();
            var roleResolver = new AccessRoleResolver(new[]
            {
                new AccessRoleBinding(new AccessRoleKey(PlatformId.QqPrivate, AccountScope.Legacy, "", "group:10001", "20002"), WorkspaceRole.GroupManager),
                new AccessRoleBinding(new AccessRoleKey(PlatformId.QqPrivate, AccountScope.Legacy, "", null, "20003"), WorkspaceRole.Guest)
            });
            var rbacSettings = new AppSettings { EnableApprovals = true, WhitelistGroups = "10001", ApprovalApprovers = "20003" };
            var livePluginEnabled = true;
            var rbacApprovals = new ApprovalUseCase(
                new Services.SettingsBox(rbacSettings),
                new ApprovalHooks(
                    _ => { },
                    (text, _) => text,
                    (_, text) => { rbacReplies.Add(text); return Task.CompletedTask; },
                    (_, text) => { rbacReplies.Add(text); return Task.CompletedTask; }),
                new SessionPolicyLedger(),
                new TurnTraceStore(),
                catalog: () => rbacCatalog,
                accessRoles: roleResolver,
                resolveConversation: (_, _) => liveConv,
                pluginAllowsTool: _ => livePluginEnabled);

            // Open 2 requests: 1st revoked immediately before click, 2nd blocked by Guest even with native admin SenderRole & ApprovalApprovers
            var reqOpen1 = rbacApprovals.OpenApprovalForRealToolAsync(conv, "mcp.demo.ticket", callerArgs, 9101L, rbacApprovals.Capabilities).GetAwaiter().GetResult();
            var id1 = reqOpen1 ? System.Text.RegularExpressions.Regex.Match(rbacReplies.Last(), @"编号 ([23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{6})").Groups[1].Value : string.Empty;

            // Explicit Guest with native admin SenderRole AND in ApprovalApprovers fails closed
            var guestAdminMsg = new QqChatMessage(9102L, true, 20003L, 10001L, "guestAdmin", $"同意 {id1}", DateTimeOffset.UtcNow, MentionedSelf: true, SenderRole: "admin");
            var guestHandled = rbacApprovals.HandleCommand(guestAdminMsg);
            Thread.Sleep(40);
            Check("显式 Guest RBAC 即使带 QQ 群 admin SenderRole 且在 ApprovalApprovers 中仍被 fail-closed 拦截",
                guestHandled && rbacArgs.Count == 0, $"handled={guestHandled} args={rbacArgs.Count}");

            // Revoke GroupManager before deciding
            roleResolver.ReplaceAll(ImmutableArray<AccessRoleBinding>.Empty);
            var revokedMgrMsg = new QqChatMessage(9103L, true, 20002L, 10001L, "revokedMgr", $"同意 {id1}", DateTimeOffset.UtcNow, MentionedSelf: true);
            rbacApprovals.HandleCommand(revokedMgrMsg);
            Thread.Sleep(40);
            Check("RBAC 撤销 GroupManager 后立即无法批准在途待批单（无 120s 失效窗口）",
                rbacArgs.Count == 0, $"args={rbacArgs.Count}");

            // Grant GroupManager back, tighten live ConversationProfile to ChatOnly before approval -> execution blocked at live gate
            roleResolver.ReplaceAll(ImmutableArray.Create(
                new AccessRoleBinding(new AccessRoleKey(PlatformId.QqPrivate, AccountScope.Legacy, "", "group:10001", "20002"), WorkspaceRole.GroupManager)));
            liveConv = liveConv with { EffectiveToolTier = ConversationToolTier.ChatOnly };
            rbacApprovals.HandleCommand(revokedMgrMsg with { MessageId = 9104L });
            Thread.Sleep(40);
            Check("待批单存在期间会话档位收紧为 ChatOnly，批准后实工具执行前活闸门仍拦截",
                rbacArgs.Count == 0, $"args={rbacArgs.Count}");

            // PendingToolInvocationStore Put(inv, now) capacity eviction never evicts active non-expired entries when an expired slot exists
            var storeType = typeof(ApprovalUseCase).Assembly.GetType("BotAgent.Services.Permissions.PendingToolInvocationStore", throwOnError: true)!;
            var invType = typeof(ApprovalUseCase).Assembly.GetType("BotAgent.Services.Permissions.PendingToolInvocation", throwOnError: true)!;
            var invStore = Activator.CreateInstance(storeType)!;
            var putMethod = storeType.GetMethod("Put")!;
            var tryTakeMethod = storeType.GetMethod("TryTake")!;
            var baseTime = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 128; i++)
            {
                var exp = i == 0 ? baseTime.AddSeconds(-10) : baseTime.AddSeconds(120);
                var item = Activator.CreateInstance(invType, $"R{i:D5}", "mcp.demo.ticket", "group:10001", new JsonObject(), "summary", exp)!;
                putMethod.Invoke(invStore, new object?[] { item, baseTime });
            }
            var extra = Activator.CreateInstance(invType, "R_NEW", "mcp.demo.ticket", "group:10001", new JsonObject(), "summary", baseTime.AddSeconds(120))!;
            putMethod.Invoke(invStore, new object?[] { extra, baseTime });
            var activeArgs = new object?[] { "R00001", null };
            var activeKept = (bool)tryTakeMethod.Invoke(invStore, activeArgs)!;
            var expiredArgs = new object?[] { "R00000", null };
            var expiredGone = !(bool)tryTakeMethod.Invoke(invStore, expiredArgs)!;
            Check("PendingToolInvocationStore.Put 仅淘汰已过期槽位而不误逐活跃未过期调用",
                activeKept && expiredGone, $"activeKept={activeKept} expiredGone={expiredGone}");
        }

        // 5. McpToolUseCase: ChatToolBindings readOnlyConfirmed vs approval + ADR-0005 memory taint gate
        var mcpCalls = 0;
        var mcpSource = new CountingMcpSource(new[]
        {
            new McpToolInfo("ro", "mcp.demo.ro", "Read-only tool", """{"type":"object","properties":{"q":{"type":"string"}}}""", "demo", true, false, "gen1", ReadOnlyConfirmed: true),
            new McpToolInfo("rw", "mcp.demo.rw", "Mutating tool", """{"type":"object","properties":{"q":{"type":"string"}}}""", "demo", false, false, "gen1", ReadOnlyConfirmed: false),
            new McpToolInfo("py", "mcp.local.py", "Local python", "{}", "local", false, IsLocalProcess: true, "gen1", ReadOnlyConfirmed: false)
        }, () => mcpCalls++);
        var mcpUseCase = new McpToolUseCase(mcpSource);
        mcpUseCase.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        var bindings = mcpUseCase.ChatToolBindings();
        var roBinding = bindings.FirstOrDefault(b => b.Definition.Spec.Id == "mcp.demo.ro");
        var rwBinding = bindings.FirstOrDefault(b => b.Definition.Spec.Id == "mcp.demo.rw");
        Check("McpToolUseCase.ChatToolBindings 仅暴露远端工具：ReadOnlyConfirmed 走文本快车道、其余走人工审批、绝不暴露本地进程工具",
            roBinding is { Definition.Spec.ReadOnly: true, Definition.Spec.Category: ToolCategory.WebRead, Definition.Spec.FeatureKey: ToolFeature.Text }
            && rwBinding is { Definition.Spec.ReadOnly: false, Definition.Spec.FeatureKey: ToolFeature.Approval }
            && bindings.All(b => b.Definition.Spec.Id != "mcp.local.py"));

        ToolOutcome taintedOutcome;
        using (var mem = MemoryUseScope.Begin())
        {
            MemoryUseScope.Register(MemorySensitivityTier.PersonalFact, commit => { commit(); return true; });
            taintedOutcome = roBinding!.ExecuteAsync("group:10001", new ToolCall("mcp.demo.ro", new JsonObject { ["q"] = "secret" }), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        Check("ADR-0005：受记忆污染的聊天回合调用只读远端 MCP 工具被零外部请求拦截（memory_tainted_blocked）",
            !taintedOutcome.Ok && taintedOutcome.ReasonCode == "memory_tainted_blocked" && mcpCalls == 0,
            $"ok={taintedOutcome.Ok} reason={taintedOutcome.ReasonCode} calls={mcpCalls}");

        // 6. AgentTaskRuntime + ZbaTaskRegistry + ZbaHostBackend.DecideApprovalAsync with PythonToolLeases when EnableApprovals=false
        var taskExec = new CountingTaskExecutor();
        var approvalGate = new StaticTaskApprovalGate("py.danger", TimeSpan.FromMinutes(2));
        var runtime = new AgentTaskRuntime(
            taskExec,
            () => DateTimeOffset.UtcNow,
            approvalGate: approvalGate);
        try
        {
            var pyLease = new McpToolInfo("py_danger", "mcp.py.danger", "Py tool", "{}", "local-py", false, IsLocalProcess: true);
            var sub = runtime.Submit(new AgentTaskRequest
            {
                Id = "t-py-1",
                SourceKey = "panel:workspace",
                SessionId = "srv-1",
                Prompt = "run leased py tool",
                ToolScope = new[] { "py.danger" },
                PythonToolLeases = new[] { pyLease }
            });

            var asm = typeof(ServerAgentRunner).Assembly;
            var zbaRegType = asm.GetType("BotAgent.Adapters.Panel.Zba.ZbaTaskRegistry", throwOnError: true)!;
            var sessionsStub = DispatchProxy.Create<IAgentSessionStore, NullSessionProxy>();
            var zbaRegCtor = zbaRegType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).Single();
            var zbaRegArgs = zbaRegCtor.GetParameters().Select(p => p.Name switch
            {
                "sessions" => (object?)sessionsStub,
                "runtime" => runtime,
                "now" => (Func<DateTimeOffset>)(() => DateTimeOffset.UtcNow),
                "serverEnabled" => (Func<bool>)(() => true),
                _ => p.HasDefaultValue ? p.DefaultValue : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            }).ToArray();
            var zbaRegistry = zbaRegCtor.Invoke(zbaRegArgs);

            var backendType = asm.GetType("BotAgent.Adapters.Panel.Zba.ZbaHostBackend", throwOnError: true)!;
            var ctor = backendType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).Single();
            var boxOffApprovals = new Services.SettingsBox(new AppSettings { EnableApprovals = false });
            var eventsBuf = Activator.CreateInstance(
                asm.GetType("BotAgent.Adapters.Panel.Zba.ZbaEventBuffer", throwOnError: true)!,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new object?[] { null }, null);
            var ctorArgs = ctor.GetParameters().Select(p => p.Name switch
            {
                "box" => (object?)boxOffApprovals,
                "tasks" => zbaRegistry,
                "approvals" => approvalUseCase,
                "now" => (Func<DateTimeOffset>)(() => DateTimeOffset.UtcNow),
                "startedAt" => DateTimeOffset.UtcNow,
                "version" => "0.0.0-probe",
                "events" => eventsBuf,
                _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
            }).ToArray();
            var zbaBackend = ctor.Invoke(ctorArgs)!;

            var decideMethod = backendType.GetMethod("DecideApprovalAsync", BindingFlags.Public | BindingFlags.Instance)!;
            var adminCaller = new BotAgent.Domain.Configuration.ConfigurationCaller(
                "zba", "zba:admin", "bot", BotAgent.Domain.Configuration.ConfigurationAccess.SaveConfiguration);
            var decideTask = (ValueTask<BotAgent.Panel.ZbaApprovalDecisionResult>)decideMethod.Invoke(
                zbaBackend, new object?[] { adminCaller, "task-req-1", true, CancellationToken.None })!;
            var decideRes = decideTask.GetAwaiter().GetResult();

            var waitTask = DateTime.UtcNow.AddSeconds(3);
            while (runtime.Get("t-py-1")?.State == AgentTaskState.WaitingApproval && DateTime.UtcNow < waitTask) Thread.Sleep(15);
            while (runtime.Get("t-py-1")?.State == AgentTaskState.Running && DateTime.UtcNow < waitTask) Thread.Sleep(15);
            var finished = runtime.Get("t-py-1");

            Check("ZbaHostBackend.DecideApprovalAsync 在 EnableApprovals=false 时仍能放行含 PythonToolLeases 的任务审批并恢复执行",
                sub.Accepted
                && decideRes.Ok
                && decideRes.ReasonCode == "approved"
                && taskExec.Calls == 1
                && finished?.State == AgentTaskState.Succeeded,
                $"ok={decideRes.Ok} reason={decideRes.ReasonCode} gateRan={taskExec.Calls} state={finished?.State}");

            // Check AgentTaskRuntime.DecideApproval returns Expired once TTL passes
            var clockTime = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
            var expRuntime = new AgentTaskRuntime(
                new CountingTaskExecutor(),
                () => clockTime,
                approvalGate: new StaticTaskApprovalGate("py.danger", TimeSpan.FromSeconds(10), () => clockTime));
            try
            {
                var expSub = expRuntime.Submit(new AgentTaskRequest
                {
                    Id = "t-exp-1",
                    SourceKey = "panel:workspace",
                    SessionId = "srv-1",
                    Prompt = "exp check",
                    ToolScope = new[] { "py.danger" },
                    PythonToolLeases = new[] { pyLease }
                });
                var expiredDecision = expRuntime.DecideApproval(expSub.Task!.ApprovalRequestId!, approve: true, clockTime.AddSeconds(30));
                Check("AgentTaskRuntime.DecideApproval 在审批超期时返回 Expired",
                    expiredDecision == AgentTaskApprovalDecision.Expired, expiredDecision.ToString());
            }
            finally
            {
                expRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class CaptureRealBinding(ToolSpec spec, List<string> captured) : IToolBinding
    {
        public string ProviderId => "system.mcp-tools";
        public string Generation => "demo";
        public ToolDefinition Definition { get; } = new(spec, ImmutableArray<ArgField>.Empty);
        public bool IsReady => true;
        public Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct = default)
        {
            captured.Add(call.Arguments?.ToJsonString() ?? "{}");
            return Task.FromResult(ToolOutcome.Success("created", "ticket_ok"));
        }
        public T GetContribution<T>() where T : class, BotAgent.Domain.Plugins.IPluginContribution
            => throw new InvalidOperationException("plugin_contribution_missing");
    }

    private sealed class CountingMcpSource(IReadOnlyList<McpToolInfo> tools, Action onCall) : IMcpToolSource
    {
        public bool Enabled => true;
        public McpConnectionState State => McpConnectionState.Ready;
        public Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct) => Task.FromResult(tools);
        public bool IsToolCurrent(McpToolInfo tool) => true;
        public Task<McpCallResult> CallAsync(string qualifiedName, JsonObject arguments, CancellationToken ct)
        {
            onCall();
            return Task.FromResult(new McpCallResult(true, "ok", "ok", "ok", false, 2, 1));
        }
        public Task<McpCallResult> CallWithLeaseAsync(McpToolInfo tool, JsonObject arguments, CancellationToken ct)
        {
            onCall();
            return Task.FromResult(new McpCallResult(true, "ok", "ok", "ok", false, 2, 1));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingTaskExecutor : IAgentTaskExecutor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<AgentTaskExecution> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new AgentTaskExecution { Ok = true, Text = "py_done" });
        }
    }

    private sealed class StaticTaskApprovalGate(string toolId, TimeSpan ttl, Func<DateTimeOffset>? clock = null) : IAgentTaskApprovalGate
    {
        public AgentTaskApprovalRequest? Create(AgentTaskRequest request)
        {
            if (request.ToolScope?.Contains(toolId, StringComparer.OrdinalIgnoreCase) == true && request.PythonToolLeases.Count > 0)
                return new AgentTaskApprovalRequest("task-req-1", toolId, "run " + toolId, (clock?.Invoke() ?? DateTimeOffset.UtcNow).Add(ttl));
            return null;
        }
    }

    public class NullSessionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.ReturnType.IsValueType == true && targetMethod.ReturnType != typeof(void)
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}

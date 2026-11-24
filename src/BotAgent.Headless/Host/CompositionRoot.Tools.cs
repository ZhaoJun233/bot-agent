extern alias StorageModule;

using BotAgent.Domain.Configuration;
using System.Collections.Immutable;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;
using ConfigurationModule = StorageModule::BotAgent.Storage;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Panel;
using BotAgent.Adapters.Panel.Zba;
using BotAgent.Adapters.Model;
using BotAgent.Adapters.Platforms;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Model;
using BotAgent.Services.Platforms;
using BotAgent.Services.Resilience;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.Official;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Qq;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;
using BotAgent.Services.Links;
using BotAgent.Services.Local;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.Reply;
using BotAgent.Services.Conversations;
using BotAgent.Services.Poke;
using BotAgent.Services.Permissions;
using BotAgent.Services.Participation;
using BotAgent.Services.Panel;
using BotAgent.Services.Settings;
using BotAgent.Services.Plugins;
using BotAgent.Services.Evaluation;
using BotAgent.Services.Mcp;
namespace BotAgent.Host;

internal static partial class CompositionRoot
{    /// <summary>
    /// ⑤ 装配阶段五：插件门禁、agent 命令与后台评估队列（#110 第 2 步拆出）。
    ///
    /// 顺序是硬的：插件门禁先于 agent 命令与回复链（显式 QQ 动作要吃掉同一份门禁），
    /// agent 命令先于回复链，后台评估队列先于面板（面板的"评估"页要读它的水位）。
    /// </summary>
    private static void AssembleTools(Assembly a)
    {
        // Wave3：插件注册与能力门禁先于 agent 命令与回复链建立；显式 QQ 动作也要吃同一份门禁。
        a.Plugins = a.PluginRuntime.Registry;
        a.PluginGate = new PluginGate(a.Plugins);
        a.Poke.PluginEnabled = () => a.PluginGate.Allows(PluginGate.PokeCapability);
        a.Stickers.PluginEnabled = () => a.PluginGate.Allows(PluginGate.StickersCapability);

        var allowPrivateDocs = EnvironmentAliases.Get("BOTAGENT_ALLOW_PRIVATE_IMAGE_HOSTS", "QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS") == "1";
        var docHttp = new HttpFetcher(
            TimeSpan.FromSeconds(30),
            msg => FileLog.Write("Net", msg),
            "documents",
            allowAutoRedirect: false,
            rejectPrivateDestinations: !allowPrivateDocs);
        a.OwnedTransports.Add(docHttp);
        a.DocumentPorts = new Services.Documents.DocumentHostPortsAdapter(
            docHttp,
            () => allowPrivateDocs,
            () => a.PluginRuntime.TryReadEffectiveValues("preset.feature.documents"),
            msg => FileLog.Write("Documents", msg),
            a.Plugins);
        a.PluginServices.RegisterForPlugin<Services.Documents.IDocumentHostPorts>("preset.feature.documents", a.DocumentPorts);
        a.PluginServices.RegisterForPlugin<Services.Digest.IDigestHostPorts>(
            "preset.feature.digest",
            new Services.Digest.DigestHostPortsAdapter(
                a.Registry,
                a.Brain,
                () => a.Box.Current,
                () => a.PluginRuntime.TryReadEffectiveValues("preset.feature.digest")));

        a.IdentityLinkStore = new StorageModule::BotAgent.Storage.IdentityLinkStore();
        a.IdentityLinks = new BotAgent.Engine.Identity.IdentityLinkService(
            a.IdentityLinkStore,
            () => BotAgent.Adapters.Time.Clock.UtcNow,
            raw => AgentMask.Shorten(raw));

        var knowledgeStore = new StorageModule::BotAgent.Storage.KnowledgeStore();
        a.KnowledgePorts = new Services.Knowledge.KnowledgeHostPortsAdapter(
            knowledgeStore,
            () => a.PluginRuntime.TryReadEffectiveValues("preset.feature.knowledge"),
            msg => FileLog.Write("Knowledge", msg),
            a.DocumentPorts,
            a.Plugins,
            a.UnifiedMemory,
            a.Box,
            a.PlatformPolicies,
            a.IdentityLinks);
        a.OwnedTransports.Add(a.KnowledgePorts);
        a.PluginServices.RegisterForPlugin<Services.Knowledge.IKnowledgeHostPorts>("preset.feature.knowledge", a.KnowledgePorts);
        a.KnowledgeCommands = new Services.Knowledge.KnowledgeCommandService(
            a.Source, a.Box, a.PlatformPolicies, a.Plugins, a.PluginGate, a.KnowledgePorts, a.Registry, a.AccessRoles);

        a.WorkspaceStore = new StorageModule::BotAgent.Storage.WorkspaceConversationStore();
        a.ScheduledJobStore = new StorageModule::BotAgent.Storage.ScheduledJobStore();
        a.SchedulerPorts = new Services.Scheduler.SchedulerHostPortsAdapter(
            a.ScheduledJobStore,
            () => a.PluginRuntime.TryReadEffectiveValues("preset.feature.scheduler"),
            () => BotAgent.Adapters.Time.Clock.UtcNow,
            () => a.ScheduledJobs?.NotifyScheduleChanged());
        a.PluginServices.RegisterForPlugin<Services.Scheduler.ISchedulerHostPorts>("preset.feature.scheduler", a.SchedulerPorts);
        a.SchedulerCommands = new Services.Scheduler.SchedulerCommandService(
            a.Source, a.Box, a.PlatformPolicies, a.Plugins, a.PluginGate, a.Registry, a.AccessRoles);

        a.PersonalTodoStore = new StorageModule::BotAgent.Storage.PersonalTodoStore();
        a.TodoPorts = new Services.Todo.PersonalTodoHostPortsAdapter(
            a.PersonalTodoStore,
            a.ScheduledJobStore,
            a.IdentityLinks,
            () => BotAgent.Adapters.Time.Clock.UtcNow,
            () => a.ScheduledJobs?.NotifyScheduleChanged(),
            a.Box,
            a.PluginGate);
        a.PluginServices.RegisterForPlugin<Services.Todo.IPersonalTodoHostPorts>("preset.feature.personal-todo", a.TodoPorts);
        a.TodoCommands = new Services.Todo.PersonalTodoCommandService(
            a.Source, a.Box, a.PlatformPolicies, a.Plugins, a.PluginGate, a.TodoPorts, a.Registry);
        a.IdentityCommands = new Services.Identity.IdentityCommandService(
            a.Source, a.Box, a.IdentityLinks, a.Registry, a.AccessRoles);

        var episodeRepo = new EpisodeStore();
        var jargonRepo = new JargonStore();
        var jargonService = new Services.Jargon.JargonService(jargonRepo, a.Box);
        a.CommunityMemory = new Services.Memory.ScopedCommunityMemoryService(
            a.Box, a.PlatformPolicies, episodeRepo, jargonRepo, jargonService, a.UnifiedMemory, a.Ui.EmitLog);
        a.OwnedTransports.Add(a.CommunityMemory);
        a.ProfileProvenance = new Services.Profiles.AttestedProfileProvenanceRegistry(
            () => AppDatabase.GetMeta(Services.Profiles.AttestedProfileProvenanceRegistry.MetaKey),
            val => AppDatabase.SetMeta(Services.Profiles.AttestedProfileProvenanceRegistry.MetaKey, val));
        // agent 命令（//）：会话台账 + 内置/外部两路后端
        a.Sessions = new AgentSessionStore(
            Path.Combine(AppPaths.DataDir, "agent-sessions.json"), msg => FileLog.Write("Agent", msg));
        var serverAgentHttp = new HttpFetcher(
            TimeSpan.FromSeconds(60),
            msg => FileLog.Write("Net", msg),
            "server-agent",
            allowAutoRedirect: false,
            rejectPrivateDestinations: true);
        a.OwnedTransports.Add(serverAgentHttp);
        a.ServerAgent = new ServerAgentRunner(a.Box, a.Brain,
            serverAgentHttp,
            msg => FileLog.Write("ServerAgent", msg), a.Mcp.UseCase,
            (sourceKey, toolId, args, token) => ExecuteSameScopeReadPluginToolAsync(a, sourceKey, toolId, args, token),
            sourceKey => ListSameScopeReadPluginTools(a, sourceKey));
        a.AgentCmds = new AgentCommandService(a.Box, a.Source, a.Brain, a.Registry, a.Ui, a.Sessions, a.ServerAgent,
            new AgentImageStore(),
            new AgentHooks(
                Log: a.Ui.EmitLog,
                SelfId: () => a.Identity.SelfId,
                IsOfficialUserAllowed: a.Whitelist.IsOfficialPrivateExplicit,
                SendPlainAsync: a.Plain.SendPlainAsync),
             actionGateway: a.Gateway,
             platformPolicies: a.PlatformPolicies,
             pluginAllows: a.PluginGate.Allows, unifiedMemory: a.UnifiedMemory, plugins: a.Plugins);

        var taskLedger = new AgentTaskLedgerStore();
        taskLedger.Prune(0);
        var taskApprovalGate = new BotAgent.Adapters.Agent.TaskApprovalGate(
            () => BotAgent.Adapters.Time.Clock.Now,
            () => a.Box.Current.EnableApprovals,
            () => a.Box.Current,
            () => DockerAvailability.IsAvailable,
            a.AgentCmds.CaptureTaskPythonLeases);
        var taskRuntime = new BotAgent.Engine.Runtime.AgentTaskRuntime(
            new BotAgent.Adapters.Agent.AgentTaskExecutor(a.AgentCmds),
            () => BotAgent.Adapters.Time.Clock.Now,
            log: message => FileLog.Write("TaskRuntime", message),
            ledger: taskLedger,
            bootSeq: taskLedger.BootSeq,
            approvalGate: taskApprovalGate);
        Func<BotAgent.Domain.Agent.AgentTaskRequest, BotAgent.Domain.Agent.AgentTaskRequest> taskPrepare =
            request => taskApprovalGate.PrepareWithPluginTools(
                request,
                a.AgentCmds.GetTaskMcpToolNames(),
                a.AgentCmds.GetSameScopeReadPluginToolIds(request.SourceKey));
        a.TaskRuntime = taskRuntime;
        a.TaskPrepare = taskPrepare;
        a.AgentCmds.BindTaskRuntime(taskRuntime, taskPrepare, a.AccessRoles, () => a.Approvals.Approvers());
        a.Approvals.BindRuntimeTaskApprovals(a.AgentCmds.HasPendingRuntimeApprovalInConversation, a.AgentCmds.TryHandleRuntimeTaskApproval);
        a.Approvals.BindPeriodicSweep(now => taskRuntime.SweepApprovals(now));

        a.Background = new BackgroundEvaluatorQueue(new BackgroundEvaluatorOptions
        {
            Capacity = 64,
            MaxConcurrency = 2,
            MaxAttempts = 1,
            PerJobTimeout = TimeSpan.FromSeconds(30),
            DrainTimeout = TimeSpan.FromSeconds(3),
        });

        var delivery = new Services.Scheduler.ScheduledDeliveryAdapter(
            a.Box,
            a.Source,
            a.PlatformPolicies,
            a.Whitelist,
            a.Plugins,
            workspaceStore: a.WorkspaceStore,
            onWorkspaceAppended: _ => a.NotifyWorkspaceAppended?.Invoke(),
            dispatchTask: (scopeKey, prompt, ct) => DispatchScheduledAgentTask(a, scopeKey, prompt, ct),
            utcNow: () => BotAgent.Adapters.Time.Clock.UtcNow,
            log: a.Ui.EmitLog,
            pluginGate: a.PluginGate);
        a.ScheduledJobs = new BotAgent.Engine.Scheduler.ScheduledJobRunner(
            a.ScheduledJobStore,
            delivery,
            () => BotAgent.Adapters.Time.Clock.UtcNow,
            a.Ui.EmitLog);
    }

    private static Task<(bool Delivered, string Code)> DispatchScheduledAgentTask(
        Assembly a,
        string scopeKey,
        string prompt,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!a.Box.Current.EnableAgentBridge)
            return Task.FromResult((false, "agent_bridge_disabled"));
        if (!a.Box.Current.EnableServerAgent)
            return Task.FromResult((false, "server_agent_disabled"));
        IReadOnlyList<string>? narrowedScope = null;
        if (!string.Equals(scopeKey, BotAgent.Domain.Scheduler.SchedulerLimits.WorkspaceOwnerScopeKey, StringComparison.Ordinal))
        {
            var convSettings = ConversationProfileResolver.Resolve(
                a.Box.Current, a.PlatformPolicies.Resolve(ResolveSourcePlatformContext(a.Box.Current, scopeKey)), scopeKey);
            if (convSettings.EffectiveToolTier == BotAgent.Domain.Configuration.ConversationToolTier.ChatOnly)
                return Task.FromResult((false, "conversation_profile_denied"));
            if (convSettings.EffectiveToolTier == BotAgent.Domain.Configuration.ConversationToolTier.ReadOnlyTools)
                narrowedScope = a.AgentCmds.BuildGeneralAssistantToolScope(scopeKey);
        }
        var session = a.Sessions.FindCurrent(scopeKey, "server") ?? a.Sessions.EnsureCurrent(scopeKey, "server");
        var req = new BotAgent.Domain.Agent.AgentTaskRequest
        {
            Id = $"st-{BotAgent.Adapters.Time.Clock.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N")[..8]}",
            SourceKey = scopeKey,
            SessionId = session.Id,
            Prompt = prompt,
            Target = "server",
            Timeout = TimeSpan.FromSeconds(120),
            ToolScope = narrowedScope,
            CaptureResult = string.Equals(scopeKey, BotAgent.Domain.Scheduler.SchedulerLimits.WorkspaceOwnerScopeKey, StringComparison.Ordinal)
        };
        var prepared = a.TaskPrepare is not null ? a.TaskPrepare(req) : req;
        var submitted = a.TaskRuntime?.Submit(prepared);
        if (submitted is null || !submitted.Accepted || submitted.Task is null)
            return Task.FromResult((false, submitted?.Rejection.ToString()?.ToLowerInvariant() ?? "task_runtime_unavailable"));
        var code = submitted.Task.State == BotAgent.Domain.Agent.AgentTaskState.WaitingApproval
            ? $"task_waiting_approval:{submitted.Task.Id}"
            : $"task_dispatched:{submitted.Task.Id}";
        return Task.FromResult((true, code));
    }

    private static async Task<BotAgent.Domain.Tools.ToolOutcome?> ExecuteSameScopeReadPluginToolAsync(
        Assembly a,
        string sourceKey,
        string toolId,
        System.Text.Json.Nodes.JsonObject args,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(toolId)
            || !a.Box.Current.AiModeEnabled
            || BotAgent.Domain.Memory.MemoryTaintPolicy.ClassifyTool(toolId, readOnly: true)
                != BotAgent.Domain.Memory.ToolEgressClass.SameScopeRead)
            return null;
        if (!a.PluginGate.AllowsTool(toolId)) return null;
        var catalog = a.Plugins.GetToolCatalog();
        var binding = catalog.FindBinding(toolId);
        if (binding is null
            || binding.Definition.Spec.Origin != BotAgent.Domain.Tools.ToolOrigin.Plugin
            || !binding.Definition.Spec.ReadOnly
            || !binding.IsReady)
            return null;

        var isWorkspace = string.Equals(sourceKey, BotAgent.Domain.Scheduler.SchedulerLimits.WorkspaceOwnerScopeKey, StringComparison.Ordinal);
        if (!isWorkspace)
        {
            var effective = ConversationProfileResolver.Resolve(
                a.Box.Current, a.PlatformPolicies.Resolve(ResolveSourcePlatformContext(a.Box.Current, sourceKey)), sourceKey);
            if (!effective.AllowsReadOnlyTools) return null;

            var featureKey = binding.Definition.Spec.FeatureKey;
            if (featureKey == BotAgent.Domain.Tools.ToolFeature.PersonalTodo && IsGroupSourceKey(sourceKey))
                return null;
            if (!string.IsNullOrWhiteSpace(featureKey) && !effective.FeatureEnabled(featureKey))
                return null;
        }

        if (BotAgent.Services.Memory.MemoryUseScope.HasMemory)
        {
            try
            {
                return await BotAgent.Services.Memory.MemoryUseScope
                    .Use(() => binding.ExecuteAsync(sourceKey, new(toolId, args), ct))
                    .ConfigureAwait(false);
            }
            catch (BotAgent.Services.Memory.MemoryUseInvalidatedException)
            {
                return null;
            }
        }
        return await binding.ExecuteAsync(sourceKey, new(toolId, args), ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ListSameScopeReadPluginTools(Assembly a, string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || !a.Box.Current.AiModeEnabled) return Array.Empty<string>();
        var isWorkspace = string.Equals(sourceKey, BotAgent.Domain.Scheduler.SchedulerLimits.WorkspaceOwnerScopeKey, StringComparison.Ordinal);
        EffectiveConversationSettings? effective = null;
        if (!isWorkspace)
        {
            effective = ConversationProfileResolver.Resolve(
                a.Box.Current, a.PlatformPolicies.Resolve(ResolveSourcePlatformContext(a.Box.Current, sourceKey)), sourceKey);
            if (!effective.AllowsReadOnlyTools) return Array.Empty<string>();
        }
        var isGroup = !isWorkspace && IsGroupSourceKey(sourceKey);

        var catalog = a.Plugins.GetToolCatalog();
        var items = new List<string>();
        foreach (var spec in catalog.Specs)
        {
            if (spec.Origin != BotAgent.Domain.Tools.ToolOrigin.Plugin || !spec.ReadOnly) continue;
            if (spec.FeatureKey == BotAgent.Domain.Tools.ToolFeature.PersonalTodo && isGroup) continue;
            if (BotAgent.Domain.Memory.MemoryTaintPolicy.ClassifyTool(spec.Id, readOnly: true)
                != BotAgent.Domain.Memory.ToolEgressClass.SameScopeRead) continue;
            if (!a.PluginGate.AllowsTool(spec.Id)) continue;
            if (catalog.FindBinding(spec.Id) is not { IsReady: true }) continue;
            if (effective is not null && !string.IsNullOrWhiteSpace(spec.FeatureKey) && !effective.FeatureEnabled(spec.FeatureKey)) continue;
            items.Add($"{spec.Id}({spec.Summary})");
        }
        return items;
    }

    private static bool IsGroupSourceKey(string sourceKey)
    {
        if (BotAgent.Domain.Messaging.ConversationIdCodec.TryParse(sourceKey, out var conv))
            return conv.Kind == BotAgent.Domain.Conversation.ConversationKind.GroupChat;
        return sourceKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase)
            || sourceKey.Contains(":group:", StringComparison.OrdinalIgnoreCase);
    }

    private static PlatformContext ResolveSourcePlatformContext(AppSettings settings, string sourceKey)
    {
        if (PlatformSourceExecution.Current is { IsCurrent: true } current)
            return current.Context;
        if (BotAgent.Domain.Messaging.ConversationIdCodec.TryParse(sourceKey, out var parsedConv))
            return new PlatformContext(parsedConv.PlatformId, parsedConv.AccountScope,
                ResolveDeclaredInstanceId(settings, parsedConv.PlatformId, parsedConv.AccountScope));
        var ch = Channels.ChannelOf(sourceKey);
        var pid = PlatformId.Normalize(ch);
        var scope = pid == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy;
        return new PlatformContext(pid, scope, ResolveDeclaredInstanceId(settings, pid, scope));
    }

    private static string ResolveDeclaredInstanceId(AppSettings settings, string platformId, string accountScope)
    {
        var declared = settings.DeclaredPlatformDrivers;
        if (declared.IsDefaultOrEmpty) return string.Empty;
        DriverKey? match = null;
        var count = 0;
        var norm = PlatformId.Normalize(platformId);
        foreach (var d in declared)
        {
            if (string.Equals(PlatformId.Normalize(d.PlatformId), norm, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.AccountScope, accountScope, StringComparison.OrdinalIgnoreCase))
            {
                match = d;
                count++;
            }
        }
        return count == 1 ? (match?.InstanceId ?? string.Empty) : string.Empty;
    }
}

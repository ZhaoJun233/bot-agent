using System.Collections.Immutable;
using System.Reflection;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Profiles;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Tools;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Tools;

namespace BotAgent.EngineProbe;

internal static class Phase4EngineChecks
{
    public static Task RunAsync()
    {
        CheckAccessRoleResolverPrecedenceAndIsolation();
        CheckToolVisibilityChatOnlyBlocksMcp();
        CheckReplyPipelineGuestDowngrade();
        return Task.CompletedTask;
    }

    private static void CheckAccessRoleResolverPrecedenceAndIsolation()
    {
        var bindings = ImmutableArray.Create(
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "feishu-main", "feishu:group:oc_g1", "ou_mgr"),
                WorkspaceRole.GroupManager,
                "group-1 manager"),
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "feishu-main", null, "ou_mgr"),
                WorkspaceRole.Member,
                "global member"),
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "feishu-main", null, "ou_guest"),
                WorkspaceRole.Guest,
                "explicit guest"),
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.QqPrivate, AccountScope.Legacy, "", null, "20001"),
                WorkspaceRole.BotOwner,
                "owner"));

        var resolver = new AccessRoleResolver(bindings);

        var mgr = new SubjectIdentity(PlatformId.Feishu, AccountScope.Default, "feishu-main", "ou_mgr");
        Ensure(
            resolver.ResolveRole(mgr, "feishu:group:oc_g1", "", isOfficialExplicitPrivateAllowed: false) == WorkspaceRole.GroupManager,
            "scoped GroupManager binding overrides global Member binding in target group");
        Ensure(
            resolver.ResolveRole(mgr, "feishu:group:oc_other", "", isOfficialExplicitPrivateAllowed: false) == WorkspaceRole.Member,
            "GroupManager in group1 never leaks manager privilege into group2");
        Ensure(
            resolver.ResolveRole(mgr, "feishu:dm:ou_mgr", "", isOfficialExplicitPrivateAllowed: false) == WorkspaceRole.Member,
            "GroupManager in group1 never leaks manager privilege into private DM");

        Ensure(
            resolver.IsConversationApprover(mgr, "feishu:group:oc_g1"),
            "GroupManager is an approver in bound group");
        Ensure(
            !resolver.IsConversationApprover(mgr, "feishu:group:oc_other"),
            "GroupManager is NOT an approver in another group");

        var scopedTokens = resolver.ApproverTokensForConversation(PlatformId.Feishu, AccountScope.Default, "feishu-main", "feishu:group:oc_g1");
        Ensure(scopedTokens.Contains("user:ou_mgr", StringComparer.Ordinal), "ApproverTokensForConversation includes GroupManager for bound group");
        var otherTokens = resolver.ApproverTokensForConversation(PlatformId.Feishu, AccountScope.Default, "feishu-main", "feishu:group:oc_other");
        Ensure(!otherTokens.Contains("user:ou_mgr", StringComparer.Ordinal), "ApproverTokensForConversation excludes out-of-scope GroupManager");

        // Legacy AgentAllowedUsers fallback applies ONLY to qq.private when no explicit RBAC binding exists
        var qqLegacyUser = new SubjectIdentity(PlatformId.QqPrivate, AccountScope.Legacy, "", "30001", LegacyNumericUserId: 30001);
        Ensure(
            resolver.ResolveRole(qqLegacyUser, "group:10001", "30001", isOfficialExplicitPrivateAllowed: false) == WorkspaceRole.TrustedMember,
            "legacy AgentAllowedUsers grants TrustedMember on qq.private when unmapped");
        var feishuUnmapped = new SubjectIdentity(PlatformId.Feishu, AccountScope.Default, "feishu-main", "30001");
        Ensure(
            resolver.ResolveRole(feishuUnmapped, "feishu:group:oc_g1", "30001", isOfficialExplicitPrivateAllowed: false) == WorkspaceRole.Member,
            "legacy numeric AgentAllowedUsers NEVER elevates non-qq.private identities");

        // Explicit Guest binding beats legacy fallback even on qq.private
        var qqGuest = new SubjectIdentity(PlatformId.QqPrivate, AccountScope.Legacy, "", "40001", LegacyNumericUserId: 40001);
        resolver.ReplaceAll(bindings.Add(new AccessRoleBinding(
            new AccessRoleKey(PlatformId.QqPrivate, AccountScope.Legacy, "", null, "40001"),
            WorkspaceRole.Guest)));
        Ensure(
            resolver.ResolveRole(qqGuest, "group:10001", "40001", isOfficialExplicitPrivateAllowed: false) == WorkspaceRole.Guest,
            "explicit Guest RBAC binding blocks legacy AgentAllowedUsers elevation");
        Ensure(
            resolver.TryResolveExplicitRole(qqGuest, "group:10001", out var explicitGuest) && explicitGuest == WorkspaceRole.Guest,
            "TryResolveExplicitRole returns true for explicit RBAC binding");
        Ensure(
            !resolver.TryResolveExplicitRole(qqLegacyUser, "group:10001", out _),
            "TryResolveExplicitRole returns false for unmapped legacy user");

        // Scoped demotion overrides instance-wide BotOwner in ApproverTokensForConversation and Feishu empty accountScope defaults to default
        var demoteResolver = new AccessRoleResolver(new[]
        {
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "", null, "ou_owner_demoted"),
                WorkspaceRole.BotOwner),
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "feishu-main", null, "ou_owner_demoted"),
                WorkspaceRole.Guest),
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "feishu-main", null, "ou_scoped_demote"),
                WorkspaceRole.BotOwner),
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.Feishu, AccountScope.Default, "feishu-main", "feishu:group:oc_g1", "ou_scoped_demote"),
                WorkspaceRole.Guest),
        });
        var ownerExactSubject = new SubjectIdentity(PlatformId.Feishu, AccountScope.Default, "feishu-main", "ou_owner_demoted");
        Ensure(
            demoteResolver.ResolveRole(ownerExactSubject, "feishu:group:oc_other", "", false) == WorkspaceRole.Guest,
            "exact Feishu instance binding overrides wildcard empty instance binding");
        var g1Approvers = demoteResolver.ApproverTokensForConversation(PlatformId.Feishu, "", "feishu-main", "feishu:group:oc_g1");
        Ensure(
            !g1Approvers.Contains("user:ou_scoped_demote", StringComparer.Ordinal),
            "ApproverTokensForConversation excludes instance BotOwner explicitly demoted to Guest in target group");
        var otherGroupApprovers = demoteResolver.ApproverTokensForConversation(PlatformId.Feishu, "", "feishu-main", "feishu:group:oc_other");
        Ensure(
            otherGroupApprovers.Contains("user:ou_scoped_demote", StringComparer.Ordinal),
            "ApproverTokensForConversation keeps BotOwner in unbound groups and defaults Feishu empty accountScope to default");
    }

    private static void CheckToolVisibilityChatOnlyBlocksMcp()
    {
        var mcpSpec = new ToolSpec(
            "mcp.docs.search",
            ToolCategory.WebRead,
            "Search external docs",
            ReadOnly: true,
            Executor: "mcp.client",
            Parameters: "query:string",
            Default: ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.Text,
            Origin: ToolOrigin.Mcp);
        var catalog = new ChatToolCatalog([new SyntheticBinding(mcpSpec)]);

        var policy = new EffectivePlatformPolicy(
            new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy),
            true, true, true, true,
            PlatformCapabilities.QqOneBot,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["text"] = true },
            Array.Empty<string>());

        var settings = new AppSettings();
        var baseConv = ConversationProfileResolver.Resolve(settings, policy, (ConversationProfile?)null);
        var vis = new ToolVisibility(catalog: () => catalog);

        Ensure(vis.Allows(settings, policy, "mcp.docs.search", catalog, baseConv with { EffectiveToolTier = ConversationToolTier.ReadOnlyTools }),
            "ReadOnlyTools tier allows verified read-only MCP tool");
        Ensure(!vis.Allows(settings, policy, "mcp.docs.search", catalog, baseConv with { EffectiveToolTier = ConversationToolTier.ChatOnly }),
            "ChatOnly tier blocks ToolOrigin.Mcp tools even with FeatureKey.Text");
    }

    private static void CheckReplyPipelineGuestDowngrade()
    {
        var settings = new AppSettings { PrivateChatEnabled = true, WhitelistGroups = "10001" };
        var box = new SyntheticBox(settings);
        var resolver = new AccessRoleResolver(new[]
        {
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.QqPrivate, AccountScope.Legacy, "", null, "55001"),
                WorkspaceRole.Guest),
            new AccessRoleBinding(
                new AccessRoleKey(PlatformId.QqPrivate, AccountScope.Legacy, "", null, "55002"),
                WorkspaceRole.Member)
        });

        var conv = new BotConversation { SourceKey = "group:10001", Name = "group:10001", Kind = ConversationKind.GroupChat };
        conv.Append(new ChatMessage { Role = MessageRole.Peer, SenderName = "guest", Text = "hi", SenderId = 55001, QqMessageId = 1, Timestamp = DateTimeOffset.UtcNow });
        conv.Append(new ChatMessage { Role = MessageRole.Peer, SenderName = "member", Text = "hi", SenderId = 55002, QqMessageId = 2, Timestamp = DateTimeOffset.UtcNow });

        var source = Stub<IQqChatSource>((_, _) => null);
        var registry = new ConversationRegistry(Stub<IConversationRepository>((_, _) => null), box, source, _ => true, _ => { });
        var policy = new EffectivePlatformPolicy(
            new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy),
            true, true, true, true,
            PlatformCapabilities.QqOneBot,
            new HashSet<string>(), new Dictionary<string, bool>(), Array.Empty<string>());
        var policies = Stub<IPlatformPolicyResolver>((m, _) => m.Name == "IsChatEnabled" ? true : policy);
        var constructor = typeof(ReplyPipeline).GetConstructors().Single();
        var args = constructor.GetParameters().Select(p => p.Name switch
        {
            "box" => (object)box,
            "source" => source,
            "registry" => registry,
            "whitelist" => new WhitelistGate(box, policies),
            "platformPolicies" => policies,
            "participation" => new ParticipationUseCase(box, _ => { }),
            "hooks" => new ReplyHooks(_ => { }, () => 10001, () => false),
            "ui" => Stub(p.ParameterType),
            "agentCmds" => Stub(p.ParameterType),
            "accessRoles" => resolver,
            _ => p.HasDefaultValue ? p.DefaultValue : null
        }).ToArray();

        var pipeline = (ReplyPipeline)constructor.Invoke(args);
        var method = typeof(ReplyPipeline).GetMethod("IsGuestSpeaker", BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException("ReplyPipeline.IsGuestSpeaker not found");
        var isGuest1 = (bool)method.Invoke(pipeline, new object?[] { conv, 1L, policy.Context, settings })!;
        var isGuest2 = (bool)method.Invoke(pipeline, new object?[] { conv, 2L, policy.Context, settings })!;
        Ensure(isGuest1, "ReplyPipeline.IsGuestSpeaker detects Guest binding and forces ChatOnly downgrade");
        Ensure(!isGuest2, "ReplyPipeline.IsGuestSpeaker keeps Member binding non-guest");
    }

    private sealed class SyntheticBox(AppSettings current) : ISettingsBox
    {
        public AppSettings Current => current;
    }

    private sealed class SyntheticBinding(ToolSpec spec) : IToolBinding
    {
        public string ProviderId => "system.mcp-tools";
        public string Generation => "demo";
        public ToolDefinition Definition { get; } = new(spec, ImmutableArray<ArgField>.Empty);
        public bool IsReady => true;
        public Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct = default)
            => Task.FromResult(ToolOutcome.Success("ok", "ok"));
        public T GetContribution<T>() where T : class, IPluginContribution
            => throw new InvalidOperationException("plugin_contribution_missing");
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?>? onCall = null) where T : class
        => (T)Stub(typeof(T), onCall);

    private static object Stub(Type type, Func<MethodInfo, object?[]?, object?>? onCall = null)
    {
        var method = typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create), Type.EmptyTypes)!.MakeGenericMethod(type, typeof(StubProxy));
        var proxy = method.Invoke(null, null)!;
        ((StubProxy)proxy).Handler = onCall;
        return proxy;
    }

    public class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Handler is not null && targetMethod is not null)
            {
                var res = Handler(targetMethod, args);
                if (res is not null || !targetMethod.ReturnType.IsValueType) return res;
            }
            return targetMethod?.ReturnType.IsValueType == true && targetMethod.ReturnType != typeof(void)
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

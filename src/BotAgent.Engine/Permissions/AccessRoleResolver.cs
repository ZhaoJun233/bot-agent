using System.Collections.Immutable;
using System.Globalization;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Permissions;

/// <summary>
/// 跨平台群/私聊轻量分级 RBAC 解析器：
///   1. 同源会话精确绑定 > 同实例全局绑定 > 向后兼容旧 AgentAllowedUsers / 官方显式私聊白名单；
///   2. 严格群隔离：GroupManager 永不跨群继承；
///   3. 绝不自动授予统一记忆管理员或面板管理权限。
/// </summary>
public sealed class AccessRoleResolver : IAccessRoleResolver
{
    private readonly object _gate = new();
    private ImmutableArray<AccessRoleBinding> _bindings;

    public AccessRoleResolver(IEnumerable<AccessRoleBinding>? initialBindings = null)
    {
        _bindings = AccessRoleValidator.FreezeAll(initialBindings);
    }

    public ImmutableArray<AccessRoleBinding> Snapshot()
    {
        lock (_gate) return _bindings;
    }

    public void ReplaceAll(ImmutableArray<AccessRoleBinding> bindings)
    {
        var frozen = AccessRoleValidator.FreezeAll(bindings);
        lock (_gate) _bindings = frozen;
    }

    public WorkspaceRole ResolveRole(
        SubjectIdentity subject,
        string? conversationKey,
        string? legacyAgentAllowedUsers,
        bool isOfficialExplicitPrivateAllowed)
    {
        if (TryResolveExplicitRole(subject, conversationKey, out var explicitRole))
            return explicitRole;

        var platform = PlatformId.Normalize(subject.PlatformId);
        if (MatchesLegacyAllowlist(subject, platform, legacyAgentAllowedUsers) || isOfficialExplicitPrivateAllowed)
            return WorkspaceRole.TrustedMember;

        return WorkspaceRole.Member;
    }

    public bool TryResolveExplicitRole(
        SubjectIdentity subject,
        string? conversationKey,
        out WorkspaceRole role)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var platform = PlatformId.Normalize(subject.PlatformId);
        var account = string.IsNullOrWhiteSpace(subject.AccountScope)
            ? (platform == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy)
            : subject.AccountScope.Trim();
        var instance = subject.InstanceId ?? string.Empty;
        var current = Snapshot();

        AccessRoleBinding? scopedMatch = null;
        var scopedScore = -1;
        AccessRoleBinding? instanceMatch = null;
        var instanceScore = -1;

        foreach (var binding in current)
        {
            if (!MatchesInstance(binding.Key, platform, account, instance)) continue;
            if (!MatchesUser(binding.Key.NativeUserId, subject)) continue;

            var instExact = string.Equals(binding.Key.InstanceId, instance, StringComparison.Ordinal) ? 2 : 0;
            var userExact = string.Equals(binding.Key.NativeUserId, subject.NativeUserId, StringComparison.Ordinal) ? 1 : 0;

            if (binding.Key.ScopeConversationKey is { Length: > 0 } boundScope)
            {
                if (!string.IsNullOrWhiteSpace(conversationKey) && ScopesMatch(platform, boundScope, conversationKey))
                {
                    var scopeExact = string.Equals(boundScope, conversationKey, StringComparison.Ordinal) ? 4 : 0;
                    var score = scopeExact + instExact + userExact;
                    if (scopedMatch is null || score > scopedScore || (score == scopedScore && binding.Role < scopedMatch.Role))
                    {
                        scopedMatch = binding;
                        scopedScore = score;
                    }
                }
            }
            else if (binding.Role != WorkspaceRole.GroupManager)
            {
                var score = instExact + userExact;
                if (instanceMatch is null || score > instanceScore || (score == instanceScore && binding.Role < instanceMatch.Role))
                {
                    instanceMatch = binding;
                    instanceScore = score;
                }
            }
        }

        if (scopedMatch is not null)
        {
            role = scopedMatch.Role;
            return true;
        }
        if (instanceMatch is not null)
        {
            role = instanceMatch.Role;
            return true;
        }

        role = default;
        return false;
    }

    public bool IsConversationApprover(SubjectIdentity subject, string conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey)) return false;
        var role = ResolveRole(subject, conversationKey, legacyAgentAllowedUsers: null, isOfficialExplicitPrivateAllowed: false);
        return role is WorkspaceRole.GroupManager or WorkspaceRole.BotOwner;
    }

    public IReadOnlyList<string> ApproverTokensForConversation(
        string platformId,
        string accountScope,
        string instanceId,
        string conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey)) return Array.Empty<string>();
        var platform = PlatformId.Normalize(platformId);
        var account = string.IsNullOrWhiteSpace(accountScope)
            ? (platform == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy)
            : accountScope.Trim();
        var instance = instanceId ?? string.Empty;
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in Snapshot())
        {
            if (binding.Role is not (WorkspaceRole.GroupManager or WorkspaceRole.BotOwner)) continue;
            if (!MatchesInstance(binding.Key, platform, account, instance)) continue;
            if (binding.Role == WorkspaceRole.GroupManager
                && (string.IsNullOrWhiteSpace(binding.Key.ScopeConversationKey)
                    || !ScopesMatch(platform, binding.Key.ScopeConversationKey, conversationKey)))
            {
                continue;
            }

            if (binding.Key.ScopeConversationKey is { Length: > 0 } scope
                && !ScopesMatch(platform, scope, conversationKey))
            {
                continue;
            }

            candidates.Add(binding.Key.NativeUserId);
        }

        if (candidates.Count == 0) return Array.Empty<string>();
        var list = new List<string>(candidates.Count);
        foreach (var uid in candidates.OrderBy(x => x, StringComparer.Ordinal))
        {
            var subject = new SubjectIdentity(platform, account, instance, uid);
            if (IsConversationApprover(subject, conversationKey))
                list.Add("user:" + uid);
        }
        return list.ToArray();
    }

    private static bool MatchesInstance(AccessRoleKey key, string platform, string account, string instance)
    {
        if (!string.Equals(key.PlatformId, platform, StringComparison.Ordinal)) return false;
        if (!string.Equals(key.AccountScope, account, StringComparison.Ordinal)) return false;
        if (string.Equals(key.InstanceId, instance, StringComparison.Ordinal)) return true;
        // Feishu single default driver allows empty instance wildcard when incoming uses "feishu-main" or vice versa.
        if (platform == PlatformId.Feishu && string.Equals(account, AccountScope.Default, StringComparison.Ordinal))
        {
            var a = key.InstanceId;
            var b = instance;
            return (a.Length == 0 || a == "feishu-main") && (b.Length == 0 || b == "feishu-main");
        }
        return false;
    }

    private static bool MatchesUser(string bindingNativeUserId, SubjectIdentity subject)
    {
        if (string.Equals(bindingNativeUserId, subject.NativeUserId, StringComparison.Ordinal))
            return true;
        if (subject.LegacyNumericUserId is long uid
            && string.Equals(bindingNativeUserId, uid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            return true;
        return false;
    }

    private static bool ScopesMatch(string platform, string boundScope, string targetScope)
    {
        if (string.Equals(boundScope, targetScope, StringComparison.Ordinal)) return true;
        if (!ConversationIdCodec.TryParse(boundScope, out var a) || !ConversationIdCodec.TryParse(targetScope, out var b))
            return false;
        return PlatformId.Normalize(a.PlatformId) == platform
            && PlatformId.Normalize(b.PlatformId) == platform
            && a.Kind == b.Kind
            && string.Equals(a.NativeTargetId, b.NativeTargetId, StringComparison.Ordinal)
            && string.Equals(a.ThreadId ?? string.Empty, b.ThreadId ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool MatchesLegacyAllowlist(SubjectIdentity subject, string normalizedPlatformId, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var pieces = raw.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawPiece in pieces)
        {
            var token = rawPiece.Trim();
            if (token == "*") return true;
            if (subject.LegacyNumericUserId is long uid && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed == uid)
                return true;
            var prefixed = normalizedPlatformId + ":" + subject.NativeUserId;
            if (string.Equals(token, prefixed, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

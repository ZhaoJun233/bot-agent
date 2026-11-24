using System.Collections.Immutable;
using System.Text;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;

namespace BotAgent.Domain.Permissions;

/// <summary>跨平台群/私聊轻量分级角色（严格不替代统一记忆主体授权或面板管理员令牌）。</summary>
public enum WorkspaceRole
{
    Guest = 0,
    Member = 1,
    TrustedMember = 2,
    GroupManager = 3,
    BotOwner = 4,
}

public sealed record AccessRoleKey(
    string PlatformId,
    string AccountScope,
    string InstanceId,
    string? ScopeConversationKey,
    string NativeUserId)
{
    public override string ToString() => "AccessRoleKey";
}

public sealed record AccessRoleBinding(
    AccessRoleKey Key,
    WorkspaceRole Role,
    string? Note = null)
{
    public override string ToString() => "AccessRoleBinding";
}

/// <summary>纯领域 RBAC 规则校验与规范化（无 IO / 零副作用）。</summary>
public static class AccessRoleValidator
{
    public const int MaxBindings = 128;
    public const int MaxJsonBytes = 64 * 1024;
    public const int MaxUserIdLength = 128;
    public const int MaxScopeLength = 256;
    public const int MaxNoteLength = 80;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static AccessRoleKey FreezeKey(AccessRoleKey? key)
    {
        if (key is null) throw new ArgumentException("invalid_access_role_key");
        var platform = CheckToken(key.PlatformId, 64);
        platform = PlatformId.Normalize(platform);
        if (platform is not (PlatformId.QqPrivate or PlatformId.QqOfficial or PlatformId.Feishu or PlatformId.Local))
            throw new ArgumentException("invalid_access_role_platform");
        var accountScope = CheckToken(key.AccountScope, 32);
        var instanceId = key.InstanceId ?? throw new ArgumentException("invalid_access_role_instance");
        if (instanceId.Length > 64 || instanceId.Trim() != instanceId || HasControl(instanceId))
            throw new ArgumentException("invalid_access_role_instance");
        StrictUtf8.GetByteCount(instanceId);

        var nativeUserId = CheckToken(key.NativeUserId, MaxUserIdLength);
        string? scopeKey = null;
        if (!string.IsNullOrWhiteSpace(key.ScopeConversationKey))
        {
            scopeKey = CheckToken(key.ScopeConversationKey, MaxScopeLength);
            if (!MatchesPlatform(platform, scopeKey))
                throw new ArgumentException("access_role_scope_platform_mismatch");
        }

        return new AccessRoleKey(platform, accountScope, instanceId, scopeKey, nativeUserId);
    }

    public static AccessRoleBinding Freeze(AccessRoleBinding? binding)
    {
        if (binding is null || !Enum.IsDefined(binding.Role))
            throw new ArgumentException("invalid_access_role");
        var key = FreezeKey(binding.Key);
        if (binding.Role == WorkspaceRole.GroupManager && !IsGroupScope(key.PlatformId, key.ScopeConversationKey))
            throw new ArgumentException("group_manager_requires_group_scope");

        string? note = null;
        if (!string.IsNullOrWhiteSpace(binding.Note))
        {
            var trimmed = binding.Note.Trim();
            if (trimmed.Length > MaxNoteLength || HasControl(trimmed))
                throw new ArgumentException("invalid_access_role_note");
            StrictUtf8.GetByteCount(trimmed);
            note = trimmed;
        }

        return new AccessRoleBinding(key, binding.Role, note);
    }

    public static ImmutableArray<AccessRoleBinding> FreezeAll(IEnumerable<AccessRoleBinding>? bindings)
    {
        var list = ImmutableArray.CreateBuilder<AccessRoleBinding>();
        var seen = new HashSet<AccessRoleKey>();
        if (bindings is null) return ImmutableArray<AccessRoleBinding>.Empty;
        foreach (var item in bindings)
        {
            var frozen = Freeze(item);
            if (!seen.Add(frozen.Key)) throw new ArgumentException("duplicate_access_role");
            if (list.Count >= MaxBindings) throw new ArgumentException("access_role_limit_exceeded");
            list.Add(frozen);
        }

        return list.OrderBy(x => KeyOrder(x.Key), StringComparer.Ordinal).ToImmutableArray();
    }

    public static string KeyOrder(AccessRoleKey key)
        => string.Concat(key.PlatformId, "\u001F", key.AccountScope, "\u001F",
            key.InstanceId, "\u001F", key.ScopeConversationKey ?? string.Empty, "\u001F", key.NativeUserId);

    public static bool IsGroupScope(string normalizedPlatformId, string? scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey)) return false;
        if (ConversationIdCodec.TryParse(scopeKey, out var parsed))
            return PlatformId.Normalize(parsed.PlatformId) == normalizedPlatformId
                && parsed.Kind is Domain.Conversation.ConversationKind.GroupChat or Domain.Conversation.ConversationKind.ChannelChat;
        return normalizedPlatformId switch
        {
            PlatformId.QqPrivate => scopeKey.StartsWith("group:", StringComparison.Ordinal) && scopeKey.Length > 6,
            PlatformId.QqOfficial => scopeKey.StartsWith("official:group:", StringComparison.Ordinal) && scopeKey.Length > 15,
            PlatformId.Feishu => scopeKey.StartsWith("feishu:group:", StringComparison.Ordinal) && scopeKey.Length > 13,
            PlatformId.Local => scopeKey.StartsWith("local:group:", StringComparison.Ordinal) && scopeKey.Length > 12,
            _ => false,
        };
    }

    private static bool MatchesPlatform(string normalizedPlatformId, string scopeKey)
    {
        if (ConversationIdCodec.TryParse(scopeKey, out var parsed))
            return PlatformId.Normalize(parsed.PlatformId) == normalizedPlatformId;
        return normalizedPlatformId switch
        {
            PlatformId.QqPrivate => scopeKey.StartsWith("group:", StringComparison.Ordinal) || scopeKey.StartsWith("private:", StringComparison.Ordinal),
            PlatformId.QqOfficial => scopeKey.StartsWith("official:group:", StringComparison.Ordinal) || scopeKey.StartsWith("official:private:", StringComparison.Ordinal),
            PlatformId.Feishu => scopeKey.StartsWith("feishu:group:", StringComparison.Ordinal) || scopeKey.StartsWith("feishu:private:", StringComparison.Ordinal),
            PlatformId.Local => scopeKey.StartsWith("local:", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string CheckToken(string? raw, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("invalid_access_role_token");
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLen || HasControl(trimmed) || trimmed.Any(char.IsWhiteSpace))
            throw new ArgumentException("invalid_access_role_token");
        StrictUtf8.GetByteCount(trimmed);
        return trimmed;
    }

    private static bool HasControl(string text) => text.Any(char.IsControl);
}

using System.Collections.Immutable;
using BotAgent.Domain.Platforms;

namespace BotAgent.Domain.Identity;

public enum IdentityResourceKind
{
    Todos = 0,
    Knowledge = 1,
    Preferences = 2
}

public sealed record IdentityEndpointKey(
    string PlatformId,
    string AccountScope,
    string InstanceId,
    string NativeUserId)
{
    public string CanonicalToken()
        => $"{Platforms.PlatformId.Normalize(PlatformId)}|{AccountScope}|{InstanceId}|{NativeUserId}";

    public bool IsValid()
        => ValidPart(PlatformId)
            && !string.IsNullOrWhiteSpace(AccountScope) && AccountScope.Length <= 80 && !AccountScope.Any(char.IsControl)
            && (InstanceId == string.Empty || ValidPart(InstanceId))
            && ValidPart(NativeUserId);

    private static bool ValidPart(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 200
            && value == value.Trim()
            && !value.Any(char.IsControl)
            && !value.Contains('|', StringComparison.Ordinal);
}

public sealed record IdentitySharePreferences(
    bool ShareTodos = false,
    bool ShareKnowledge = false,
    bool SharePreferences = false);

public sealed record IdentityLinkRecord(
    string CanonicalPersonId,
    IdentityEndpointKey Endpoint,
    long LinkedUnix);

public sealed record IdentityLinkState(
    bool Enabled,
    IdentitySharePreferences Preferences,
    ImmutableArray<IdentityLinkRecord> Links,
    string? WorkspaceCanonicalPersonId = null)
{
    public static IdentityLinkState EmptyDisabled()
        => new(false, new IdentitySharePreferences(), ImmutableArray<IdentityLinkRecord>.Empty, null);
}

public sealed record IdentityLinkStatusView(
    bool Enabled,
    IdentitySharePreferences Preferences,
    IReadOnlyList<IdentityEndpointMask> Links,
    string? WorkspaceCanonicalPersonId = null);

public sealed record IdentityEndpointMask(
    string CanonicalPersonId,
    string PlatformId,
    string AccountScope,
    string InstanceId,
    string MaskedUserId,
    long LinkedUnix);

public sealed record IdentityOperationResult(
    bool Ok,
    string ReasonCode,
    string? ChallengeCode = null,
    long? ExpiresAtUnix = null,
    string? CanonicalPersonId = null);

public interface IIdentityLinkStore
{
    IdentityLinkState Load();

    void Save(IdentityLinkState state);
}

/// <summary>
/// ADR-0006 跨平台本人身份关联解析端口：
/// 仅在显式开启且本人双向挑战确认后返回共享 <c>canonical:&lt;id&gt;</c> 作用域；
/// 绝不向 RBAC 角色裁决、审批或聊天历史暴露跨端映射。
/// </summary>
public interface IIdentityLinkResolver
{
    bool IsEnabled { get; }

    IdentityLinkStatusView GetStatus();

    IdentityOperationResult SetEnabled(bool enabled);

    IdentityOperationResult UpdateSharePreferences(IdentitySharePreferences preferences);

    IdentityOperationResult BindWorkspace(string? canonicalPersonId);

    IdentityOperationResult StartChallenge(IdentityEndpointKey source);

    IdentityOperationResult ConfirmChallenge(IdentityEndpointKey target, string challengeCode);

    IdentityOperationResult Unlink(IdentityEndpointKey endpoint);

    string? ResolveSharedOwnerScope(IdentityEndpointKey endpoint, IdentityResourceKind resource);

    string? ResolveWorkspaceSharedScope(IdentityResourceKind resource);
}

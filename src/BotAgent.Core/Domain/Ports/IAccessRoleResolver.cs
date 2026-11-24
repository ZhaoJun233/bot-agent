using System.Globalization;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;

namespace BotAgent.Domain.Ports;

public sealed record SubjectIdentity(
    string PlatformId,
    string AccountScope,
    string InstanceId,
    string NativeUserId,
    long? LegacyNumericUserId = null)
{
    public override string ToString() => "SubjectIdentity";
}

/// <summary>轻量化、可组合的跨平台群/私聊权限判定端口。</summary>
public interface IAccessRoleResolver
{
    WorkspaceRole ResolveRole(
        SubjectIdentity subject,
        string? conversationKey,
        string? legacyAgentAllowedUsers,
        bool isOfficialExplicitPrivateAllowed);

    bool TryResolveExplicitRole(
        SubjectIdentity subject,
        string? conversationKey,
        out WorkspaceRole role);

    bool IsConversationApprover(SubjectIdentity subject, string conversationKey);

    IReadOnlyList<string> ApproverTokensForConversation(
        string platformId,
        string accountScope,
        string instanceId,
        string conversationKey);
}

public static class InboundSubjectIdentity
{
    public static SubjectIdentity Resolve(QqChatMessage msg, PlatformContext? currentContext = null)
    {
        if (msg.Envelope is { } env)
        {
            var d = env.Origin.Driver;
            return new SubjectIdentity(
                Platforms.PlatformId.Normalize(d.PlatformId),
                d.AccountScope,
                d.InstanceId ?? string.Empty,
                env.Message.Sender.NativeUserId,
                msg.UserId > 0 ? msg.UserId : null);
        }

        return ResolveFromChannel(msg.Channel, msg.UserId, null, currentContext);
    }

    public static SubjectIdentity ResolveFromChannel(
        string? channel,
        long numericUserId,
        string? nativeUserId,
        PlatformContext? currentContext = null)
    {
        string platform;
        string account;
        string instance;
        if (currentContext is not null)
        {
            platform = Platforms.PlatformId.Normalize(currentContext.PlatformId);
            account = currentContext.AccountScope ?? AccountScope.Legacy;
            instance = currentContext.InstanceId ?? string.Empty;
        }
        else
        {
            var declared = Channels.Declared(channel);
            platform = declared switch
            {
                Channels.Official => Platforms.PlatformId.QqOfficial,
                Channels.Local => Platforms.PlatformId.Local,
                Channels.Feishu => Platforms.PlatformId.Feishu,
                _ => Platforms.PlatformId.QqPrivate,
            };
            account = platform == Platforms.PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy;
            instance = platform == Platforms.PlatformId.Feishu ? "feishu-main" : string.Empty;
        }

        var native = !string.IsNullOrWhiteSpace(nativeUserId)
            ? nativeUserId!.Trim()
            : numericUserId.ToString(CultureInfo.InvariantCulture);
        return new SubjectIdentity(platform, account, instance, native, numericUserId > 0 ? numericUserId : null);
    }
}

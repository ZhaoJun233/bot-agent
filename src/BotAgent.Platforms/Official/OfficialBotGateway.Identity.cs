using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private volatile bool _identityRetired;

    private bool ApplicationIsCurrent()
    {
        if (!_identityRetired && string.Equals(_applicationId, _settings.OfficialAppId, StringComparison.Ordinal)) return true;
        _identityRetired = true;
        InvalidateMemberRoles();
        InvalidateReferences();
        LastErrorCode = "identity_source_changed";
        return false;
    }

    private bool TryLoadIdentities()
    {
        if (!ApplicationIsCurrent()) return false;
        try
        {
            _ = new OfficialIdentityScope(_applicationId, OfficialIdentityKind.Group);
            _ = _ids.Value.Count;
            return true;
        }
        catch (Exception error) when (IsIdentityFailure(error)) { RejectIdentity(error); return false; }
    }

    private string? ResolveTarget(bool isGroup, long alias)
    {
        if (!TryLoadIdentities()) return null;
        var target = _ids.Value.OriginalOf(alias, TargetScope(isGroup));
        if (target is null) LastErrorCode = "identity_target_mismatch";
        return target;
    }

    private OfficialIdentityScope TargetScope(bool isGroup)
        => new(_applicationId, isGroup ? OfficialIdentityKind.Group : OfficialIdentityKind.C2cUser);

    /// <summary>Preserves existing scoped profile keys after verifying the participant's current app and conversation.</summary>
    public string? ResolveProfileSubject(long alias, ConversationId conversation)
    {
        if (conversation.PlatformId != PlatformId.QqOfficial || conversation.AccountScope != AccountScope.Legacy
            || conversation.Kind is not (ConversationKind.GroupChat or ConversationKind.PrivateChat)
            || !string.IsNullOrEmpty(conversation.ThreadId)
            || !long.TryParse(conversation.NativeTargetId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var targetAlias)) return null;
        var group = conversation.Kind == ConversationKind.GroupChat;
        var target = ResolveTarget(group, targetAlias);
        if (target is null || !group && alias != targetAlias) return null;
        var scope = group ? new OfficialIdentityScope(_applicationId, OfficialIdentityKind.GroupMember, NativeConversation(true, target))
            : TargetScope(false);
        return _ids.Value.OriginalOf(alias, scope);
    }

    private OfficialIdentityKey TargetIdentity(bool isGroup, string nativeId) => new(TargetScope(isGroup), nativeId);

    private ConversationId NativeConversation(bool isGroup, string target)
        => new(PlatformId.QqOfficial, _applicationId, isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, target);

    private OfficialIdentityKey MessageIdentity(bool isGroup, string target, string id, OfficialMessageDirection direction, string? index = null)
        => new(new(_applicationId, OfficialIdentityKind.Message, NativeConversation(isGroup, target), direction), id, index);

    private static string ReadIdentityId(JsonNode? value)
        => value is JsonValue node && node.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id)
            ? id : throw new ArgumentException("identity_missing");

    private static bool IsIdentityFailure(Exception error)
        => error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException;

    private void RejectIdentity(Exception error)
    {
        LastErrorCode = error.Message is "identity_migration_required" or "identity_conflict" or "identity_missing" or "identity_scope_invalid"
            ? error.Message : "identity_store_unavailable";
        Log("官方入站拒绝 reason=" + LastErrorCode);
    }
}

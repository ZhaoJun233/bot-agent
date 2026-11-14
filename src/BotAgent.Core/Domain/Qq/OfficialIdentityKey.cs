using System.Text.Json.Serialization;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;

namespace BotAgent.Domain.Qq;

public enum OfficialIdentityKind { Group, GroupMember, C2cUser, BotSelf, Message }
public enum OfficialMessageDirection { None, Inbound, Outbound }

/// <summary>Permanent official identity scope. Driver generations and display names are not identity.</summary>
public sealed record OfficialIdentityScope
{
    public string AppId { get; }
    public OfficialIdentityKind Kind { get; }
    public ConversationId? Conversation { get; }
    public OfficialMessageDirection Direction { get; }

    public OfficialIdentityScope(string appId, OfficialIdentityKind kind, ConversationId? conversation = null,
        OfficialMessageDirection direction = OfficialMessageDirection.None)
    {
        if (!ValidId(appId) || !Enum.IsDefined(kind) || !Enum.IsDefined(direction))
            throw new ArgumentException("identity_context_invalid");
        var needsConversation = kind is OfficialIdentityKind.GroupMember or OfficialIdentityKind.BotSelf or OfficialIdentityKind.Message;
        if (needsConversation != (conversation is not null)
            || (kind == OfficialIdentityKind.Message) != (direction != OfficialMessageDirection.None))
            throw new ArgumentException("identity_context_invalid");
        if (conversation is not null && (conversation.PlatformId != PlatformId.QqOfficial
            || conversation.AccountScope != appId || !ValidId(conversation.NativeTargetId) || conversation.ThreadId is not null
            || conversation.Kind is not (ConversationKind.GroupChat or ConversationKind.PrivateChat)
            || kind == OfficialIdentityKind.GroupMember && conversation.Kind != ConversationKind.GroupChat))
            throw new ArgumentException("identity_context_mismatch");
        AppId = appId;
        Kind = kind;
        Conversation = conversation;
        Direction = direction;
    }

    internal static bool ValidId(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]) || char.IsControl(value[index])) return false;
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsSurrogatePair(value, index)) return false;
            index++;
        }
        return true;
    }

    public override string ToString() => "OfficialIdentityScope";
}

/// <summary>An opaque, case-sensitive platform identifier within a validated scope.</summary>
public sealed record OfficialIdentityKey
{
    public OfficialIdentityScope Scope { get; }
    public string NativeId { get; }
    public string? MessageIndex { get; }
    public OfficialIdentityKey(OfficialIdentityScope scope, string nativeId) : this(scope, nativeId, null) { }
    [JsonConstructor]
    public OfficialIdentityKey(OfficialIdentityScope scope, string nativeId, string? messageIndex = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!OfficialIdentityScope.ValidId(nativeId)) throw new ArgumentException("identity_missing");
        if (messageIndex is not null && (scope.Kind != OfficialIdentityKind.Message
            || scope.Direction != OfficialMessageDirection.Inbound || messageIndex.Length > 512
            || !OfficialIdentityScope.ValidId(messageIndex))) throw new ArgumentException("identity_message_index_invalid");
        Scope = scope;
        NativeId = nativeId;
        MessageIndex = messageIndex;
    }
    public override string ToString() => "OfficialIdentityKey";
}

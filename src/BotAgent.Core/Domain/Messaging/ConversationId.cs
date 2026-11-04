using BotAgent.Domain.Conversation;

namespace BotAgent.Domain.Messaging;

/// <summary>
/// 平台中立会话标识值对象。原生 id 保持原始大小写与字符串，不转 long。
/// </summary>
public sealed record ConversationId(
    string PlatformId,
    string AccountScope,
    ConversationKind Kind,
    string NativeTargetId,
    string? ThreadId = null);

/// <summary>
/// 平台中立参与者身份值对象。
/// </summary>
public sealed record ParticipantId(
    string PlatformId,
    string AccountScope,
    string NativeUserId);

/// <summary>
/// 平台中立单消息引用。
/// </summary>
public sealed record MessageRef(
    ConversationId Conversation,
    string NativeMessageId);

/// <summary>
/// 归一化入站消息。
/// </summary>
public sealed record InboundMessage(
    MessageRef Ref,
    ParticipantId Sender,
    string SenderName,
    string Text,
    DateTimeOffset Timestamp,
    string? ReplyToMessageId = null,
    bool IsMentioned = false,
    IReadOnlyList<string>? ImageUrls = null);

/// <summary>
/// 归一化出站消息。
/// </summary>
public sealed record OutboundMessage(
    ConversationId Target,
    string Text,
    string? ReplyToMessageId = null,
    byte[]? ImageData = null,
    string? VoiceUrl = null,
    string? ImageDescription = null);

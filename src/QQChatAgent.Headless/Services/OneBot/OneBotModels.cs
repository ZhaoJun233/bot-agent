using System.Text.Json;
using System.Text.Json.Serialization;

namespace QQChatAgent.Services.OneBot;

/// <summary>OneBot v11 消息事件（post_type=message）。</summary>
public sealed class OneBotEventMessage
{
    [JsonPropertyName("post_type")] public string? PostType { get; set; }
    [JsonPropertyName("message_type")] public string? MessageType { get; set; }
    [JsonPropertyName("sub_type")] public string? SubType { get; set; }
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("group_id")] public long GroupId { get; set; }
    [JsonPropertyName("message")] public JsonElement Message { get; set; }
    [JsonPropertyName("raw_message")] public string? RawMessage { get; set; }
    [JsonPropertyName("self_id")] public long SelfId { get; set; }
    [JsonPropertyName("sender")] public OneBotSender? Sender { get; set; }
    [JsonPropertyName("time")] public long Time { get; set; }
}

public sealed class OneBotSender
{
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("card")] public string? Card { get; set; }

    /// <summary>群身份：owner / admin / member（只有群消息才有）。</summary>
    [JsonPropertyName("role")] public string? Role { get; set; }

    /// <summary>群头衔（自定义头衔）。有的协议端在事件里也带，带了就省一次查询。</summary>
    [JsonPropertyName("title")] public string? Title { get; set; }
}

/// <summary>
/// 群成员资料（OneBot v11 的 get_group_member_info）。
/// </summary>
/// <param name="Role">owner / admin / member。</param>
/// <param name="Title">群头衔（自定义头衔，可能是空的）。</param>
public sealed record GroupMemberInfo(
    long UserId,
    long GroupId,
    string? Name,
    string? Card,
    string? Role,
    string? Title,
    int Level = 0)
{
    /// <summary>展示名：优先群名片，其次昵称。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Card) ? (Name ?? UserId.ToString()) : Card!;
}

public sealed class GroupInfo
{
    [JsonPropertyName("group_id")] public long GroupId { get; set; }
    [JsonPropertyName("group_name")] public string? GroupName { get; set; }
}

/// <summary>统一后的 QQ 入站消息（与传输层无关）。</summary>
/// <param name="MentionedSelf">群消息中是否 @ 了机器人自己。</param>
/// <param name="ReplyToMessageId">这条消息是**引用回复**时，被引用那条的消息 id（QQ 的“回复”功能）。
/// 以前 reply 段被直接丢掉，模型只看到一句“我也是”，不知道在回什么 —— 见 handoff-4 §27。</param>
/// <param name="ReplyToPreviewText">有的协议端会在 reply 段里直接带上被引用消息的摘要文本（有就用，省一次查）。</param>
public sealed record QqChatMessage(
    long MessageId,
    bool IsGroup,
    long UserId,
    long GroupId,
    string SenderName,
    string Text,
    DateTimeOffset Time,
    bool MentionedSelf,
    IReadOnlyList<string>? ImageUrls = null,
    IReadOnlyList<QQChatAgent.Services.Music.MusicShare>? MusicShares = null,
    string? SenderRole = null,
    string? SenderTitle = null,
    long? ReplyToMessageId = null,
    string? ReplyToPreviewText = null,
    /// <summary>被引用那条消息的发送者 QQ（协议端在 reply 段里给了才有；有了就不必猜“他是在回我吗”）。</summary>
    long? ReplyToSenderId = null,

    /// <summary>
    /// 来自哪条通道（见 <see cref="QQChatAgent.Services.Qq.Channels"/>）。
    /// 默认私域 —— 官方通道的网关在上报前会把它改成 <c>official</c>，
    /// 上层据此拼会话 key（<see cref="QQChatAgent.Services.Qq.Channels.Key"/>），两套场景的上下文才不会串。
    /// </summary>
    string Channel = QQChatAgent.Services.Qq.Channels.Private);

/// <summary>
/// 戳一戳事件（OneBot v11：post_type=notice）。
/// NapCat/go-cqhttp 都有两种写法（notice_type=notice+sub_type=poke 或 notice_type=poke），解析时两种都收。
/// </summary>
/// <param name="UserId">发起戳的人。</param>
/// <param name="TargetId">被戳的人；<paramref name="IsSelfPoked"/> 为 true 时就是机器人自己。</param>
public sealed record QqPokeEvent(
    bool IsGroup,
    long GroupId,
    long UserId,
    long TargetId,
    bool IsSelfPoked,
    DateTimeOffset Time,

    /// <summary>来自哪条通道（见 <see cref="QQChatAgent.Services.Qq.Channels"/>）。</summary>
    string Channel = QQChatAgent.Services.Qq.Channels.Private);

/// <summary>
/// 撤回事件（OneBot v11：<c>post_type=notice</c> + <c>notice_type=group_recall</c> / <c>friend_recall</c>）。
/// </summary>
/// <param name="UserId">原消息的发送者。</param>
/// <param name="OperatorId">动手撤回的人（群管理可以撤回别人的消息）；0 = 未知或本人撤的。</param>
/// <param name="MessageId">被撤回的那条消息 id —— 用它去会话里把那条标成已撤回。</param>
public sealed record QqRecallEvent(
    bool IsGroup,
    long GroupId,
    long UserId,
    long OperatorId,
    long MessageId,
    DateTimeOffset Time,

    /// <summary>来自哪条通道（见 <see cref="QQChatAgent.Services.Qq.Channels"/>）。</summary>
    string Channel = QQChatAgent.Services.Qq.Channels.Private);

public static class OneBotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 同 WebUiServer.Json：trimmed 发布下必须显式给 resolver，否则第一次序列化就抛异常
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };
}
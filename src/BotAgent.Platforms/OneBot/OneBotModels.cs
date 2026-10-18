using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotAgent.Services.OneBot;

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

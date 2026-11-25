using System.Text.Json.Serialization;
using BotAgent.Domain.Platforms;

namespace BotAgent.Services.Qq;

/// <summary>发一条消息的结果。</summary>
/// <param name="Ok">到底发出去没有。</param>
/// <param name="MessageId">协议端给的消息 id；拿不到时为 0（有的协议端不回 id，不是错误）。</param>
public readonly record struct SendResult(bool Ok, long MessageId = 0)
{
    /// <summary>Optional platform receipt; legacy callers keep their existing bool/id contract.</summary>
    [JsonIgnore]
    public DeliveryResult? Delivery { get; init; }

    /// <summary>Actual confirmed text after platform transformation; null retains the caller's text.</summary>
    [JsonIgnore]
    public string? DeliveredText { get; init; }

    public override string ToString() => $"SendResult {{ Ok = {Ok}, HasMessageId = {MessageId > 0} }}";
}

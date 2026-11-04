namespace BotAgent.Domain.Platforms;

public enum DeliveryStatus
{
    Success,
    PartialSuccess,
    Degraded,
    Rejected,
    Throttled,
    TransientFailure,
    PermanentFailure,
    Unknown,
}

/// <summary>
/// 平台消息投递结果。使用结构化状态码与稳定原因码，不向核心抛原始错误。
/// </summary>
public sealed record DeliveryResult(
    DeliveryStatus Status,
    string? MessageId = null,
    string ReasonCode = "ok",
    string? DegradedFrom = null,
    int RetryAfterSeconds = 0,
    string? Wording = null)
{
    public bool IsSuccess => Status is DeliveryStatus.Success or DeliveryStatus.Degraded;

    public static DeliveryResult Ok(string? messageId = null)
        => new(DeliveryStatus.Success, MessageId: messageId);

    public static DeliveryResult Degraded(string? messageId, string degradedFrom)
        => new(DeliveryStatus.Degraded, MessageId: messageId, ReasonCode: "degraded", DegradedFrom: degradedFrom);

    public static DeliveryResult Rejected(string reasonCode, string? wording = null)
        => new(DeliveryStatus.Rejected, ReasonCode: reasonCode, Wording: wording);

    public static DeliveryResult Throttled(int retryAfterSeconds = 0, string? wording = null)
        => new(DeliveryStatus.Throttled, ReasonCode: "throttled", RetryAfterSeconds: retryAfterSeconds, Wording: wording);

    public static DeliveryResult Transient(string reasonCode, string? wording = null)
        => new(DeliveryStatus.TransientFailure, ReasonCode: reasonCode, Wording: wording);

    public static DeliveryResult Permanent(string reasonCode, string? wording = null)
        => new(DeliveryStatus.PermanentFailure, ReasonCode: reasonCode, Wording: wording);

    public static DeliveryResult Unknown(string reasonCode, string? wording = null)
        => new(DeliveryStatus.Unknown, ReasonCode: reasonCode, Wording: wording);
}

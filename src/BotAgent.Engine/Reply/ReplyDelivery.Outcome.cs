using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

internal sealed record VoiceDeliveryReport(IReadOnlyList<string> SentParts, List<string> FailedParts, DeliveryResult? Delivery = null)
{
    public bool VoiceSent => SentParts.Count > 0;
    public bool StopFurtherSending => ReplyDeliveryResults.StopsFurtherSending(Delivery);
}

internal static class ReplyDeliveryResults
{
    internal static bool Confirmed(SendResult result) => result.Ok && (result.Delivery?.IsSuccess ?? true);

    internal static bool StopsFurtherSending(DeliveryResult? result)
        => result?.Status is DeliveryStatus.Unknown or DeliveryStatus.Throttled;

    internal static DeliveryResult? Merge(DeliveryResult? first, DeliveryResult? next)
    {
        if (first?.Status == DeliveryStatus.Unknown) return first;
        if (next?.Status == DeliveryStatus.Unknown) return next;
        if (next?.Status == DeliveryStatus.Throttled && first?.Status != DeliveryStatus.Throttled) return next;
        if (first is { IsSuccess: false }) return first;
        if (next is { IsSuccess: false }) return next;
        if (first?.Status == DeliveryStatus.Degraded) return first;
        return next ?? first;
    }

    internal static ChatDeliveryReport Finish(ChatDeliveryReport report, bool anyConfirmed, bool allIntendedDelivered,
        params DeliveryResult?[] results)
    {
        DeliveryResult? result = null;
        foreach (var candidate in results) result = Merge(result, candidate);
        if (result?.Status == DeliveryStatus.Unknown)
            return report with { CompleteChatReply = false, Delivery = result with { MessageId = null } };
        if (anyConfirmed && allIntendedDelivered && result is not { IsSuccess: false })
            return report with { Delivery = result ?? DeliveryResult.Ok() };
        var failure = result is { IsSuccess: false } ? result : DeliveryResult.Rejected("delivery_incomplete");
        return report with
        {
            CompleteChatReply = false,
            Delivery = anyConfirmed ? failure with { Status = DeliveryStatus.PartialSuccess, MessageId = null } : failure,
        };
    }
}

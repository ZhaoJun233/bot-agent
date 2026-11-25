using BotAgent.Domain.Platforms;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    [Flags]
    private enum ContentChanges { None = 0, Link = 1, Music = 2, Quote = 4 }

    private static DeliveryResult ApplyContentChanges(DeliveryResult receipt, ContentChanges changes)
    {
        if (!receipt.IsSuccess || changes == ContentChanges.None) return receipt;
        var (reason, from) = changes switch
        {
            ContentChanges.Link => ("link_domain_unverified", "link"),
            ContentChanges.Music => ("music_card_unavailable", "music"),
            ContentChanges.Quote => ("reference_unavailable", "quote"),
            ContentChanges.Link | ContentChanges.Music => ("music_link_degraded", "music,link"),
            ContentChanges.Link | ContentChanges.Quote => ("link_reference_degraded", "link,quote"),
            ContentChanges.Music | ContentChanges.Quote => ("music_reference_degraded", "music,quote"),
            _ => ("music_link_reference_degraded", "music,link,quote"),
        };
        return receipt with { Status = DeliveryStatus.Degraded, ReasonCode = reason, DegradedFrom = from };
    }
}

namespace BotAgent.Domain.Messaging;

/// <summary>Untrusted platform voice metadata; carrying a URL does not authorize fetching it.</summary>
public sealed record InboundVoiceAttachment(string? WavUrl, string? Transcript)
{
    public override string ToString() => "InboundVoiceAttachment";
}

namespace BotAgent.Domain.Messaging;

/// <summary>Untrusted platform voice metadata; carrying a URL or handle does not authorize fetching or command execution.</summary>
public sealed record InboundVoiceAttachment(
    string? WavUrl,
    string? Transcript,
    string? PlatformAudioRef = null,
    int? DurationSeconds = null)
{
    public override string ToString() => "InboundVoiceAttachment";
}

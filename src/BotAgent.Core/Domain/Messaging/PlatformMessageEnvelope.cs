using BotAgent.Domain.Configuration;
using BotAgent.Domain.Platforms;

namespace BotAgent.Domain.Messaging;

/// <summary>Immutable source identity, not an authorization token or an adapter lifetime lease.</summary>
public sealed record PlatformMessageOrigin
{
    public DriverKey Driver { get; }
    public MessageRef? Trigger { get; }

    public PlatformMessageOrigin(DriverKey driver, MessageRef? trigger = null)
    {
        if (string.IsNullOrWhiteSpace(driver.PlatformId) || string.IsNullOrWhiteSpace(driver.AccountScope)
            || driver.InstanceId is null || driver.InstanceId.Length != 0 && string.IsNullOrWhiteSpace(driver.InstanceId))
            throw new ArgumentException("invalid_platform_origin");
        Driver = driver with { PlatformId = PlatformId.Normalize(driver.PlatformId) };
        if (trigger is not null && (trigger.Conversation is null
                || string.IsNullOrWhiteSpace(trigger.NativeMessageId)
                || !Matches(trigger.Conversation.PlatformId, trigger.Conversation.AccountScope)))
            throw new ArgumentException("invalid_platform_trigger");
        Trigger = trigger;
    }

    internal bool Matches(string platform, string scope)
        => !string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(scope)
            && PlatformId.Normalize(platform) == Driver.PlatformId
            && string.Equals(scope, Driver.AccountScope, StringComparison.Ordinal);

    public override string ToString() => "PlatformMessageOrigin";
}

/// <summary>The host creates this from an actual adapter subscription, never from a message body.</summary>
public sealed record PlatformInboundEnvelope
{
    public PlatformMessageOrigin Origin { get; }
    public InboundMessage Message { get; }

    public PlatformInboundEnvelope(PlatformMessageOrigin origin, InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(message);
        if (message.Ref is null || message.Sender is null || origin.Trigger is null
            || origin.Trigger != message.Ref
            || !origin.Matches(message.Sender.PlatformId, message.Sender.AccountScope))
            throw new ArgumentException("invalid_platform_envelope");
        Origin = origin;
        Message = message with { ImageUrls = message.ImageUrls is null
            ? null : Array.AsReadOnly(message.ImageUrls.ToArray()) };
    }

    public override string ToString() => "PlatformInboundEnvelope";
}

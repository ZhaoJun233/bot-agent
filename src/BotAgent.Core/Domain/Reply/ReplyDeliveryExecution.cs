using System.Text.Json.Serialization;
using BotAgent.Domain.Messaging;
using BotAgent.Services.Qq;

namespace BotAgent.Domain.Reply;

/// <summary>A selected reply trigger, not delivery authorization. The original source object is retained.</summary>
public sealed class ReplyDeliveryBinding
{
    [JsonIgnore] public PlatformSourceLease? Source { get; }
    [JsonIgnore] public MessageRef? Trigger { get; }
    [JsonIgnore] public bool IsCurrent => Source is { IsCurrent: true }
        && ReferenceEquals(PlatformSourceExecution.Current, Source);

    internal ReplyDeliveryBinding(PlatformSourceLease? source, MessageRef? trigger)
    {
        if (trigger is not null && (source?.Origin.Trigger is not { } original
            || original.Conversation != trigger.Conversation
            || string.IsNullOrWhiteSpace(trigger.NativeMessageId)))
            throw new ArgumentException("reply_trigger_scope_mismatch");
        Source = source;
        Trigger = trigger;
    }

    public override string ToString() => "ReplyDeliveryBinding";
}

/// <summary>An explicit empty binding prevents borrowing an earlier trigger. Queues still own source capture.</summary>
public static class ReplyDeliveryExecution
{
    private static readonly AsyncLocal<ReplyDeliveryBinding?> Ambient = new();
    public static ReplyDeliveryBinding? Current => Ambient.Value;

    public static IDisposable Enter(PlatformSourceLease? source, MessageRef? trigger)
    {
        var binding = new ReplyDeliveryBinding(source, trigger);
        var previous = Ambient.Value;
        Ambient.Value = binding;
        return new Restore(previous);
    }

    private sealed class Restore(ReplyDeliveryBinding? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}

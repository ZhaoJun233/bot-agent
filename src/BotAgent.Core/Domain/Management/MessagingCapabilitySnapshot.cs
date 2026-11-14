using System.Collections.Immutable;

namespace BotAgent.Domain.Management;

public enum MessagingImplementation { Implemented = 0, Deferred = 1 }
public enum MessagingConditionState { Satisfied = 0, Blocked = 1, Unknown = 2, Unavailable = 3 }

public sealed record MessagingCondition(MessagingConditionState State, string ReasonCode);

/// <summary>Read-only observations; these facts never authorize a send or a management action.</summary>
public sealed record MessagingCapability(
    string Id, MessagingImplementation Implementation, MessagingCondition Policy,
    MessagingCondition ApplicationGrant, MessagingCondition Conversation, MessagingCondition Actor);

public sealed record MessagingCapabilitySnapshot(
    DateTimeOffset ObservedAt, ImmutableArray<MessagingCapability> Items);

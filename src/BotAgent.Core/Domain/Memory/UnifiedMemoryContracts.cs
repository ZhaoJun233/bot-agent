using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Messaging;

namespace BotAgent.Domain.Memory;

/// <summary>Transport-attested identity only; never construct from model/tool arguments or history keys.</summary>
public sealed record UnifiedMemoryContext(
    ParticipantId? Actor, DriverKey Driver, ConversationId Conversation, bool IsReliableIdentity);

/// <summary>Exact destination, including platform, bot account, driver instance and optional thread.</summary>
public sealed record UnifiedMemoryScope(DriverKey Driver, ConversationId Conversation);

public enum UnifiedMemoryFactCategory { ReplyLanguage, ReplyLength, ExplanationStyle }

/// <summary>Conservative, finite preference vocabulary. No arbitrary text, names or inferred traits.</summary>
public sealed record UnifiedMemoryFact(UnifiedMemoryFactCategory Category, string Value);

/// <summary>Only a direct, explicit self-expression may create this request. No model extraction in v1.</summary>
public sealed record UnifiedMemoryRequest(
    UnifiedMemoryFact Fact, ImmutableArray<UnifiedMemoryScope> Targets, bool ExplicitSelfExpression);

/// <summary>The token binds the exact version, value, source and proposed targets for private review.</summary>
public sealed record UnifiedMemoryConfirmation(
    Guid FactId, int Version, Guid Token, bool AllowSourceLocal, bool AcknowledgeGroupAudience);

public sealed record UnifiedMemoryEntry(
    Guid FactId, int Version, ParticipantId Subject, UnifiedMemoryScope Source,
    UnifiedMemoryFact Fact, ImmutableArray<UnifiedMemoryScope> ProposedTargets,
    Guid ConfirmationToken, DateTimeOffset ConfirmationExpiresAt,
    bool Confirmed, bool AllowSourceLocal, ImmutableArray<UnifiedMemoryScope> SharedTargets);

public enum UnifiedMemoryStatus
{
    Success, Disabled, UnreliableIdentity, NotOwnerPrivateChat, InvalidFact, InvalidTarget,
    ExplicitRequestRequired, NotFound, StaleVersion, InvalidConfirmation, Suppressed, StorageFailure
}

public sealed record UnifiedMemoryResult(UnifiedMemoryStatus Status, UnifiedMemoryEntry? Entry = null)
{
    public bool Succeeded => Status == UnifiedMemoryStatus.Success;
}

/// <summary>Opaque issuer nonce; stale or unknown receipts are invalid. Never serialize as authority.</summary>
public sealed record UnifiedMemoryReceipt(Guid Token);
public sealed record UnifiedMemoryRead(
    ImmutableArray<UnifiedMemoryEntry> Entries, UnifiedMemoryReceipt? Receipt);

/// <summary>Body-free event. Consumers must discard pending prompts, model outputs and sends.</summary>
public sealed record UnifiedMemoryInvalidation(long Revision, string Reason);

/// <summary>Permanent source-wide suppression, intentionally containing no fact/body/hash of body.</summary>
public sealed record UnifiedMemorySuppression(ParticipantId Subject, UnifiedMemoryScope Source);

/// <summary>Persistence DTO, not an authorization API. Only the memory module may write it.</summary>
public sealed record UnifiedMemoryState(
    long Revision, bool Enabled, ImmutableArray<UnifiedMemoryEntry> Entries,
    ImmutableArray<UnifiedMemorySuppression> Suppressions)
{
    public static UnifiedMemoryState Empty => new(0, false, [], []);
}

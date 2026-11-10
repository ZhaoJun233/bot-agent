using System.Collections.Immutable;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;

namespace BotAgent.Domain.Ports;

/// <summary>
/// Shared authorization boundary for chat, Agent and controlled extensions (not a process sandbox).
/// Confirm/list/lifecycle operations require reliable subject-owned private chat. Reads are limited
/// to the current subject and exact scope. Never inject Entries without retaining and checking the
/// returned receipt before EACH model call and EACH real send. Invalid receipts fail closed.
/// </summary>
public interface IUnifiedMemory
{
    event Action<UnifiedMemoryInvalidation>? Invalidated;
    bool Enabled { get; }
    UnifiedMemoryResult RequestRemember(UnifiedMemoryContext context, UnifiedMemoryRequest request);
    UnifiedMemoryResult Confirm(UnifiedMemoryContext context, UnifiedMemoryConfirmation confirmation);
    UnifiedMemoryResult Edit(UnifiedMemoryContext context, Guid factId, int expectedVersion, UnifiedMemoryRequest replacement);
    UnifiedMemoryResult Revoke(UnifiedMemoryContext context, Guid factId, UnifiedMemoryScope target);
    UnifiedMemoryResult Forget(UnifiedMemoryContext context, Guid factId);
    UnifiedMemoryResult Reject(UnifiedMemoryContext context, Guid factId);
    UnifiedMemoryResult SetEnabled(bool enabled);
    ImmutableArray<UnifiedMemoryEntry> List(UnifiedMemoryContext context);
    UnifiedMemoryRead Read(UnifiedMemoryContext context, string query, int limit = 3);
    bool ValidateReceipt(UnifiedMemoryReceipt receipt, UnifiedMemoryContext context);
    /// <summary>Validate and synchronously initiate an external operation under the invalidation lock. Never await/block in commit.</summary>
    bool TryUseReceipt(UnifiedMemoryReceipt receipt, UnifiedMemoryContext context, Action commit);
    bool IsLegacyProfileSuppressed(ParticipantId? subject, UnifiedMemoryScope source);
    /// <summary>Gate an existing legacy profile injection, independent of Enabled. Null subject means a conservative scope-wide gate.</summary>
    UnifiedMemoryReceipt? AuthorizeLegacyProfile(UnifiedMemoryContext context, ParticipantId? subject = null);
}

/// <summary>
/// Single-process durable snapshot seam. Save MUST atomically compare revision and commit all state;
/// a failure throws, never publishes a successful partial write. Unknown commit outcomes fail closed.
/// No production history reads, scanning or backfill. Implementations are trusted infrastructure.
/// </summary>
public interface IUnifiedMemoryPersistence
{
    UnifiedMemoryState Load();
    void Save(long expectedRevision, UnifiedMemoryState state);
}

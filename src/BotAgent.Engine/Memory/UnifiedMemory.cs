using System.Collections.Immutable;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Ports;

namespace BotAgent.Engine.Memory;

/// <summary>One durable authorization implementation. All publications follow successful atomic persistence.</summary>
public sealed class UnifiedMemory : IUnifiedMemory
{
    private readonly object _gate = new();
    private readonly IUnifiedMemoryPersistence _persistence;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, Reading> _receipts = new();
    private UnifiedMemoryState _state = UnifiedMemoryState.Empty;
    private bool _faulted;
    private sealed record Reading(long Revision, UnifiedMemoryContext Context, DateTimeOffset ExpiresAt,
        ImmutableArray<(Guid Id, int Version)> Versions, bool Legacy = false, ParticipantId? LegacySubject = null);

    public UnifiedMemory(IUnifiedMemoryPersistence persistence, TimeProvider? timeProvider = null)
    {
        _persistence = persistence;
        _clock = timeProvider ?? TimeProvider.System;
        try
        {
            var state = persistence.Load();
            if (!UnifiedMemoryPolicy.ValidState(state)) throw new InvalidDataException("Invalid unified memory state.");
            _state = state;
        }
        catch { _faulted = true; } // Corruption/unavailable storage never becomes an empty writable authorization store.
    }

    public event Action<UnifiedMemoryInvalidation>? Invalidated;
    public bool Enabled { get { lock (_gate) return !_faulted && _state.Enabled; } }

    public UnifiedMemoryResult RequestRemember(UnifiedMemoryContext context, UnifiedMemoryRequest request) =>
        Change(state => UnifiedMemoryChanges.Request(state, context, request, _clock.GetUtcNow()), "candidate");
    public UnifiedMemoryResult Confirm(UnifiedMemoryContext context, UnifiedMemoryConfirmation confirmation) =>
        Change(state => UnifiedMemoryChanges.Confirm(state, context, confirmation, _clock.GetUtcNow()), "confirmed");
    public UnifiedMemoryResult Edit(UnifiedMemoryContext context, Guid factId, int expectedVersion, UnifiedMemoryRequest replacement) =>
        Change(state => UnifiedMemoryChanges.Edit(state, context, factId, expectedVersion, replacement, _clock.GetUtcNow()), "edited");
    public UnifiedMemoryResult Revoke(UnifiedMemoryContext context, Guid factId, UnifiedMemoryScope target) =>
        Change(state => UnifiedMemoryChanges.Revoke(state, context, factId, target), "revoked");
    public UnifiedMemoryResult Forget(UnifiedMemoryContext context, Guid factId) =>
        Change(state => UnifiedMemoryChanges.Forget(state, context, factId), "forgotten");
    public UnifiedMemoryResult Reject(UnifiedMemoryContext context, Guid factId) =>
        Change(state => UnifiedMemoryChanges.Reject(state, context, factId, _clock.GetUtcNow()), "rejected");
    public UnifiedMemoryResult SetEnabled(bool enabled) =>
        Change(state => state.Enabled == enabled ? new(null, new(UnifiedMemoryStatus.Success))
            : UnifiedMemoryChange.Accept(state with { Enabled = enabled }), enabled ? "enabled" : "disabled");

    public ImmutableArray<UnifiedMemoryEntry> List(UnifiedMemoryContext context)
    {
        lock (_gate)
        {
            if (_faulted || !UnifiedMemoryPolicy.Reliable(context) || context.Conversation.Kind != ConversationKind.PrivateChat) return [];
            // Management remains available while disabled; this method must NEVER feed model context.
            return _state.Entries.Where(entry => UnifiedMemoryPolicy.OwnerPrivate(context, entry)).ToImmutableArray();
        }
    }

    public UnifiedMemoryRead Read(UnifiedMemoryContext context, string query, int limit = 3)
    {
        lock (_gate)
        {
            if (_faulted || !_state.Enabled || !UnifiedMemoryPolicy.Reliable(context) || string.IsNullOrWhiteSpace(query)) return new([], null);
            var entries = _state.Entries.Where(entry => UnifiedMemoryPolicy.CanRead(entry, context)
                && UnifiedMemoryPolicy.Relevant(entry.Fact, query))
                .GroupBy(entry => entry.Fact.Category)
                .Where(group => group.Select(entry => entry.Fact.Value).Distinct().Count() == 1)
                .Select(group => group.First()).Take(Math.Clamp(limit, 0, 3)).ToImmutableArray();
            if (entries.IsEmpty || !HasReceiptCapacity()) return new([], null);
            var receipt = new UnifiedMemoryReceipt(Guid.NewGuid());
            _receipts.Add(receipt.Token, new(_state.Revision, context, _clock.GetUtcNow().AddMinutes(10),
                entries.Select(entry => (entry.FactId, entry.Version)).ToImmutableArray()));
            return new(entries, receipt);
        }
    }

    public bool ValidateReceipt(UnifiedMemoryReceipt receipt, UnifiedMemoryContext context)
    {
        lock (_gate) return ValidReceipt(receipt, context);
    }

    public bool TryUseReceipt(UnifiedMemoryReceipt receipt, UnifiedMemoryContext context, Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            if (!ValidReceipt(receipt, context)) return false;
            commit(); // Submission linearizes here. Never wait for an asynchronous transport under this lock.
            return true;
        }
    }

    // Do not retire tickets already consumed by active work under capacity pressure.
    // Reclaim only expired tickets; otherwise refuse to expose new memory.
    private bool HasReceiptCapacity()
    {
        if (_receipts.Count < 1024) return true;
        var now = _clock.GetUtcNow();
        foreach (var token in _receipts.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _receipts.Remove(token);
        return _receipts.Count < 1024;
    }

    private bool ValidReceipt(UnifiedMemoryReceipt receipt, UnifiedMemoryContext context) =>
        !_faulted && receipt is not null && UnifiedMemoryPolicy.Reliable(context)
        && _receipts.TryGetValue(receipt.Token, out var reading) && reading.Revision == _state.Revision
        && reading.Context == context && reading.ExpiresAt > _clock.GetUtcNow()
        && (reading.Legacy ? LegacyAllowed(context, reading.LegacySubject) : _state.Enabled
            && reading.Versions.All(version => _state.Entries.Any(entry => entry.FactId == version.Id
                && entry.Version == version.Version && UnifiedMemoryPolicy.CanRead(entry, context))));

    public UnifiedMemoryReceipt? AuthorizeLegacyProfile(UnifiedMemoryContext context, ParticipantId? subject = null)
    {
        lock (_gate)
        {
            if (_faulted || !UnifiedMemoryPolicy.Reliable(context) || !LegacyAllowed(context, subject)) return null;
            if (!HasReceiptCapacity()) return null;
            var receipt = new UnifiedMemoryReceipt(Guid.NewGuid());
            _receipts.Add(receipt.Token, new(_state.Revision, context, _clock.GetUtcNow().AddMinutes(10), [], true, subject));
            return receipt;
        }
    }

    private bool LegacyAllowed(UnifiedMemoryContext context, ParticipantId? subject)
    {
        var scope = new UnifiedMemoryScope(context.Driver, context.Conversation);
        if (subject is null) return !_state.Suppressions.Any(marker => marker.Source == scope);
        return UnifiedMemoryPolicy.ValidParticipant(subject) && subject.PlatformId == context.Conversation.PlatformId
            && subject.AccountScope == context.Conversation.AccountScope
            && !_state.Suppressions.Contains(new(subject, scope));
    }

    public bool IsLegacyProfileSuppressed(ParticipantId? subject, UnifiedMemoryScope source)
    {
        lock (_gate)
        {
            // Unknown scope cannot prove an old profile is safe: skip that injection rather than guessing.
            return _faulted || subject is null || !UnifiedMemoryPolicy.ValidParticipant(subject)
                || !UnifiedMemoryPolicy.ValidScope(source) || subject.PlatformId != source.Conversation.PlatformId
                || subject.AccountScope != source.Conversation.AccountScope || _state.Suppressions.Contains(new(subject, source));
        }
    }

    private UnifiedMemoryResult Change(Func<UnifiedMemoryState, UnifiedMemoryChange> apply, string reason)
    {
        UnifiedMemoryResult result;
        UnifiedMemoryInvalidation? notification = null;
        lock (_gate)
        {
            if (_faulted) return new(UnifiedMemoryStatus.StorageFailure);
            var change = apply(_state);
            result = change.Result;
            if (change.State is not null)
            {
                try
                {
                    var next = change.State with { Revision = checked(_state.Revision + 1) };
                    if (!UnifiedMemoryPolicy.ValidState(next)) throw new InvalidDataException("Invalid unified memory transition.");
                    _persistence.Save(_state.Revision, next);
                    _state = next;
                    notification = new(next.Revision, reason);
                }
                catch
                {
                    _faulted = true;
                    result = new(UnifiedMemoryStatus.StorageFailure);
                    notification = new(_state.Revision, "storage-failure");
                }
                _receipts.Clear();
            }
        }
        if (notification is not null) Notify(notification);
        return result;
    }

    private void Notify(UnifiedMemoryInvalidation notification)
    {
        foreach (var handler in Invalidated?.GetInvocationList() ?? [])
        {
            try { ((Action<UnifiedMemoryInvalidation>)handler)(notification); }
            catch { /* A consumer cannot undo a committed invalidation; synchronous receipt checks stay authoritative. */ }
        }
    }
}

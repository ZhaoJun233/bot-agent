// Responsibilities: per-turn immutable contracts plus the single atomic admission/commit state machine.
// Kept together (500+ lines) because splitting the shared invariants would expose mutable turn internals.
// Scheduling, model calls, tools, source storage, cancellation and delivery deliberately live elsewhere.
namespace BotAgent.Services.Reply;

/// <summary>Immutable per-turn policy. Numeric changes affect only subsequently created turns.</summary>
public sealed record ReplyPacingOptions(TimeSpan GroupContinuationWait, TimeSpan PrivateContinuationWait,
    int GroupInterjectionProbability, TimeSpan GroupInterjectionCooldown)
{
    public static ReplyPacingOptions Default { get; } = new(TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(1.5), 20, TimeSpan.FromSeconds(30));

    internal void Validate()
    {
        if (GroupContinuationWait < TimeSpan.Zero || GroupContinuationWait > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(GroupContinuationWait));
        if (PrivateContinuationWait < TimeSpan.Zero || PrivateContinuationWait > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(PrivateContinuationWait));
        if (GroupInterjectionProbability is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(GroupInterjectionProbability));
        if (GroupInterjectionCooldown < TimeSpan.Zero || GroupInterjectionCooldown > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(GroupInterjectionCooldown));
    }
}

public enum ReplyPacingObservationKind { Created, Updated, Terminal, RejectedCapacity, Disabled }
public enum ReplyPacingDecisionKind { Ready, Wait, Retry, Terminal }
public enum ReplyPacingReason
{
    Accepted, Continuation, QuietWindow, SoftDraft, RetryLatest, Busy, ExplicitPriority,
    ProbabilityRejected, Cooldown, DuplicateOpportunity, Capacity, Ineligible, Preempted,
    Completed, NotSent, Disabled, LegacyHandoff, Stopped, StaleGeneration, SoftDraftOnly, AlreadySending
}

public readonly record struct ReplyPacingDecision(ReplyPacingDecisionKind Kind, TimeSpan Delay,
    ReplyPacingReason Reason, bool DiscardDraft = false);
public sealed record ReplyPacingObservation(ReplyPacingTurn? Turn, ReplyPacingObservationKind Kind,
    ReplyPacingReason Reason)
{
    public bool Created => Kind == ReplyPacingObservationKind.Created;
}

/// <summary>Opaque ownership handle. Getters take the coordinator's lock; no source data is diagnostic.</summary>
public sealed class ReplyPacingTurn
{
    internal ReplyPacingTurn(ReplyPacingCoordinator owner, ReplyPacingCoordinator.TurnState state)
    { Owner = owner; State = state; }
    internal ReplyPacingCoordinator Owner { get; }
    internal ReplyPacingCoordinator.TurnState State { get; }
    public long Id => State.Id;
    public object? OpaqueTicket => Owner.Read(this).Ticket;
    public bool Explicit => Owner.Read(this).IsExplicit;
    public bool IsTerminal => Owner.Read(this).Terminal;
    public override string ToString() => nameof(ReplyPacingTurn);
}

public sealed record ReplyPacingSnapshot(ReplyPacingTurn Turn, string Key, long SpeakerId, long Sequence,
    long? MessageId, bool IsExplicit, bool IsGroup, bool IsProactive, long Version, ReplyPacingOptions Options,
    object? Ticket, bool EffectsCommitted, bool FirstSendCommitted, bool Terminal)
{
    public override string ToString() => nameof(ReplyPacingSnapshot);
}

public sealed class ReplyPacingGeneration
{
    internal ReplyPacingGeneration(ReplyPacingSnapshot snapshot, bool soft)
    { Snapshot = snapshot; DiscardableSoftDraft = soft; }
    internal ReplyPacingSnapshot Snapshot { get; }
    public ReplyPacingTurn Turn => Snapshot.Turn;
    public long Version => Snapshot.Version;
    public long Sequence => Snapshot.Sequence;
    public long? MessageId => Snapshot.MessageId;
    public long? TriggerMessageId => MessageId;
    public bool IsExplicit => Snapshot.IsExplicit;
    public bool IsGroup => Snapshot.IsGroup;
    public bool IsProactive => Snapshot.IsProactive;
    public ReplyPacingOptions Options => Snapshot.Options;
    public object? Ticket => Snapshot.Ticket;
    public bool DiscardableSoftDraft { get; }
    public override string ToString() => nameof(ReplyPacingGeneration);
}

/// <summary>
/// Pure pacing state machine: one lock protects all turns, admission and commit boundaries.
/// Injected clock/random delegates must be fast, nonblocking and must not re-enter this instance.
/// Callers own cancellation, timers, queues, source tickets and delivery. Never wait under a queue lock:
/// EndAttempt releases the conversation reservation without discarding a retained generation.
/// Successful commit methods reserve permission, not IO success; Complete records whole-chat success.
/// </summary>
public sealed class ReplyPacingCoordinator : IDisposable
{
    public const int DefaultCapacity = 256;
    public static readonly TimeSpan SoftDraftWait = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SchedulingDelay = TimeSpan.FromMilliseconds(50);
    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;
    private readonly Func<int> _randomPercent;
    private readonly int _capacity;
    private readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<ReplyPacingReason, long> _counters = new();
    private long _nextId;
    private int _activeCount;
    private bool _enabled = true;
    private bool _stopped;

    private sealed class Conversation
    {
        internal readonly Dictionary<long, ReplyPacingTurn> Latest = new();
        internal readonly Dictionary<long, long> SpeakerSequences = new();
        internal readonly List<ReplyPacingTurn> Active = new();
        internal long OpportunitySequence = long.MinValue;
        internal ReplyPacingTurn? LastOpportunity;
        internal DateTime CooldownUntil;
        internal ReplyPacingGeneration? Busy;
    }

    internal sealed class TurnState
    {
        internal required long Id;
        internal required string Key;
        internal required long SpeakerId;
        internal required ReplyPacingOptions Options;
        internal required DateTime FirstInput;
        internal DateTime LastInput;
        internal long Sequence;
        internal long QuietSequence;
        internal long? MessageId;
        internal long Version = 1;
        internal object? Ticket;
        internal bool Explicit, Group, Proactive, AcceptedResult, Admitted, SoftAttempted, RetryUsed, ImmediateRetry;
        internal bool RequireQuiet, Effects, FirstSend, Terminal, HandedOff, Completed;
        internal ReplyPacingReason Reason = ReplyPacingReason.Accepted;
        internal ReplyPacingGeneration? Generation;
        internal ReplyPacingTurn? Predecessor;
    }

    public ReplyPacingCoordinator(Func<DateTime> utcNow, Func<int>? randomPercent = null,
        int capacity = DefaultCapacity)
    {
        if (capacity < 1 || capacity > DefaultCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _randomPercent = randomPercent ?? (() => Random.Shared.Next(100));
        _capacity = capacity;
    }

    public ReplyPacingObservation Observe(string key, long speakerId, long sequence, long? messageId,
        bool isExplicit, bool isGroup, ReplyPacingOptions options, object? ticket = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lock (_gate)
        {
            if (_stopped || !_enabled) return Unavailable();
            var now = _utcNow();
            var conversation = GetConversation(key);
            if (conversation.SpeakerSequences.TryGetValue(speakerId, out var seen) && sequence <= seen)
            {
                if (sequence == seen && isExplicit && conversation.Latest.TryGetValue(speakerId, out var current) &&
                    current.State.Sequence == sequence && !current.State.Explicit && !current.State.Proactive &&
                    !current.State.Effects && !current.State.FirstSend &&
                    (!current.State.Terminal || IsUnsentRefusal(current.State)))
                {
                    var state = current.State;
                    if (state.Terminal)
                    {
                        var promoted = Create(conversation, key, speakerId, sequence, messageId ?? state.MessageId,
                            true, state.Group, false, state.Options, ticket ?? state.Ticket, null, state.LastInput);
                        if (promoted.Turn is { } accepted) accepted.State.FirstInput = state.FirstInput;
                        return promoted;
                    }
                    state.Explicit = true;
                    state.Version++;
                    state.MessageId = messageId ?? state.MessageId;
                    state.Ticket = ticket ?? state.Ticket;
                    PreemptOrdinary(conversation, current);
                    return Observation(current, ReplyPacingObservationKind.Updated, ReplyPacingReason.Continuation);
                }
                return Observation(null, ReplyPacingObservationKind.Terminal, ReplyPacingReason.DuplicateOpportunity);
            }
            Prune(conversation, now);
            conversation.SpeakerSequences[speakerId] = sequence;
            conversation.OpportunitySequence = Math.Max(conversation.OpportunitySequence, sequence);
            isExplicit |= !isGroup;
            conversation.Latest.TryGetValue(speakerId, out var previous);
            var prior = previous?.State;
            // Once effects start, preserve the accepted task; only its same-human quiet clock moves.
            foreach (var active in conversation.Active)
                if (!active.State.AcceptedResult && active.State.SpeakerId == speakerId && active.State.Effects && !active.State.FirstSend)
                    ExtendQuiet(active.State, sequence, now);
            if (prior is { Terminal: false, Effects: false, FirstSend: false })
            {
                ExtendQuiet(prior, sequence, now);
                prior.Sequence = sequence;
                prior.MessageId = messageId;
                prior.Ticket = ticket;
                prior.Explicit |= isExplicit;
                prior.Version++;
                conversation.LastOpportunity = previous;
                if (prior.Explicit) PreemptOrdinary(conversation, previous!);
                return Observation(previous, ReplyPacingObservationKind.Updated, ReplyPacingReason.Continuation);
            }
            if (prior is { Terminal: true } && !isExplicit && now < prior.LastInput + QuietWait(prior))
            {
                ExtendQuiet(prior, sequence, now);
                prior.Sequence = sequence;
                conversation.LastOpportunity = previous;
                return Observation(previous, ReplyPacingObservationKind.Terminal, prior.Reason);
            }
            var predecessor = prior is { Terminal: false } && (prior.Effects || prior.FirstSend) ? previous : null;
            isExplicit |= predecessor?.State.Explicit == true;
            return Create(conversation, key, speakerId, sequence, messageId, isExplicit, isGroup,
                false, options, ticket, predecessor, now);
        }
    }

    /// <summary>
    /// Update an existing authorized human opportunity, never admit a new candidate. Effects keep
    /// their accepted snapshot; quiet-only updates leave admission available for a later followup.
    /// </summary>
    public ReplyPacingObservation ObserveContinuation(string key, long speakerId, long sequence,
        long? messageId, object? ticket = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            if (_stopped || !_enabled) return Unavailable();
            if (!_conversations.TryGetValue(key, out var conversation))
                return Observation(null, ReplyPacingObservationKind.Terminal, ReplyPacingReason.Ineligible);
            var now = _utcNow();
            if (conversation.SpeakerSequences.TryGetValue(speakerId, out var seen) && sequence <= seen)
                return Observation(null, ReplyPacingObservationKind.Terminal, ReplyPacingReason.DuplicateOpportunity);
            ReplyPacingTurn? effects = null;
            foreach (var active in conversation.Active)
            {
                var state = active.State;
                if (!state.AcceptedResult && !state.Proactive && state.SpeakerId == speakerId && state.Effects && !state.FirstSend)
                {
                    ExtendQuiet(state, sequence, now);
                    effects = active;
                }
            }
            if (conversation.Latest.TryGetValue(speakerId, out var previous))
            {
                var prior = previous.State;
                if (!prior.AcceptedResult && !prior.Proactive && !prior.Effects && !prior.FirstSend &&
                    (!prior.Terminal || now < prior.LastInput + QuietWait(prior)))
                {
                    ExtendQuiet(prior, sequence, now);
                    prior.Sequence = sequence;
                    prior.MessageId = messageId;
                    prior.Ticket = ticket;
                    prior.Version++;
                    conversation.SpeakerSequences[speakerId] = sequence;
                    conversation.OpportunitySequence = Math.Max(conversation.OpportunitySequence, sequence);
                    conversation.LastOpportunity = previous;
                    return Observation(previous, prior.Terminal ? ReplyPacingObservationKind.Terminal : ReplyPacingObservationKind.Updated,
                        prior.Terminal ? prior.Reason : ReplyPacingReason.Continuation);
                }
            }
            return effects is null
                ? Observation(null, ReplyPacingObservationKind.Terminal, ReplyPacingReason.Ineligible)
                : Observation(effects, ReplyPacingObservationKind.Updated, ReplyPacingReason.Continuation);
        }
    }

    /// <summary>
    /// Own an already accepted task result independently of human ingress. The caller owns result
    /// identity/deduplication; this never advances human watermarks or absorbs later human input.
    /// </summary>
    public ReplyPacingObservation ObserveAcceptedResult(string key, long contextSequence, bool isGroup,
        ReplyPacingOptions options, object? ticket = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lock (_gate)
        {
            if (_stopped || !_enabled) return Unavailable();
            return Create(GetConversation(key), key, 0, contextSequence, null, true, isGroup,
                false, options, ticket, null, _utcNow(), acceptedResult: true);
        }
    }

    /// <summary>A human sequence already considered by any ingress cannot be revived by a timer.</summary>
    public ReplyPacingObservation ObserveProactive(string key, long humanSequence, ReplyPacingOptions options,
        object? ticket = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lock (_gate)
        {
            if (_stopped || !_enabled) return Unavailable();
            var now = _utcNow();
            var conversation = GetConversation(key);
            Prune(conversation, now);
            if (humanSequence <= conversation.OpportunitySequence)
                return Observation(conversation.LastOpportunity, ReplyPacingObservationKind.Terminal, ReplyPacingReason.DuplicateOpportunity);
            conversation.OpportunitySequence = humanSequence;
            return Create(conversation, key, 0, humanSequence, null, false, true, true, options, ticket, null, now);
        }
    }

    public ReplyPacingSnapshot Read(ReplyPacingTurn turn)
    {
        lock (_gate)
        {
            var state = State(turn);
            return new(turn, state.Key, state.SpeakerId, state.Sequence, state.MessageId, state.Explicit,
                state.Group, state.Proactive, state.Version, state.Options, state.Ticket, state.Effects,
                state.FirstSend, state.Terminal);
        }
    }

    public IReadOnlyList<ReplyPacingSnapshot> ActiveTurns(string key)
    {
        lock (_gate) return _conversations.TryGetValue(key, out var conversation)
            ? conversation.Active.Select(Read).ToArray() : Array.Empty<ReplyPacingSnapshot>();
    }

    public IReadOnlyDictionary<ReplyPacingReason, long> GetCounters()
    {
        lock (_gate) return new Dictionary<ReplyPacingReason, long>(_counters);
    }

    public ReplyPacingDecision GetReadiness(ReplyPacingTurn turn)
    {
        lock (_gate) return Readiness(turn, _utcNow());
    }

    public bool TryBegin(ReplyPacingTurn turn, out ReplyPacingGeneration? generation)
    {
        lock (_gate)
        {
            generation = null;
            var now = _utcNow();
            var decision = Readiness(turn, now);
            if (decision.Kind != ReplyPacingDecisionKind.Ready) return false;
            var state = State(turn);
            if (!Admit(turn, now)) return false;
            var soft = !IsQuiet(state, now);
            generation = new(Read(turn), soft);
            state.Generation = generation;
            state.SoftAttempted |= soft;
            state.ImmediateRetry = false;
            GetConversation(state.Key).Busy = generation;
            return true;
        }
    }

    /// <summary>Release generation/queue ownership on yield; its immutable draft remains checkable.</summary>
    public void EndAttempt(ReplyPacingGeneration generation)
    {
        lock (_gate) Release(generation);
    }

    public ReplyPacingDecision CheckFreshness(ReplyPacingGeneration generation)
    {
        lock (_gate) return Freshness(generation, _utcNow());
    }

    public ReplyPacingDecision TryCommitEffects(ReplyPacingGeneration generation)
    {
        lock (_gate)
        {
            if (generation.DiscardableSoftDraft) return Terminal(ReplyPacingReason.SoftDraftOnly);
            var decision = Freshness(generation, _utcNow());
            if (decision.Kind != ReplyPacingDecisionKind.Ready) return decision;
            var state = State(generation.Turn);
            if (state.Effects) return Terminal(ReplyPacingReason.StaleGeneration);
            state.Effects = true;
            GetConversation(state.Key).Busy = generation;
            return decision;
        }
    }

    /// <summary>Exactly-once reservation immediately before the first actual send; never resend after failure.</summary>
    public ReplyPacingDecision TryCommitFirstSend(ReplyPacingGeneration generation)
    {
        lock (_gate)
        {
            var decision = Freshness(generation, _utcNow());
            if (decision.Kind != ReplyPacingDecisionKind.Ready) return decision;
            var state = State(generation.Turn);
            state.FirstSend = true;
            GetConversation(state.Key).Busy = generation;
            return decision;
        }
    }

    /// <param name="fullChatSend">True ONLY for nonempty chat output after every segment succeeds.</param>
    public void Complete(ReplyPacingTurn turn, bool fullChatSend)
    {
        lock (_gate)
        {
            var state = State(turn);
            if (state.Completed || (state.Terminal && !state.HandedOff)) return;
            state.Completed = true;
            if (fullChatSend && state.Group && (state.FirstSend || state.HandedOff) && !_stopped)
            {
                var conversation = GetConversation(state.Key);
                var until = _utcNow() + state.Options.GroupInterjectionCooldown;
                if (until > conversation.CooldownUntil) conversation.CooldownUntil = until;
            }
            Finish(turn, fullChatSend ? ReplyPacingReason.Completed : ReplyPacingReason.NotSent);
        }
    }

    public void Reject(ReplyPacingTurn turn, ReplyPacingReason reason = ReplyPacingReason.Ineligible)
    {
        lock (_gate)
        {
            var state = State(turn);
            if (!state.Terminal && !state.Effects && !state.FirstSend) Finish(turn, reason);
        }
    }

    /// <summary>Hands explicit unsent work to legacy once; old pumps become terminal. Irreversible work stays.</summary>
    public IReadOnlyList<ReplyPacingSnapshot> Disable()
    {
        lock (_gate)
        {
            _enabled = false;
            var handoffs = new List<ReplyPacingSnapshot>();
            foreach (var conversation in _conversations.Values)
                foreach (var turn in conversation.Active.ToArray())
                {
                    var state = turn.State;
                    if (state.Effects || state.FirstSend) continue;
                    state.HandedOff = state.Explicit;
                    Finish(turn, state.Explicit ? ReplyPacingReason.LegacyHandoff : ReplyPacingReason.Disabled);
                    if (state.HandedOff) handoffs.Add(Read(turn));
                }
            return handoffs;
        }
    }

    public void Enable() { lock (_gate) { if (!_stopped) _enabled = true; } }
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _enabled = false;
            foreach (var conversation in _conversations.Values)
                foreach (var turn in conversation.Active.ToArray()) Finish(turn, ReplyPacingReason.Stopped);
        }
    }
    public void Dispose() => Stop();

    private ReplyPacingObservation Create(Conversation conversation, string key, long speakerId, long sequence,
        long? messageId, bool isExplicit, bool isGroup, bool proactive, ReplyPacingOptions options, object? ticket,
        ReplyPacingTurn? predecessor, DateTime now, bool acceptedResult = false)
    {
        if (isExplicit) PreemptOrdinary(conversation, null);
        if (isExplicit && _activeCount >= _capacity)
        {
            var expendable = conversation.Active.FirstOrDefault(t => !t.State.Explicit && !t.State.Effects && !t.State.FirstSend);
            if (expendable != null) Finish(expendable, ReplyPacingReason.Preempted);
        }
        if (_activeCount >= _capacity)
        {
            if (!acceptedResult) conversation.LastOpportunity = null;
            return Observation(null, isExplicit ? ReplyPacingObservationKind.RejectedCapacity : ReplyPacingObservationKind.Terminal,
                ReplyPacingReason.Capacity);
        }
        var state = new TurnState { Id = ++_nextId, Key = key, SpeakerId = speakerId, Sequence = sequence, QuietSequence = sequence,
            MessageId = messageId, Explicit = isExplicit, Group = isGroup, Proactive = proactive, Options = options,
            FirstInput = now, LastInput = now, Ticket = ticket, Predecessor = predecessor, AcceptedResult = acceptedResult };
        var turn = new ReplyPacingTurn(this, state);
        conversation.Active.Add(turn);
        _activeCount++;
        if (!acceptedResult)
        {
            conversation.Latest[speakerId] = turn;
            conversation.LastOpportunity = turn;
        }
        if (isExplicit) PreemptOrdinary(conversation, turn);
        // Refusal at arrival is terminal: the quiet deadline cannot postpone admission past cooldown.
        if (!isExplicit && isGroup && now < conversation.CooldownUntil)
        {
            Finish(turn, ReplyPacingReason.Cooldown);
            return Observation(turn, ReplyPacingObservationKind.Terminal, ReplyPacingReason.Cooldown);
        }
        return Observation(turn, ReplyPacingObservationKind.Created, ReplyPacingReason.Accepted);
    }

    private ReplyPacingDecision Readiness(ReplyPacingTurn turn, DateTime now)
    {
        var state = State(turn);
        if (state.Terminal) return Terminal(state.Reason);
        if (state.FirstSend) return Terminal(ReplyPacingReason.AlreadySending);
        if (state.Generation != null) return Wait(SchedulingDelay, ReplyPacingReason.Busy);
        var conversation = GetConversation(state.Key);
        if (!state.Explicit && state.Group && now < conversation.CooldownUntil)
        { Finish(turn, ReplyPacingReason.Cooldown); return Terminal(ReplyPacingReason.Cooldown); }
        if (state.Predecessor is { } predecessor && !predecessor.State.Completed)
            return Wait(SchedulingDelay, ReplyPacingReason.Busy);
        if (conversation.Busy != null) return Wait(SchedulingDelay, ReplyPacingReason.Busy);
        if (!state.Explicit && conversation.Active.Any(t => t.State.Explicit && !t.State.FirstSend &&
            t.State.Generation == null && IsQuiet(t.State, now) && (t.State.Predecessor == null || t.State.Predecessor.State.Completed)))
            return Wait(SchedulingDelay, ReplyPacingReason.ExplicitPriority);
        var quiet = state.LastInput + QuietWait(state);
        if (now >= quiet) return Ready();
        if (state.ImmediateRetry) return Ready(ReplyPacingReason.RetryLatest);
        var soft = state.FirstInput + SoftDraftWait;
        if (!state.RequireQuiet && !state.SoftAttempted && now >= soft) return Ready(ReplyPacingReason.SoftDraft);
        var due = !state.RequireQuiet && !state.SoftAttempted && soft < quiet ? soft : quiet;
        return Wait(due - now, ReplyPacingReason.QuietWindow);
    }

    private ReplyPacingDecision Freshness(ReplyPacingGeneration generation, DateTime now)
    {
        var turn = generation.Turn;
        var state = State(turn);
        if (state.Terminal || !ReferenceEquals(state.Generation, generation))
        { Release(generation); return Terminal(state.Terminal ? state.Reason : ReplyPacingReason.StaleGeneration); }
        if (state.FirstSend) return Terminal(ReplyPacingReason.AlreadySending);
        if (!state.Effects && state.Version != generation.Version)
        {
            state.Generation = null;
            Release(generation);
            if (!state.RetryUsed)
            {
                state.RetryUsed = true;
                state.ImmediateRetry = true;
                Count(ReplyPacingReason.RetryLatest);
                return new(ReplyPacingDecisionKind.Retry, TimeSpan.Zero, ReplyPacingReason.RetryLatest, true);
            }
            state.RequireQuiet = true;
            return new(ReplyPacingDecisionKind.Wait, RemainingQuiet(state, now), ReplyPacingReason.QuietWindow, true);
        }
        if (!IsQuiet(state, now))
        { Release(generation); return Wait(RemainingQuiet(state, now), ReplyPacingReason.QuietWindow); }
        var conversation = GetConversation(state.Key);
        if (!state.Explicit && !state.Effects && state.Group && now < conversation.CooldownUntil)
        { Finish(turn, ReplyPacingReason.Cooldown); return Terminal(ReplyPacingReason.Cooldown); }
        if (conversation.Busy != null && !ReferenceEquals(conversation.Busy, generation))
            return Wait(SchedulingDelay, ReplyPacingReason.Busy);
        return Ready();
    }

    private bool Admit(ReplyPacingTurn turn, DateTime now)
    {
        var state = turn.State;
        if (state.Admitted) return true;
        if (!state.Explicit && state.Group)
        {
            if (now < GetConversation(state.Key).CooldownUntil)
            { Finish(turn, ReplyPacingReason.Cooldown); return false; }
            var draw = _randomPercent();
            if (draw is < 0 or >= 100) throw new InvalidOperationException("Random percent must be in [0,100).");
            if (draw >= state.Options.GroupInterjectionProbability)
            { Finish(turn, ReplyPacingReason.ProbabilityRejected); return false; }
        }
        state.Admitted = true;
        return true;
    }

    private void PreemptOrdinary(Conversation conversation, ReplyPacingTurn? except)
    {
        foreach (var other in conversation.Active.ToArray())
            if (!ReferenceEquals(other, except) && !other.State.Explicit && other.State.Generation != null &&
                !other.State.Effects && !other.State.FirstSend) Finish(other, ReplyPacingReason.Preempted);
    }

    private void Finish(ReplyPacingTurn turn, ReplyPacingReason reason)
    {
        var state = turn.State;
        state.Terminal = true;
        state.Reason = reason;
        if (state.Generation != null) Release(state.Generation);
        state.Generation = null;
        if (GetConversation(state.Key).Active.Remove(turn)) _activeCount--;
        Count(reason);
    }

    private void Release(ReplyPacingGeneration generation)
    {
        var state = State(generation.Turn);
        var conversation = GetConversation(state.Key);
        if (ReferenceEquals(conversation.Busy, generation)) conversation.Busy = null;
    }

    private TurnState State(ReplyPacingTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (!ReferenceEquals(turn.Owner, this)) throw new ArgumentException("Turn belongs to another coordinator.", nameof(turn));
        return turn.State;
    }

    private Conversation GetConversation(string key)
    {
        if (!_conversations.TryGetValue(key, out var conversation))
            _conversations.Add(key, conversation = new());
        return conversation;
    }

    private static void Prune(Conversation conversation, DateTime now)
    {
        foreach (var pair in conversation.Latest.ToArray())
            // Latest refusals remain addressable for same-sequence asynchronous quote confirmation.
            if (pair.Value.State.Terminal && !IsUnsentRefusal(pair.Value.State) && now >= pair.Value.State.LastInput + QuietWait(pair.Value.State))
                conversation.Latest.Remove(pair.Key);
    }

    private ReplyPacingObservation Unavailable() => Observation(null, ReplyPacingObservationKind.Disabled,
        _stopped ? ReplyPacingReason.Stopped : ReplyPacingReason.Disabled);
    private ReplyPacingObservation Observation(ReplyPacingTurn? turn, ReplyPacingObservationKind kind, ReplyPacingReason reason)
    { Count(reason); return new(turn, kind, reason); }
    private void Count(ReplyPacingReason reason) => _counters[reason] = _counters.GetValueOrDefault(reason) + 1;
    private static void ExtendQuiet(TurnState state, long sequence, DateTime now)
    {
        if (sequence <= state.QuietSequence) return;
        state.QuietSequence = sequence;
        state.LastInput = now;
    }
    private static bool IsUnsentRefusal(TurnState state) => state.Terminal &&
        state.Reason is ReplyPacingReason.ProbabilityRejected or ReplyPacingReason.Cooldown;
    private static TimeSpan QuietWait(TurnState state) => state.AcceptedResult ? TimeSpan.Zero :
        state.Group ? state.Options.GroupContinuationWait : state.Options.PrivateContinuationWait;
    private static bool IsQuiet(TurnState state, DateTime now) => now >= state.LastInput + QuietWait(state);
    private static TimeSpan RemainingQuiet(TurnState state, DateTime now) => IsQuiet(state, now) ? TimeSpan.Zero : state.LastInput + QuietWait(state) - now;
    private static ReplyPacingDecision Ready(ReplyPacingReason reason = ReplyPacingReason.Accepted) => new(ReplyPacingDecisionKind.Ready, TimeSpan.Zero, reason);
    private static ReplyPacingDecision Wait(TimeSpan delay, ReplyPacingReason reason) => new(ReplyPacingDecisionKind.Wait, delay, reason);
    private static ReplyPacingDecision Terminal(ReplyPacingReason reason) => new(ReplyPacingDecisionKind.Terminal, TimeSpan.Zero, reason, true);
}

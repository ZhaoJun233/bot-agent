using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Engine.Runtime;
using BotAgent.Services.Qq;
using BotAgent.Services.Conversations;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    // Lock order: pacing -> pending. No external callbacks or waits under either lock.
    private readonly object _pacingSync = new();
    private readonly ReplyPacingCoordinator _pacingCoordinator = new(() => EngineClock.UtcNow.UtcDateTime);
    private readonly Dictionary<long, PacedReplyWork> _pacedWorks = new();
    private readonly Dictionary<string, DateTime> _pacingBusyUntil = new(StringComparer.Ordinal);
    private bool _pacingEnabled;
    private bool _pacingStopped;

    private sealed record PacedReplyOwner(BotConversation Conversation, PlatformSourceLease? Source,
        object Epoch, AppSettings Snapshot);

    private sealed class PacedReplyWork(ReplyPacingTurn turn, PacedReplyOwner owner, PendingReplyIntent intent)
    {
        internal readonly ReplyPacingTurn Turn = turn;
        internal readonly PendingReplyIntent Intent = intent;
        internal readonly PacedReplyOwner Owner = owner;
        internal readonly ReplyPacingPromptState PromptState = new();
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            owner.Source?.Lifetime ?? default);
        internal bool LegacyHandoff;
        internal PacedReplyAttempt? Attempt;
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { } // A detached cancellation may race worker cleanup.
        }
    }

    private PlatformSourceLease? ResolveReplySource(BotConversation conversation)
        => PlatformSourceExecution.Current ?? (_source as IPlatformSourceRouter)?.CaptureForConversation(conversation.SourceKey);

    private bool IsPacedOwnerCurrent(PacedReplyOwner owner)
    {
        if (_pacingStopped || _hooks.IsDisposed() || !_settings.AiModeEnabled
            || !IsReplyEpochCurrent(owner.Conversation, owner.Epoch)
            || owner.Source is { IsCurrent: false }) return false;
        if (_source is IPlatformSourceRouter router
            && (owner.Source is null || !ReferenceEquals(owner.Source.Owner, router))) return false;
        var (isGroup, target) = owner.Conversation.Target;
        if (!(owner.Source is null ? _whitelist.AllowsKey(owner.Conversation.SourceKey)
            : _whitelist.AllowsSource(owner.Source.Context, isGroup, target))) return false;
        var policy = owner.Source is { } source ? _platformPolicies.Resolve(source.Context)
            : _platformPolicies.ResolveForChannel(Channels.ChannelOf(owner.Conversation.SourceKey));
        return policy.Registered && policy.Connected && policy.Enabled && policy.ChatEnabled;
    }

    private PacedReplyOwner CapturePacedOwner(BotConversation conversation, AppSettings? snapshot = null, object? expectedEpoch = null)
        => new(conversation, ResolveReplySource(conversation), expectedEpoch ?? CaptureReplyEpoch(conversation), snapshot ?? _settings.Snapshot());

    private static ReplyPacingOptions PacingOptions(AppSettings snapshot) => new(
        TimeSpan.FromSeconds(snapshot.GroupContinuationWaitSeconds),
        TimeSpan.FromSeconds(snapshot.PrivateContinuationWaitSeconds),
        snapshot.GroupInterjectionProbability, TimeSpan.FromSeconds(snapshot.GroupInterjectionCooldownSeconds));

    private void ObservePacedReply(BotConversation conversation, long? triggerMessageId, bool proactive, bool directInWindow, object epoch)
    {
        // Deferred ingress must retain its original epoch across policy refresh and Stop.
        var owner = CapturePacedOwner(conversation, expectedEpoch: epoch);
        if (!IsPacedOwnerCurrent(owner)) return;
        var messages = conversation.Messages;
        var message = triggerMessageId is long id
            ? messages.LastOrDefault(m => m.Role == MessageRole.Peer && m.QqMessageId == id)
            : messages.LastOrDefault(m => m.Role == MessageRole.Peer);
        if (message?.SenderId is not long speakerId) return; // Never manufacture a human sequence for timer/recall work.
        PacedDispatch dispatch;
        lock (_pacingSync)
        {
            if (!_pacingEnabled || !IsPacedOwnerCurrent(owner)) return;
            var observation = proactive
                ? _pacingCoordinator.ObserveProactive(conversation.SourceKey, message.Seq, PacingOptions(owner.Snapshot), owner)
                : _pacingCoordinator.Observe(conversation.SourceKey, speakerId, message.Seq, message.QqMessageId,
                    directInWindow || message.DirectToBot, conversation.Kind == ConversationKind.GroupChat,
                    PacingOptions(owner.Snapshot), owner);
            dispatch = TrackPacedObservation(observation, owner);
        }
        DispatchPacedWork(dispatch);
    }

    private void ObservePacedContinuation(BotConversation conversation, ChatMessage message)
    {
        var owner = CapturePacedOwner(conversation);
        if (!IsPacedOwnerCurrent(owner) || message.SenderId is not long speakerId) return;
        PacedDispatch dispatch;
        lock (_pacingSync)
        {
            if (!_pacingEnabled || !IsPacedOwnerCurrent(owner)) return;
            var observation = _pacingCoordinator.ObserveContinuation(conversation.SourceKey, speakerId,
                message.Seq, message.QqMessageId, owner);
            dispatch = TrackPacedObservation(observation, owner);
        }
        DispatchPacedWork(dispatch);
    }

    private void RequestAcceptedTaskReply(BotConversation conversation, AppSettings snapshot)
    {
        RefreshPacingPolicy();
        var owner = CapturePacedOwner(conversation, snapshot);
        if (!IsPacedOwnerCurrent(owner)) return;
        if (!_settings.ReplyPacingEnabled)
        {
            using var sourceScope = PlatformSourceExecution.Enter(owner.Source);
            EnqueueReplyInEpoch(conversation, null, false, true, owner.Epoch, PendingReplyIntent.AcceptedResult);
            return;
        }
        PacedDispatch dispatch;
        lock (_pacingSync)
        {
            if (!_pacingEnabled || !IsPacedOwnerCurrent(owner)) return;
            var observation = _pacingCoordinator.ObserveAcceptedResult(conversation.SourceKey, conversation.NextSeq,
                conversation.Kind == ConversationKind.GroupChat, PacingOptions(snapshot), owner);
            dispatch = TrackPacedObservation(observation, owner, PendingReplyIntent.AcceptedResult);
        }
        DispatchPacedWork(dispatch);
    }

    private sealed record PacedDispatch(PacedReplyWork? Created, PacedReplyWork[] Cancelled, PacedReplyOwner? Busy);

    // Called in the same ingress critical section as Observe: first-input ownership cannot be overtaken.
    private PacedDispatch TrackPacedObservation(ReplyPacingObservation observation, PacedReplyOwner owner,
        PendingReplyIntent intent = PendingReplyIntent.Explicit)
    {
        PacedReplyWork? created = null;
        PacedReplyWork[] cancelled;
        bool notifyBusy = false;
        lock (_pacingSync)
        {
            if (observation.Turn is { IsTerminal: false } turn && !_pacedWorks.ContainsKey(turn.Id))
            {
                // The first owner is immutable even when later inputs update the coordinator ticket.
                if (_pacingEnabled && IsPacedOwnerCurrent(owner))
                {
                    created = new(turn, owner, intent);
                    _pacedWorks.Add(turn.Id, created);
                }
                else _pacingCoordinator.Reject(turn);
            }
            cancelled = _pacedWorks.Values.Where(w => w.Turn.IsTerminal).ToArray();
            if (observation.Kind == ReplyPacingObservationKind.RejectedCapacity)
            {
                var now = EngineClock.UtcNow.UtcDateTime;
                var key = owner.Conversation.SourceKey;
                if (!_pacingBusyUntil.TryGetValue(key, out var until) || now >= until)
                {
                    _pacingBusyUntil[key] = now + TimeSpan.FromSeconds(30);
                    notifyBusy = true;
                }
            }
        }
        return new(created, cancelled, notifyBusy ? owner : null);
    }

    private void DispatchPacedWork(PacedDispatch dispatch)
    {
        foreach (var work in dispatch.Cancelled) work.Cancel();
        if (dispatch.Created is not null) _ = RunPacedWorkAsync(dispatch.Created);
        if (dispatch.Busy is not null) _ = NotifyPacedBusyAsync(dispatch.Busy);
    }

    private async Task NotifyPacedBusyAsync(PacedReplyOwner owner)
    {
        try
        {
            using var sourceScope = PlatformSourceExecution.Enter(owner.Source);
            if (IsPacedOwnerCurrent(owner))
                await _plain.SendPlainAsync(owner.Conversation, "当前请求较多，这条请求未受理，请稍后重试。");
        }
        catch (Exception) { _hooks.Log("reply_pacing_busy_notice_failed"); }
    }

    public void RefreshPacingPolicy()
    {
        if (_hooks.IsDisposed() || !_settings.AiModeEnabled)
        {
            DropPacedPending(); // Stop/revocation is not a legacy handoff.
            lock (_pacingSync)
            {
                _pacingCoordinator.Disable();
                _pacingEnabled = false;
            }
            return;
        }
        PacedReplyWork[] cancelled = [];
        LegacyPacingTransition? legacyTransition = null;
        lock (_pacingSync)
        {
            if (_pacingStopped || _pacingEnabled == _settings.ReplyPacingEnabled) return;
            _pacingEnabled = _settings.ReplyPacingEnabled;
            if (_pacingEnabled)
            {
                _pacingCoordinator.Enable();
                legacyTransition = RetireLegacyForPacing();
            }
            else
            {
                var handoffs = _pacingCoordinator.Disable().Select(s => s.Turn.Id).ToHashSet();
                foreach (var work in _pacedWorks.Values)
                    if (handoffs.Contains(work.Turn.Id)) work.LegacyHandoff = true;
                cancelled = _pacedWorks.Values.Where(w => w.Turn.IsTerminal).ToArray();
            }
        }
        if (legacyTransition is not null) DispatchLegacyTransition(legacyTransition);
        foreach (var work in cancelled) work.Cancel();
    }

    private void DropPacedPending(Func<BotConversation, bool>? predicate = null)
    {
        PacedReplyWork[] cancelled;
        lock (_pacingSync) cancelled = RetirePacedPendingLocked(predicate);
        foreach (var work in cancelled) work.Cancel();
    }

    private PacedReplyWork[] RetirePacedPendingLocked(Func<BotConversation, bool>? predicate)
    {
        var cancelled = _pacedWorks.Values.Where(w => predicate is null || predicate(w.Owner.Conversation)).ToArray();
        foreach (var work in cancelled)
        {
            work.LegacyHandoff = false;
            RetireReplyEpoch(work.Owner.Conversation);
            _pacingCoordinator.Reject(work.Turn, ReplyPacingReason.Stopped);
        }
        return cancelled;
    }

    private void StopPacing()
    {
        PacedReplyWork[] cancelled;
        lock (_pacingSync)
        {
            _pacingStopped = true;
            _pacingEnabled = false;
            _pacingCoordinator.Stop();
            cancelled = _pacedWorks.Values.ToArray();
            foreach (var work in cancelled) work.LegacyHandoff = false;
        }
        foreach (var work in cancelled) work.Cancel();
    }
}

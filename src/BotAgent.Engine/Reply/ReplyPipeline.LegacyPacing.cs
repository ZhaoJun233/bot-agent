using BotAgent.Domain.Reply;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Services.Conversations;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    private readonly HashSet<LegacyReplyWork> _legacyReplyWorks = new();

    private static PendingReplyIntent LegacyReplyIntent(BotConversation conversation, long? triggerMessageId, bool proactive, bool direct)
    {
        if (proactive) return PendingReplyIntent.Ordinary;
        if (direct) return PendingReplyIntent.Explicit;
        if (triggerMessageId is not long id) return PendingReplyIntent.Ordinary;
        var message = conversation.Messages.LastOrDefault(m => m.Role == MessageRole.Peer && m.QqMessageId == id);
        return message is not null && (message.DirectToBot || conversation.Kind != ConversationKind.GroupChat)
            ? PendingReplyIntent.Explicit : PendingReplyIntent.Ordinary;
    }

    private sealed record LegacyReplyRequest(PacedReplyOwner Owner, PendingReply Pending);
    private sealed record LegacyPacingTransition(LegacyReplyWork[] Cancelled, LegacyReplyRequest[] Handoffs);

    // Registration and retirement share _pacingSync with the final irreversible boundary.
    // Thus enable either wins before effects/send, or retains the already-committed result.
    private sealed class LegacyReplyWork(ReplyPipeline pipeline, LegacyReplyRequest request) : IReplyCommitGuard
    {
        internal LegacyReplyRequest Request { get; } = request;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal bool Irreversible;
        internal bool Retired;
        internal bool Handoff;

        public Task BeforeSendAsync() => BeforeEffectAsync();
        public Task BeforeEffectAsync() { Check(commit: true); return Task.CompletedTask; }
        public Task BeforePrepareAsync() { Check(commit: false); return Task.CompletedTask; }
        public bool TryBeforeSend() { Check(commit: true); return true; }
        internal void Check(bool commit = false)
        {
            lock (pipeline._pacingSync)
            {
                if (Retired || !pipeline.IsPacedOwnerCurrent(Request.Owner))
                    throw new ReplyPacingInterruptedException();
                if (commit) Irreversible = true;
            }
        }
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    // Called under pacing, then pending/deferred. No cancellation callbacks execute here.
    private LegacyPacingTransition RetireLegacyForPacing()
    {
        var cancelled = _legacyReplyWorks.Where(work => !work.Irreversible && !work.Retired).ToArray();
        foreach (var work in cancelled)
        {
            work.Retired = true;
            work.Handoff = work.Request.Pending.Intent != PendingReplyIntent.Ordinary;
        }
        var handoffs = new List<LegacyReplyRequest>();
        lock (_pendingGate)
        {
            foreach (var (key, queue) in _pendingReplies)
            {
                if (!_pendingConversations.TryGetValue(key, out var conversation)) continue;
                while (queue.TryDequeue(out var pending))
                {
                    if (pending.Intent == PendingReplyIntent.Ordinary) continue;
                    var owner = new PacedReplyOwner(conversation, pending.Source, CaptureReplyEpoch(conversation), _settings.Snapshot());
                    handoffs.Add(new(owner, pending));
                }
            }
            _pendingReplies.Clear();
            _pendingConversations.Clear();
        }
        lock (_deferredGate)
        {
            foreach (var item in _deferredTriggers.Values)
            {
                var id = item.TriggerId > 0 ? (long?)item.TriggerId : null;
                var intent = LegacyReplyIntent(item.Conversation, id, false, item.Direct);
                var pending = new PendingReply(id, false, item.Direct, item.Source, intent);
                if (pending.Intent == PendingReplyIntent.Ordinary) continue;
                handoffs.Add(new(new(item.Conversation, item.Source, item.Epoch, _settings.Snapshot()), pending));
            }
            _deferredTriggers.Clear();
        }
        return new(cancelled, handoffs.ToArray());
    }

    private void DispatchLegacyTransition(LegacyPacingTransition transition)
    {
        foreach (var work in transition.Cancelled) work.Cancel();
        foreach (var request in transition.Handoffs) HandoffLegacyReply(request);
    }

    // The original source and conversation epoch travel with a handoff, including fast off/on/off.
    private void HandoffLegacyReply(LegacyReplyRequest request)
    {
        if (request.Pending.Intent == PendingReplyIntent.Ordinary) return;
        var owner = request.Owner;
        PacedDispatch? dispatch = null;
        lock (_pacingSync)
        {
            if (!IsPacedOwnerCurrent(owner)) return;
            if (_pacingEnabled)
            {
                ReplyPacingObservation observation;
                if (request.Pending.Intent == PendingReplyIntent.AcceptedResult)
                    observation = _pacingCoordinator.ObserveAcceptedResult(owner.Conversation.SourceKey, owner.Conversation.NextSeq,
                        owner.Conversation.Kind == ConversationKind.GroupChat, PacingOptions(owner.Snapshot), owner);
                else
                {
                    var message = request.Pending.TriggerMessageId is { } id
                        ? owner.Conversation.Messages.LastOrDefault(m => m.Role == MessageRole.Peer && m.QqMessageId == id)
                        : owner.Conversation.Messages.LastOrDefault(m => m.Role == MessageRole.Peer);
                    if (message?.SenderId is not long speaker) return;
                    observation = _pacingCoordinator.Observe(owner.Conversation.SourceKey, speaker, message.Seq, message.QqMessageId, true,
                        owner.Conversation.Kind == ConversationKind.GroupChat, PacingOptions(owner.Snapshot), owner);
                }
                dispatch = TrackPacedObservation(observation, owner, request.Pending.Intent);
            }
        }
        if (dispatch is not null) DispatchPacedWork(dispatch);
        else
        {
            using var sourceScope = PlatformSourceExecution.Enter(owner.Source);
            EnqueueReplyInEpoch(owner.Conversation, request.Pending.TriggerMessageId, request.Pending.Proactive, true, owner.Epoch, request.Pending.Intent);
        }
    }

    private void FinishLegacyReply(LegacyReplyWork work)
    {
        bool handoff;
        lock (_pacingSync)
        {
            _legacyReplyWorks.Remove(work);
            handoff = work.Handoff;
            work.Handoff = false;
        }
        work.Cancellation.Dispose();
        if (handoff) HandoffLegacyReply(work.Request);
    }
}

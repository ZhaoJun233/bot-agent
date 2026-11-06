using BotAgent.Domain.Reply;
using BotAgent.Engine.Runtime;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    private sealed class PacedReplyAttempt(ReplyPipeline pipeline, PacedReplyWork work,
        ReplyPacingGeneration generation, PacedResources resources) : IPacedReplyAttempt, IReplyCommitGuard
    {
        private PacedResources? _resources = resources;
        private bool _effectsCommitted;
        private bool _sendCommitted;
        internal bool RetryLatest { get; private set; }
        internal bool CompleteChatReply { get; private set; }
        public AppSettings Snapshot => work.Owner.Snapshot;
        public bool IsExplicit => generation.IsExplicit;
        public bool IsSoftDraft => generation.DiscardableSoftDraft;
        public long Sequence => generation.Sequence;
        public ReplyPacingPromptState PromptState => work.PromptState;
        public Task ValidateDraftAsync() => AwaitBoundaryAsync(CommitKind.Validate);
        public Task BeforeEffectAsync() => AwaitBoundaryAsync(CommitKind.Effect);
        public Task BeforePrepareAsync() => AwaitBoundaryAsync(CommitKind.Validate);
        public Task BeforeSendAsync() => AwaitBoundaryAsync(CommitKind.Send);

        public void RecordDelivery(ChatDeliveryReport report) => CompleteChatReply = report.CompleteChatReply;

        public bool TryBeforeSend()
        {
            lock (pipeline._pacingSync)
            {
                EnsureOwner();
                if (_sendCommitted) return true;
                var decision = pipeline._pacingCoordinator.CheckFreshness(generation);
                ThrowIfDiscarded(decision);
                if (decision.Kind != ReplyPacingDecisionKind.Ready || _resources is null)
                {
                    ReleaseResources();
                    return false; // The platform releases its send gate, then retries BeforePrepareAsync.
                }
                decision = pipeline._pacingCoordinator.TryCommitFirstSend(generation);
                ThrowIfDiscarded(decision);
                _sendCommitted = decision.Kind == ReplyPacingDecisionKind.Ready;
                return _sendCommitted;
            }
        }

        private enum CommitKind { Validate, Effect, Send }

        private async Task AwaitBoundaryAsync(CommitKind kind)
        {
            var cancellation = work.Cancellation.Token;
            while (true)
            {
                ReplyPacingDecision decision;
                bool needsResources;
                lock (pipeline._pacingSync)
                {
                    EnsureOwner();
                    // Later segments retain the first-send lease; only lifecycle/permission checks stay live.
                    if (_sendCommitted) return;
                    decision = pipeline._pacingCoordinator.CheckFreshness(generation);
                    ThrowIfDiscarded(decision);
                    needsResources = decision.Kind == ReplyPacingDecisionKind.Ready && _resources is null;
                    if (decision.Kind == ReplyPacingDecisionKind.Ready && !needsResources)
                    {
                        decision = kind switch
                        {
                            CommitKind.Effect when !_effectsCommitted => pipeline._pacingCoordinator.TryCommitEffects(generation),
                            CommitKind.Send when !_sendCommitted => pipeline._pacingCoordinator.TryCommitFirstSend(generation),
                            _ => decision,
                        };
                        ThrowIfDiscarded(decision);
                        if (decision.Kind == ReplyPacingDecisionKind.Ready)
                        {
                            if (kind == CommitKind.Effect) _effectsCommitted = true;
                            if (kind == CommitKind.Send) _sendCommitted = true;
                            return;
                        }
                    }
                    if (!needsResources) ReleaseResources();
                }
                if (needsResources)
                {
                    var acquired = await pipeline.AcquirePacedResourcesAsync(work, cancellation);
                    lock (pipeline._pacingSync) _resources = acquired;
                    // Source, quiet, sequence, priority and ownership are all checked again after acquiring.
                    continue;
                }
                await pipeline.DelayPacingAsync(decision, cancellation);
            }
        }

        private void EnsureOwner()
        {
            work.Cancellation.Token.ThrowIfCancellationRequested();
            if (!pipeline.IsPacedOwnerCurrent(work.Owner)) throw new ReplyPacingInterruptedException();
        }

        private void ThrowIfDiscarded(ReplyPacingDecision decision)
        {
            if (decision.Kind == ReplyPacingDecisionKind.Retry || decision.DiscardDraft)
            {
                RetryLatest = true;
                ReleaseResources();
                throw new ReplyPacingInterruptedException();
            }
            if (decision.Kind == ReplyPacingDecisionKind.Terminal)
            {
                ReleaseResources();
                throw new ReplyPacingInterruptedException();
            }
        }

        internal void ReleaseResources()
        {
            lock (pipeline._pacingSync)
            {
                pipeline._pacingCoordinator.EndAttempt(generation);
                _resources?.Dispose();
                _resources = null;
            }
        }
    }
}

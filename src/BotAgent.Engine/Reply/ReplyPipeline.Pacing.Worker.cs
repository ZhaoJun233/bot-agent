using BotAgent.Domain.Reply;
using BotAgent.Engine.Runtime;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    private static readonly TimeSpan PacingPoll = TimeSpan.FromMilliseconds(50);

    private async Task RunPacedWorkAsync(PacedReplyWork work)
    {
        await Task.Yield(); // Never run model code inline on the ingress or policy-update call stack.
        using var sourceScope = PlatformSourceExecution.Enter(work.Owner.Source);
        var cancellation = work.Cancellation.Token;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                ReplyPacingDecision decision;
                lock (_pacingSync)
                {
                    if (!IsPacedOwnerCurrent(work.Owner)) return;
                    decision = _pacingCoordinator.GetReadiness(work.Turn);
                }
                if (decision.Kind == ReplyPacingDecisionKind.Terminal) return;
                if (decision.Kind != ReplyPacingDecisionKind.Ready)
                {
                    await DelayPacingAsync(decision, cancellation);
                    continue;
                }

                using var resources = await AcquirePacedResourcesAsync(work, cancellation);
                ReplyPacingGeneration? generation;
                lock (_pacingSync)
                {
                    if (!IsPacedOwnerCurrent(work.Owner)) return;
                    _pacingCoordinator.TryBegin(work.Turn, out generation);
                }
                if (generation is null) continue; // Quiet/priority changed while waiting for a permit.
                var attempt = new PacedReplyAttempt(this, work, generation, resources);
                work.Attempt = attempt;
                try
                {
                    using var boundary = ReplyCommitBoundary.Enter(attempt);
                    await GenerateReplyAsync(work.Owner.Conversation, generation.MessageId,
                        _pacingCoordinator.Read(work.Turn).IsProactive, cancellation, attempt);
                }
                catch (ReplyPacingInterruptedException) { }
                finally
                {
                    attempt.ReleaseResources();
                    work.Attempt = null;
                }
                if (attempt.RetryLatest) continue;
                lock (_pacingSync) _pacingCoordinator.Complete(work.Turn, attempt.CompleteChatReply);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { _hooks.Log("reply_pacing_failed:" + ex.GetType().Name); }
        finally
        {
            work.Attempt?.ReleaseResources();
            bool handoff;
            lock (_pacingSync)
            {
                _pacedWorks.Remove(work.Turn.Id);
                handoff = work.LegacyHandoff && IsPacedOwnerCurrent(work.Owner);
                if (!work.Turn.IsTerminal) _pacingCoordinator.Complete(work.Turn, false);
            }
            // Only after all paced resources are released. Bypass RequestReply's opt-in intercept once.
            if (handoff)
                EnqueueReplyInEpoch(work.Owner.Conversation, _pacingCoordinator.Read(work.Turn).MessageId, false, true, work.Owner.Epoch, work.Intent);
            work.Cancellation.Dispose();
        }
    }

    private async Task DelayPacingAsync(ReplyPacingDecision decision, CancellationToken cancellation)
    {
        var delay = decision.Delay;
        // Bounded delay observes stop/policy/source changes even for clocks without external wakeups.
        if (delay <= TimeSpan.Zero || delay > PacingPoll) delay = PacingPoll;
        await EngineClock.Delay(delay, cancellation);
    }

    private async Task<PacedResources> AcquirePacedResourcesAsync(PacedReplyWork work, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!IsPacedOwnerCurrent(work.Owner)) throw new ReplyPacingInterruptedException();
            // Never occupy this conversation while waiting for the shared model permit.
            var lease = _replyGate.Reserve();
            bool acquired = false;
            try
            {
                await lease.WaitAsync(cancellation);
                lock (_pendingGate)
                {
                    if (IsReplyEpochCurrent(work.Owner.Conversation, work.Owner.Epoch)
                        && _inFlight.TryAdd(work.Owner.Conversation.SourceKey, 0))
                    {
                        acquired = true;
                        _activeConversations[work.Owner.Conversation.SourceKey] = (work.Owner.Conversation, work.Owner.Epoch);
                    }
                }
                if (acquired) return new(this, work.Owner.Conversation.SourceKey, lease);
            }
            finally { if (!acquired) lease.Dispose(); }
            await EngineClock.Delay(PacingPoll, cancellation);
        }
    }

    private sealed class PacedResources(ReplyPipeline pipeline, string key, ResizableReplyGate.Lease lease) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lease.Dispose();
            lock (pipeline._pendingGate)
            {
                pipeline._activeConversations.TryRemove(key, out _);
                pipeline._inFlight.TryRemove(key, out _);
            }
            // Legacy queues may be waiting behind the same conversation during a mode transition.
            _ = pipeline.DrainReplyQueueAsync();
        }
    }
}

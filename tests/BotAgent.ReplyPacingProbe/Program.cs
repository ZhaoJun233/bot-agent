using BotAgent.Services.Reply;

var tests = new (string Name, Action Run)[]
{
    ("A01-A02 same-human quiet windows and private 1.5 seconds", QuietWindows),
    ("A02 ready speakers and explicit priority", ReadyPriority),
    ("A03 monotone probability and one lottery", Probability),
    ("A03-A04 rejection tombstone and explicit inheritance", Tombstones),
    ("A04 proactive dedup and zero percent", Proactive),
    ("A05 cooldown starts after all segments and extends", Cooldown),
    ("A05 unsuccessful sends and private isolation", FailedSends),
    ("A06 arrivals during cooldown never become debts", CooldownArrival),
    ("A06 queued ordinary work cannot bypass later cooldown", QueuedCooldown),
    ("A07 soft draft and exactly one immediate recomputation", SoftDraft),
    ("A08 pre-send freshness and generation identity", BeforeSend),
    ("A08 effects retain result and wait only same human", Effects),
    ("A09 sending followup inherits but post-completion does not", SendingFollowup),
    ("A11 preemption and bounded capacity", Preemption),
    ("A12 immutable option and source snapshots", Snapshots),
    ("A13 disable transfers explicit ownership exactly once", DisableHandoff),
    ("A13 irreversible work keeps its policy snapshot", DisableIrreversible),
    ("A14 stop and dispose fence every callback", Stop),
    ("A14 simultaneous and out-of-order observations", ConcurrentIngress),
    ("A11 queued ordinary cannot crowd out explicit requests", CapacityPriority),
    ("A06 retained draft checks cooldown at actual send", RetainedCooldown),
    ("A13 disabled normal task results remain deliverable", DisabledNormalEffects),
    ("A15 diagnostic shapes and input validation", Diagnostics),
    ("Accepted results isolate human and proactive admission", AcceptedResultIdentity),
    ("Accepted results bypass refusal and extend cooldown", AcceptedResultCooldown),
    ("Accepted result lifecycle retains ownership", AcceptedResultLifecycle),
    ("Late explicit confirmation promotes without reroll", LateExplicitPromotion),
    ("Late explicit confirmation cannot revive older or sent turns", LateExplicitFences),
    ("Continuation-only ingress never admits candidates", ContinuationAdmission),
    ("Continuation-only ingress extends rejected tombstones", ContinuationRefusal),
    ("Continuation-only effects retain snapshot and followup admission", ContinuationEffects),
    ("Continuation-only ingress preserves preempted soft tombstones", () => ContinuationSoftTerminal(ReplyPacingReason.Preempted)),
    ("Continuation-only ingress preserves unsent soft tombstones", () => ContinuationSoftTerminal(ReplyPacingReason.NotSent)),
    ("Continuation-only ingress preserves ineligible soft tombstones", () => ContinuationSoftTerminal(ReplyPacingReason.Ineligible))
};
var failures = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"Reply pacing probe: {tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;

static void QuietWindows()
{
    using var h = new Harness();
    var first = h.Observe();
    h.At(1); Same(first, h.Observe());
    h.At(2); var other = h.Observe(speaker: 2);
    h.At(3); Equal(1d, h.Pacing.GetReadiness(first).Delay.TotalSeconds);
    h.At(4); Kind(h.Pacing.GetReadiness(first), ReplyPacingDecisionKind.Ready);
    Equal(1d, h.Pacing.GetReadiness(other).Delay.TotalSeconds);
    var gen = h.Begin(first);
    Equal(2L, gen.Sequence);
    Equal(1, h.Draws);
    h.Pacing.EndAttempt(gen);
    h.At(5); var next = h.Begin(other);
    Kind(h.Pacing.TryCommitFirstSend(next), ReplyPacingDecisionKind.Ready);
    using var p = new Harness();
    var direct = p.Observe(group: false);
    p.At(1.49); Kind(p.Pacing.GetReadiness(direct), ReplyPacingDecisionKind.Wait);
    p.At(1.5); Check(p.Begin(direct).IsExplicit, "private must be explicit");
    Equal(0, p.Draws);
}

static void ReadyPriority()
{
    using var h = new Harness();
    var continuing = h.Observe(explicitRequest: true);
    var ready = h.Observe(speaker: 2, explicitRequest: true);
    h.At(2); h.Observe(explicitRequest: true);
    h.At(3); Kind(h.Pacing.GetReadiness(continuing), ReplyPacingDecisionKind.Wait);
    var gen = h.Begin(ready);
    Check(!h.Pacing.TryBegin(continuing, out _), "one generation per conversation");
    h.Pacing.EndAttempt(gen);
    Kind(h.Pacing.TryCommitFirstSend(gen), ReplyPacingDecisionKind.Ready);
    h.Pacing.Complete(ready, false);
    h.At(5); h.Begin(continuing);
    using var priority = new Harness();
    var normal = priority.Observe();
    var explicitTurn = priority.Observe(speaker: 2, explicitRequest: true);
    priority.At(3);
    Equal(ReplyPacingReason.ExplicitPriority, priority.Pacing.GetReadiness(normal).Reason);
    priority.Begin(explicitTurn);
}

static void Probability()
{
    for (var draw = 0; draw < 100; draw++)
    {
        using var low = new Harness(draw);
        using var high = new Harness(draw);
        var a = low.Observe(options: Harness.Policy(20));
        var b = high.Observe(options: Harness.Policy(60));
        low.At(3); high.At(3);
        var lowAccepted = low.Pacing.TryBegin(a, out _);
        var highAccepted = high.Pacing.TryBegin(b, out _);
        Check(!lowAccepted || highAccepted, "lower probability must form a subset");
        Equal(draw < 20, lowAccepted);
        Equal(draw < 60, highAccepted);
        low.Pacing.TryBegin(a, out _); high.Pacing.TryBegin(b, out _);
        Equal(1, low.Draws); Equal(1, high.Draws);
    }
}

static void Tombstones()
{
    using var h = new Harness(90);
    var rejected = h.Observe(options: Harness.Policy(20));
    h.At(2); h.Observe(options: Harness.Policy(20));
    h.At(4); h.Observe(options: Harness.Policy(20));
    h.At(6); h.Observe(options: Harness.Policy(20));
    h.At(8); h.Observe(options: Harness.Policy(20));
    h.At(10); h.Observe(options: Harness.Policy(20));
    h.At(12); h.Observe(options: Harness.Policy(20));
    Check(!h.Pacing.TryBegin(rejected, out _), "soft opportunity refusal");
    h.At(12.1); Same(rejected, h.Observe(options: Harness.Policy(100)));
    Equal(1, h.Draws);
    h.At(12.2); var direct = h.Observe(explicitRequest: true);
    Check(direct.Id != rejected.Id, "explicit refusal promotion starts accepted request");
    h.At(12.3); Same(direct, h.Observe()); Check(direct.Explicit, "inherit explicit");
    h.At(15.3); Check(h.Begin(direct).IsExplicit, "generation inherits explicit");
    Equal(1, h.Draws);
    h.Pacing.Complete(direct, false);
    h.At(15.4); var after = h.Observe(); Check(!after.Explicit, "completed exemption does not leak");
}

static void Proactive()
{
    using var h = new Harness();
    var first = h.Pacing.ObserveProactive(Harness.Group, 1, Harness.Policy(0)).Turn!;
    h.At(3); Check(!h.Pacing.TryBegin(first, out _), "zero percent proactive");
    Equal(1, h.Draws);
    for (var i = 0; i < 10; i++)
        Equal(ReplyPacingObservationKind.Terminal, h.Pacing.ObserveProactive(Harness.Group, 1, Harness.Policy(100)).Kind);
    Equal(1, h.Draws);
    var normal = h.Pacing.Observe(Harness.Group, 1, 2, null, false, true, Harness.Policy(0)).Turn!;
    h.At(6); Check(!h.Pacing.TryBegin(normal, out _), "zero percent ordinary");
    Equal(ReplyPacingReason.DuplicateOpportunity, h.Pacing.ObserveProactive(Harness.Group, 2, Harness.Policy(100)).Reason);
    Equal(2, h.Draws);
    var direct = h.Pacing.Observe(Harness.Group, 1, 3, null, true, true, Harness.Policy(0)).Turn!;
    h.At(9); h.Begin(direct); Equal(2, h.Draws);
}

static void Cooldown()
{
    using var h = new Harness();
    var first = h.Observe(); h.At(3);
    var gen = h.Begin(first); Kind(h.Pacing.TryCommitFirstSend(gen), ReplyPacingDecisionKind.Ready);
    h.At(10); h.Pacing.Complete(first, true); // final segment completes, not first segment
    h.At(12); var explicitTurn = h.Observe(speaker: 2, explicitRequest: true);
    h.At(15); var explicitGen = h.Begin(explicitTurn);
    Kind(h.Pacing.TryCommitFirstSend(explicitGen), ReplyPacingDecisionKind.Ready);
    h.At(18); h.Pacing.Complete(explicitTurn, true);
    h.At(42); Check(h.Observe(speaker: 3).IsTerminal, "explicit extends cooldown to 48");
    var independent = h.Observe(key: "group:10002"); h.At(45); h.Begin(independent);
    h.At(48); var after = h.Observe(speaker: 4); Check(!after.IsTerminal, "expiry is inclusive");
    h.At(51); h.Begin(after);
    h.At(100); h.Pacing.Complete(explicitTurn, true); // cannot extend a second time
    Check(!h.Observe(speaker: 5).IsTerminal, "completion idempotent");
}

static void FailedSends()
{
    foreach (var firstSend in new[] { false, true })
    {
        using var h = new Harness();
        var turn = h.Observe(); h.At(3); var gen = h.Begin(turn);
        if (firstSend) h.Pacing.TryCommitFirstSend(gen);
        h.Pacing.Complete(turn, false);
        Check(!h.Observe(speaker: 2).IsTerminal, "no cooldown for empty/failed/partial chat");
    }
    using var p = new Harness();
    var direct = p.Observe(group: false); p.At(1.5);
    var privateGen = p.Begin(direct); p.Pacing.TryCommitFirstSend(privateGen); p.Pacing.Complete(direct, true);
    Check(!p.Observe(speaker: 2).IsTerminal, "private does not set group cooldown");
}

static void CooldownArrival()
{
    using var h = new Harness();
    var turn = h.Observe(); h.At(3); var gen = h.Begin(turn);
    h.Pacing.TryCommitFirstSend(gen); h.Pacing.Complete(turn, true);
    h.At(32); var refused = h.Observe(speaker: 2);
    Check(refused.IsTerminal, "cooldown refusal at arrival");
    h.At(34); Same(refused, h.Observe(speaker: 2));
    h.At(37); Check(!h.Pacing.TryBegin(refused, out _), "expiry cannot revive old input");
    Equal(1, h.Draws);
    var fresh = h.Observe(speaker: 2); Check(fresh.Id != refused.Id, "new quiet-delimited opportunity");
    h.At(40); h.Begin(fresh); Equal(2, h.Draws);
}

static void QueuedCooldown()
{
    using var h = new Harness();
    var normal = h.Observe();
    var direct = h.Observe(speaker: 2, explicitRequest: true);
    h.At(3); var gen = h.Begin(direct);
    h.Pacing.TryCommitFirstSend(gen); h.Pacing.Complete(direct, true);
    Check(!h.Pacing.TryBegin(normal, out _), "queued ordinary is rechecked");
    Check(normal.IsTerminal, "cooldown rejection remains final"); Equal(0, h.Draws);
}

static void SoftDraft()
{
    using var h = new Harness();
    var turn = h.Observe();
    for (var second = 2; second <= 12; second += 2) { h.At(second); Same(turn, h.Observe()); }
    var first = h.Begin(turn); Check(first.DiscardableSoftDraft, "12 second draft text-only");
    Equal(ReplyPacingReason.SoftDraftOnly, h.Pacing.TryCommitEffects(first).Reason);
    Kind(h.Pacing.TryCommitFirstSend(first), ReplyPacingDecisionKind.Wait);
    h.At(13); h.Observe(); Kind(h.Pacing.CheckFreshness(first), ReplyPacingDecisionKind.Retry);
    var retry = h.Begin(turn); Check(retry.DiscardableSoftDraft, "retry still has no tool permission");
    h.At(14); h.Observe(); var secondStale = h.Pacing.CheckFreshness(retry);
    Kind(secondStale, ReplyPacingDecisionKind.Wait); Check(secondStale.DiscardDraft, "second stale draft discarded");
    for (var second = 16; second <= 38; second += 2)
    {
        h.At(second); h.Observe();
        Check(!h.Pacing.TryBegin(turn, out _), "no periodic soft retry loop");
    }
    h.At(41); var final = h.Begin(turn);
    Check(!final.DiscardableSoftDraft, "quiet allows final generation");
    Equal(1, h.Draws);
    Kind(h.Pacing.TryCommitFirstSend(final), ReplyPacingDecisionKind.Ready);
}

static void BeforeSend()
{
    using var h = new Harness();
    var turn = h.Observe(explicitRequest: true); h.At(3); var old = h.Begin(turn);
    h.Pacing.EndAttempt(old); h.At(3.1); h.Observe();
    Kind(h.Pacing.TryCommitFirstSend(old), ReplyPacingDecisionKind.Retry);
    var fresh = h.Begin(turn); Check(fresh.Version > old.Version, "new generation captures version");
    Kind(h.Pacing.CheckFreshness(old), ReplyPacingDecisionKind.Terminal);
    h.At(6.1); Kind(h.Pacing.TryCommitFirstSend(fresh), ReplyPacingDecisionKind.Ready);
    Equal(ReplyPacingReason.AlreadySending, h.Pacing.TryCommitFirstSend(fresh).Reason);
    Check(!h.Pacing.TryBegin(turn, out _), "no automatic replay after first send");
}

static void Effects()
{
    using var h = new Harness();
    var original = h.Observe(explicitRequest: true); h.At(3); var gen = h.Begin(original);
    Kind(h.Pacing.TryCommitEffects(gen), ReplyPacingDecisionKind.Ready);
    Kind(h.Pacing.TryCommitEffects(gen), ReplyPacingDecisionKind.Terminal);
    h.At(4); var followup = h.Observe(); Check(followup.Id != original.Id, "new followup after effects");
    Check(followup.Explicit, "accepted task continuation inherits explicit");
    h.At(5); Same(followup, h.Observe());
    h.At(6); h.Observe(speaker: 2); // must not extend original's quiet clock
    Kind(h.Pacing.CheckFreshness(gen), ReplyPacingDecisionKind.Wait);
    h.At(8); Kind(h.Pacing.CheckFreshness(gen), ReplyPacingDecisionKind.Ready);
    Check(!h.Pacing.TryBegin(followup, out _), "followup waits for accepted result");
    Kind(h.Pacing.TryCommitFirstSend(gen), ReplyPacingDecisionKind.Ready);
    h.Pacing.Complete(original, true); h.Begin(followup);
    Equal(0, h.Draws);
}

static void SendingFollowup()
{
    using var h = new Harness();
    var original = h.Observe(explicitRequest: true); h.At(3); var gen = h.Begin(original);
    h.Pacing.TryCommitFirstSend(gen);
    h.At(4); var followup = h.Observe();
    h.At(5); Same(followup, h.Observe()); Check(followup.Explicit, "sending continuation inherits");
    Equal(ReplyPacingReason.AlreadySending, h.Pacing.CheckFreshness(gen).Reason);
    h.At(8); h.Pacing.Complete(original, false);
    var next = h.Begin(followup); h.Pacing.TryCommitFirstSend(next); h.Pacing.Complete(followup, false);
    h.At(12); Check(!h.Observe().Explicit, "completed request exemption expires");
}

static void Preemption()
{
    using var h = new Harness(capacity: 1);
    var ordinary = h.Observe(); h.At(3); var draft = h.Begin(ordinary);
    var direct = h.Observe(speaker: 2, explicitRequest: true);
    Check(ordinary.IsTerminal, "explicit preempts discardable ordinary");
    Kind(h.Pacing.CheckFreshness(draft), ReplyPacingDecisionKind.Terminal);
    h.At(6); var gen = h.Begin(direct); h.Pacing.TryCommitEffects(gen);
    var overflow = h.Pacing.Observe(Harness.Group, 3, 20, null, true, true, Harness.Policy());
    Equal(ReplyPacingObservationKind.RejectedCapacity, overflow.Kind);
    Equal(ReplyPacingReason.Capacity, overflow.Reason); Check(overflow.Turn == null, "no false acceptance");
    Check(!direct.IsTerminal, "never preempt explicit/effects");
    Equal(1, h.Pacing.ActiveTurns(Harness.Group).Count);
}

static void Snapshots()
{
    using var h = new Harness();
    var firstTicket = new object(); var latestTicket = new object();
    var options = Harness.Policy(20);
    var turn = h.Observe(explicitRequest: true, options: options, ticket: firstTicket);
    h.At(1); h.Observe(options: Harness.Policy(0) with { GroupContinuationWait = TimeSpan.FromSeconds(30) }, ticket: latestTicket);
    Same(latestTicket, turn.OpaqueTicket!); Same(options, h.Pacing.Read(turn).Options);
    h.At(4); var gen = h.Begin(turn); Same(latestTicket, gen.Ticket!);
    Equal(2L, gen.TriggerMessageId!.Value);
    h.At(5); h.Observe(ticket: new object()); Same(latestTicket, gen.Ticket!);
    Equal(2L, gen.Sequence);
}

static void DisableHandoff()
{
    using var h = new Harness();
    var normal = h.Observe(); var direct = h.Observe(speaker: 2, explicitRequest: true);
    var hands = h.Pacing.Disable(); Equal(1, hands.Count); Same(direct, hands[0].Turn);
    Check(normal.IsTerminal && direct.IsTerminal, "old owner terminal after handoff");
    Equal(0, h.Pacing.Disable().Count); Check(!h.Pacing.TryBegin(direct, out _), "old pump fenced");
    h.At(10); h.Pacing.Complete(direct, true);
    h.Pacing.Enable(); Check(h.Observe(speaker: 3).IsTerminal, "legacy completion extends cooldown once");
    h.At(41); h.Pacing.Complete(direct, true);
    Check(!h.Observe(speaker: 4).IsTerminal, "legacy handoff consumed once");
}

static void DisableIrreversible()
{
    foreach (var sending in new[] { false, true })
    {
        using var h = new Harness();
        var turn = h.Observe(explicitRequest: true); h.At(3); var gen = h.Begin(turn);
        if (sending) h.Pacing.TryCommitFirstSend(gen); else h.Pacing.TryCommitEffects(gen);
        Equal(0, h.Pacing.Disable().Count); Check(!turn.IsTerminal, "irreversible work remains owned");
        Same(Harness.PolicySnapshot, gen.Options);
        if (!sending) Kind(h.Pacing.TryCommitFirstSend(gen), ReplyPacingDecisionKind.Ready);
        h.Pacing.Complete(turn, true); Check(turn.IsTerminal, "irreversible work can finish");
    }
}

static void Stop()
{
    using var h = new Harness();
    var turn = h.Observe(explicitRequest: true); h.At(3); var gen = h.Begin(turn);
    h.Pacing.TryCommitEffects(gen); h.Pacing.Stop();
    Kind(h.Pacing.TryCommitFirstSend(gen), ReplyPacingDecisionKind.Terminal);
    Check(turn.IsTerminal, "stop cancels all handles");
    h.Pacing.Enable();
    Equal(ReplyPacingObservationKind.Disabled, h.Pacing.Observe(Harness.Group, 1, 2, null, true, true, Harness.Policy()).Kind);
    h.Pacing.Dispose(); Kind(h.Pacing.CheckFreshness(gen), ReplyPacingDecisionKind.Terminal);
    Equal(0, h.Pacing.ActiveTurns(Harness.Group).Count);
}

static void ConcurrentIngress()
{
    using var h = new Harness();
    var accepted = 0;
    Parallel.For(1, 65, i =>
    {
        var observed = h.Pacing.Observe(Harness.Group, i, i, null, true, true, Harness.Policy());
        if (observed.Created) Interlocked.Increment(ref accepted);
    });
    Equal(64, accepted); Equal(64, h.Pacing.ActiveTurns(Harness.Group).Count);
    var duplicates = 0;
    Parallel.For(1, 65, i =>
    {
        if (h.Pacing.Observe(Harness.Group, i, i, null, true, true, Harness.Policy()).Reason == ReplyPacingReason.DuplicateOpportunity)
            Interlocked.Increment(ref duplicates);
    });
    Equal(64, duplicates); Equal(0, h.Draws);
}

static void CapacityPriority()
{
    using var h = new Harness(capacity: 1);
    var normal = h.Observe();
    var direct = h.Observe(speaker: 2, explicitRequest: true);
    Check(normal.IsTerminal, "queued ordinary must yield capacity");
    Check(!direct.IsTerminal, "explicit request accepted in freed slot");
}

static void RetainedCooldown()
{
    using var h = new Harness();
    var first = h.Observe(); var second = h.Observe(speaker: 2);
    h.At(3); var a = h.Begin(first); h.Pacing.EndAttempt(a);
    var b = h.Begin(second); h.Pacing.TryCommitFirstSend(b); h.Pacing.Complete(second, true);
    Equal(ReplyPacingReason.Cooldown, h.Pacing.TryCommitFirstSend(a).Reason);
    Check(first.IsTerminal, "retained ordinary draft expires on another full response");
}

static void DisabledNormalEffects()
{
    using var h = new Harness();
    var normal = h.Observe(); h.At(3); var gen = h.Begin(normal);
    h.Pacing.TryCommitEffects(gen); h.Pacing.EndAttempt(gen);
    var direct = h.Observe(speaker: 2, explicitRequest: true);
    h.At(6); var response = h.Begin(direct); h.Pacing.TryCommitFirstSend(response); h.Pacing.Complete(direct, true);
    Equal(0, h.Pacing.Disable().Count);
    Kind(h.Pacing.TryCommitFirstSend(gen), ReplyPacingDecisionKind.Ready);
    Equal(1, h.Draws); // accepted effect result neither rerolls nor gets preempted by the explicit reply
}

static void AcceptedResultIdentity()
{
    using var h = new Harness();
    var policy = Harness.Policy(probability: 0);
    var human = h.Pacing.Observe(Harness.Group, 10001, 10, 10, false, true, policy).Turn!;
    var ticket = new object();
    var result = h.Pacing.ObserveAcceptedResult(Harness.Group, 10000, true, policy, ticket).Turn!;
    var generation = h.Begin(result);
    Check(generation.IsExplicit && !generation.DiscardableSoftDraft, "accepted result is immediate explicit work");
    Equal(10000L, generation.Sequence); Same(ticket, generation.Ticket!);
    var next = h.Pacing.Observe(Harness.Group, 10001, 11, 11, false, true, policy);
    Same(human, next.Turn!);
    Kind(h.Pacing.CheckFreshness(generation), ReplyPacingDecisionKind.Ready);
    Equal(11L, h.Pacing.Read(human).Sequence);
    Check(h.Pacing.ObserveContinuation(Harness.Group, 0, 10001, null).Turn == null,
        "human continuation cannot attach to accepted-result synthetic speaker");
    Check(h.Pacing.ObserveProactive(Harness.Group, 12, policy).Created,
        "accepted result context does not advance proactive opportunity watermark");
    Equal(0, h.Draws);
}

static void AcceptedResultCooldown()
{
    using var h = new Harness();
    var policy = Harness.Policy(probability: 0);
    var refused = h.Observe(options: policy); h.At(3); Check(!h.Pacing.TryBegin(refused, out _), "refused candidate cannot begin");
    var result = h.Pacing.ObserveAcceptedResult(Harness.Group, 1, true, policy).Turn!;
    var generation = h.Begin(result);
    Kind(h.Pacing.TryCommitFirstSend(generation), ReplyPacingDecisionKind.Ready);
    h.Pacing.Complete(result, true);
    var during = h.Pacing.Observe(Harness.Group, 10002, 2, 2, false, true, policy);
    Equal(ReplyPacingReason.Cooldown, during.Reason);
    h.At(10);
    var second = h.Pacing.ObserveAcceptedResult(Harness.Group, 2, true, policy).Turn!;
    var secondGeneration = h.Begin(second);
    Kind(h.Pacing.TryCommitFirstSend(secondGeneration), ReplyPacingDecisionKind.Ready);
    h.Pacing.Complete(second, true);
    h.At(34);
    Equal(ReplyPacingReason.Cooldown, h.Pacing.Observe(Harness.Group, 10003, 3, 3, false, true, policy).Reason);
    var privateResult = h.Pacing.ObserveAcceptedResult("private:10001", 1, false, policy).Turn!;
    Kind(h.Pacing.TryCommitFirstSend(h.Begin(privateResult)), ReplyPacingDecisionKind.Ready);
    h.Pacing.Complete(privateResult, true);
    h.At(40);
    Check(h.Pacing.Observe(Harness.Group, 10004, 4, 4, false, true, policy).Created,
        "only accepted group result extends same-group cooldown");
    Equal(1, h.Draws);
}

static void AcceptedResultLifecycle()
{
    using var h = new Harness(capacity: 2);
    var first = h.Pacing.ObserveAcceptedResult(Harness.Group, 1, true, Harness.Policy()).Turn!;
    var second = h.Pacing.ObserveAcceptedResult(Harness.Group, 1, true, Harness.Policy()).Turn!;
    Check(!ReferenceEquals(first, second), "accepted result identity belongs to caller, not human sequence");
    Equal(ReplyPacingObservationKind.RejectedCapacity,
        h.Pacing.ObserveAcceptedResult(Harness.Group, 1, true, Harness.Policy()).Kind);
    h.Begin(first);
    Check(!h.Pacing.TryBegin(second, out _), "conversation permits one generation at a time");
    Equal(2, h.Pacing.Disable().Count); Equal(0, h.Pacing.Disable().Count);
    Equal(ReplyPacingObservationKind.Disabled,
        h.Pacing.ObserveAcceptedResult(Harness.Group, 1, true, Harness.Policy()).Kind);
    h.Pacing.Enable(); h.Pacing.Stop();
    Equal(ReplyPacingReason.Stopped,
        h.Pacing.ObserveAcceptedResult(Harness.Group, 1, true, Harness.Policy()).Reason);
}

static void LateExplicitPromotion()
{
    using var h = new Harness();
    var ticket = new object(); var originalPolicy = Harness.Policy();
    var turn = h.Pacing.Observe(Harness.Group, 10001, 1, 1, false, true, originalPolicy).Turn!;
    h.At(3); var generation = h.Begin(turn);
    var promoted = h.Pacing.Observe(Harness.Group, 10001, 1, 1, true, true, Harness.Policy(probability: 0), ticket);
    Same(turn, promoted.Turn!); Check(turn.Explicit, "same-sequence direct confirmation promotes active request");
    Same(originalPolicy, h.Pacing.Read(turn).Options); Same(ticket, turn.OpaqueTicket!);
    Kind(h.Pacing.CheckFreshness(generation), ReplyPacingDecisionKind.Retry);
    Equal(1, h.Draws);
    using var rejected = new Harness();
    var prior = rejected.Pacing.Observe(Harness.Group, 10001, 1, 1, false, true, Harness.Policy(probability: 0)).Turn!;
    rejected.At(3); Check(!rejected.Pacing.TryBegin(prior, out _), "zero-percent candidate is refused"); rejected.At(20);
    var direct = rejected.Pacing.Observe(Harness.Group, 10001, 1, 1, true, true, Harness.Policy()).Turn!;
    Check(!ReferenceEquals(prior, direct), "late quote confirmation creates accepted ownership from refusal");
    Check(direct.Explicit, "explicit confirmation bypasses prior lottery refusal");
    Check(rejected.Begin(direct).IsExplicit, "late confirmation preserves already elapsed quiet");
    Equal(1, rejected.Draws);
}

static void LateExplicitFences()
{
    using var h = new Harness();
    var turn = h.Pacing.Observe(Harness.Group, 10001, 1, 1, false, true, Harness.Policy()).Turn!;
    h.Pacing.Observe(Harness.Group, 10001, 2, 2, false, true, Harness.Policy());
    Equal(ReplyPacingReason.DuplicateOpportunity,
        h.Pacing.Observe(Harness.Group, 10001, 1, 1, true, true, Harness.Policy()).Reason);
    Check(!turn.Explicit, "older direct confirmation cannot change newer request");
    h.At(3); var generation = h.Begin(turn); h.Pacing.TryCommitFirstSend(generation);
    Equal(ReplyPacingReason.DuplicateOpportunity,
        h.Pacing.Observe(Harness.Group, 10001, 2, 2, true, true, Harness.Policy()).Reason);
    h.Pacing.Complete(turn, true);
    Equal(ReplyPacingReason.DuplicateOpportunity,
        h.Pacing.Observe(Harness.Group, 10001, 2, 2, true, true, Harness.Policy()).Reason);
    Equal(0, h.Pacing.ActiveTurns(Harness.Group).Count);
}

static void ContinuationAdmission()
{
    using var h = new Harness();
    Check(h.Pacing.ObserveContinuation(Harness.Group, 10001, 1, 1).Turn == null,
        "unrelated ingress cannot create first candidate");
    var original = h.Pacing.Observe(Harness.Group, 10001, 1, 1, true, true, Harness.Policy()).Turn!;
    h.At(1);
    Check(h.Pacing.ObserveContinuation(Harness.Group, 10002, 2, 2).Turn == null,
        "another human cannot inherit opportunity");
    var ticket = new object();
    Same(original, h.Pacing.ObserveContinuation(Harness.Group, 10001, 3, 3, ticket).Turn!);
    Check(original.Explicit, "ordinary continuation preserves explicit authorization");
    Same(ticket, original.OpaqueTicket!); Equal(3L, h.Pacing.Read(original).Sequence);
    h.At(2);
    Equal(ReplyPacingReason.DuplicateOpportunity, h.Pacing.ObserveContinuation(Harness.Group, 10001, 3, 3).Reason);
    h.At(4); var generation = h.Begin(original);
    h.Pacing.TryCommitFirstSend(generation); h.Pacing.Complete(original, true);
    Check(h.Pacing.ObserveContinuation(Harness.Group, 10001, 4, 4).Turn == null,
        "completed ownership cannot become another automatic reply");
    Equal(0, h.Draws);
}

static void ContinuationRefusal()
{
    using var h = new Harness();
    var options = Harness.Policy(probability: 0);
    var explicitReply = h.Pacing.ObserveAcceptedResult(Harness.Group, 1, true, options).Turn!;
    h.Pacing.TryCommitFirstSend(h.Begin(explicitReply)); h.Pacing.Complete(explicitReply, true);
    var refused = h.Pacing.Observe(Harness.Group, 10001, 1, 1, false, true, options).Turn!;
    h.At(2); Same(refused, h.Pacing.ObserveContinuation(Harness.Group, 10001, 2, 2).Turn!);
    h.At(4); Same(refused, h.Pacing.ObserveContinuation(Harness.Group, 10001, 3, 3).Turn!);
    var promoted = h.Pacing.Observe(Harness.Group, 10001, 3, 3, true, true, options).Turn!;
    Check(promoted.Explicit && !promoted.IsTerminal, "latest refused continuation supports late explicit confirmation");
    h.At(6.9); Check(!h.Pacing.TryBegin(promoted, out _), "continuation requires full quiet window"); h.At(7); h.Begin(promoted);
    Equal(0, h.Draws);
}

static void ContinuationSoftTerminal(ReplyPacingReason terminalReason)
{
    using var h = new Harness();
    var options = Harness.Policy();
    var turn = h.Pacing.Observe(Harness.Group, 10001, 1, 1, false, true, options).Turn!;
    for (var second = 2; second <= 12; second += 2)
    {
        h.At(second);
        Same(turn, h.Pacing.Observe(Harness.Group, 10001, second / 2 + 1, second / 2 + 1, false, true, options).Turn!);
    }
    Check(h.Begin(turn).DiscardableSoftDraft, "continuous input creates a soft draft at twelve seconds");
    Equal(1, h.Draws);
    switch (terminalReason)
    {
        case ReplyPacingReason.Preempted:
            var direct = h.Pacing.Observe(Harness.Group, 10002, 8, 8, true, true, options).Turn!;
            h.Pacing.Complete(direct, false);
            break;
        case ReplyPacingReason.NotSent:
            h.Pacing.Complete(turn, false);
            break;
        case ReplyPacingReason.Ineligible:
            h.Pacing.Reject(turn);
            break;
        default:
            throw new ArgumentOutOfRangeException(nameof(terminalReason));
    }
    Equal(terminalReason, h.Pacing.GetReadiness(turn).Reason);
    h.At(14);
    var continuation = h.Pacing.ObserveContinuation(Harness.Group, 10001, 9, 9);
    h.At(16);
    var continued = h.Pacing.Observe(Harness.Group, 10001, 10, 10, false, true, options);
    Equal(ReplyPacingObservationKind.Terminal, continued.Kind);
    Same(turn, continued.Turn!); Equal(terminalReason, continued.Reason);
    Same(turn, continuation.Turn!); Equal(terminalReason, continuation.Reason);
    Check(!h.Pacing.TryBegin(turn, out _), "same continuous speech cannot acquire another lottery");
    Equal(1, h.Draws);
    Equal(ReplyPacingReason.DuplicateOpportunity,
        h.Pacing.Observe(Harness.Group, 10001, 10, 10, true, true, options).Reason);
    Check(!turn.Explicit, "quiet extension cannot widen late-explicit revival permission");
    h.At(18);
    Equal(ReplyPacingReason.DuplicateOpportunity, h.Pacing.ObserveContinuation(Harness.Group, 10001, 10, 10).Reason);
    h.At(19);
    var fresh = h.Pacing.Observe(Harness.Group, 10001, 11, 11, false, true, options);
    Check(fresh.Created && !ReferenceEquals(turn, fresh.Turn), "only a full actual quiet gap creates a fresh opportunity");
    h.At(22); h.Begin(fresh.Turn!); Equal(2, h.Draws);
}

static void ContinuationEffects()
{
    using var h = new Harness();
    var original = h.Pacing.Observe(Harness.Group, 10001, 1, 1, true, true, Harness.Policy()).Turn!;
    h.At(3); var generation = h.Begin(original); h.Pacing.TryCommitEffects(generation);
    h.At(4); Same(original, h.Pacing.ObserveContinuation(Harness.Group, 10001, 2, 2).Turn!);
    Equal(1L, generation.Sequence); Equal(1L, h.Pacing.Read(original).Sequence);
    h.At(5); h.Pacing.ObserveContinuation(Harness.Group, 10001, 2, 2);
    var followup = h.Pacing.Observe(Harness.Group, 10001, 2, 2, true, true, Harness.Policy());
    Check(followup.Created && !ReferenceEquals(original, followup.Turn),
        "quiet-only observation cannot swallow later authorized followup");
    h.At(7); Kind(h.Pacing.CheckFreshness(generation), ReplyPacingDecisionKind.Ready);
    Equal(0, h.Draws);
}

static void Diagnostics()
{
    using var h = new Harness();
    var turn = h.Observe(ticket: new object());
    Equal(nameof(ReplyPacingTurn), turn.ToString()); Equal(nameof(ReplyPacingSnapshot), h.Pacing.Read(turn).ToString());
    Check(h.Pacing.GetCounters().ContainsKey(ReplyPacingReason.Accepted), "fixed reason counter");
    Throws<ArgumentOutOfRangeException>(() => h.Observe(options: Harness.Policy(-1)));
    Throws<ArgumentNullException>(() => new ReplyPacingCoordinator(null!));
    Throws<ArgumentOutOfRangeException>(() => new ReplyPacingCoordinator(() => DateTime.UnixEpoch, capacity: 0));
    using var other = new Harness(); Throws<ArgumentException>(() => other.Pacing.Read(turn));
    using var badRandom = new Harness(100); var candidate = badRandom.Observe(); badRandom.At(3);
    Throws<InvalidOperationException>(() => badRandom.Pacing.TryBegin(candidate, out _));
}

static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}."); }
static void Same(object expected, object actual) => Check(ReferenceEquals(expected, actual), "identity changed");
static void Kind(ReplyPacingDecision value, ReplyPacingDecisionKind expected) => Equal(expected, value.Kind);
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class Harness : IDisposable
{
    public const string Group = "group:10001";
    public static readonly ReplyPacingOptions PolicySnapshot = ReplyPacingOptions.Default with { GroupInterjectionProbability = 100 };
    private readonly DateTime _epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private DateTime _now;
    private long _sequence;
    public int Draws { get; private set; }
    public ReplyPacingCoordinator Pacing { get; }
    public Harness(int draw = 0, int capacity = ReplyPacingCoordinator.DefaultCapacity)
    {
        _now = _epoch;
        Pacing = new(() => _now, () => { Draws++; return draw; }, capacity);
    }
    public static ReplyPacingOptions Policy(int probability = 100) => PolicySnapshot with { GroupInterjectionProbability = probability };
    public void At(double seconds) => _now = _epoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    public ReplyPacingTurn Observe(long speaker = 1, bool explicitRequest = false, bool group = true,
        ReplyPacingOptions? options = null, string? key = null, object? ticket = null)
    {
        var seq = ++_sequence;
        return Pacing.Observe(key ?? Group, speaker, seq, seq, explicitRequest, group, options ?? PolicySnapshot, ticket).Turn
            ?? throw new InvalidOperationException("Expected an owned turn.");
    }
    public ReplyPacingGeneration Begin(ReplyPacingTurn turn)
    {
        if (!Pacing.TryBegin(turn, out var generation) || generation == null)
            throw new InvalidOperationException($"Expected generation; readiness={Pacing.GetReadiness(turn).Reason}.");
        return generation;
    }
    public void Dispose() => Pacing.Dispose();
}

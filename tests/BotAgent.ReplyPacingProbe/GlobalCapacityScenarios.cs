using BotAgent.Services.Reply;

internal static class GlobalCapacityScenarios
{
    internal static void AcrossConversations()
    {
        using var pacing = new ReplyPacingCoordinator(() => DateTime.UnixEpoch);
        var accepted = new List<ReplyPacingTurn>();
        for (var i = 0; i < 300; i++)
        {
            var result = pacing.Observe($"group:capacity-{i}", 1, 1, null, true, true,
                ReplyPacingOptions.Default);
            if (i < ReplyPacingCoordinator.DefaultCapacity)
            {
                Check(result.Created && result.Turn != null, $"request {i} should fit the global budget");
                accepted.Add(result.Turn!);
            }
            else
            {
                Check(result.Kind == ReplyPacingObservationKind.RejectedCapacity &&
                    result.Reason == ReplyPacingReason.Capacity && result.Turn == null,
                    $"request {i} must return explicit capacity rejection across conversations; got {result.Kind}/{result.Reason}");
            }
        }
        foreach (var turn in accepted)
            Check(!pacing.Read(turn).Terminal, "overflow must not evict accepted explicit work");
        var ordinary = pacing.Observe("group:capacity-ordinary", 1, 1, null, false, true,
            ReplyPacingOptions.Default);
        Check(ordinary.Kind == ReplyPacingObservationKind.Terminal &&
            ordinary.Reason == ReplyPacingReason.Capacity && ordinary.Turn == null,
            "ordinary overflow must skip rather than return explicit busy");
    }

    private static readonly ReplyPacingOptions Immediate = new(TimeSpan.Zero, TimeSpan.Zero, 100, TimeSpan.Zero);

    internal static void RetainedWork()
    {
        foreach (var commit in new[] { "draft", "effects", "send" })
        {
            var now = DateTime.UnixEpoch;
            using var pacing = new ReplyPacingCoordinator(() => now, capacity: 1);
            var turn = Create(pacing, "retained");
            Check(pacing.TryBegin(turn, out var generation), "generation should begin");
            Check(pacing.CheckFreshness(generation!).Kind == ReplyPacingDecisionKind.Ready, "draft is ready");
            if (commit == "effects") Check(pacing.TryCommitEffects(generation!).Kind == ReplyPacingDecisionKind.Ready, "effects commit");
            if (commit == "send") Check(pacing.TryCommitFirstSend(generation!).Kind == ReplyPacingDecisionKind.Ready, "send commit");
            pacing.EndAttempt(generation!);
            now += TimeSpan.FromHours(1);
            ExpectFull(pacing, "retained-overflow");
            Check(!turn.IsTerminal, $"{commit} remains owned while yielded and aged");
            if (commit != "draft")
            {
                pacing.Reject(turn);
                Check(pacing.Disable().Count == 0, "irreversible work must not be handed off");
                pacing.Enable();
                ExpectFull(pacing, "irreversible-overflow");
            }
            pacing.Complete(turn, commit == "send");
            Create(pacing, "retained-readmission");
            pacing.Complete(turn, false);
            pacing.Reject(turn);
            ExpectFull(pacing, "duplicate-completion");
        }
    }

    internal static void RetirementReadmission()
    {
        using var pacing = new ReplyPacingCoordinator(() => DateTime.UnixEpoch, capacity: 1);
        for (var i = 0; i < 300; i++)
        {
            var turn = Create(pacing, $"retirement-{i}");
            ExpectFull(pacing, $"overflow-{i}");
            if (i % 2 == 0) pacing.Complete(turn, false);
            else pacing.Reject(turn);
            Check(turn.IsTerminal, "completion and rejection must retire the owner");
        }
        Create(pacing, "final-owner");
        ExpectFull(pacing, "final-overflow");
    }

    internal static void RefusalAndPreemption()
    {
        using var pacing = new ReplyPacingCoordinator(() => DateTime.UnixEpoch, () => 99, capacity: 1);
        var refused = pacing.Observe("group:refusal", 1, 1, null, false, true,
            Immediate with { GroupInterjectionProbability = 0 }).Turn!;
        Check(!pacing.TryBegin(refused, out _) && refused.IsTerminal, "probability refusal retires the owner");
        var ordinary = pacing.Observe("group:preemption", 1, 1, null, false, true, Immediate).Turn!;
        Check(pacing.TryBegin(ordinary, out var generation), "ordinary generation starts");
        var explicitTurn = pacing.Observe("group:preemption", 2, 2, null, true, true, Immediate).Turn;
        Check(explicitTurn != null && ordinary.IsTerminal, "local explicit preemption reuses one global slot");
        pacing.EndAttempt(generation!);
        pacing.Complete(ordinary, false);
        ExpectFull(pacing, "preemption-overflow");
        pacing.Reject(explicitTurn!);
        Create(pacing, "preemption-readmission");
    }

    internal static void ContinuationAndSuccessor()
    {
        using var pacing = new ReplyPacingCoordinator(() => DateTime.UnixEpoch, capacity: 2);
        var turn = Create(pacing, "successor");
        var continuation = pacing.Observe("group:successor", 1, 2, null, true, true, Immediate);
        Check(ReferenceEquals(turn, continuation.Turn), "continuation reuses the active turn");
        Check(pacing.TryBegin(turn, out var generation), "continued generation starts");
        Check(pacing.CheckFreshness(generation!).Kind == ReplyPacingDecisionKind.Ready, "continued draft ready");
        Check(pacing.TryCommitFirstSend(generation!).Kind == ReplyPacingDecisionKind.Ready, "first send commits");
        var successor = pacing.Observe("group:successor", 1, 3, null, true, true, Immediate).Turn;
        Check(successor != null && !ReferenceEquals(turn, successor), "sending followup gets its own slot");
        ExpectFull(pacing, "successor-overflow");
        Check(pacing.GetReadiness(successor!).Kind == ReplyPacingDecisionKind.Wait, "successor waits for original send completion");
        pacing.Complete(turn, true);
        Check(pacing.GetReadiness(successor!).Kind == ReplyPacingDecisionKind.Ready, "successor is now ready");
        Create(pacing, "successor-readmission");
        ExpectFull(pacing, "successor-still-bounded");
    }

    internal static void DisableAndStop()
    {
        using var pacing = new ReplyPacingCoordinator(() => DateTime.UnixEpoch, capacity: 2);
        var explicitTurn = Create(pacing, "handoff");
        var ordinary = pacing.Observe("group:disable", 1, 1, null, false, true, Immediate).Turn!;
        var handoff = pacing.Disable();
        Check(handoff.Count == 1 && ReferenceEquals(handoff[0].Turn, explicitTurn), "only explicit work is handed off");
        Check(ordinary.IsTerminal && pacing.Disable().Count == 0, "disable retires unsent work exactly once");
        pacing.Enable();
        var first = Create(pacing, "enabled-first");
        var second = Create(pacing, "enabled-second");
        pacing.Complete(explicitTurn, true);
        ExpectFull(pacing, "handoff-completion-overflow");
        pacing.Stop();
        Check(first.IsTerminal && second.IsTerminal, "stop retires remaining owners");
        pacing.Dispose();
        pacing.Enable();
        Check(pacing.Observe("group:stopped", 1, 1, null, true, true, Immediate).Reason == ReplyPacingReason.Stopped,
            "stop and dispose fence further admission");
    }

    internal static void AcceptedAndProactiveShareBudget()
    {
        using var pacing = new ReplyPacingCoordinator(() => DateTime.UnixEpoch, capacity: 2);
        var accepted = pacing.ObserveAcceptedResult("group:accepted", 1, true, Immediate).Turn!;
        var proactive = pacing.ObserveProactive("group:proactive", 1, Immediate).Turn!;
        Check(accepted != null && proactive != null, "accepted and proactive work consume shared slots");
        ExpectFull(pacing, "mixed-overflow");
        var rejected = pacing.ObserveAcceptedResult("group:accepted-overflow", 1, true, Immediate);
        Check(rejected.Kind == ReplyPacingObservationKind.RejectedCapacity, "accepted result overflow stays explicit busy");
        pacing.Complete(accepted!, false);
        Create(pacing, "mixed-readmission");
        pacing.Reject(proactive!);
        Check(pacing.ObserveAcceptedResult("group:accepted-readmission", 1, true, Immediate).Created, "result can reenter released slot");
        ExpectFull(pacing, "mixed-still-bounded");
    }

    internal static void ParallelAdmission()
    {
        using var pacing = new ReplyPacingCoordinator(() => DateTime.UnixEpoch);
        var results = new ReplyPacingObservation[300];
        Parallel.For(0, results.Length, i =>
            results[i] = pacing.Observe($"group:parallel-{i}", 1, 1, null, true, true, Immediate));
        Check(results.Count(r => r.Created) == ReplyPacingCoordinator.DefaultCapacity, "parallel admission cannot oversubscribe");
        Check(results.Count(r => r.Kind == ReplyPacingObservationKind.RejectedCapacity) == 44, "parallel overflow rejects exactly 44");
        Parallel.ForEach(results.Where(r => r.Created), r => pacing.Complete(r.Turn!, false));
        for (var i = 0; i < ReplyPacingCoordinator.DefaultCapacity; i++) Create(pacing, $"parallel-readmission-{i}");
        ExpectFull(pacing, "parallel-still-bounded");
    }

    private static ReplyPacingTurn Create(ReplyPacingCoordinator pacing, string suffix)
    {
        var result = pacing.Observe($"group:{suffix}", 1, 1, null, true, true, Immediate);
        Check(result.Created && result.Turn != null, $"{suffix} should be admitted");
        return result.Turn!;
    }

    private static void ExpectFull(ReplyPacingCoordinator pacing, string suffix)
    {
        var result = pacing.Observe($"group:{suffix}", 1, 1, null, true, true, Immediate);
        Check(result.Kind == ReplyPacingObservationKind.RejectedCapacity && result.Turn == null &&
            result.Reason == ReplyPacingReason.Capacity, $"{suffix} must not exceed the global budget");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using BotAgent.ReplyPipelinePacingProbe;

var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    passed++;
    Console.WriteLine("PASS " + label);
}
var scenarios = new (string Name, Func<Action<bool, string>, Task> Run)[]
{
    ("last-segment-cooldown", Scenarios.LastSegmentCooldown),
    ("partial-segment-no-cooldown", Scenarios.PartialSegmentNoCooldown),
    ("same-speaker-promotion", Scenarios.SameSpeakerPromotion),
    ("mode-off-generating", Scenarios.ModeOffGenerating),
    ("mode-off-emitting", Scenarios.ModeOffEmitting),
    ("shutdown-late-result", Scenarios.ShutdownLateResult),
    ("stale-tool-draft", Scenarios.StaleToolDraft),
    ("accepted-result-bypass", Scenarios.AcceptedResultBypass),
    ("inline-effects-protected", Scenarios.InlineEffectsProtected),
    ("continuation-context", Scenarios.ContinuationContext),
    ("first-send-stale", Scenarios.FinalBoundaryStale),
    ("explicit-preemption", Scenarios.ExplicitPreemption),
    ("effects-not-preempted", Scenarios.ExplicitDoesNotPreemptEffects),
    ("soft-deadline", Scenarios.SoftDeadline),
    ("global-slot", Scenarios.QuietWaitReleasesGlobalSlot),
    ("mode-off", Scenarios.ModeOff),
    ("full-ack-cooldown", Scenarios.FullAckCooldown),
    ("failed-ack-no-cooldown", Scenarios.FailedAckDoesNotCoolDown),
};
if (args.Length > 0)
{
    scenarios = scenarios.Where(scenario => args.Contains(scenario.Name, StringComparer.Ordinal)).ToArray();
    if (scenarios.Length == 0) throw new ArgumentException("Unknown scenario name");
}
foreach (var scenario in scenarios)
{
    Console.WriteLine("SCENARIO " + scenario.Name);
    await scenario.Run(Check).WaitAsync(TimeSpan.FromSeconds(30));
}
Console.WriteLine($"PASS actual pipeline pacing: {scenarios.Length} scenarios, {passed} assertions");

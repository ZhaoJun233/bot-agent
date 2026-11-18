using BotAgent.ReplySchedulerProbe;
using static BotAgent.ReplySchedulerProbe.ProbeHarness;

var tests = new (string Name, Func<Task> Run)[]
{
    ("group quiet restarts and selects latest input", GroupQuiet),
    ("private quiet uses exact 1.5 second boundary", PrivateQuiet),
    ("context-only ingress delays but never admits", ContextOnly),
    ("quiet work does not occupy global generation slot", ResourceFairness),
    ("explicit request preempts cancellable ordinary generation", Preemption),
    ("stale draft retries latest without sending old draft", StaleDraft),
    ("final send preflight rechecks freshness after await", FinalPreflight),
    ("committed effects retain draft and wait only same human", EffectQuiet),
    ("stop fences generation and subsequent ingress", Stop),
    ("AI disable discards queued work without replay", Toggle),
    ("parameter updates preserve irreversible work without replay", EffectParameterUpdate),
    ("revoked permission fences owned generation", PermissionRevocation),
    ("failed delivery does not start cooldown but success does", DeliveryCooldown)
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + ": " + error.Message); }
}
Console.WriteLine($"Reply scheduler probe: {tests.Length - failures}/{tests.Length} passed.");
Environment.ExitCode = failures == 0 ? 0 : 1;

static async Task GroupQuiet()
{
    await using var f = new ProbeHarness();
    var conversation = Group();
    f.Input(conversation); await f.SleepingAsync();
    await f.AtAsync(2000); var latest = f.Input(conversation); await f.SleepingAsync();
    await f.AtAsync(4999); await f.SleepingAsync();
    Equal(0, f.History.Count, "generation cannot precede renewed quiet deadline");
    await f.AtAsync(5000); var call = await f.NextAsync();
    Equal(latest.Seq, call.Attempt.Sequence, "quiet generation owns latest input");
    call.Send(); await call.DoneAsync(); await f.DrainedAsync();
    Equal(1, f.Sends, "one final reply");
}

static async Task PrivateQuiet()
{
    await using var f = new ProbeHarness();
    f.Settings.GroupInterjectionProbability = 0;
    f.Input(Private(), direct: false); await f.SleepingAsync();
    await f.AtAsync(1499); await f.SleepingAsync();
    Equal(0, f.History.Count, "private generation waits below 1.5 seconds");
    await f.AtAsync(1500); var call = await f.NextAsync();
    Assert(call.Attempt.IsExplicit, "private reply is explicit and bypasses group lottery");
    call.Send(); await call.DoneAsync(); await f.DrainedAsync(); Equal(1, f.Sends, "private reply sent");
}

static async Task ContextOnly()
{
    await using var f = new ProbeHarness();
    var conversation = Group();
    f.Input(conversation, contextOnly: true);
    Equal(0, f.Pipeline.Works, "context alone does not own a worker");
    await f.AtAsync(1000); f.Input(conversation); await f.SleepingAsync();
    await f.AtAsync(2000); var latest = f.Input(conversation, contextOnly: true);
    await f.AtAsync(4999); await f.SleepingAsync(); Equal(0, f.History.Count, "context continuation renewed quiet");
    await f.AtAsync(5000); var call = await f.NextAsync();
    Equal(latest.Seq, call.Attempt.Sequence, "admitted work sees context freshness");
    call.Send(); await call.DoneAsync(); await f.DrainedAsync(); Equal(1, f.Sends, "one admitted reply");
}

static async Task ResourceFairness()
{
    await using var f = new ProbeHarness();
    f.Settings.PrivateContinuationWaitSeconds = 0;
    var group = Group(); var privateConversation = Private();
    f.Input(group); await f.SleepingAsync();
    f.Input(privateConversation); var ready = await f.NextAsync();
    Assert(ReferenceEquals(privateConversation, ready.Conversation), "ready conversation acquires the one global slot");
    ready.Send(); await ready.DoneAsync();
    await f.AtAsync(3000); var waiting = await f.NextAsync();
    Assert(ReferenceEquals(group, waiting.Conversation), "quiet conversation still gets its later turn");
    waiting.Send(); await waiting.DoneAsync(); await f.DrainedAsync(); Equal(2, f.Sends, "both conversations delivered");
}

static async Task Preemption()
{
    await using var f = new ProbeHarness(); f.Settings.GroupContinuationWaitSeconds = 0;
    var conversation = Group();
    f.Input(conversation, direct: false); var ordinary = await f.NextAsync();
    f.Input(conversation, speaker: 10002); var explicitCall = await f.NextAsync();
    await ordinary.DoneAsync();
    Assert(ordinary.Interrupted && ordinary.Token.IsCancellationRequested, "ordinary generation canceled before explicit work");
    Assert(explicitCall.Attempt.IsExplicit, "explicit generation takes over");
    Equal(0, f.Sends, "preempted draft cannot send");
    explicitCall.Send(); await explicitCall.DoneAsync(); await f.DrainedAsync(); Equal(1, f.Sends, "only explicit reply sent");
}

static async Task StaleDraft()
{
    await using var f = new ProbeHarness(); f.Settings.GroupContinuationWaitSeconds = 0;
    var conversation = Group();
    f.Input(conversation); var stale = await f.NextAsync();
    var latest = f.Input(conversation); stale.Send(); await stale.DoneAsync();
    Assert(stale.Interrupted, "stale draft validation interrupts attempt"); Equal(0, f.Sends, "stale draft not sent");
    var fresh = await f.NextAsync(); Equal(latest.Seq, fresh.Attempt.Sequence, "retry snapshots latest sequence");
    fresh.Send(); await fresh.DoneAsync(); await f.DrainedAsync();
    Equal(2, f.History.Count, "one recomputation"); Equal(1, f.Sends, "only fresh draft sent");
}

static async Task FinalPreflight()
{
    await using var f = new ProbeHarness(); f.Settings.GroupContinuationWaitSeconds = 0;
    var conversation = Group(); f.Input(conversation); var stale = await f.NextAsync(); stale.SendPrepared();
    await stale.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var latest = f.Input(conversation); stale.Continue(); await stale.DoneAsync();
    Equal(0, f.Sends, "freshness changed during source preflight");
    var fresh = await f.NextAsync(); Equal(latest.Seq, fresh.Attempt.Sequence, "final guard schedules latest draft");
    fresh.SendPrepared(hold: false); await fresh.DoneAsync(); await f.DrainedAsync();
    Equal(2, f.Preflights, "preflight repeated for fresh attempt"); Equal(1, f.Sends, "one final send");
}

static async Task EffectQuiet()
{
    await using var f = new ProbeHarness();
    var conversation = Group(); f.Input(conversation, direct: false); await f.SleepingAsync();
    await f.AtAsync(3000); var call = await f.NextAsync(); call.EffectThenHold();
    await call.EffectReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await f.AtAsync(4000); f.Input(conversation, contextOnly: true); call.Continue(); await f.SleepingAsync();
    await f.AtAsync(6500); f.Input(conversation, speaker: 10002, contextOnly: true);
    await f.AtAsync(6999); await f.SleepingAsync(); Equal(0, f.Sends, "effect draft waits latest same-human quiet");
    await f.AtAsync(7000); await call.DoneAsync(); await f.DrainedAsync();
    Equal(1, f.History.Count, "committed result never regenerated"); Equal(1, f.Effects, "effect executed once");
    Equal(1, f.Sends, "other human did not postpone effect result");
}

static async Task Stop()
{
    await using var f = new ProbeHarness(); f.Settings.GroupContinuationWaitSeconds = 0;
    var conversation = Group(); f.Input(conversation); var call = await f.NextAsync();
    f.Pipeline.Stop(); await call.DoneAsync(); await f.DrainedAsync();
    Assert(call.Interrupted && call.Token.IsCancellationRequested, "stop cancels generation");
    f.Input(conversation); await f.AtAsync(100000);
    Equal(1, f.History.Count, "stopped pipeline refuses ingress"); Equal(0, f.Sends, "stop has no late send");
}

static async Task Toggle()
{
    await using var f = new ProbeHarness();
    var conversation = Group();
    f.Input(conversation); f.Input(conversation, direct: false, speaker: 10002); await f.SleepingAsync();
    f.ToggleAi(false); await f.DrainedAsync();
    Equal(0, f.History.Count, "no generation after AI disable");
    f.ToggleAi(false); f.ToggleAi(true); await f.AtAsync(3000);
    Equal(0, f.History.Count, "reenable cannot replay queued work");
    f.Input(conversation); await f.SleepingAsync();
    await f.AtAsync(6000); var call = await f.NextAsync(); call.Send(); await call.DoneAsync(); await f.DrainedAsync();
    Equal(1, f.Sends, "reenabled AI creates only fresh work");
}

static async Task EffectParameterUpdate()
{
    await using var f = new ProbeHarness(); f.Settings.GroupContinuationWaitSeconds = 0;
    f.Input(Group(), direct: false); var call = await f.NextAsync(); call.EffectThenHold();
    await call.EffectReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var settings = f.Settings.Snapshot();
    settings.GroupInterjectionProbability = 0;
    settings.GroupContinuationWaitSeconds = 10;
    f.Pipeline.ReplaceSettings(settings); call.Continue(); await call.DoneAsync(); await f.DrainedAsync();
    Equal(1, f.Effects, "accepted effect remains owned"); Equal(1, f.Sends, "irreversible result stays deliverable");
    Equal(1, f.History.Count, "irreversible work is not regenerated after parameter changes");
}

static async Task PermissionRevocation()
{
    await using var f = new ProbeHarness(); f.Settings.GroupContinuationWaitSeconds = 0;
    f.Input(Group()); var call = await f.NextAsync(); f.Pipeline.Deny(); call.Send();
    await call.DoneAsync(); await f.DrainedAsync();
    Assert(call.Interrupted, "revoked permission invalidates final boundary"); Equal(0, f.Sends, "revoked owner never sends");
}

static async Task DeliveryCooldown()
{
    await using var f = new ProbeHarness(); f.Settings.GroupContinuationWaitSeconds = 0;
    var conversation = Group(); f.Input(conversation, direct: false); var failed = await f.NextAsync();
    failed.Send(full: false); await failed.DoneAsync(); await f.DrainedAsync();
    f.Input(conversation, direct: false, speaker: 10002); var success = await f.NextAsync();
    success.Send(); await success.DoneAsync(); await f.DrainedAsync();
    f.Input(conversation, direct: false, speaker: 10003); await f.DrainedAsync();
    Equal(2, f.History.Count, "failure allows another attempt while successful send starts cooldown");
    Equal(1, f.Sends, "exactly one full send");
}

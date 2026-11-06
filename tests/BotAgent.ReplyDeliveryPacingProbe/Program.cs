using System.Reflection;
using BotAgent.Domain.Reply;
using BotAgent.Platforms;
using BotAgent.Services.Conversations;
using BotAgent.Services.Official;
using BotAgent.Services.Ops;
using BotAgent.Services.Ports;
using BotAgent.Services.Reply;

PlatformClock.Initialize(new TestClock());

var passed = 0;
void Check(string label, bool value)
{
    if (!value) throw new InvalidOperationException(label);
    passed++;
}
async Task Canceled(string label, Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { passed++; return; }
    throw new InvalidOperationException(label);
}
PlainSender Sender(SyntheticSource source, TestRiskBackoff? backoff = null) => new(new TestSettings(), source, null!, null!,
    new OwnMessageLedger(new MemoryOwnMessages(), _ => { }), _ => { }, new TurnTraceStore(), riskBackoff: backoff);
OfficialBotGateway Official(SyntheticHttp http) => new(new PlatformSettingsBox(new PlatformOptions
{
    OfficialApiBase = "https://example.com", OfficialTokenUrl = "https://example.com/token",
}), http, new SyntheticIds(), _ => { });

Check("empty cadence is not completed", !new CadenceSendReport(Array.Empty<string>()).AllSent);
Check("none cadence is not completed", !CadenceSendReport.None.AllSent);
Check("nonempty acknowledged cadence completes", new CadenceSendReport(new[] { "a" }).AllSent);
Check("partial cadence is not completed", !new CadenceSendReport(new[] { "a" }, "segment_failed").AllSent);
var noParts = Array.Empty<string>();
Check("voice-only acknowledgment completes chat", ChatDeliveryReport.FromAcknowledgements(CadenceSendReport.None, false, new[] { "a" }, true, noParts).CompleteChatReply);
Check("partial voice never completes", !ChatDeliveryReport.FromAcknowledgements(CadenceSendReport.None, false, new[] { "a", "b" }, true, new[] { "b" }).CompleteChatReply);
Check("text failure after voice preserves partial chat", ChatDeliveryReport.FromAcknowledgements(CadenceSendReport.None, true, new[] { "a" }, true, noParts) is { AnyChatSent: true, CompleteChatReply: false });
Check("image or poke only is not chat", ChatDeliveryReport.FromAcknowledgements(CadenceSendReport.None, false, noParts, false, noParts) is { AnyChatSent: false, CompleteChatReply: false });
Check("fully filtered text is not complete", !ChatDeliveryReport.FromAcknowledgements(CadenceSendReport.None, true, noParts, false, noParts).CompleteChatReply);
Check("empty acknowledged segment is not chat", !ChatDeliveryReport.FromAcknowledgements(new CadenceSendReport(new[] { " " }), true, noParts, false, noParts).AnyChatSent);
Check("text fallback does not erase voice failure", !ChatDeliveryReport.FromAcknowledgements(new CadenceSendReport(new[] { "fallback" }), true, new[] { "voice" }, false, new[] { "voice" }).CompleteChatReply);
Check("failed extras conservatively prevent completion", !ChatDeliveryReport.FromAcknowledgements(new CadenceSendReport(new[] { "a" }), true, noParts, false, noParts, false).CompleteChatReply);
Check("full text and voice both complete", ChatDeliveryReport.FromAcknowledgements(new CadenceSendReport(new[] { "a" }), true, new[] { "a" }, true, noParts).CompleteChatReply);
await ReplyCommitBoundary.BeforeSendAsync();
await ReplyCommitBoundary.BeforePrepareAsync();
Check("off synchronous commit never waits", ReplyCommitBoundary.TryBeforeSend());

var outer = new TestGuard();
using (ReplyCommitBoundary.Enter(outer))
{
    await ReplyCommitBoundary.BeforeSendAsync();
    var inner = new TestGuard();
    using (ReplyCommitBoundary.Enter(inner)) await Task.Run(ReplyCommitBoundary.BeforeSendAsync);
    await ReplyCommitBoundary.BeforeSendAsync();
    Check("async nested guard restores outer", outer.Tries == 2 && inner.Tries == 1 && outer.Sends == 0 && inner.Sends == 0);
    await ReplyCommitBoundary.BeforeSourceSendAsync(new StagedSyntheticSource());
    Check($"staged preparation is neither send nor business effect (prepare={outer.Prepares}, commit={outer.Tries}, effect={outer.Effects})", outer.Prepares == 3 && outer.Tries == 2 && outer.Effects == 0);
    await ReplyCommitBoundary.BeforeSourceSendAsync(new SyntheticSource());
    Check("direct source gets actual send boundary", outer.Tries == 3 && outer.Prepares == 4);
    var allowed = true;
    using (ReplyCommitBoundary.EnterValidation(() => { if (!allowed) throw new OperationCanceledException(); }))
    {
        allowed = false;
        await Canceled("late validation blocks asynchronous send", ReplyCommitBoundary.BeforeSendAsync);
        await Canceled("late validation blocks synchronous commit", () => { ReplyCommitBoundary.TryBeforeSend(); return Task.CompletedTask; });
        Check("failed local validation never reaches first-send fence", outer.Tries == 3);
    }
    Check("validation scope restores previous checks", ReplyCommitBoundary.TryBeforeSend());
}
Check("guard scope restored to off", !ReplyCommitBoundary.IsActive);

var retryGuard = new TestGuard { OnTry = call => call > 1 };
var retryValidations = 0;
using (ReplyCommitBoundary.Enter(retryGuard))
using (ReplyCommitBoundary.EnterValidation(() => retryValidations++))
{
    await ReplyCommitBoundary.BeforeSendAsync();
    Check("direct boundary retries readiness before final commit", retryGuard.Prepares == 2 && retryGuard.Tries == 2 && retryValidations == 2 && retryGuard.Sends == 0);
}
using (ReplyCommitBoundary.EnterValidation(() => throw new InvalidOperationException("off_validation_ran")))
{
    await ReplyCommitBoundary.BeforeSendAsync();
    await ReplyCommitBoundary.BeforePrepareAsync();
    Check("off skips optional validation entirely", ReplyCommitBoundary.TryBeforeSend());
}

var first = new SyntheticSource();
using (ReplyCommitBoundary.Enter(new TestGuard { OnTry = _ => throw new OperationCanceledException() }))
    await Canceled("first-send rejection propagates for redraft", async () => await Sender(first).SendWithCadenceAsync(true, 10001, "firstpart。secondpart。", null));
Check("rejected draft has no outward call", first.TextCalls == 0);

var partial = new SyntheticSource();
var partialGuard = new TestGuard { OnTry = call => { if (call == 2) throw new OperationCanceledException(); return true; } };
using (ReplyCommitBoundary.Enter(partialGuard))
{
    var report = await Sender(partial).SendWithCadenceAsync(true, 10001, "firstpart。secondpart。", null);
    Check("late rejection preserves exactly one acknowledgment", report.AnySent && !report.AllSent && report.SentSegments.Count == 1 && partial.TextCalls == 1);
}
var failed = new SyntheticSource { TextResult = call => new(call != 2) };
using (ReplyCommitBoundary.Enter(new TestGuard()))
{
    var report = await Sender(failed).SendWithCadenceAsync(true, 10001, "firstpart。secondpart。thirdpart。", null);
    Check("failed second segment ends further sends", report.AnySent && !report.AllSent && failed.TextCalls == 2);
}
var complete = new SyntheticSource();
var completeGuard = new TestGuard();
using (ReplyCommitBoundary.Enter(completeGuard))
{
    var report = await Sender(complete).SendWithCadenceAsync(true, 10001, "firstpart。secondpart。", null);
    Check("every segment reaches its final boundary", report.AllSent && complete.TextCalls == 2 && completeGuard.Tries == 2 && completeGuard.Sends == 0);
}
var legacy = new SyntheticSource();
Check("off sender retains delivery", (await Sender(legacy).SendWithCadenceAsync(true, 10001, "firstpart。secondpart。", null)).AllSent && legacy.TextCalls == 2);

var backoff = new TestRiskBackoff();
var riskySource = new SyntheticSource { TextResult = _ => { backoff.Active = true; return new(true); } };
using (ReplyCommitBoundary.Enter(new TestGuard()))
{
    var report = await Sender(riskySource, backoff).SendWithCadenceAsync(true, 10001, "firstpart。secondpart。", null);
    Check("live risk backoff stops remaining cadence", report.AnySent && !report.AllSent && riskySource.TextCalls == 1);
}
backoff.Active = false;
var shortenedSource = new SyntheticSource();
using (ReplyCommitBoundary.Enter(new TestGuard { OnPrepare = _ => backoff.Active = true }))
    await Canceled("final backoff cannot silently rewrite prepared text", async () => await Sender(shortenedSource, backoff).SendWithCadenceAsync(true, 10001, "synthetic text", null, directAddress: true));
Check("backoff rewrite rejection has no outward call", shortenedSource.TextCalls == 0);
backoff.Active = false;
var legacyRiskSource = new SyntheticSource { TextResult = _ => { backoff.Active = true; return new(true); } };
Check("off retains legacy cadence after risk change", (await Sender(legacyRiskSource, backoff).SendWithCadenceAsync(true, 10001, "firstpart。secondpart。", null)).AllSent && legacyRiskSource.TextCalls == 2);

var mediaHttp = new SyntheticHttp();
using (var gateway = Official(mediaHttp))
{
    var guard = new TestGuard();
    using (ReplyCommitBoundary.Enter(guard))
    {
        await ReplyCommitBoundary.BeforeSourceSendAsync(gateway);
        Check("official prehook does not mark first send", guard.Sends == 0 && guard.Tries == 0 && guard.Effects == 0);
        Check("official voice acknowledged", await gateway.SendVoiceAsync(true, 10001, "https://example.com/speak"));
        Check("voice commits after one upload only", mediaHttp.Uploads == 1 && mediaHttp.Messages == 1 && guard.Tries == 1);
    }
}

var staleHttp = new SyntheticHttp();
using (var gateway = Official(staleHttp))
{
    var allowed = true;
    staleHttp.AfterUpload = () => allowed = false;
    using (ReplyCommitBoundary.Enter(new TestGuard()))
    using (ReplyCommitBoundary.EnterValidation(() => { if (!allowed) throw new OperationCanceledException(); }))
        await Canceled("policy change during upload blocks final send", async () => await gateway.SendVoiceAsync(true, 10001, "https://example.com/speak"));
    Check("uploaded stale media never reaches messages endpoint", staleHttp.Uploads == 1 && staleHttp.Messages == 0);
}

var gateHttp = new SyntheticHttp();
using (var gateway = Official(gateHttp))
{
    var gate = (SemaphoreSlim)typeof(OfficialBotGateway).GetField("_sendGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
    await gate.WaitAsync();
    var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var guard = new TestGuard { OnPrepare = _ => prepared.TrySetResult(), OnTry = call => call > 1 };
    using (ReplyCommitBoundary.Enter(guard))
    {
        var send = gateway.SendTextAsync(true, 10001, "synthetic text");
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
        Check("readiness rejected inside gate is retried outside it", (await send.WaitAsync(TimeSpan.FromSeconds(5))).Ok && guard.Prepares == 2 && guard.Tries == 2 && gateHttp.Messages == 1);
        Check("transport gate released after send", await gate.WaitAsync(0));
        gate.Release();
    }
}

var retryHttp = new SyntheticHttp { RejectFirstToken = true };
using (var gateway = Official(retryHttp))
{
    var guard = new TestGuard();
    using (ReplyCommitBoundary.Enter(guard))
    {
        Check("token rejection retries once", (await gateway.SendTextAsync(true, 10001, "synthetic text")).Ok);
        Check("retry repeats final commit validation", retryHttp.Messages == 2 && retryHttp.Tokens == 2 && guard.Tries == 2 && guard.Prepares == 2);
    }
}
await OneBotChecks.Run(Check, Canceled);
Console.WriteLine($"Reply delivery pacing probe: {passed} checks passed.");

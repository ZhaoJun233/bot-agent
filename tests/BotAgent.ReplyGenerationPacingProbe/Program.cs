using System.Reflection;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Stickers;
using BotAgent.Services;
using BotAgent.Services.Reply;

var cases = new (string Name, Func<Task> Run)[]
{
    ("soft attempt advertises no tools and invokes one model step", SoftDraft),
    ("normal loop validates each completion before inline actions", NormalOrdering),
    ("stale draft interrupts before actions without losing usage", StaleDraft),
    ("quiet wait retains one completed result instead of rerunning", WaitForQuiet),
    ("turn notes are consumed once across retries including empty notes", RetainedNotes),
    ("paced sampling mode is carried by the immutable turn settings", SnapshotMode),
    ("legacy loop has no pacing validation and preserves tool steps", LegacyLoop),
    ("commit ambient scope is restored after nested operations", CommitScope),
    ("accepted natural task outlives reply generation without replay", NaturalTaskChecks.DetachedCompletion),
    ("accepted natural task retains independent source validation", NaturalTaskChecks.RetainedValidation)
};
var failed = 0;
foreach (var (name, run) in cases)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"Reply generation pacing probe: {cases.Length - failed}/{cases.Length} passed.");
return failed == 0 ? 0 : 1;

static async Task SoftDraft()
{
    var fixture = new Fixture();
    var attempt = new Attempt(fixture.Settings, soft: true);
    var inline = 0;
    var result = await new AgentTurnLoop(fixture.Model).RunAsync("group:10001", fixture.Turn, fixture.Caps,
        false, 3, _ => { inline++; return Task.FromResult<string?>("Synthetic fact"); },
        agentHint: "Synthetic natural task hint", pacing: attempt);
    Ensure(result.Steps == 1 && inline == 0 && fixture.Calls == 1, "soft mode must be a single no-tool step");
    Ensure(attempt.Validations == 1, "completed soft result is validated once");
    foreach (var name in new[] { "enableWebSearch", "enableListen", "enableVoice", "enableAsk", "enableToolRequest", "pokeContext" })
        Ensure(fixture.Last[name] is false, $"soft flag must be false: {name}");
    Ensure(fixture.Last["stickers"] is null, "no sticker choices in soft prompt");
    Ensure(string.IsNullOrEmpty((string?)fixture.Last["toolList"]), "no tools or natural-task hint in soft prompt");
    Ensure(result.Result.Reply == "Synthetic reply", "the text draft remains available for later delivery");
}

static async Task NormalOrdering()
{
    var fixture = new Fixture();
    var events = new List<string>();
    fixture.BeforeReturn = () => events.Add("model");
    var attempt = new Attempt(fixture.Settings, validate: () => { events.Add("validate"); return Task.CompletedTask; });
    var outcome = await new AgentTurnLoop(fixture.Model).RunAsync("group:10001", fixture.Turn, fixture.Caps,
        false, 3, _ => { events.Add("inline"); return Task.FromResult<string?>("Synthetic fact"); }, pacing: attempt);
    Ensure(events.SequenceEqual(new[] { "model", "validate", "inline", "model", "validate", "inline", "model", "validate" }),
        "freshness must precede every tool callback, including final result return");
    Ensure(outcome.Steps == 3 && outcome.InlineTools == 2 && attempt.Validations == 3, "normal bounded loop unchanged");
}

static async Task StaleDraft()
{
    var fixture = new Fixture();
    var attempt = new Attempt(fixture.Settings, validate: () => throw new ReplyPacingInterruptedException());
    var inline = 0; var charged = 0; var interrupted = false;
    try
    {
        await new AgentTurnLoop(fixture.Model).RunAsync("group:10001", fixture.Turn, fixture.Caps, false, 3,
            _ => { inline++; return Task.FromResult<string?>("Must not run"); },
            recordCompletedStep: result => charged += result.PromptTokens, pacing: attempt);
    }
    catch (ReplyPacingInterruptedException ex)
    {
        interrupted = true;
        Ensure(ex.Message == "reply_pacing_interrupted", "exception contains only a fixed code");
    }
    Ensure(interrupted && inline == 0 && fixture.Calls == 1 && charged == 7, "stale draft never executes tools or loses charged usage");
}

static async Task WaitForQuiet()
{
    var fixture = new Fixture();
    var quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var attempt = new Attempt(fixture.Settings, soft: true, validate: () => quiet.Task);
    var pending = new AgentTurnLoop(fixture.Model).RunAsync("group:10001", fixture.Turn, fixture.Caps, false, 3,
        _ => throw new InvalidOperationException("soft callback executed"), pacing: attempt);
    Ensure(!pending.IsCompleted && fixture.Calls == 1, "completed draft must await quiet without another model call");
    quiet.SetResult();
    var completed = await pending;
    Ensure(completed.Result.Reply == "Synthetic reply" && fixture.Calls == 1, "quiet releases the retained draft");
}

static Task RetainedNotes()
{
    var state = new ReplyPacingPromptState();
    var take = typeof(ReplyPacingPromptState).GetMethod("TakeOnce", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var kind = take.GetParameters()[0].ParameterType;
    foreach (var name in new[] { "Music", "Recall", "Search", "Link" })
    {
        var calls = 0;
        var note = Enum.Parse(kind, name);
        string? Pull() { calls++; return name == "Link" ? null : "Synthetic note"; }
        var first = take.Invoke(state, new object[] { note, (Func<string?>)Pull });
        var retry = take.Invoke(state, new object[] { note, (Func<string?>)Pull });
        Ensure(Equals(first, retry) && calls == 1, "same turn cannot consume destructive note twice");
    }
    Ensure(state.ToString() == nameof(ReplyPacingPromptState), "prompt state diagnostics omit note content");
    return Task.CompletedTask;
}

static async Task SnapshotMode()
{
    foreach (var enabled in new[] { false, true })
    {
        var fixture = new Fixture();
        fixture.Settings.ReplyPacingEnabled = enabled;
        await new AgentTurnLoop(fixture.Model).RunAsync("group:10001", fixture.Turn, fixture.Caps, false, 1,
            _ => Task.FromResult<string?>(null), pacing: new Attempt(fixture.Settings));
        var profile = (SamplingProfile)fixture.Last["sampling"]!;
        Ensure(profile.ReplyPacingEnabled == enabled, "sampling must carry the turn mode even with adaptive sampling disabled");
    }
}

static async Task LegacyLoop()
{
    var fixture = new Fixture();
    var inline = 0;
    var outcome = await new AgentTurnLoop(fixture.Model).RunAsync("group:10001", fixture.Turn, fixture.Caps, false, 2,
        _ => { inline++; return Task.FromResult<string?>("Synthetic fact"); }, agentHint: "Synthetic task hint");
    Ensure(outcome.Steps == 2 && inline == 1, "legacy max steps and callbacks are unchanged");
    Ensure(((string?)fixture.Last["toolList"])?.Contains("Synthetic task hint", StringComparison.Ordinal) == true,
        "legacy model still receives its task hint");
}

static async Task CommitScope()
{
    var first = new Guard(); var second = new Guard();
    using (ReplyCommitBoundary.Enter(first))
    {
        await ReplyCommitBoundary.BeforeEffectAsync();
        using (ReplyCommitBoundary.Enter(second)) await ReplyCommitBoundary.BeforeSendAsync();
        await ReplyCommitBoundary.BeforePrepareAsync();
    }
    await ReplyCommitBoundary.BeforeEffectAsync(); // legacy scope must be a no-op
    Ensure(first.Effects == 1 && first.Prepares == 1 && first.Sends == 0 && second.Sends == 1,
        "guard scopes do not leak to legacy or neighbouring async work");
}

static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

sealed class Attempt(AppSettings snapshot, bool soft = false, Func<Task>? validate = null) : IPacedReplyAttempt
{
    public AppSettings Snapshot { get; } = snapshot;
    public bool IsExplicit => true;
    public bool IsSoftDraft => soft;
    public long Sequence => 1;
    public ReplyPacingPromptState PromptState { get; } = new();
    public int Validations { get; private set; }
    public Task ValidateDraftAsync() { Validations++; return validate?.Invoke() ?? Task.CompletedTask; }
    public void RecordDelivery(ChatDeliveryReport report) { }
}

sealed class Guard : IReplyCommitGuard
{
    public int Effects, Sends, Prepares;
    public Task BeforeEffectAsync() { Effects++; return Task.CompletedTask; }
    public Task BeforeSendAsync() { Sends++; return Task.CompletedTask; }
    public Task BeforePrepareAsync() { Prepares++; return Task.CompletedTask; }
    public bool TryBeforeSend() { Sends++; return true; }
}

sealed class Fixture
{
    public AppSettings Settings { get; } = new()
    {
        ReplyPacingEnabled = true, EnableWebSearch = true, EnableMusic = true,
        EnableVoice = true, EnableStickers = true, EnablePoke = true, EnableApprovals = true,
        EnableQuestions = true, MaxAgentSteps = 3, AdaptiveSamplingEnabled = false
    };
    public ChatCapabilitySet Caps { get; } = ChatCapabilitySet.FromSwitches(true, true, true, true, true, null);
    public IModelClient Model { get; }
    public TurnInputs Turn { get; }
    public Dictionary<string, object?> Last { get; } = new();
    public int Calls { get; private set; }
    public Action? BeforeReturn { get; set; }
    public Fixture()
    {
        var platform = new EffectivePlatformPolicy(new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy),
            true, true, true, true, PlatformCapabilities.QqOneBot, new HashSet<string>(), new Dictionary<string, bool>(), Array.Empty<string>());
        Turn = new TurnInputs(DateTime.UnixEpoch, Settings, Caps, platform,
            new[] { new ChatMessage { Seq = 1, Role = MessageRole.Peer, Text = "Synthetic input", Timestamp = DateTimeOffset.UnixEpoch } },
            new(), 0, new() { new StickerChoice("synthetic", "Synthetic sticker") }, 0, null, null, "中性", true,
            null, "Synthetic music note", "Synthetic recall note", null, null);
        Model = DispatchProxy.Create<IModelClient, ModelPort>();
        ((ModelPort)(object)Model).Call = (method, arguments) =>
        {
            if (method.Name != "CompleteAsync") throw new InvalidOperationException("Unexpected model port method");
            Calls++; Last.Clear();
            for (var index = 0; index < method.GetParameters().Length; index++)
                Last[method.GetParameters()[index].Name!] = arguments![index];
            BeforeReturn?.Invoke();
            return Task.FromResult(new CompletionResult(100, "Synthetic reply", "{}", Search: "Synthetic search",
                Listen: "Synthetic song", Speak: "Synthetic speech", PromptTokens: 7));
        };
    }
}

public class ModelPort : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) => Call(method!, arguments);
}

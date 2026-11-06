using BotAgent.Engine.Runtime;

namespace BotAgent.Services.Reply;

/// <summary>Per-generation policy and lifecycle supplied by the scheduler, never by model output.</summary>
public interface IPacedReplyAttempt
{
    AppSettings Snapshot { get; }
    bool IsExplicit { get; }
    bool IsSoftDraft { get; }
    long Sequence { get; }
    Task ValidateDraftAsync();
    void RecordDelivery(ChatDeliveryReport report);
    ReplyPacingPromptState PromptState { get; }
}

/// <summary>Scheduler control flow, not a model/provider failure. Contains no source identifiers.</summary>
public sealed class ReplyPacingInterruptedException : OperationCanceledException
{
    public ReplyPacingInterruptedException() : base("reply_pacing_interrupted") { }
}

/// <summary>
/// Turn-owned destructive prompt reads. A discarded draft must not consume the next attempt's notes.
/// Each source is pulled once (including an empty result); later notes remain available to the next turn.
/// The short lock protects memoization only; callers must pass synchronous local-store reads.
/// </summary>
public sealed class ReplyPacingPromptState
{
    private readonly object _gate = new();
    private readonly Dictionary<Note, string?> _notes = new();
    internal enum Note { Music, Recall, Search, Link }

    internal string? TakeOnce(Note note, Func<string?> take)
    {
        lock (_gate)
        {
            if (_notes.TryGetValue(note, out var value)) return value;
            value = take();
            _notes.Add(note, value);
            return value;
        }
    }

    public override string ToString() => nameof(ReplyPacingPromptState);
}

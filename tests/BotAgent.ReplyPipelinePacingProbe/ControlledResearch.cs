using System.Collections.Concurrent;
using System.Threading.Channels;
using BotAgent.Services.Net;

namespace BotAgent.ReplyPipelinePacingProbe;

internal sealed class ControlledResearch : IResearchUseCase
{
    private readonly Channel<CancellationToken> _started = Channel.CreateUnbounded<CancellationToken>();
    private readonly ConcurrentDictionary<string, string> _notes = new();
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public bool IsReady => true;
    public Func<CancellationToken, Task<WebSearchResult?>>? OnSearch { get; set; }
    public bool TryBeginSearch(string sourceKey, int cooldownSeconds, out string reason)
    {
        reason = string.Empty;
        return true;
    }
    public async Task<WebSearchResult?> SearchAsync(string sourceKey, string query, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        _started.Writer.TryWrite(ct);
        var result = OnSearch is null ? Result() : await OnSearch(ct).WaitAsync(ct);
        if (result?.Answer is { } answer) AddNote(sourceKey, answer);
        return result;
    }
    public Task<string?> ReadPageAsync(string sourceKey, string url, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        _started.Writer.TryWrite(ct);
        return Task.FromResult<string?>("synthetic-page-result");
    }
    public void AddNote(string sourceKey, string note) => _notes[sourceKey] = note;
    public void KeepNote(string sourceKey, string? note, string why)
    {
        if (note is not null) AddNote(sourceKey, note);
    }
    public string? TakeNote(string sourceKey) => _notes.TryRemove(sourceKey, out var note) ? note : null;
    public Task<CancellationToken> NextSearch() => _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    public static WebSearchResult Result() => new("synthetic-query", "synthetic-research-result", [], "synthetic-provider");
}

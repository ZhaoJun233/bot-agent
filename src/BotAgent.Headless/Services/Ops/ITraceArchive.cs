using BotAgent.Domain.Ops;

namespace BotAgent.Services.Ops;

public interface ITraceArchive
{
    void Append(TurnTrace trace);
    TraceArchiveSummary Snapshot();
}

public readonly record struct TraceArchiveSummary(int Count, int[] Durations, int PromptTokens, int CompletionTokens);
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Ops;

namespace BotAgent.Services.Ops;

public interface ITraceArchive
{
    void Append(TurnTrace trace);
    TraceArchiveSummary Snapshot();
}

/// <summary>
/// 归档快照。<c>CachedPromptTokens</c> 是 <b>null = 这两天没有一条带缓存用量的行</b>（未知），
/// 不是 0；<c>CacheUnknownTurns</c> 是「上游没提供缓存用量」的条数（#135）。
/// </summary>
public readonly record struct TraceArchiveSummary(
    int Count,
    int[] Durations,
    int PromptTokens,
    int CompletionTokens,
    int? CachedPromptTokens = null,
    int CacheUnknownTurns = 0,
    int? CacheWriteTokens = null);
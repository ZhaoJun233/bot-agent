using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Ops;
using BotAgent.Services.Ops;

namespace BotAgent.Adapters.Persistence;

/// <summary>慢/异常轨迹的结构化归档。正文、提示词、模型响应和参数值永远不进入归档。</summary>
public sealed class TraceArchiveStore : ITraceArchive
{
    private readonly object _gate = new();
    private readonly List<int> _fallbackDurations = new();
    private int _fallbackCount;
    private int _fallbackPromptTokens;
    private int _fallbackCompletionTokens;
    private int? _fallbackCachedPromptTokens;
    private int _fallbackCacheUnknownTurns;
    private int? _fallbackCacheWriteTokens;

    public void Append(TurnTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var stageTimings = JsonSerializer.Serialize(trace.Nodes.Select(n => new
        {
            kind = n.Kind.ToString(),
            status = n.Status,
            ms = n.DurationMs,
            tool = n.ToolId,
            reason = n.ReasonCode,
            count = n.Count
        }));

        var promptTokens = Math.Max(0, trace.PromptTokens);
        var completionTokens = Math.Max(0, trace.CompletionTokens);
        var fallbackHops = Math.Max(0, trace.FallbackHops);

        AppDatabase.Write(conn => AppDatabase.Exec(conn, """
            INSERT OR REPLACE INTO trace_archive
              (trace_id, tenant_id, status_code, reason_code, total_ms, stage_timings_json,
               prompt_tokens, completion_tokens, fallback_hops,
               cached_prompt_tokens, cache_write_tokens, cache_usage_unknown)
            VALUES ($id, $tenant, $status, $reason, $total, $stages, $prompt, $completion, $hops,
                    $cached, $write, $unknown);
            DELETE FROM trace_archive
            WHERE rowid IN (
                SELECT rowid FROM trace_archive
                WHERE created_at < datetime('now', '-7 days')
                ORDER BY created_at
                LIMIT 500
            );
            """,
            ("$id", trace.RunId),
            ("$tenant", TenantHash(trace.ConversationKey)),
            ("$status", trace.Outcome),
            ("$reason", trace.Nodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.ReasonCode))?.ReasonCode),
            ("$total", trace.TotalMs),
            ("$stages", stageTimings),
            ("$prompt", promptTokens),
            ("$completion", completionTokens),
            ("$hops", fallbackHops),
            // #135：缓存用量三态 —— 未知写 NULL（不是 0），未知的轮次在 cache_usage_unknown 里留痕。
            ("$cached", trace.CachedPromptTokens),
            ("$write", trace.CacheWriteTokens),
            ("$unknown", trace.CacheUsageUnknown ? 1 : 0)));

        lock (_gate)
        {
            _fallbackCount++;
            _fallbackPromptTokens = (int)Math.Clamp((long)_fallbackPromptTokens + promptTokens, 0, int.MaxValue);
            _fallbackCompletionTokens = (int)Math.Clamp((long)_fallbackCompletionTokens + completionTokens, 0, int.MaxValue);
            if (trace.CacheUsageUnknown)
            {
                _fallbackCacheUnknownTurns++;
            }
            else if (trace.CachedPromptTokens is { } cached)
            {
                _fallbackCachedPromptTokens = (int)Math.Clamp((long)(_fallbackCachedPromptTokens ?? 0) + Math.Max(0, cached), 0, int.MaxValue);
            }

            if (trace.CacheWriteTokens is { } written)
            {
                _fallbackCacheWriteTokens = (int)Math.Clamp((long)(_fallbackCacheWriteTokens ?? 0) + Math.Max(0, written), 0, int.MaxValue);
            }
            _fallbackDurations.Add(trace.TotalMs);
            if (_fallbackDurations.Count > 5000)
            {
                _fallbackDurations.RemoveAt(0);
            }
        }
    }

    public TraceArchiveSummary Snapshot()
    {
        try
        {
            var rows = AppDatabase.Query("""
                SELECT total_ms, prompt_tokens, completion_tokens,
                       cached_prompt_tokens, cache_usage_unknown, cache_write_tokens
                FROM trace_archive
                WHERE created_at >= datetime('now', '-7 days')
                ORDER BY total_ms;
                """, r => new ArchiveRow(
                    r.GetInt32(0),
                    r.GetInt64(1),
                    r.GetInt64(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3),
                    // 旧行（v13 之前归档的、或这一轮上游没给字段）两列都是「没有」—— 同样算未知。
                    r.IsDBNull(3) || r.GetInt32(4) != 0,
                    r.IsDBNull(5) ? null : r.GetInt64(5)));

            var cachedKnown = rows.Where(row => row.CachedPromptTokens is not null).ToList();
            return new TraceArchiveSummary(
                rows.Count,
                rows.Select(row => row.TotalMs).ToArray(),
                checked((int)Math.Clamp(rows.Sum(row => row.PromptTokens), 0, int.MaxValue)),
                checked((int)Math.Clamp(rows.Sum(row => row.CompletionTokens), 0, int.MaxValue)),
                cachedKnown.Count == 0
                    ? null
                    : checked((int)Math.Clamp(cachedKnown.Sum(row => row.CachedPromptTokens!.Value), 0, int.MaxValue)),
                rows.Count(row => row.CacheUnknown),
                rows.Any(row => row.CacheWriteTokens is not null)
                    ? checked((int)Math.Clamp(rows.Sum(row => row.CacheWriteTokens ?? 0), 0, int.MaxValue))
                    : null);
        }
        catch
        {
            // 观测端点不可因数据库瞬时不可用而拖垮主链；回退到本进程内的最近归档形状。
            lock (_gate)
            {
                return new TraceArchiveSummary(
                    _fallbackCount,
                    _fallbackDurations.OrderBy(x => x).ToArray(),
                    _fallbackPromptTokens,
                    _fallbackCompletionTokens,
                    _fallbackCachedPromptTokens,
                    _fallbackCacheUnknownTurns,
                    _fallbackCacheWriteTokens);
            }
        }
    }

    private static string TenantHash(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key ?? string.Empty)))
            .ToLowerInvariant()[..16];

    private readonly record struct ArchiveRow(
        int TotalMs,
        long PromptTokens,
        long CompletionTokens,
        long? CachedPromptTokens,
        bool CacheUnknown,
        long? CacheWriteTokens);
}

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

        AppDatabase.Write(conn => AppDatabase.Exec(conn, """
            INSERT OR REPLACE INTO trace_archive
              (trace_id, tenant_id, status_code, reason_code, total_ms, stage_timings_json,
               prompt_tokens, completion_tokens, fallback_hops)
            VALUES ($id, $tenant, $status, $reason, $total, $stages, 0, 0, 0);
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
            ("$stages", stageTimings)));

        lock (_gate)
        {
            _fallbackCount++;
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
                SELECT total_ms, prompt_tokens, completion_tokens
                FROM trace_archive
                WHERE created_at >= datetime('now', '-7 days')
                ORDER BY total_ms;
                """, r => new ArchiveRow(
                    r.GetInt32(0),
                    r.GetInt64(1),
                    r.GetInt64(2)));

            return new TraceArchiveSummary(
                rows.Count,
                rows.Select(row => row.TotalMs).ToArray(),
                checked((int)Math.Clamp(rows.Sum(row => row.PromptTokens), 0, int.MaxValue)),
                checked((int)Math.Clamp(rows.Sum(row => row.CompletionTokens), 0, int.MaxValue)));
        }
        catch
        {
            // 观测端点不可因数据库瞬时不可用而拖垮主链；回退到本进程内的最近归档形状。
            lock (_gate)
            {
                return new TraceArchiveSummary(
                    _fallbackCount,
                    _fallbackDurations.OrderBy(x => x).ToArray(),
                    0,
                    0);
            }
        }
    }

    private static string TenantHash(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key ?? string.Empty)))
            .ToLowerInvariant()[..16];

    private readonly record struct ArchiveRow(int TotalMs, long PromptTokens, long CompletionTokens);
}

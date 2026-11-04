using System.Globalization;
using BotAgent.Domain.Agent;
using BotAgent.Engine.Runtime;
using BotAgent.Services;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 任务台账的 SQLite 实现（#116）：<c>agent_tasks</c> 一张表，一份任务一行。
///
/// 三条硬约束（ADR-0003）：
///   • 只存形状与计数 —— 提示词、回答正文、工具参数一律不进这张表；
///   • 写入失败只能记日志（<see cref="IAgentTaskLedger" /> 的约定），存储问题不许决定任务成败；
///   • 行数有上限（<see cref="Retain" />），避免长期运行把库撑大。
///
/// 为什么放在 Headless/Adapters/Persistence：只有这个前缀允许写 SQL（ArchitectureProbe R2），
/// 放进 BotAgent.Storage 反而会红（那条规则是"存储层不新增长期不登记的文件"）。
/// </summary>
public sealed class AgentTaskLedgerStore : IAgentTaskLedger
{
    /// <summary>库里最多保留多少行（与通道台账 200 条同量级，留一点余量给淘汰后的追溯）。</summary>
    private const int Retain = 256;

    private const string BootSeqMetaKey = "agent_tasks_boot_seq";

    /// <summary>本次进程的启动序号；恢复时用它区分"上一进程写的行"。</summary>
    public long BootSeq { get; }

    public AgentTaskLedgerStore() => BootSeq = NextBootSeq();

    /// <summary>写入或覆盖一行（状态每变一次写一次，必须幂等）。</summary>
    public void Save(AgentTaskLedgerEntry entry)
    {
        try
        {
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                INSERT OR REPLACE INTO agent_tasks
                  (id, state, error_code, source_key, session_id, target, created_at, updated_at,
                   duration_ms, tool_calls, cancel_requested, result_chars, result_truncated, boot_seq)
                VALUES ($id, $state, $error, $source, $session, $target, $created, $updated,
                        $duration, $tools, $cancel, $chars, $truncated, $boot);
                """,
                ("$id", entry.Id),
                ("$state", (int)entry.State),
                ("$error", entry.ErrorCode ?? string.Empty),
                ("$source", entry.SourceKey ?? string.Empty),
                ("$session", entry.SessionId ?? string.Empty),
                ("$target", entry.Target ?? string.Empty),
                ("$created", Stamp(entry.CreatedAt)),
                ("$updated", Stamp(entry.UpdatedAt)),
                ("$duration", entry.DurationMs),
                ("$tools", entry.ToolCalls),
                ("$cancel", entry.CancellationRequested ? 1 : 0),
                ("$chars", entry.ResultChars),
                ("$truncated", entry.ResultTruncated ? 1 : 0),
                ("$boot", entry.BootSeq)));
        }
        catch (Exception exception)
        {
            // 存不下就不存：台账是观测设施，不是任务成败的一部分。
            FileLog.Warn("Task", $"任务台账写入失败（任务状态不受影响）：{exception.Message}");
        }
    }

    /// <summary>按更新时间倒序取最近若干行（启动对账用）。</summary>
    public IReadOnlyList<AgentTaskLedgerEntry> LoadRecent(int limit)
    {
        if (limit <= 0) return Array.Empty<AgentTaskLedgerEntry>();
        try
        {
            return AppDatabase.Query("""
                SELECT id, state, error_code, source_key, session_id, target, created_at, updated_at,
                       duration_ms, tool_calls, cancel_requested, result_chars, result_truncated, boot_seq
                FROM agent_tasks
                ORDER BY updated_at DESC
                LIMIT $limit;
                """,
                reader => new AgentTaskLedgerEntry
                {
                    Id = AppDatabase.Str(reader, "id") ?? string.Empty,
                    State = (AgentTaskState)AppDatabase.Int(reader, "state"),
                    ErrorCode = NullIfEmpty(AppDatabase.Str(reader, "error_code")),
                    SourceKey = AppDatabase.Str(reader, "source_key") ?? string.Empty,
                    SessionId = AppDatabase.Str(reader, "session_id") ?? string.Empty,
                    Target = AppDatabase.Str(reader, "target") ?? "server",
                    CreatedAt = ParseStamp(AppDatabase.Str(reader, "created_at")),
                    UpdatedAt = ParseStamp(AppDatabase.Str(reader, "updated_at")),
                    DurationMs = AppDatabase.LongOrNull(reader, "duration_ms"),
                    ToolCalls = AppDatabase.Int(reader, "tool_calls"),
                    CancellationRequested = AppDatabase.Bool(reader, "cancel_requested"),
                    ResultChars = AppDatabase.Int(reader, "result_chars"),
                    ResultTruncated = AppDatabase.Bool(reader, "result_truncated"),
                    BootSeq = AppDatabase.Long(reader, "boot_seq")
                },
                ("$limit", limit));
        }
        catch (Exception exception)
        {
            FileLog.Warn("Task", $"任务台账读取失败（按空台账启动）：{exception.Message}");
            return Array.Empty<AgentTaskLedgerEntry>();
        }
    }

    /// <summary>只保留最近 <paramref name="keep" /> 行，返回删掉的条数。</summary>
    public int Prune(int keep)
    {
        if (keep <= 0) keep = Retain;
        try
        {
            var removed = 0;
            AppDatabase.Write(conn => removed = AppDatabase.ExecCount(conn, """
                DELETE FROM agent_tasks
                WHERE id NOT IN (
                    SELECT id FROM agent_tasks ORDER BY updated_at DESC LIMIT $keep
                );
                """,
                ("$keep", keep)));
            return removed;
        }
        catch (Exception exception)
        {
            FileLog.Warn("Task", $"任务台账清理失败（不影响任务）：{exception.Message}");
            return 0;
        }
    }

    private static long NextBootSeq()
    {
        try
        {
            var raw = AppDatabase.GetMeta(BootSeqMetaKey);
            var current = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            var next = current + 1;
            AppDatabase.SetMeta(BootSeqMetaKey, next.ToString(CultureInfo.InvariantCulture));
            return next;
        }
        catch (Exception exception)
        {
            FileLog.Warn("Task", $"启动序号不可用（按 0 处理）：{exception.Message}");
            return 0;
        }
    }

    private static string Stamp(DateTimeOffset value)
        => value.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? DateTimeOffset.UnixEpoch
            : DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

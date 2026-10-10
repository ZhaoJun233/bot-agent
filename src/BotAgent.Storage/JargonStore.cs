using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BotAgent.Domain.Jargon;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 圈子黑话/俚语 SQLite 持久化仓储（对标 MaiBot Jargon 机制）。
/// </summary>
public class JargonStore : IJargonRepository
{
    public Task<JargonEntry?> GetAsync(string scope, string phrase)
    {
        var list = AppDatabase.Query(
            "SELECT id, scope, phrase, meaning, status, hit_count, created_unix, updated_unix " +
            "FROM jargons WHERE scope = $s AND phrase = $p LIMIT 1",
            Map,
            ("$s", scope), ("$p", phrase));

        return Task.FromResult(list.FirstOrDefault());
    }

    public Task<IReadOnlyList<JargonEntry>> ListByScopeAsync(string scope, JargonStatus? status = null)
    {
        var sql = status.HasValue
            ? "SELECT id, scope, phrase, meaning, status, hit_count, created_unix, updated_unix " +
              "FROM jargons WHERE scope = $s AND status = $st ORDER BY updated_unix DESC"
            : "SELECT id, scope, phrase, meaning, status, hit_count, created_unix, updated_unix " +
              "FROM jargons WHERE scope = $s ORDER BY updated_unix DESC";

        var list = status.HasValue
            ? AppDatabase.Query(sql, Map, ("$s", scope), ("$st", (int)status.Value))
            : AppDatabase.Query(sql, Map, ("$s", scope));

        return Task.FromResult<IReadOnlyList<JargonEntry>>(list);
    }

    public Task<IReadOnlyList<JargonEntry>> ListConfirmedForPromptAsync(string scope, int limit = 20)
    {
        var list = AppDatabase.Query(
            "SELECT id, scope, phrase, meaning, status, hit_count, created_unix, updated_unix " +
            "FROM jargons WHERE (scope = $s OR scope = 'global') AND status IN (1, 3) " +
            "ORDER BY hit_count DESC, updated_unix DESC LIMIT $lim",
            Map,
            ("$s", scope), ("$lim", Math.Clamp(limit, 1, 50)));

        return Task.FromResult<IReadOnlyList<JargonEntry>>(list);
    }

    public Task UpsertAsync(JargonEntry entry)
    {
        var now = Clock.Now.ToUnixTimeSeconds();
        var created = entry.CreatedAt > DateTimeOffset.MinValue
            ? entry.CreatedAt.ToUnixTimeSeconds()
            : now;
        var updated = entry.UpdatedAt > DateTimeOffset.MinValue
            ? entry.UpdatedAt.ToUnixTimeSeconds()
            : now;

        AppDatabase.Write(conn =>
        {
            AppDatabase.Exec(conn,
                "INSERT INTO jargons(scope, phrase, meaning, status, hit_count, created_unix, updated_unix) " +
                "VALUES($s, $p, $m, $st, $h, $c, $u) " +
                "ON CONFLICT(scope, phrase) DO UPDATE SET " +
                "meaning = excluded.meaning, " +
                "hit_count = jargons.hit_count + 1, " +
                "updated_unix = excluded.updated_unix",
                ("$s", entry.Scope),
                ("$p", entry.Phrase),
                ("$m", entry.Meaning),
                ("$st", (int)entry.Status),
                ("$h", Math.Max(1, entry.HitCount)),
                ("$c", created),
                ("$u", updated));
        });
        return Task.CompletedTask;
    }

    public Task SetStatusAsync(long id, JargonStatus status)
    {
        var now = Clock.Now.ToUnixTimeSeconds();
        AppDatabase.Write(conn =>
        {
            AppDatabase.Exec(conn,
                "UPDATE jargons SET status = $st, updated_unix = $u WHERE id = $id",
                ("$st", (int)status), ("$u", now), ("$id", id));
        });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(long id)
    {
        AppDatabase.Write(conn =>
        {
            AppDatabase.Exec(conn, "DELETE FROM jargons WHERE id = $id", ("$id", id));
        });
        return Task.CompletedTask;
    }

    private static JargonEntry Map(SqliteDataReader r)
    {
        return new JargonEntry
        {
            Id = AppDatabase.Long(r, "id"),
            Scope = AppDatabase.Str(r, "scope") ?? string.Empty,
            Phrase = AppDatabase.Str(r, "phrase") ?? string.Empty,
            Meaning = AppDatabase.Str(r, "meaning") ?? string.Empty,
            Status = (JargonStatus)AppDatabase.Int(r, "status"),
            HitCount = AppDatabase.Int(r, "hit_count"),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(AppDatabase.Long(r, "created_unix")),
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(AppDatabase.Long(r, "updated_unix"))
        };
    }
}

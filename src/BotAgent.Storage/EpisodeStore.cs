using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 长期记忆事件切片 SQLite 仓储实现（对标 MaiBot A-Memorix 架构）。
/// </summary>
public class EpisodeStore : IEpisodeRepository
{
    public Task<long> InsertAsync(EpisodeEntry episode)
    {
        var now = Clock.Now.ToUnixTimeSeconds();
        var occurred = episode.OccurredAt > DateTimeOffset.MinValue
            ? episode.OccurredAt.ToUnixTimeSeconds()
            : now;
        var created = episode.CreatedAt > DateTimeOffset.MinValue
            ? episode.CreatedAt.ToUnixTimeSeconds()
            : now;
        var updated = episode.UpdatedAt > DateTimeOffset.MinValue
            ? episode.UpdatedAt.ToUnixTimeSeconds()
            : now;

        var partJson = JsonSerializer.Serialize(episode.Participants ?? Array.Empty<string>());
        var tagsStr = string.Join(",", episode.Tags ?? Array.Empty<string>());

        long newId = 0;
        AppDatabase.Write(conn =>
        {
            AppDatabase.Exec(conn,
                "INSERT INTO episodes(scope, title, summary, participants, tags, importance, occurred_unix, created_unix, updated_unix) " +
                "VALUES($s, $t, $sm, $p, $tg, $imp, $occ, $c, $u)",
                ("$s", episode.Scope),
                ("$t", episode.Title),
                ("$sm", episode.Summary),
                ("$p", partJson),
                ("$tg", tagsStr),
                ("$imp", Math.Clamp(episode.Importance, 1, 5)),
                ("$occ", occurred),
                ("$c", created),
                ("$u", updated));

            // last_insert_rowid() 属于连接本身，必须在插入所用的事务连接上读取。
            using var idCommand = conn.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            newId = Convert.ToInt64(idCommand.ExecuteScalar());
        });

        return Task.FromResult(newId);
    }

    public Task<IReadOnlyList<EpisodeEntry>> ListRecentByScopeAsync(string scope, int limit = 10)
    {
        var list = AppDatabase.Query(
            "SELECT id, scope, title, summary, participants, tags, importance, occurred_unix, created_unix, updated_unix " +
            "FROM episodes WHERE scope = $s ORDER BY occurred_unix DESC LIMIT $lim",
            Map,
            ("$s", scope),
            ("$lim", Math.Clamp(limit, 1, 100)));

        return Task.FromResult<IReadOnlyList<EpisodeEntry>>(list);
    }

    public Task<IReadOnlyList<EpisodeEntry>> SearchAsync(string scope, string query, int limit = 5)
    {
        var pattern = $"%{query.Trim()}%";
        var list = AppDatabase.Query(
            "SELECT id, scope, title, summary, participants, tags, importance, occurred_unix, created_unix, updated_unix " +
            "FROM episodes WHERE scope = $s AND (title LIKE $q OR summary LIKE $q OR tags LIKE $q) " +
            "ORDER BY importance DESC, occurred_unix DESC LIMIT $lim",
            Map,
            ("$s", scope),
            ("$q", pattern),
            ("$lim", Math.Clamp(limit, 1, 50)));

        return Task.FromResult<IReadOnlyList<EpisodeEntry>>(list);
    }

    public Task DeleteAsync(long id)
    {
        AppDatabase.Write(conn =>
        {
            AppDatabase.Exec(conn, "DELETE FROM episodes WHERE id = $id", ("$id", id));
        });
        return Task.CompletedTask;
    }

    private static EpisodeEntry Map(SqliteDataReader r)
    {
        var partJson = AppDatabase.Str(r, "participants");
        IReadOnlyList<string> participants = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(partJson))
        {
            try
            {
                participants = JsonSerializer.Deserialize<List<string>>(partJson) ?? (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch
            {
                participants = Array.Empty<string>();
            }
        }

        var tagsStr = AppDatabase.Str(r, "tags");
        var tags = !string.IsNullOrWhiteSpace(tagsStr)
            ? (IReadOnlyList<string>)tagsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        return new EpisodeEntry
        {
            Id = AppDatabase.Long(r, "id"),
            Scope = AppDatabase.Str(r, "scope") ?? string.Empty,
            Title = AppDatabase.Str(r, "title") ?? string.Empty,
            Summary = AppDatabase.Str(r, "summary") ?? string.Empty,
            Participants = participants,
            Tags = tags,
            Importance = AppDatabase.Int(r, "importance"),
            OccurredAt = DateTimeOffset.FromUnixTimeSeconds(AppDatabase.Long(r, "occurred_unix")),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(AppDatabase.Long(r, "created_unix")),
            UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(AppDatabase.Long(r, "updated_unix"))
        };
    }
}

using Microsoft.Data.Sqlite;

namespace BotAgent.Platforms.Persistence;

internal static class PlatformDedupStore
{
    private static readonly object Gate = new();

    private static string ResolveDbPath()
    {
        var configured = Environment.GetEnvironmentVariable("BOTAGENT_DATA_DIR");
        if (string.IsNullOrWhiteSpace(configured))
            configured = Environment.GetEnvironmentVariable("QQCHAT_DATA_DIR");

        if (!string.IsNullOrWhiteSpace(configured))
            return Path.Combine(Path.GetFullPath(configured.Trim()), "data", "qqchat.db");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "runtime", "data", "qqchat.db");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        if (Directory.Exists("/data")) return "/data/data/qqchat.db";
        return Path.Combine(Directory.GetCurrentDirectory(), "runtime", "data", "qqchat.db");
    }

    public static bool TryRegisterFeishuWebhook(string eventKey, DateTimeOffset seenAt, TimeSpan ttl)
    {
        var dbPath = ResolveDbPath();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString();

        lock (Gate)
        {
            try
            {
                using var conn = new SqliteConnection(cs);
                conn.Open();
                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL;";
                    pragma.ExecuteNonQuery();
                }
                using (var createCmd = conn.CreateCommand())
                {
                    createCmd.CommandText = "CREATE TABLE IF NOT EXISTS feishu_webhook_dedup(event_key TEXT PRIMARY KEY, seen_unix INTEGER NOT NULL);";
                    createCmd.ExecuteNonQuery();
                }

                var cutoff = seenAt.ToUnixTimeSeconds() - Math.Max(1, (long)ttl.TotalSeconds);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM feishu_webhook_dedup WHERE seen_unix < $cutoff;
                    INSERT OR IGNORE INTO feishu_webhook_dedup(event_key, seen_unix)
                    VALUES($key, $seen)
                    RETURNING event_key;
                    """;
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                cmd.Parameters.AddWithValue("$key", eventKey);
                cmd.Parameters.AddWithValue("$seen", seenAt.ToUnixTimeSeconds());
                return cmd.ExecuteScalar() is not null;
            }
            catch
            {
                return false;
            }
        }
    }
}

using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>Trusted historical composition; both owners share the caller's transaction.</summary>
internal static class TokenUsageMigration
{
    // The minimum recovery unit is token_usage + tenant_quotas. These immutable
    // package-owned inputs are retained in Storage so recovery needs no business ALC.
    // They are not an admission path for external/runtime package migrations.
    internal static void Apply(SqliteConnection connection)
    {
        Execute(connection, "host.token-usage.14-15.up.sql", "preset.token-quota.14-15.up.sql");
        AppDatabase.SetSchemaVersion(connection, 15);
    }

    internal static void Revert(SqliteConnection connection)
    {
        Execute(connection, "preset.token-quota.14-15.down.sql", "host.token-usage.14-15.down.sql");
        AppDatabase.SetSchemaVersion(connection, 14);
    }

    private static void Execute(SqliteConnection connection, params string[] resources)
    {
        // Resolve every input before executing any of it. Only the closed list above
        // can reach this method; package data sessions still cannot execute DDL.
        var scripts = resources.Select(name =>
        {
            using var stream = typeof(TokenUsageMigration).Assembly.GetManifestResourceStream("BotAgent.Schema." + name)
                ?? throw new InvalidDataException("historical_schema_resource_missing");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }).ToArray();
        foreach (var script in scripts) AppDatabase.Exec(connection, script);
    }
}

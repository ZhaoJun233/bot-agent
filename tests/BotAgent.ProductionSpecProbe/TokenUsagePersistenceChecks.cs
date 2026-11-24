extern alias StorageModule;

using BotAgent.TestFixtures;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using Microsoft.Data.Sqlite;
using Database = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;

namespace BotAgent.ProductionSpecProbe;

internal static class TokenUsagePersistenceChecks
{
    private const string Tenant = "synthetic:usage";
    private const string Day = "2030-01-01";
    private const string Scope = "synthetic:usage-admin";

    internal static void Run(Action<bool, string> check)
    {
        var host = typeof(Database).Assembly;
        var resourcePrefix = "BotAgent.Schema.preset.token-quota.";
        var quotaScripts = host.GetManifestResourceNames().Where(n => n.StartsWith(resourcePrefix, StringComparison.Ordinal)).Order().ToArray();
        check(quotaScripts.SequenceEqual([resourcePrefix + "14-15.down.sql", resourcePrefix + "14-15.up.sql"])
            && quotaScripts.All(name =>
            {
                using var retained = host.GetManifestResourceStream(name);
                return retained is not null && retained.Length > 0;
            }), "Storage embeds both historical token-quota recovery migrations");
        check(host.GetManifestResourceNames().Count(n => n.StartsWith("BotAgent.Schema.host.token-usage.", StringComparison.Ordinal)) == 2,
            "generic metering schema belongs to Storage");
        check(RunChild("--usage-migrate"), "legacy usage migrates once, preserves quota receipts and survives absent quota state");
        check(RunChild("--usage-migration-fault"), "partial DDL failure rolls back copied usage, removed columns and version marker");
    }

    internal static int? TryRunChild(string[] args)
    {
        if (args.Length == 2 && args[0] == "--usage-reopen") return Reopen(args[1]);
        if (args.Length != 1 || args[0] is not ("--usage-migrate" or "--usage-migration-fault")) return null;
        var root = Path.Combine(Path.GetTempPath(), "botagent-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SetClock(Day);
            var path = Path.Combine(root, "synthetic.db");
            Seed(path);
            if (args[0] == "--usage-migration-fault") VerifyFailedMigration(path);
            else VerifyMigration(path);
            Console.WriteLine("TOKEN_USAGE passed failed=0");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("TOKEN_USAGE failed=1 " + exception);
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (OwnedRoot(root)) Directory.Delete(root, true);
        }
    }

    private static void VerifyMigration(string path)
    {
        Database.Initialize(path);
        Ensure(Database.Scalar<long>("PRAGMA user_version") == 15, "migration version");
        Ensure(Database.Query("PRAGMA table_info(tenant_quotas)", row => row.GetString(1))
            .SequenceEqual(new[] { "tenant_id", "daily_token_limit", "updated_at" }), "old counters are removed");
        Ensure(Database.GetMeta("synthetic:unrelated") == "preserved", "unrelated state preserved");
        var usage = new TokenUsageStore();
        Ensure(usage.GetUsage(Tenant) == new TokenUsageSnapshot(Tenant, 100, 50, Day), "exact counters and date migrated");
        var quota = new QuotaFixture();
        Ensure(quota.ReadManagement(Tenant).Revision == 8 && quota.GetQuota(Tenant).DailyTokenLimit == 90
            && quota.IsEnergySaving(Tenant), "quota projection derives energy and retains revision");
        var replay = quota.SaveManagement(Tenant, 70, 6, "migration-save", Scope, (_, _, _) =>
            throw new InvalidOperationException("replay audited twice"));
        Ensure(replay == SavedReceipt(), "historical receipt replay precedes stale CAS and retains original usage");
        Ensure(usage.GetUsage(Tenant).UsedTotalTokens == 150 && quota.GetQuota(Tenant).DailyTokenLimit == 90,
            "replay changes neither current counters nor limit");
        Parallel.Invoke(
            () => Parallel.For(0, 32, _ => new TokenUsageStore().RecordUsage(Tenant, 2, 1)),
            () => quota.SaveManagement(Tenant, 1000, 8, "new-save", Scope));
        Ensure(usage.GetUsage(Tenant).UsedTotalTokens == 246 && quota.ReadManagement(Tenant).Revision == 9,
            "independent recorders and quota save retain both updates");
        VerifyCounterBoundaries(usage);
        Database.Write(conn => Database.Exec(conn, "DROP TABLE tenant_quotas"));
        ITokenUsageRecorder recorder = usage;
        recorder.RecordUsage(Tenant, 4, 2);
        Ensure(usage.GetUsage(Tenant).UsedTotalTokens == 252, "meter works after physical quota table removal");
        Ensure(RunChild("--usage-reopen", path), "independent process resumes without quota table");
        Ensure(usage.GetUsage(Tenant).UsedTotalTokens == 258, "new process adds to existing counters");
        Ensure(Database.Scalar<long>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='tenant_quotas'") == 0,
            "restart does not resurrect absent quota state");
    }

    private static void VerifyCounterBoundaries(TokenUsageStore usage)
    {
        usage.RecordUsage("synthetic:overflow", int.MaxValue, 5);
        try { usage.RecordUsage("synthetic:overflow", 1, 8); throw new InvalidOperationException("overflow accepted"); }
        catch (OverflowException) { }
        Ensure(usage.GetUsage("synthetic:overflow").UsedCompletionTokens == 5, "overflow commits neither counter");
        Ensure(usage.RecordUsage("  ", -10, 3) == new TokenUsageSnapshot("default:general", 0, 3, Day),
            "negative increments and blank tenant preserve legacy semantics");
        Ensure(usage.RecordUsage("  synthetic:boundary  ", 8, 4).TenantId == "synthetic:boundary", "tenant trimmed");
        SetClock("2030-01-02");
        Ensure(usage.GetUsage("synthetic:boundary") == new TokenUsageSnapshot("synthetic:boundary", 0, 0, "2030-01-02"),
            "UTC rollover persists reset");
        SetClock(Day);
    }

    private static void VerifyFailedMigration(string path)
    {
        using (var connection = Open(path)) Database.Exec(connection, """
            CREATE TRIGGER synthetic_legacy_observer AFTER UPDATE ON tenant_quotas
              BEGIN SELECT NEW.energy_saving; END;
            """);
        try { Database.Initialize(path); throw new InvalidOperationException("fault did not reject"); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1
            && exception.Message.Contains("synthetic_legacy_observer", StringComparison.Ordinal)) { }
        Ensure(!Database.IsReady, "failed migration is not ready");
        using (var connection = Open(path))
        {
            Ensure(Scalar(connection, "PRAGMA user_version") == 14, "failed migration retains version");
            Ensure(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name='token_usage'") == 0, "copied table rolled back");
            Ensure(Scalar(connection, "SELECT used_prompt_tokens+used_completion_tokens+energy_saving FROM tenant_quotas") == 151,
                "removed columns and original rows restored");
            Ensure(Scalar(connection, "SELECT count(*) FROM pragma_table_info('tenant_quotas')") == 7, "all old columns restored");
            Database.Exec(connection, "DROP TRIGGER synthetic_legacy_observer");
        }
        Database.Initialize(path);
        Ensure(new TokenUsageStore().GetUsage(Tenant).UsedTotalTokens == 150, "retry imports once after removing blocker");
    }

    private static int Reopen(string path)
    {
        if (Path.GetFileName(path) != "synthetic.db" || !OwnedRoot(Path.GetDirectoryName(Path.GetFullPath(path))!)) return 1;
        try
        {
            SetClock(Day);
            Database.Initialize(path);
            var usage = new TokenUsageStore();
            Ensure(usage.GetUsage(Tenant).UsedTotalTokens == 252, "restart did not reset or reimport usage");
            Ensure(usage.RecordUsage(Tenant, 4, 2).UsedTotalTokens == 258, "restart records usage");
            Console.WriteLine("TOKEN_USAGE passed failed=0");
            return 0;
        }
        catch (Exception exception) { Console.WriteLine("TOKEN_USAGE failed=1 " + exception); return 1; }
    }

    internal static void Seed(string path)
    {
        using var connection = Open(path);
        Database.Exec(connection, """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO meta VALUES('synthetic:unrelated','preserved');
            CREATE TABLE tenant_quotas(tenant_id TEXT PRIMARY KEY, daily_token_limit INTEGER NOT NULL,
              used_prompt_tokens INTEGER NOT NULL, used_completion_tokens INTEGER NOT NULL,
              reset_date TEXT NOT NULL, energy_saving INTEGER NOT NULL, updated_at TEXT NOT NULL);
            INSERT INTO tenant_quotas VALUES($tenant,90,100,50,$day,1,'synthetic-time');
            PRAGMA user_version = 14;
            """, ("$tenant", Tenant), ("$day", Day));
        var fingerprint = Hash(JsonSerializer.Serialize(new { tenant = Tenant, dailyTokenLimit = 70, expectedRevision = 6L }));
        var journal = JsonSerializer.Serialize(new Dictionary<string, object>
        { [Hash("migration-save")] = new { Fingerprint = fingerprint, Result = SavedReceipt() } });
        Database.Exec(connection, "INSERT INTO meta VALUES($revision,'8'),($journal,$json)",
            ("$revision", "quota:revision:" + Hash(Tenant)), ("$journal", "quota:receipts:" + Hash(Scope)), ("$json", journal));
    }

    private static TenantQuotaManagementSnapshot SavedReceipt() => new(new(Tenant, 70, 60, 20, Day, true), 7);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    private static bool RunChild(string mode, string? path = null)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add(mode);
        if (path is not null) start.ArgumentList.Add(path);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30_000)) { child.Kill(true); throw new TimeoutException("usage child timeout"); }
        var text = output.GetAwaiter().GetResult();
        if (child.ExitCode != 0) Console.WriteLine(text + error.GetAwaiter().GetResult());
        return child.ExitCode == 0 && text.Contains("TOKEN_USAGE passed failed=0", StringComparison.Ordinal);
    }
    private static bool OwnedRoot(string root)
        => string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
               OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root), "^botagent-usage-[0-9a-f]{32}$");
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static void SetClock(string day) { Clock.Use(new FixedClock(DateTimeOffset.Parse(day + "T00:00:00Z"))); ClockBindings.InitializeStorage(); }
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now => now; public DateTimeOffset UtcNow => now.ToUniversalTime();
        public DateTime LocalDateTime => now.LocalDateTime; public long TickCount => 0;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.CompletedTask;
    }
}

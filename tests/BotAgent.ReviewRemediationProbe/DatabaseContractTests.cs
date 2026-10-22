extern alias StorageModule;

using System.Diagnostics;
using System.Security.Cryptography;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Ports;
using Microsoft.Data.Sqlite;
using HostDatabase = BotAgent.Adapters.Persistence.AppDatabase;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;

namespace BotAgent.ReviewRemediationProbe;

internal static class DatabaseContractTests
{
    public static Task<int?> TryRunChildAsync(string[] args)
    {
        if (args.Length == 2 && args[0] == "--database-reopen")
            return Task.FromResult<int?>(ReopenChild(args[1]));
        if (args.Length != 1 || args[0] != "--database-future") return Task.FromResult<int?>(null);
        var root = Path.Combine(Path.GetTempPath(), "botagent-c2-future-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); passed++; }
        try
        {
            var path = Path.Combine(root, "future.db");
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                conn.Open();
                HostDatabase.Exec(conn, "PRAGMA user_version = 10; CREATE TABLE synthetic_marker(id INTEGER PRIMARY KEY);");
            }
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            ClockBindings.InitializeStorage();
            ExpectCode(() => StorageDatabase.Initialize(path), "database_schema_newer", Check,
                "newer schema is rejected before mutation");
            SqliteConnection.ClearAllPools();
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "rejected newer database bytes remain unchanged");
            Check(!StorageDatabase.IsReady, "failed initialization never reports ready");
            StorageDatabase.Initialize(Path.Combine(root, "retry.db"));
            Check(StorageDatabase.IsReady && StorageDatabase.GetMeta("synthetic-marker") is null, "failed initialization allows a clean retry");
            Console.WriteLine($"C2_CHILD future passed={passed} failed=0");
            return Task.FromResult<int?>(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"C2_CHILD future passed={passed} failed=1 type={ex.GetType().Name}");
            return Task.FromResult<int?>(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteOwnedRoot(root, "botagent-c2-future-");
        }
    }

    private static int ReopenChild(string restoreRoot)
    {
        var full = Path.GetFullPath(restoreRoot);
        var parent = Path.GetDirectoryName(full)!;
        if (Path.GetFileName(full) != "c2-restore" || !System.Text.RegularExpressions.Regex.IsMatch(
            Path.GetFileName(parent), "^botagent-review-remediation-[0-9a-f]{32}$")
            || !string.Equals(Path.GetDirectoryName(parent), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return 1;
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", full);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", full);
        try
        {
            HostDatabase.Initialize();
            if (!StorageDatabase.IsReady || HostDatabase.GetMeta("synthetic:c2-backup") != "snapshot-value"
                || StorageDatabase.Scalar<long>("PRAGMA user_version") != 9) return 1;
            Console.WriteLine("C2_CHILD restore passed=3 failed=0");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("C2_CHILD restore failed=1 type=" + ex.GetType().Name); return 1; }
        finally { SqliteConnection.ClearAllPools(); }
    }

    public static Task SharedAsync(Action<bool, string> check)
    {
        check(StorageDatabase.IsReady, "host startup initializes the Storage database");
        check(HostDatabase.FilePath == StorageDatabase.FilePath, "host and Storage use one bound database path");
        HostDatabase.SetMeta("synthetic:c2-host", "host-value");
        check(StorageDatabase.GetMeta("synthetic:c2-host") == "host-value", "Storage reads host commits");
        StorageDatabase.SetMeta("synthetic:c2-storage", "storage-value");
        check(HostDatabase.GetMeta("synthetic:c2-storage") == "storage-value", "host reads Storage commits");
        return Task.CompletedTask;
    }

    public static Task NestedWriteAsync(Action<bool, string> check)
    {
        var rejected = false;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        HostDatabase.Write(conn =>
        {
            HostDatabase.Exec(conn, "INSERT INTO meta(key,value) VALUES('synthetic:c2-outer','committed')");
            try { StorageDatabase.Write(_ => { }); }
            catch (InvalidOperationException ex) when (ex.Message == "database_nested_write") { rejected = true; }
        });
        check(rejected && timer.Elapsed < TimeSpan.FromSeconds(2), "cross-facade nested write rejects without waiting for SQLite");
        check(StorageDatabase.GetMeta("synthetic:c2-outer") == "committed", "handled nesting rejection preserves the outer transaction");
        return Task.CompletedTask;
    }

    public static Task FutureSchemaAsync(Action<bool, string> check)
        => RunChildAsync("--database-future", null, "passed=4 failed=0", check, "isolated newer-schema rejection and retry pass");

    private static async Task RunChildAsync(string mode, string? root, string result, Action<bool, string> check, string label)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(DatabaseContractTests).Assembly.Location);
        start.ArgumentList.Add(mode);
        if (root is not null) start.ArgumentList.Add(root);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("database_child_start");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
        var text = await output;
        Console.Write(text);
        check(child.ExitCode == 0 && text.Contains(result) && string.IsNullOrWhiteSpace(await error), label);
    }

    public static async Task AtomicityAsync(Action<bool, string> check)
    {
        var bound = HostDatabase.FilePath;
        StorageDatabase.Initialize(Path.Combine(Path.GetDirectoryName(bound)!, ".", Path.GetFileName(bound)));
        check(StorageDatabase.IsReady && StorageDatabase.FilePath == bound, "same canonical path initialization is idempotent");
        ExpectCode(() => StorageDatabase.Initialize(Path.Combine(Path.GetDirectoryName(bound)!, "other.db")),
            "database_path_already_initialized", check, "another database path is rejected");
        check(HostDatabase.FilePath == bound, "path rejection preserves the live database");
        try
        {
            HostDatabase.Write(conn =>
            {
                StorageDatabase.Exec(conn, "INSERT INTO meta(key,value) VALUES('synthetic:c2-rollback','not-committed')");
                throw new IOException("synthetic-write-fault");
            });
        }
        catch (IOException ex) when (ex.Message == "synthetic-write-fault") { }
        check(!StorageDatabase.HasMeta("synthetic:c2-rollback"), "write failure rolls back across host and Storage facades");
        var seen = new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ttl = TimeSpan.FromSeconds(10);
        check(HostDatabase.TryRegisterFeishuWebhooks(new[] { "synthetic:existing", "synthetic:existing" }, seen, ttl),
            "dedup repeated keys in one batch remain compatible");
        check(!StorageDatabase.TryRegisterFeishuWebhooks(new[] { "synthetic:new", "synthetic:existing" }, seen, ttl),
            "one duplicate rejects the complete cross-facade batch");
        check(HostDatabase.TryRegisterFeishuWebhook("synthetic:new", seen, ttl), "duplicate rollback does not consume earlier new keys");
        check(!HostDatabase.TryRegisterFeishuWebhook("synthetic:existing", seen.AddSeconds(10), ttl), "dedup TTL boundary stays inclusive");
        check(HostDatabase.TryRegisterFeishuWebhook("synthetic:existing", seen.AddSeconds(11), ttl), "dedup expires after the original boundary");
        var winners = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => i % 2 == 0
            ? HostDatabase.TryRegisterFeishuWebhook("synthetic:race", seen, ttl)
            : StorageDatabase.TryRegisterFeishuWebhook("synthetic:race", seen, ttl))));
        check(winners.Count(w => w) == 1, "both facades share one dedup winner under concurrent writers");
        HostDatabase.Write(conn => HostDatabase.Exec(conn,
            "CREATE TRIGGER synthetic_dedup_fault BEFORE INSERT ON feishu_webhook_dedup WHEN NEW.event_key = 'synthetic:fault' BEGIN SELECT RAISE(ABORT,'synthetic'); END;"));
        try
        {
            var faultRaised = false;
            try { StorageDatabase.TryRegisterFeishuWebhooks(new[] { "synthetic:first", "synthetic:fault" }, seen, ttl); }
            catch (SqliteException) { faultRaised = true; }
            check(faultRaised, "mid-batch SQL fault propagates to the caller");
            check(HostDatabase.TryRegisterFeishuWebhook("synthetic:first", seen, ttl), "mid-batch SQL fault rolls back earlier keys");
        }
        finally { HostDatabase.Write(conn => HostDatabase.Exec(conn, "DROP TRIGGER synthetic_dedup_fault")); }
        StorageDatabase.Write(_ => ExpectCode(() => HostDatabase.VacuumIntoBackup(), "database_nested_write", check,
            "backup inside a write rejects without opening another connection"));
        StorageDatabase.Write(_ => ExpectCode(() => HostDatabase.WriteAsync(_ => { }).GetAwaiter().GetResult(),
            "database_nested_write", check, "nested async writes inherit the rejection context"));
    }

    public static async Task RestoreAsync(Action<bool, string> check)
    {
        HostDatabase.SetMeta("synthetic:c2-backup", "snapshot-value");
        var originalClock = Clock.Current;
        string backup;
        Clock.Use(new BackupClock());
        try
        {
            backup = HostDatabase.VacuumIntoBackup();
            check(Path.GetFileName(backup).StartsWith("qqchat_daily_20350101_000000_000_", StringComparison.Ordinal),
                "actual Storage backup uses the stable host fake clock");
        }
        finally { Clock.Use(originalClock); }
        HostDatabase.SetMeta("synthetic:c2-backup", "new-live-value");
        var runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(HostDatabase.FilePath))!;
        var restoreRoot = Path.Combine(runtimeRoot, "c2-restore");
        Directory.CreateDirectory(Path.Combine(restoreRoot, "data"));
        File.Copy(backup, Path.Combine(restoreRoot, "data", "qqchat.db"));
        await RunChildAsync("--database-reopen", restoreRoot, "passed=3 failed=0", check,
            "fresh process restores the actual backup and committed schema");
        check(HostDatabase.GetMeta("synthetic:c2-backup") == "new-live-value", "backup restore does not replace the live database");
    }

    public static Task BackupPathAsync(Action<bool, string> check)
    {
        var expectedDir = Path.Combine(Path.GetDirectoryName(HostDatabase.FilePath)!, "backups");
        var oldBot = Environment.GetEnvironmentVariable("BOTAGENT_DATA_DIR");
        var oldQq = Environment.GetEnvironmentVariable("QQCHAT_DATA_DIR");
        try
        {
            var alternate = Path.Combine(Path.GetDirectoryName(HostDatabase.FilePath)!, "c2-alternate");
            Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", alternate);
            Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", alternate);
            var backup = HostDatabase.VacuumIntoBackup();
            check(Path.GetDirectoryName(backup) == expectedDir, "default backup follows the bound database, not changed environment roots");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", oldBot);
            Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", oldQq);
        }
        return Task.CompletedTask;
    }

    private static void ExpectCode(Action action, string code, Action<bool, string> check, string label)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message == code) { check(true, label); return; }
        check(false, label);
    }

    private static void DeleteOwnedRoot(string root, string prefix)
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("unsafe_database_fixture_cleanup");
        Directory.Delete(full, recursive: true);
    }

    private sealed class BackupClock : IClock
    {
        public DateTimeOffset Now => new(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
        public DateTime LocalDateTime => Now.DateTime;
        public long TickCount => 0;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.CompletedTask;
    }
}

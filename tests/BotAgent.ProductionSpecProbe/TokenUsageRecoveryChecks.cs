extern alias StorageModule;

using BotAgent.TestFixtures;
using System.Diagnostics;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using Microsoft.Data.Sqlite;
using Database = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;
using TextSnapshotFile = StorageModule::BotAgent.Adapters.Persistence.TextSnapshotFile;

namespace BotAgent.ProductionSpecProbe;

internal static class TokenUsageRecoveryChecks
{
    internal static int? TryRunChild(string[] args)
    {
        if ((args.Length == 1 || args.Length == 3 && args[1] == "--legacy-reader") && args[0] == "--usage-recovery")
        {
            Run((ok, label) => { if (!ok) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); },
                args.Length == 3 ? args[2] : null);
            return 0;
        }
        if (args.Length != 2 || !args[0].StartsWith("--usage-recovery-", StringComparison.Ordinal)) return null;
        if (!OwnedPath(args[1])) return 2;
        try
        {
            TokenUsagePersistenceChecks.SetClock("2030-01-01");
            Database.Initialize(args[1]);
            if (args[0] == "--usage-recovery-hold")
            {
                Console.WriteLine("DATABASE_OWNED");
                Console.Out.Flush();
                Console.ReadLine();
            }
            else if (args[0] == "--usage-recovery-prepare")
            {
                var usage = new TokenUsageStore();
                usage.RecordUsage("synthetic:usage", 7, 3);
                usage.RecordUsage("synthetic:new-a", 11, 5);
                usage.RecordUsage("synthetic:new-b", 4, 2);
                new QuotaFixture().SaveManagement("synthetic:usage", 200, 8, "post-migration", "synthetic:usage-admin",
                    (connection, _, _) => Database.Exec(connection, "INSERT INTO meta VALUES('synthetic:new-audit','preserved')"));
                Database.SetMeta("synthetic:unrelated", "newer-preserved");
                Database.Write(connection => Database.Exec(connection,
                    "CREATE TABLE synthetic_other_package(id INTEGER PRIMARY KEY, value TEXT); INSERT INTO synthetic_other_package VALUES(1,'newer');"));
            }
            else if (args[0] == "--usage-recovery-reopen")
            {
                var usage = new TokenUsageStore();
                if (usage.GetUsage("synthetic:usage").UsedTotalTokens != 163
                    || usage.GetUsage("synthetic:new-a").UsedTotalTokens != 16) return 1;
                var quota = new QuotaFixture();
                var replay = quota.SaveManagement("synthetic:usage", 70, 6, "migration-save", "synthetic:usage-admin",
                    (_, _, _) => throw new InvalidOperationException("historical replay repeated audit"));
                if (replay.Quota.UsedTotalTokens != 80 || replay.Revision != 7
                    || quota.ReadManagement("synthetic:usage").Revision != 9) return 1;
            }
            else return 2;
            return 0;
        }
        catch (IOException) { Console.Error.WriteLine("database_owned_by_maintenance"); return 3; }
    }

    internal static void Run(Action<bool, string> check, string? legacyReader = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-usage-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "synthetic.db");
        try
        {
            TokenUsagePersistenceChecks.Seed(path);
            check(Child("--usage-recovery-prepare", path).Exit == 0, "recovery fixture records post-migration usage, quota and audit");
            if (legacyReader is not null)
                check(Capture(legacyReader, path, "--reject-future").Exit == 0, "original schema-14 storage rejects unrecovered data");
            using (var holder = Start(typeof(Program).Assembly.Location, "--usage-recovery-hold", path))
            {
                try
                {
                    check(holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult()
                        == "DATABASE_OWNED", "actual initialized process holds shared database ownership");
                    var refused = Recover(path);
                    check(refused.Exit == 3 && refused.Error.Trim() == "database_in_use", "offline recovery refuses a live database user before mutation");
                    check(Scalar(path, "PRAGMA user_version") == 15, "live-process refusal preserves schema");
                    var alias = Path.Combine(root, "directory-alias");
                    CreateDirectoryAlias(alias, root);
                    try
                    {
                        check(Recover(Path.Combine(alias, "synthetic.db")).Exit == 3,
                            "directory links cannot bypass the live database ownership lock");
                    }
                    finally
                    {
                        if (new DirectoryInfo(alias).LinkTarget is not null) Directory.Delete(alias);
                    }
                }
                finally
                {
                    holder.StandardInput.WriteLine("exit"); holder.StandardInput.Flush();
                    if (!holder.WaitForExit(10_000)) holder.Kill(true);
                }
            }
            using (TextSnapshotFile.AcquireExclusiveWriter(path))
                check(Child("--usage-recovery-hold", path).Exit == 3, "maintenance ownership prevents a new host initialization");

            Exec(path, "CREATE UNIQUE INDEX synthetic_limit_conflict ON tenant_quotas(daily_token_limit)");
            var failed = Recover(path);
            check(failed.Exit == 4 && failed.Error.Trim() == "token_usage_recovery_refused", "mid-recovery data constraint failure is reported without SQL or identifiers");
            check(Scalar(path, "PRAGMA user_version") == 15
                && Scalar(path, "SELECT count(*) FROM pragma_table_info('tenant_quotas')") == 3
                && Scalar(path, "SELECT sum(used_prompt_tokens+used_completion_tokens) FROM token_usage") == 182,
                "inverse failure rolls back added columns, copied rows, counts and version");
            Exec(path, "DROP INDEX synthetic_limit_conflict; CREATE VIEW synthetic_usage_view AS SELECT * FROM token_usage");
            check(Recover(path).Exit == 4 && Scalar(path, "PRAGMA user_version") == 15, "undeclared cross-resource dependency refuses inverse migration");
            Exec(path, "DROP VIEW synthetic_usage_view; PRAGMA user_version=16");
            check(Recover(path).Exit == 4 && Scalar(path, "PRAGMA user_version") == 16, "unknown schema is never downgraded");
            Exec(path, "PRAGMA user_version=15");
            var recovered = Recover(path);
            check(recovered.Exit == 0 && JsonDocument.Parse(recovered.Output).RootElement.GetProperty("toSchema").GetInt32() == 14,
                "actual offline CLI commits the inverse migration");
            check(Scalar(path, "PRAGMA user_version") == 14
                && Scalar(path, "SELECT count(*) FROM sqlite_master WHERE name='token_usage'") == 0
                && Scalar(path, "SELECT sum(used_prompt_tokens+used_completion_tokens) FROM tenant_quotas") == 182,
                "all latest counters survive with exactly one legacy authority");
            check(Scalar(path, "SELECT daily_token_limit+energy_saving FROM tenant_quotas WHERE tenant_id='synthetic:usage'") == 200
                && Scalar(path, "SELECT count(*) FROM tenant_quotas WHERE daily_token_limit=50000") == 2,
                "latest limit and newly metered tenants retain legacy behavior");
            check(Scalar(path, "SELECT count(*) FROM meta WHERE (key='synthetic:unrelated' AND value='newer-preserved') OR (key='synthetic:new-audit' AND value='preserved')") == 2,
                "unrelated newer state and same-transaction audit survive recovery");
            check(Scalar(path, "SELECT count(*) FROM synthetic_other_package WHERE id=1 AND value='newer'") == 1,
                "an unrelated package table and its newer row survive recovery");
            check(Recover(path).Exit == 4 && Scalar(path, "PRAGMA user_version") == 14, "retry cannot reapply or duplicate a completed inverse migration");
            if (legacyReader is not null)
                check(Capture(legacyReader, path).Exit == 0, "original schema-14 storage reads, replays and increments recovered data");
            else
                Exec(path, "UPDATE tenant_quotas SET used_prompt_tokens=used_prompt_tokens+1,used_completion_tokens=used_completion_tokens+2 WHERE tenant_id='synthetic:usage'");
            check(Child("--usage-recovery-reopen", path).Exit == 0, "re-upgrade retains legacy increments and original quota receipts");
            RecoverWithoutBusinessPackage(root, path, check);
            check(!Directory.Exists(Path.Combine(root, "must-not-bootstrap")), "offline CLI never initializes default settings or storage");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (OwnedPath(path)) Directory.Delete(root, true);
        }
    }

    private static void RecoverWithoutBusinessPackage(string root, string path, Action<bool, string> check)
    {
        var isolated = Path.Combine(root, "recovery-host");
        Directory.CreateDirectory(isolated);
        var source = Path.GetDirectoryName(typeof(AppDatabase).Assembly.Location)!;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("BotAgent.Package.", StringComparison.Ordinal)) continue;
            if (Path.GetExtension(file) == ".dll" || name is "BotAgent.Headless.deps.json" or "BotAgent.Headless.runtimeconfig.json")
                File.Copy(file, Path.Combine(isolated, name));
        }
        var native = Path.Combine(source, "runtimes");
        foreach (var file in Directory.EnumerateFiles(native, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(isolated, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        check(!Directory.EnumerateFiles(isolated, "BotAgent.Package.*", SearchOption.AllDirectories).Any()
            && !Directory.Exists(Path.Combine(isolated, "plugin-distribution")), "recovery process has no business DLLs or package distribution");
        var result = Capture(Path.Combine(isolated, "BotAgent.Headless.dll"), "--recover-token-usage", path, "--to-schema", "14");
        check(result.Exit == 0 && Scalar(path, "PRAGMA user_version") == 14
            && Scalar(path, "SELECT sum(used_prompt_tokens+used_completion_tokens) FROM tenant_quotas") == 185,
            "physically missing business packages still permit offline inverse with all later increments");
    }

    private static void CreateDirectoryAlias(string alias, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(alias, target); return; }
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path '" + alias.Replace("'", "''")
                + "' -Target '" + target.Replace("'", "''") + "' | Out-Null" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000)) { process.Kill(true); throw new TimeoutException("alias creation timed out"); }
        _ = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException("synthetic_alias_creation_failed");
    }

    private static bool OwnedPath(string path)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.GetFileName(path) == "synthetic.db"
            && string.Equals(Path.GetDirectoryName(root), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root), "^botagent-usage-recovery-[0-9a-f]{32}$");
    }
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static void Exec(string path, string sql) { using var connection = Open(path); Database.Exec(connection, sql); }
    private static long Scalar(string path, string sql)
    {
        using var connection = Open(path); using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    private static Process Start(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        var runtimeLeaf = args.Contains("--recover-token-usage") ? "must-not-bootstrap" : "initialized-test-runtime";
        var forbidden = Path.Combine(Path.GetDirectoryName(args.Last(arg => arg.EndsWith(".db", StringComparison.Ordinal)))!, runtimeLeaf);
        start.Environment["BOTAGENT_DATA_DIR"] = forbidden; start.Environment["QQCHAT_DATA_DIR"] = forbidden;
        return Process.Start(start)!;
    }
    private static (int Exit, string Output, string Error) Capture(params string[] args)
    {
        using var child = Start(args);
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(20_000)) { child.Kill(true); throw new TimeoutException("usage recovery child timed out"); }
        var result = (child.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        return result;
    }
    private static (int Exit, string Output, string Error) Child(string mode, string path)
        => Capture(typeof(Program).Assembly.Location, mode, path);
    private static (int Exit, string Output, string Error) Recover(string path)
        => Capture(typeof(AppDatabase).Assembly.Location, "--recover-token-usage", path, "--to-schema", "14");
}

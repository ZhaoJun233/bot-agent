extern alias StorageModule;

using BotAgent.TestFixtures;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Ports;
using BotAgent.Services.Ops;
using PackageDataStore = StorageModule::BotAgent.Adapters.Persistence.PackageDataStore;
using System.Diagnostics;

namespace BotAgent.ProductionSpecProbe;

internal static class PackageTransactionChecks
{
    internal static void RunIsolated(Action<bool, string> check)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--package-transactions");
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30_000)) { child.Kill(true); throw new TimeoutException("package transaction child timeout"); }
        var text = output.GetAwaiter().GetResult();
        if (child.ExitCode != 0) Console.WriteLine(text + error.GetAwaiter().GetResult());
        check(child.ExitCode == 0 && text.Contains("失败 0", StringComparison.Ordinal), "independent package transaction and audit-chain checks");
    }

    internal static void Run(Action<bool, string> check)
    {
        AppDatabase.Write(connection => AppDatabase.Exec(connection, """
            CREATE TABLE package_data_probe_a(id TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE package_data_probe_b(id TEXT PRIMARY KEY, value TEXT);
            INSERT INTO package_data_probe_b VALUES('private','synthetic-only');
            """));
        IPackageDataStore store = new PackageDataStore(["package_data_probe_a"], ["package-probe:a:"], allowAudit: true);
        var audit = new AuditLogStore();
        var beforeAudit = audit.Verify().CheckedCount;
        IPackageDataSession? captured = null;
        var injectedValue = "value'); DROP TABLE package_data_probe_b; --";
        var rows = store.Write(session =>
        {
            captured = session;
            session.Execute("INSERT INTO package_data_probe_a VALUES($id,$value)", new("$id", "saved"), new("$value", injectedValue));
            session.WriteState("package-probe:a:receipt", "committed");
            session.AppendAudit(Event("committed"));
            return session.Query("SELECT value FROM PACKAGE_DATA_PROBE_A WHERE id=$id", new PackageDataParameter("$id", "saved"));
        });
        check(rows.Count == 1 && Equals(rows[0][0], injectedValue), "package transaction binds values and materializes rows");
        check(store.Read(session => session.ReadState("package-probe:a:receipt")) == "committed"
            && audit.Verify().CheckedCount == beforeAudit + 1, "package state and audit commit together");
        Reject("package_data_session_expired", () => captured!.ReadState("package-probe:a:receipt"), check, "escaped session expires");
        using (var pooled = AppDatabase.Open())
        {
            AppDatabase.Exec(pooled, """
                CREATE TEMP TABLE package_data_probe_a(id TEXT);
                INSERT INTO temp.package_data_probe_a VALUES('shadow-1'),('shadow-2');
                ATTACH DATABASE ':memory:' AS package_probe_attached;
                CREATE TABLE package_probe_attached.package_data_probe_a(id TEXT);
                """);
            pooled.Close();
            try
            {
                check(store.Read(session => Convert.ToInt64(session.Scalar("SELECT count(*) FROM package_data_probe_a"))) == 1,
                    "package connection never inherits pooled temp objects or attachments");
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooled); }
        }

        string[] forbidden = [
            "SELECT value FROM package_data_probe_b", "SELECT value FROM meta", "SELECT name FROM sqlite_master",
            "PRAGMA table_info(meta)", "ATTACH DATABASE ':memory:' AS other", "DETACH DATABASE main",
            "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT injected", "CREATE TABLE injected(id INTEGER)",
            "DROP TABLE package_data_probe_a", "ALTER TABLE package_data_probe_a ADD COLUMN injected TEXT",
            "SELECT load_extension('synthetic-extension')", "SELECT name FROM pragma_table_info('meta')"
        ];
        foreach (var sql in forbidden)
            Reject("package_data_denied", () => store.Write(session => session.Execute(sql)), check, "scoped SQL denies " + sql.Split(' ')[0]);
        Reject("package_data_denied", () => store.Read(session => session.Execute("DELETE FROM package_data_probe_a")), check, "read-only SQL cannot write");
        Reject("package_data_denied", () => store.Read(session => session.Scalar("DELETE FROM package_data_probe_a RETURNING id")), check, "scalar return cannot disguise a read-only write");
        Reject("package_data_denied", () => store.Read(session => { session.WriteState("package-probe:a:x", "x"); return 0; }), check, "read-only state cannot write");
        Reject("package_data_denied", () => store.Read(session => { session.AppendAudit(Event("denied")); return 0; }), check, "read-only audit cannot write");
        Reject("package_data_denied", () => store.Write(session => session.ReadState("package-probe:b:receipt")), check, "foreign state prefix denied");
        Reject("package_data_denied", () => store.Write(session => session.ReadUsage("synthetic:quota")), check, "ungranted metering denied");
        Reject("package_data_denied", () => store.Write(session => session.Execute("UPDATE token_usage SET used_prompt_tokens=0")), check, "meter table has no SQL grant");

        Reject("package_data_transaction_aborted", () => store.Write(session =>
        {
            session.Execute("INSERT INTO package_data_probe_a VALUES('aborted','before-error')");
            session.WriteState("package-probe:a:aborted", "before-error");
            session.AppendAudit(Event("aborted"));
            try { session.Scalar("SELECT value FROM package_data_probe_b"); } catch (InvalidOperationException) { }
            return 0;
        }), check, "caught denial poisons the whole transaction");
        check(store.Read(session => Convert.ToInt64(session.Scalar("SELECT count(*) FROM package_data_probe_a"))) == 1
            && store.Read(session => session.ReadState("package-probe:a:aborted")) is null
            && audit.Verify() is { Valid: true } verified && verified.CheckedCount == beforeAudit + 1,
            "caught denial rolls back row, state and audit chain");
        Reject("package_data_denied", () => store.Write(session => session.Execute(
            "INSERT INTO package_data_probe_a VALUES('multi','before-denial'); DELETE FROM package_data_probe_b;")), check, "multi-statement denial rolls back preceding statement");

        AppDatabase.Write(connection => AppDatabase.Exec(connection, """
            CREATE TRIGGER package_data_probe_trigger AFTER INSERT ON package_data_probe_a
            BEGIN UPDATE package_data_probe_b SET value='changed'; END;
            """));
        try
        {
            Reject("package_data_denied", () => store.Write(session => session.Execute(
                "INSERT INTO package_data_probe_a VALUES('trigger','before-denial')")), check, "trigger cannot acquire ambient table access");
        }
        finally { AppDatabase.Write(connection => AppDatabase.Exec(connection, "DROP TRIGGER package_data_probe_trigger")); }

        Reject("package_data_transaction_aborted", () => store.Write(session =>
        {
            Task.Run(() => { try { session.ReadState("package-probe:a:receipt"); } catch (InvalidOperationException) { } }).GetAwaiter().GetResult();
            return 0;
        }), check, "cross-thread use poisons session even when caught");
        Reject("package_data_transaction_aborted", () => store.Write(session =>
        {
            try { store.Read(inner => inner.ReadState("package-probe:a:receipt")); } catch (InvalidOperationException) { }
            return 0;
        }), check, "nested package transactions cannot bypass the active scope");
        Reject("package_data_async_operation", () => store.Write(session =>
        {
            session.WriteState("package-probe:a:async", "must-rollback");
            return Task.FromResult(1);
        }), check, "task-valued callback is rejected before commit");
        Reject("package_data_async_operation", () => store.Write(session => ValueTask.FromResult(1)), check, "value-task callback is rejected");
        check(store.Read(session => session.ReadState("package-probe:a:async")) is null, "async callback cannot partially commit");
        Reject("package_data_session_expired", () => store.Write(session =>
        {
            session.WriteState("package-probe:a:disposed", "must-rollback");
            ((IDisposable)session).Dispose();
            return 0;
        }), check, "early disposal cannot partially commit");
        check(store.Read(session => session.ReadState("package-probe:a:disposed")) is null, "early disposal rolls back state");
        check(Type.GetType("BotAgent.Services.Ops.AuditEvent, BotAgent.Engine") == typeof(AuditEvent)
            && Type.GetType("BotAgent.Services.Ops.IAuditChain, BotAgent.Engine") == typeof(IAuditChain),
            "legacy Engine assembly type references forward to the unique plugin SDK audit contracts");

        var ledger = new QuotaFixture();
        var state = ledger.ReadManagement("synthetic:package-quota");
        var saved = ledger.SaveManagementWithAudit(state.Quota.TenantId, 1234, state.Revision,
            "package-quota-save", "synthetic-caller", (_, _) => Event("quota-committed"));
        var replay = ledger.SaveManagementWithAudit(state.Quota.TenantId, 1234, state.Revision,
            "package-quota-save", "synthetic-caller", (_, _) => throw new Exception("replay_must_not_audit"));
        check(saved == replay && audit.Verify().CheckedCount == beforeAudit + 2,
            "real quota new audit port replays original key without another append");
        try
        {
            ledger.SaveManagementWithAudit(state.Quota.TenantId, 4321, saved.Revision,
                "package-quota-failed", "synthetic-caller", (_, _) => new AuditEvent("", "synthetic", "synthetic", "invalid", "1"));
            throw new Exception("invalid_audit_accepted");
        }
        catch (ArgumentException) { }
        check(ledger.ReadManagement(state.Quota.TenantId) == saved
            && ledger.FindManagement("package-quota-failed", "synthetic-caller") is null && audit.Verify().Valid,
            "audit failure rolls back actual quota revision, limit and receipt");
        check(AppDatabase.Scalar<string>("SELECT value FROM package_data_probe_b WHERE id='private'") == "synthetic-only",
            "all denied paths preserve another resource");
    }

    private static AuditEvent Event(string detail) => new("config_change", "synthetic", "synthetic", detail, "1");
    private static void Reject(string code, Action action, Action<bool, string> check, string label)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message == code) { check(true, label); return; }
        throw new InvalidOperationException("expected " + code + ": " + label);
    }
}

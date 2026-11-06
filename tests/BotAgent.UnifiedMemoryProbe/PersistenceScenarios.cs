using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Memory;
using BotAgent.Engine.Memory;
using BotAgent.Storage;
using Microsoft.Data.Sqlite;
using static BotAgent.UnifiedMemoryProbe.Probe;

namespace BotAgent.UnifiedMemoryProbe;

internal static class PersistenceScenarios
{
    private const string SnapshotKey = "unified_memory_snapshot";
    public static void Run()
    {
        using var fixture = new SyntheticDatabase();
        ReopenAndForget();
        ResetMemorySnapshot();
        RollbackAndCompareExchange();
        ResetMemorySnapshot();
        CorruptSnapshot();
    }

    private static void ReopenAndForget()
    {
        Guid id;
        UnifiedMemoryReceipt oldReceipt;
        {
            var store = new UnifiedMemoryStore();
            var memory = new UnifiedMemory(store);
            id = Remember(memory).FactId;
            oldReceipt = memory.Read(Target, "help").Receipt!;
            Check(store.Load().Entries.Single().Source == Scope(Source), "SQLite preserves exact driver and source identity");
        }
        {
            var memory = new UnifiedMemory(new UnifiedMemoryStore());
            Check(memory.Read(Target, "help").Entries.Length == 1, "confirmed authorization survives database reopen");
            Check(!memory.ValidateReceipt(oldReceipt, Target), "reopen never revives process-local receipt");
            Check(memory.Revoke(Private, id, Scope(Target)).Succeeded, "SQLite revoke commits");
        }
        {
            var memory = new UnifiedMemory(new UnifiedMemoryStore());
            Check(memory.Read(Target, "help").Entries.IsEmpty && memory.Read(Source, "help").Entries.Length == 1, "revocation persists while source remains");
            Check(memory.SetEnabled(false).Succeeded, "SQLite disable commits");
        }
        {
            var store = new UnifiedMemoryStore();
            var memory = new UnifiedMemory(store);
            Check(!memory.Enabled && memory.List(Private).Length == 1, "disable survives restart without deleting data");
            Check(memory.Forget(Private, id).Succeeded, "SQLite forget commits while disabled");
            using var document = JsonDocument.Parse(AppDatabase.GetMeta(SnapshotKey)!);
            var state = document.RootElement.GetProperty("State");
            Check(state.GetProperty("Entries").GetArrayLength() == 0, "live persistence has no forgotten body or derived entries");
            var marker = state.GetProperty("Suppressions")[0];
            Check(marker.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "Source", "Subject" }), "suppression schema has only subject and source, no body or body hash");
        }
        {
            var memory = new UnifiedMemory(new UnifiedMemoryStore());
            Check(memory.IsLegacyProfileSuppressed(Subject, Scope(Source)), "forget legacy suppression persists after restart");
            memory.SetEnabled(true);
            Check(memory.RequestRemember(Source, Request()).Status == UnifiedMemoryStatus.Suppressed, "forgotten source remains closed after restart");
        }
    }

    private static void RollbackAndCompareExchange()
    {
        var store = new UnifiedMemoryStore();
        var before = store.Load();
        var next = before with { Revision = before.Revision + 1, Enabled = true };
        store.Save(before.Revision, next);
        var conflict = false;
        try { store.Save(before.Revision, next with { Enabled = false }); }
        catch (InvalidOperationException) { conflict = true; }
        Check(conflict && store.Load().Enabled, "stale revision fails atomically without overwriting winning commit");
        var memory = new UnifiedMemory(store);
        var entry = Remember(memory);
        var receipt = memory.Read(Target, "help").Receipt!;
        var snapshot = JsonSerializer.Serialize(store.Load());
        AppDatabase.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER unified_memory_probe_fail BEFORE UPDATE ON meta
                WHEN OLD.key = 'unified_memory_snapshot'
                BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END;
                """;
            command.ExecuteNonQuery();
        });
        Check(memory.Forget(Private, entry.FactId).Status == UnifiedMemoryStatus.StorageFailure, "SQLite transaction failure reported without claiming forget");
        Check(JsonSerializer.Serialize(store.Load()) == snapshot, "SQLite failed mutation rolls back the whole snapshot");
        Check(!memory.Enabled && !memory.ValidateReceipt(receipt, Target) && memory.Read(Target, "help").Entries.IsEmpty, "SQLite write failure stops all existing in-memory permissions");
        Check(memory.IsLegacyProfileSuppressed(Subject, Scope(Source)), "failed persistence also closes legacy bypass");
    }

    private static T Read<T>(Func<SqliteConnection, T> read)
    {
        using var connection = AppDatabase.Open();
        return read(connection);
    }

    private static void ResetMemorySnapshot() => AppDatabase.Write(connection =>
    {
        AppDatabase.Exec(connection, "DROP TRIGGER IF EXISTS unified_memory_probe_fail;");
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM meta WHERE key=$key";
        command.Parameters.AddWithValue("$key", SnapshotKey);
        command.ExecuteNonQuery();
    });

    private static void CorruptSnapshot()
    {
        var store = new UnifiedMemoryStore();
        var before = store.Load();
        using (var document = JsonDocument.Parse(AppDatabase.GetMeta(SnapshotKey)!))
            Check(document.RootElement.GetProperty("SchemaVersion").GetInt32() == 1,
                "dedicated metadata snapshot declares its schema version");

        var corruptions = new (string Name, string Json)[]
        {
            ("malformed JSON", "{"),
            ("newer schema", SnapshotJson(before, schemaVersion: 2)),
            ("unsupported old schema", SnapshotJson(before, schemaVersion: 0)),
            ("missing schema", JsonSerializer.Serialize(new { before.Revision, State = before })),
            ("missing envelope revision", JsonSerializer.Serialize(new { SchemaVersion = 1, State = before })),
            ("missing state", JsonSerializer.Serialize(new { SchemaVersion = 1, before.Revision })),
            ("null state", JsonSerializer.Serialize(new { SchemaVersion = 1, before.Revision, State = (UnifiedMemoryState?)null })),
            ("revision mismatch", SnapshotJson(before, revision: before.Revision + 1)),
            ("negative revision", SnapshotJson(before with { Revision = -1 })),
            ("incomplete state shape", JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, Revision = 0, State = new { Revision = 0, Enabled = false }
            }))
        };
        foreach (var (name, json) in corruptions)
        {
            AppDatabase.SetMeta(SnapshotKey, json);
            var reopened = new UnifiedMemoryStore();
            var loadRejected = RejectsCorruption(() => reopened.Load());
            var saveRejected = RejectsCorruption(() => store.Save(before.Revision, before with { Revision = before.Revision + 1 }));
            Check(loadRejected && saveRejected && AppDatabase.GetMeta(SnapshotKey) == json,
                $"{name} rejects reload and save without overwriting evidence");

            var memory = new UnifiedMemory(reopened);
            Check(!memory.Enabled && memory.Read(Target, "help").Entries.IsEmpty, $"{name} starts fail-closed");
            Check(memory.SetEnabled(true).Status == UnifiedMemoryStatus.StorageFailure, $"{name} cannot be replaced by silently empty enabled state");
            Check(memory.IsLegacyProfileSuppressed(Subject, Scope(Source)), $"{name} denies legacy fallback rather than resurrecting profile");
        }

        ResetMemorySnapshot();
        Check(RejectsCorruption(() => store.Load())
            && RejectsCorruption(() => store.Save(before.Revision, before with { Revision = before.Revision + 1 })),
            "missing initialized snapshot rejects reads and CAS writes");
    }

    private static string SnapshotJson(UnifiedMemoryState state, int schemaVersion = 1, long? revision = null)
        => JsonSerializer.Serialize(new { SchemaVersion = schemaVersion, Revision = revision ?? state.Revision, State = state });

    private static bool RejectsCorruption(Action action)
    {
        try { action(); }
        catch (JsonException) { return true; }
        catch (InvalidDataException) { return true; }
        return false;
    }
}

internal sealed class SyntheticDatabase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "BotAgent.UnifiedMemoryProbe", Guid.NewGuid().ToString("N"));
    public SyntheticDatabase()
    {
        Directory.CreateDirectory(_directory);
        AppDatabase.Initialize(File("probe.db"));
    }
    public string File(string name) => Path.Combine(_directory, name);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BotAgent.UnifiedMemoryProbe")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(_directory);
        if (!target.StartsWith(root, StringComparison.Ordinal) || Path.GetFileName(target).Length != 32)
            throw new InvalidOperationException("Refusing non-synthetic cleanup path.");
        Directory.Delete(target, recursive: true);
    }
}

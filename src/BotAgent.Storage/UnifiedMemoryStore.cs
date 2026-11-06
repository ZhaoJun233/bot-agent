using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Ports;

namespace BotAgent.Storage;

/// <summary>
/// Single-process AppDatabase-backed durable memory snapshot, with atomic revision CAS. Register one
/// UnifiedMemory authority per database. Does not inspect, migrate or backfill any history/profile table.
/// Logical deletion removes live bodies; SQLite WAL/backups are not a secure-erasure guarantee.
/// </summary>
public sealed class UnifiedMemoryStore : IUnifiedMemoryPersistence
{
    // Keep one stable key so an older reader sees and rejects a newer schema rather than starting empty.
    private const string SnapshotKey = "unified_memory_snapshot";
    private const int SchemaVersion = 1;
    private const int MaximumPayloadLength = 16 * 1024 * 1024;

    public UnifiedMemoryStore()
    {
        // The existing metadata transaction owns SQL/schema; this module owns only its versioned value.
        AppDatabase.Write(connection =>
        {
            if (AppDatabase.GetMetaInTransaction(connection, SnapshotKey) is null)
                AppDatabase.SetMetaInTransaction(connection, SnapshotKey, Serialize(UnifiedMemoryState.Empty));
        });
    }

    public UnifiedMemoryState Load()
    {
        using var connection = AppDatabase.Open();
        return Parse(AppDatabase.GetMetaInTransaction(connection, SnapshotKey));
    }

    public void Save(long expectedRevision, UnifiedMemoryState state)
    {
        if (expectedRevision < 0 || expectedRevision == long.MaxValue || state.Revision != expectedRevision + 1)
            throw new InvalidOperationException("Memory revision is not sequential.");
        var json = Serialize(state);
        AppDatabase.Write(connection =>
        {
            // Read, validate, compare and replace under the same process lock and database transaction.
            var current = Parse(AppDatabase.GetMetaInTransaction(connection, SnapshotKey));
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("Memory persistence revision conflict.");
            AppDatabase.SetMetaInTransaction(connection, SnapshotKey, json);
        });
    }

    private static string Serialize(UnifiedMemoryState state)
    {
        Validate(state);
        var json = JsonSerializer.Serialize(new Snapshot(SchemaVersion, state.Revision, state));
        if (json.Length > MaximumPayloadLength)
            throw new InvalidOperationException("Memory snapshot exceeds the safe payload bound.");
        return json;
    }

    private static UnifiedMemoryState Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaximumPayloadLength)
            throw new InvalidDataException("Memory snapshot is missing or exceeds the safe payload bound.");
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json)
            ?? throw new InvalidDataException("Memory snapshot is empty.");
        if (snapshot.SchemaVersion != SchemaVersion)
            throw new InvalidDataException("Unsupported memory schema version.");
        Validate(snapshot.State);
        if (snapshot.Revision != snapshot.State.Revision)
            throw new InvalidDataException("Memory snapshot revision is inconsistent.");
        return snapshot.State;
    }

    private static void Validate(UnifiedMemoryState? state)
    {
        if (state is null || state.Revision < 0 || state.Entries.IsDefault || state.Suppressions.IsDefault)
            throw new InvalidDataException("Memory snapshot shape is invalid.");
    }

    private sealed record Snapshot(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] long Revision,
        [property: JsonRequired] UnifiedMemoryState State);
}

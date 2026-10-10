extern alias StorageModule;

using Microsoft.Data.Sqlite;
using BotAgent.Services;
using BotAgent.Adapters.Time;
using Database = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility facade only: Storage owns schema, connections and writer coordination.</summary>
public static class AppDatabase
{
    public static string FilePath => Database.IsReady
        ? Database.FilePath : Path.Combine(AppPaths.DataDir, "qqchat.db");
    public static bool IsReady => Database.IsReady;

    public static void Initialize()
    {
        ClockBindings.InitializeStorage();
        Database.Initialize(FilePath);
    }

    public static SqliteConnection Open() => Database.Open();
    public static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
        => Database.Exec(conn, sql, args);
    public static int ExecCount(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
        => Database.ExecCount(conn, sql, args);
    public static bool TryRegisterFeishuWebhook(string eventKey, DateTimeOffset seenAt, TimeSpan ttl)
        => Database.TryRegisterFeishuWebhook(eventKey, seenAt, ttl);
    public static bool TryRegisterFeishuWebhooks(IReadOnlyList<string> eventKeys, DateTimeOffset seenAt, TimeSpan ttl)
        => Database.TryRegisterFeishuWebhooks(eventKeys, seenAt, ttl);
    public static void Write(Action<SqliteConnection> action) => Database.Write(action);
    public static Task WriteAsync(Action<SqliteConnection> action) => Database.WriteAsync(action);
    public static List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
        => Database.Query(sql, map, args);
    public static T? Scalar<T>(string sql, params (string Name, object? Value)[] args) => Database.Scalar<T>(sql, args);
    public static string? GetMeta(string key) => Database.GetMeta(key);
    public static void SetMeta(string key, string value) => Database.SetMeta(key, value);
    public static bool HasMeta(string key) => Database.HasMeta(key);
    public static string? Str(SqliteDataReader r, string name) => Database.Str(r, name);
    public static long? LongOrNull(SqliteDataReader r, string name) => Database.LongOrNull(r, name);
    public static long Long(SqliteDataReader r, string name, long fallback = 0) => Database.Long(r, name, fallback);
    public static int Int(SqliteDataReader r, string name, int fallback = 0) => Database.Int(r, name, fallback);
    public static bool Bool(SqliteDataReader r, string name, bool fallback = false) => Database.Bool(r, name, fallback);
    public static double Double(SqliteDataReader r, string name, double fallback = 0) => Database.Double(r, name, fallback);
    public static string VacuumIntoBackup(string? backupDir = null, int maxRetained = 3)
        => Database.VacuumIntoBackup(backupDir, maxRetained);
}
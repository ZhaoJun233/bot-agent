extern alias StorageModule;

using BotAgent.Domain.Configuration;
using StorageCredentials = StorageModule::BotAgent.Adapters.Persistence.ModelCredentialStore;

namespace BotAgent.Adapters.Persistence;

/// <summary>Friendly alias over the Storage implementation; behavior lives in Storage.</summary>
public sealed class ModelCredentialStore
{
    internal static string Name(ModelCredentialKey key) => StorageCredentials.Name(key);

    internal static string? LoadInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection, ModelCredentialKey key)
        => StorageCredentials.LoadInTransaction(connection, key);

    internal static void SaveInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection, ModelCredentialKey key,
        string? value, long now) => StorageCredentials.SaveInTransaction(connection, key, value, now);

    public string? Load(ModelCredentialKey key) => new StorageCredentials().Load(key);
}

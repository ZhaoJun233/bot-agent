using System.Text.Json;
using System.Text;
using System.Collections.Immutable;
using BotAgent.Adapters.Persistence;
using Microsoft.Data.Sqlite;

namespace BotAgent.Storage;

public sealed class PluginStore : IPluginStore
{
    private const string MetaKey = "plugins_state";

    public IReadOnlyDictionary<string, bool> LoadPluginStates()
    {
        using var connection = AppDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var states = LoadPluginStatesInTransaction(connection);
        transaction.Commit();
        return states;
    }

    public void SetPluginEnabled(string pluginId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("Plugin ID cannot be null or whitespace.", nameof(pluginId));
        }

        AppDatabase.Write(connection =>
        {
            if (ConfigurationStore.RequiresCoordinatedConfigurationWrites)
                throw new InvalidOperationException("configuration_writer_required");
            var current = new Dictionary<string, bool>(LoadPluginStatesInTransaction(connection), StringComparer.Ordinal);
            current[pluginId.Trim()] = enabled;
            SavePluginStatesInTransaction(connection, current);
        });
    }

    // The caller owns the single configuration transaction; no nested writer or registration policy here.
    internal static IReadOnlyDictionary<string, bool> LoadPluginStatesInTransaction(SqliteConnection connection)
    {
        var json = AppDatabase.GetMetaInTransaction(connection, MetaKey);
        var states = ImmutableDictionary.CreateBuilder<string, bool>(StringComparer.Ordinal);
        if (json is null) return states.ToImmutable();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                ValidateKey(property.Name);
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !states.TryAdd(property.Name, property.Value.GetBoolean())) throw new JsonException();
            }
            return states.ToImmutable();
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new JsonException("plugin_state_needs_review"); }
    }

    internal static void SavePluginStatesInTransaction(SqliteConnection connection, IReadOnlyDictionary<string, bool> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        foreach (var key in states.Keys) ValidateKey(key);
        var ordered = states.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        AppDatabase.SetMetaInTransaction(connection, MetaKey, JsonSerializer.Serialize(ordered));
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key != key.Trim() || key.Length > 128 || key.Contains('\0'))
            throw new JsonException("plugin_state_needs_review");
        _ = new UTF8Encoding(false, true).GetByteCount(key);
    }
}

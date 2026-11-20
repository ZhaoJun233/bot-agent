using System.Text.Json;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Storage;

public sealed partial class PluginStore
{
    private const string PresetIntentKey = "plugins_preset_intent_v1";

    // Startup migration only. Capture the old effective default before removing a preset's
    // registration. Later install/uninstall operations retain this explicit configuration.
    internal static IReadOnlyDictionary<string, bool> PreservePresetIntent(
        IReadOnlyDictionary<string, bool> legacyDefaults)
    {
        foreach (var id in legacyDefaults.Keys)
        {
            ValidateKey(id);
            if (!id.StartsWith("preset.", StringComparison.Ordinal) && !id.StartsWith("system.", StringComparison.Ordinal))
                throw new ArgumentException("A legacy preset or system identity is required.");
        }
        IReadOnlyDictionary<string, bool>? result = null;
        AppDatabase.Write(connection =>
        {
            var current = new Dictionary<string, bool>(LoadPluginStatesInTransaction(connection), StringComparer.Ordinal);
            var captured = new Dictionary<string, bool>(
                ReadStates(AppDatabase.GetMetaInTransaction(connection, PresetIntentKey)), StringComparer.Ordinal);
            // Losing an explicit intent after migration is damaged state, never permission
            // to restore a default and potentially turn an uninstalled feature back on.
            if (captured.Keys.Any(id => !current.ContainsKey(id)))
                throw new JsonException("plugin_preset_intent_needs_review");
            var missing = legacyDefaults.Keys.Where(id => !captured.ContainsKey(id)).ToArray();
            if (missing.Length != 0)
            {
                if (ConfigurationStore.RequiresCoordinatedConfigurationWrites)
                    throw new InvalidOperationException("configuration_writer_required");
                foreach (var id in missing)
                {
                    if (!current.TryGetValue(id, out var enabled)) current[id] = enabled = legacyDefaults[id];
                    captured.Add(id, enabled);
                }
                SavePluginStatesInTransaction(connection, current);
                AppDatabase.SetMetaInTransaction(connection, PresetIntentKey, JsonSerializer.Serialize(
                    captured.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value)));
            }
            result = current;
        });
        return result!;
    }
}

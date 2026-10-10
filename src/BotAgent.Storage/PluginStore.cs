using System.Text.Json;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Storage;

public sealed class PluginStore : IPluginStore
{
    private const string MetaKey = "plugins_state";
    private readonly object _gate = new();

    public IReadOnlyDictionary<string, bool> LoadPluginStates()
    {
        lock (_gate)
        {
            var json = AppDatabase.GetMeta(MetaKey);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                return dict != null
                    ? new Dictionary<string, bool>(dict, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void SetPluginEnabled(string pluginId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("Plugin ID cannot be null or whitespace.", nameof(pluginId));
        }

        lock (_gate)
        {
            var current = new Dictionary<string, bool>(LoadPluginStates(), StringComparer.OrdinalIgnoreCase);
            current[pluginId.Trim()] = enabled;

            var json = JsonSerializer.Serialize(current);
            AppDatabase.SetMeta(MetaKey, json);
        }
    }
}

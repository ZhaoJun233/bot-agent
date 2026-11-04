extern alias StorageModule;

using System.Text.Json;
using BotAgent.Services;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility entry; the host retains its exact typed settings validation.</summary>
public static class LegacyJsonImporter
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    public static void ImportIfNeeded()
        => StorageModule::BotAgent.Adapters.Persistence.LegacyJsonImporter.ImportIfNeeded(ValidateSettings);
    private static void ValidateSettings(string json)
        => _ = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);
}
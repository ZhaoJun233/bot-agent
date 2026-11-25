using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Services;
using BotAgent.Services.Ops;
using Microsoft.Data.Sqlite;

namespace BotAgent.Storage;

internal static partial class ConfigurationStorageBridge
{
    internal static BoundConfigurationSnapshot ReadBoundConfiguration(string owner, PluginConfigurationDeclaration declaration)
        => ConfigurationStore.ReadBoundConfiguration(owner, declaration);
    internal static long ReadBoundVersion(SqliteConnection connection) => ConfigurationStore.ReadBoundVersion(connection);
    internal static BoundConfigurationPreservation ReadBoundPreservation(string owner,
        IReadOnlyList<PluginConfigurationDeclaration> declarations)
        => ConfigurationStore.ReadBoundPreservation(owner, declarations);
    internal static Action PrepareBoundConfiguration(SqliteConnection connection, string owner,
        PluginConfigurationDeclaration declaration, ConfigurationPatch patch)
        => ConfigurationStore.PersistBoundConfiguration(connection, owner, declaration, patch);
}

internal sealed record BoundConfigurationSnapshot(long Revision, IReadOnlyDictionary<string, JsonElement> Values,
    IReadOnlyDictionary<string, JsonElement> RuntimeValues, string? Metadata);
internal sealed record BoundConfigurationPreservation(long Revision, string? Metadata,
    IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Values);

public sealed partial class ConfigurationStore
{
    internal static long ReadBoundVersion(SqliteConnection connection) => Version(connection).Value;

    internal static BoundConfigurationSnapshot ReadBoundConfiguration(string owner, PluginConfigurationDeclaration declaration)
    {
        BoundConfigurationSnapshot? snapshot = null;
        CoordinateBoundRead(connection =>
        {
            var stored = ReadBoundCarrier(connection);
            var values = LegacyPluginConfigurationFields.Read(owner, declaration, stored.Settings);
            var active = Volatile.Read(ref _active);
            snapshot = new(Version(connection).Value, values,
                active is null ? new Dictionary<string, JsonElement>() : active._participant.ReadBoundRuntime(owner, declaration),
                AppDatabase.GetMetaInTransaction(connection, "plugin_settings:" + owner));
        });
        return snapshot!;
    }

    internal static BoundConfigurationPreservation ReadBoundPreservation(string owner,
        IReadOnlyList<PluginConfigurationDeclaration> declarations)
    {
        BoundConfigurationPreservation? snapshot = null;
        CoordinateBoundRead(connection =>
        {
            var stored = ReadBoundCarrier(connection);
            snapshot = new(Version(connection).Value, AppDatabase.GetMetaInTransaction(connection, "plugin_settings:" + owner),
                declarations.Select(declaration => LegacyPluginConfigurationFields.Read(owner, declaration, stored.Settings)).ToArray());
        });
        return snapshot!;
    }

    private static void CoordinateBoundRead(Action<SqliteConnection> read)
    {
        AppDatabase.RejectNestedWrite();
        void Read()
        {
            using var connection = AppDatabase.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            read(connection);
            transaction.Commit();
        }
        var participant = Volatile.Read(ref _active);
        if (participant is not null) participant._participant.CoordinateWrite(Read);
        else lock (ActivationGate)
        {
            participant = Volatile.Read(ref _active);
            if (participant is null) Read();
            else participant._participant.CoordinateWrite(Read);
        }
    }

    // Declarative trusted mapping only. Input conversion and package schema checks have completed before this call.
    internal static Action PersistBoundConfiguration(SqliteConnection connection, string owner,
        PluginConfigurationDeclaration declaration, ConfigurationPatch patch)
    {
        if (Volatile.Read(ref _active) is null) throw new InvalidOperationException("configuration_writer_required");
        var stored = ReadBoundCarrier(connection);
        LegacyPluginConfigurationFields.Apply(stored.Settings, patch.Behavior);
        var values = LegacyPluginConfigurationFields.Read(owner, declaration, stored.Settings, includeReadOnly: false);
        foreach (var binding in declaration.Bindings.Where(binding => !binding.ReadOnly))
            stored.Original[LegacyPluginConfigurationFields.StoredName(owner, binding)] = JsonNode.Parse(values[binding.Key].GetRawText());
        var json = stored.Original.ToJsonString();
        ConfigurationStorageBridge.WriteSettingsJson(connection, json, Clock.Now.ToUnixTimeSeconds());
        PersistModes(connection, DesiredModes(patch));
        var participant = Volatile.Read(ref _active);
        return () => participant?._participant.PublishBound(owner, declaration, values);
    }

    private static (AppSettings Settings, JsonObject Original) ReadBoundCarrier(SqliteConnection connection)
    {
        var json = ConfigurationStorageBridge.ReadSettingsJson(connection);
        if (json is null) return (new AppSettings(), new JsonObject());
        var original = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("plugin_configuration_carrier_invalid");
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? throw new InvalidDataException("plugin_configuration_carrier_invalid");
        return (settings, original);
    }
}

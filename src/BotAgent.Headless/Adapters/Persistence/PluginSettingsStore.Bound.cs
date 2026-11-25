extern alias StorageModule;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Adapters.Plugins;
using Microsoft.Data.Sqlite;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;
using ConfigurationStorage = StorageModule::BotAgent.Storage;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;

namespace BotAgent.Adapters.Persistence;

public sealed partial class PluginSettingsStore
{
    private PluginSettingsView ReadBoundManifest(PluginPackageManifest manifest, IReadOnlyList<PluginSettingDefinition> schema)
    {
        var snapshot = ConfigurationBridge.ReadBoundConfiguration(manifest.Id, manifest.Configuration!);
        _ = DecodeBoundState(snapshot.Metadata, manifest, schema, snapshot.Revision);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var pending = false;
        foreach (var field in schema)
        {
            var value = snapshot.Values[field.Key];
            PluginSettingsContract.ValidateValue(field, value, allowMissing: false);
            values.Add(field.Key, value.Clone());
            if (!snapshot.RuntimeValues.TryGetValue(field.Key, out var live) || live.GetRawText() != value.GetRawText()) pending = true;
        }
        return new(snapshot.Revision, schema, new ReadOnlyDictionary<string, JsonElement>(values), [], pending);
    }

    internal PluginConfigurationSnapshot ReadRuntimeConfiguration(PluginPackageManifest manifest)
    {
        _ = ReadSchema(manifest);
        var snapshot = ConfigurationBridge.ReadBoundConfiguration(manifest.Id, manifest.Configuration
            ?? throw new PluginSettingsException(PluginSettingsContract.InvalidSchema));
        return new(snapshot.Revision, snapshot.RuntimeValues);
    }

    private PluginSettingsReceipt SaveBound(PluginPackageManifest manifest, IReadOnlyList<PluginSettingDefinition> schema,
        long expectedRevision, string key, SortedDictionary<string, JsonElement> changes, string caller)
    {
        // Conversion cannot execute package code and is completed before any configuration/SQLite gate.
        var patch = LegacyPluginConfigurationFields.ToPatch(manifest.Id, manifest.Configuration!, changes);
        PluginSettingsReceipt? result = null;
        DatabaseOperation(() =>
        {
            var committed = ConfigurationBridge.CommitPackageSettings(manifest.Id, connection =>
                SaveBoundInTransaction(connection, manifest, schema, expectedRevision, key, changes, caller, patch));
            if (committed.ApplicationFailed) throw new PluginSettingsException("plugin_settings_application_failed");
            result = committed.Result;
        });
        return result!;
    }

    private static ConfigurationStorage.PackageSettingsWrite<PluginSettingsReceipt> SaveBoundInTransaction(SqliteConnection connection,
        PluginPackageManifest manifest, IReadOnlyList<PluginSettingDefinition> schema, long expectedRevision, string key,
        SortedDictionary<string, JsonElement> changes, string caller, ConfigurationPatch patch)
    {
        var version = ConfigurationBridge.ReadBoundVersion(connection);
        var state = LoadBoundState(connection, manifest, schema, version);
        var encodedKey = StorageSecrets.LoadNamedInTransaction(connection, SecretName(manifest.Id, "__receipt_key"));
        if (encodedKey is null && state.Revision != 0) throw new PluginSettingsException(Corrupt);
        var signingKey = encodedKey is null ? RandomNumberGenerator.GetBytes(32) : DecodeKey(encodedKey);
        try
        {
            var receiptId = Authenticate(signingKey, JsonSerializer.Serialize(new[] { caller, key }, Json));
            var fingerprint = Authenticate(signingKey, JsonSerializer.Serialize(new
                { expectedRevision, state.SchemaHash, manifest.Version, Values = changes }, Json));
            var receiptKey = "plugin_settings_receipt:" + manifest.Id + ":" + receiptId;
            var previous = StorageDatabase.GetMetaInTransaction(connection, receiptKey);
            if (previous is not null)
            {
                var stored = Deserialize<SavedReceipt>(previous);
                if (stored.Result is null || stored.Result.ReceiptId != receiptId || stored.Result.Revision < 1
                    || stored.Result.Revision > version || stored.Result.RestartRequired || stored.Fingerprint?.Length != 64)
                    throw new PluginSettingsException(Corrupt);
                if (stored.Fingerprint != fingerprint) throw new PluginSettingsException("plugin_settings_idempotency_conflict");
                return new(stored.Result, Replayed: true);
            }
            if (expectedRevision != version) throw new PluginSettingsException("plugin_settings_revision_conflict");
            if (version == long.MaxValue) throw new PluginSettingsException("plugin_settings_revision_exhausted");
            var publish = ConfigurationBridge.PrepareBoundConfiguration(connection, manifest.Id, manifest.Configuration!, patch);
            var next = new State(1, checked(version + 1), state.SchemaHash, new(StringComparer.Ordinal))
                { PreservedLegacyState = state.PreservedLegacyState };
            var receipt = new PluginSettingsReceipt(next.Revision, receiptId, RestartRequired: false);
            var now = Clock.Now.ToUnixTimeSeconds();
            if (encodedKey is null) StorageSecrets.SaveNamedInTransaction(connection, SecretName(manifest.Id, "__receipt_key"),
                Convert.ToBase64String(signingKey), now);
            StorageDatabase.SetMetaInTransaction(connection, StateKey(manifest.Id), JsonSerializer.Serialize(next, Json));
            StorageDatabase.SetMetaInTransaction(connection, receiptKey, JsonSerializer.Serialize(new SavedReceipt(fingerprint, receipt), Json));
            return new(receipt, Replayed: false, Publish: publish);
        }
        finally { CryptographicOperations.ZeroMemory(signingKey); }
    }

    private static State LoadBoundState(SqliteConnection connection, PluginPackageManifest manifest,
        IReadOnlyList<PluginSettingDefinition> schema, long version)
        => DecodeBoundState(StorageDatabase.GetMetaInTransaction(connection, StateKey(manifest.Id)), manifest, schema, version);

    private static State DecodeBoundState(string? json, PluginPackageManifest manifest,
        IReadOnlyList<PluginSettingDefinition> schema, long version)
    {
        var hash = BoundSchemaHash(manifest, schema);
        if (json is null) return new(1, 0, hash, new(StringComparer.Ordinal));
        var state = Deserialize<State>(json);
        if (state.Format != 1 || state.Revision < 1 || state.Values is null)
            throw new PluginSettingsException(Corrupt);
        if (state.SchemaHash != hash)
        {
            // The old default owner declared no settings. Adopt that precise empty state,
            // never arbitrary overrides; the first bound commit retains its exact original bytes.
            if (state.SchemaHash != SchemaHash([]) || state.Values.Count != 0 || state.PreservedLegacyState is not null)
                throw new PluginSettingsException("plugin_settings_schema_mismatch");
            return state with { SchemaHash = hash, PreservedLegacyState = json };
        }
        if (state.Values.Count != 0 || state.Revision > version) throw new PluginSettingsException(Corrupt);
        if (state.PreservedLegacyState is { } legacy)
            _ = ReadPreservedEmptyState(legacy);
        return state;
    }

    private static string BoundSchemaHash(PluginPackageManifest manifest, IReadOnlyList<PluginSettingDefinition> schema)
        => PluginPackageRevision.Hash(JsonSerializer.Serialize(new { Schema = SchemaHash(schema), Configuration = manifest.Configuration! with
            { Bindings = manifest.Configuration!.Bindings.OrderBy(binding => binding.Key, StringComparer.Ordinal).ToArray() } }, Json));

    private string ResolveBoundRuntimeValues(PluginPackageManifest manifest, IReadOnlyList<PluginSettingDefinition> schema, out long revision)
    {
        var view = ReadBoundManifest(manifest, schema);
        revision = view.Revision;
        Prepared[RuntimeKey(manifest.Id)] = new(view.Revision, BoundSchemaHash(manifest, schema), manifest.Version);
        return JsonSerializer.Serialize(view.Values, Json);
    }

    private bool MarkBoundApplied(PluginPackageManifest manifest, IReadOnlyList<PluginSettingDefinition> schema, long revision)
    {
        var expected = new AppliedSnapshot(revision, BoundSchemaHash(manifest, schema), manifest.Version);
        var key = RuntimeKey(manifest.Id);
        if (!Prepared.TryGetValue(key, out var prepared) || prepared != expected) return false;
        var view = ReadBoundManifest(manifest, schema);
        if (view.Revision != revision || view.RestartRequired) return false;
        Applied[key] = prepared;
        return true;
    }
}

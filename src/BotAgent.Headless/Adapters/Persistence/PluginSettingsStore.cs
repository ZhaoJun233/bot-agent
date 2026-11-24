extern alias StorageModule;

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Plugins;
using BotAgent.Adapters.Plugins;
using Microsoft.Data.Sqlite;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;
using ConfigurationStorage = StorageModule::BotAgent.Storage;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;

namespace BotAgent.Adapters.Persistence;

/// <summary>Restart-bound settings; one SQLite transaction owns overrides, scoped secrets and receipts.</summary>
public sealed partial class PluginSettingsStore
{
    private readonly TrustedPluginDistribution _distribution;
    public PluginSettingsStore() : this(TrustedPluginDistribution.Empty) { }
    public PluginSettingsStore(TrustedPluginDistribution distribution)
        => _distribution = distribution ?? throw new ArgumentNullException(nameof(distribution));
    private const int MaximumJsonLength = 1_048_576;
    private const string Corrupt = "plugin_settings_corrupt";
    private static readonly JsonSerializerOptions Json = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private static readonly ConcurrentDictionary<string, AppliedSnapshot> Prepared = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, AppliedSnapshot> Applied = new(StringComparer.Ordinal);
    private static string RuntimeKey(string id)
    {
        try { return StorageDatabase.FilePath + "|" + id; }
        catch (InvalidOperationException) { return "uninitialized|" + id; }
    }
    internal static void ClearRuntimeState(string id)
    {
        var key = RuntimeKey(id);
        Prepared.TryRemove(key, out _);
        Applied.TryRemove(key, out _);
    }
    private sealed record State(int Format, long Revision, string SchemaHash, Dictionary<string, JsonElement> Values)
    {
        // Exact prior empty-schema bytes, retained only when adopting legacy configuration bindings.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PreservedLegacyState { get; init; }
        [JsonIgnore]
        public bool IsPreservedProjection { get; init; }
    }
    private sealed record SavedReceipt(string Fingerprint, PluginSettingsReceipt Result);
    private sealed record AppliedSnapshot(long Revision, string SchemaHash, string Version);
    private sealed record Snapshot(State State, Dictionary<string, JsonElement> Values);

    // Lifecycle uninstall binds opaque persisted metadata, not a possibly incompatible schema.
    public string ReadPreservationStamp(string id)
    {
        string? raw = null;
        DatabaseOperation(() =>
        {
            using var connection = StorageDatabase.Open();
            raw = StorageDatabase.GetMetaInTransaction(connection, StateKey(id));
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw ?? "")));
    }

    /// <summary>Update admission also binds the current and target legacy values at one global version.</summary>
    public string ReadPreservationStamp(PluginPackageManifest current, PluginPackageManifest? target)
    {
        var declarations = new[] { current.Configuration, target?.Configuration }.OfType<PluginConfigurationDeclaration>()
            .Select(declaration => declaration with { Bindings = declaration.Bindings.OrderBy(binding => binding.Key, StringComparer.Ordinal).ToArray() })
            .ToArray();
        if (declarations.Length == 0) return ReadPreservationStamp(current.Id);
        ConfigurationStorage.BoundConfigurationPreservation? snapshot = null;
        DatabaseOperation(() => snapshot = ConfigurationBridge.ReadBoundPreservation(current.Id, declarations));
        return PluginPackageRevision.Hash(JsonSerializer.Serialize(snapshot, Json));
    }

    public PluginSettingsView ReadManifest(PluginPackageManifest manifest)
    {
        var schema = ReadSchema(manifest);
        if (manifest.Configuration is not null) return ReadBoundManifest(manifest, schema);
        var snapshot = ReadSnapshot(manifest.Id, schema, allowMissing: true);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var configured = new List<string>();
        foreach (var field in schema)
            if (field.Type != "secret") values.Add(field.Key, snapshot.Values[field.Key]);
            else if (snapshot.Values[field.Key].ValueKind == JsonValueKind.String) configured.Add(field.Key);
        var target = Stamp(manifest, snapshot.State);
        return new(snapshot.State.Revision, schema, new ReadOnlyDictionary<string, JsonElement>(values),
            configured.AsReadOnly(), !Applied.TryGetValue(RuntimeKey(manifest.Id), out var applied) || applied != target);
    }

    /// <summary>callerScope must be the authenticated principal, never a token or plugin-supplied identity.</summary>
    public PluginSettingsReceipt Save(PluginPackageManifest manifest, long expectedRevision, string idempotencyKey,
        JsonElement values, string callerScope)
    {
        var schema = ReadSchema(manifest);
        if (expectedRevision < 0 || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256 ||
            idempotencyKey.Any(char.IsControl) || string.IsNullOrWhiteSpace(callerScope) || callerScope.Length > 256 ||
            callerScope.Any(char.IsControl)) throw new PluginSettingsException("plugin_settings_invalid_request");
        var changes = ReadChanges(values, schema);
        if (manifest.Configuration is not null)
            return SaveBound(manifest, schema, expectedRevision, idempotencyKey, changes, callerScope);
        PluginSettingsReceipt? result = null;
        DatabaseOperation(() =>
        {
            result = ConfigurationBridge.CommitPackageSettings(manifest.Id, connection =>
                SaveInTransaction(connection, manifest, schema, expectedRevision, idempotencyKey, changes, callerScope)).Result;
        });
        return result!;
    }

    /// <summary>Read-only lifecycle admission: no prepared/applied runtime state and no writes.</summary>
    public long ValidatePreserved(PluginPackageManifest manifest)
        => manifest.Configuration is not null ? ReadBoundManifest(manifest, ReadSchema(manifest)).Revision
            : ReadSnapshot(manifest.Id, ReadSchema(manifest), allowMissing: false).State.Revision;

    public string ResolveRuntimeValues(PluginPackageManifest manifest) => ResolveRuntimeValues(manifest, out _);

    /// <summary>无副作用读取当前清单声明字段的有效值（含 secret 明文），供宿主端口按需实时读取，不改 Prepared/Applied 标记。</summary>
    public IReadOnlyDictionary<string, JsonElement> ReadEffectiveValues(PluginPackageManifest manifest)
    {
        var schema = ReadSchema(manifest);
        if (manifest.Configuration is not null) return ReadBoundManifest(manifest, schema).Values;
        return ReadSnapshot(manifest.Id, schema, allowMissing: true).Values;
    }

    /// <summary>Only this manifest's declared values are returned; call MarkApplied after successful initialization.</summary>
    public string ResolveRuntimeValues(PluginPackageManifest manifest, out long revision)
    {
        var schema = ReadSchema(manifest);
        if (manifest.Configuration is not null) return ResolveBoundRuntimeValues(manifest, schema, out revision);
        var snapshot = ReadSnapshot(manifest.Id, schema, allowMissing: false);
        var json = JsonSerializer.Serialize(snapshot.Values, Json);
        Prepared[RuntimeKey(manifest.Id)] = Stamp(manifest, snapshot.State);
        revision = snapshot.State.Revision;
        return json;
    }

    /// <summary>Host-only acknowledgement. Failed initialization must not call this method.</summary>
    public bool MarkApplied(PluginPackageManifest manifest, long revision)
    {
        var schema = ReadSchema(manifest);
        if (manifest.Configuration is not null) return MarkBoundApplied(manifest, schema, revision);
        var expected = new AppliedSnapshot(revision, SchemaHash(schema), manifest.Version);
        var key = RuntimeKey(manifest.Id);
        if (!Prepared.TryGetValue(key, out var prepared) || prepared != expected) return false;
        var applied = false;
        DatabaseOperation(() =>
        {
            StorageDatabase.Write(connection =>
            {
                var current = LoadState(connection, manifest.Id, schema);
                if (Stamp(manifest, current) == expected)
                {
                    Applied[key] = prepared;
                    applied = true;
                }
            });
        });
        return applied;
    }

    private IReadOnlyList<PluginSettingDefinition> ReadSchema(PluginPackageManifest manifest)
    {
        if (manifest is null) throw new PluginSettingsException(PluginSettingsContract.InvalidSchema);
        try
        {
            BotAgent.Domain.Configuration.LegacyPluginConfigurationFields.Validate(manifest);
            _distribution.ValidateId(manifest.Id);
            if (manifest.Id.StartsWith("preset.", StringComparison.Ordinal)) _distribution.EnsureApproved(manifest);
        }
        catch (InvalidDataException) { throw new PluginSettingsException(PluginSettingsContract.InvalidSchema); }
        return PluginSettingsContract.ReadSchema(manifest);
    }

    private static Snapshot ReadSnapshot(string id, IReadOnlyList<PluginSettingDefinition> schema, bool allowMissing)
    {
        Snapshot? result = null;
        DatabaseOperation(() =>
        {
            using var connection = StorageDatabase.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var state = LoadState(connection, id, schema);
            var secrets = ReadSecrets(connection, id, schema);
            result = new(state, EffectiveValues(schema, state.Values, secrets, allowMissing));
            transaction.Commit();
        });
        return result!;
    }

    private static ConfigurationStorage.PackageSettingsWrite<PluginSettingsReceipt> SaveInTransaction(SqliteConnection connection, PluginPackageManifest manifest,
        IReadOnlyList<PluginSettingDefinition> schema, long expectedRevision, string key,
        SortedDictionary<string, JsonElement> changes, string caller)
    {
        var state = LoadState(connection, manifest.Id, schema);
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
                if (stored.Result is null || stored.Result.ReceiptId != receiptId || stored.Result.Revision < 1 ||
                    stored.Result.Revision > state.Revision || !stored.Result.RestartRequired ||
                    stored.Fingerprint?.Length != 64) throw new PluginSettingsException(Corrupt);
                if (stored.Fingerprint != fingerprint) throw new PluginSettingsException("plugin_settings_idempotency_conflict");
                return new(stored.Result, Replayed: true);
            }
            if (state.IsPreservedProjection) throw new PluginSettingsException("plugin_settings_schema_mismatch");
            if (state.Revision != expectedRevision) throw new PluginSettingsException("plugin_settings_revision_conflict");
            if (state.Revision == long.MaxValue) throw new PluginSettingsException("plugin_settings_revision_exhausted");
            var overrides = new Dictionary<string, JsonElement>(state.Values, StringComparer.Ordinal);
            var secrets = ReadSecrets(connection, manifest.Id, schema);
            foreach (var field in schema)
            {
                if (!changes.TryGetValue(field.Key, out var change)) continue;
                if (field.Type == "secret") secrets[field.Key] = change.ValueKind == JsonValueKind.Null ? null : change.GetString();
                else if (change.ValueKind == JsonValueKind.Null) overrides.Remove(field.Key);
                else overrides[field.Key] = change;
            }
            _ = EffectiveValues(schema, overrides, secrets, allowMissing: false);
            var next = new State(1, state.Revision + 1, state.SchemaHash, overrides);
            var metadata = JsonSerializer.Serialize(next, Json);
            if (metadata.Length > MaximumJsonLength) throw new PluginSettingsException(PluginSettingsContract.InvalidValues);
            var receipt = new PluginSettingsReceipt(next.Revision, receiptId, RestartRequired: true);
            var now = Clock.Now.ToUnixTimeSeconds();
            if (encodedKey is null)
                StorageSecrets.SaveNamedInTransaction(connection, SecretName(manifest.Id, "__receipt_key"),
                    Convert.ToBase64String(signingKey), now);
            foreach (var field in schema.Where(f => f.Type == "secret" && changes.ContainsKey(f.Key)))
                StorageSecrets.SaveNamedInTransaction(connection, SecretName(manifest.Id, field.Key), secrets[field.Key], now);
            StorageDatabase.SetMetaInTransaction(connection, StateKey(manifest.Id), metadata);
            StorageDatabase.SetMetaInTransaction(connection, receiptKey, JsonSerializer.Serialize(new SavedReceipt(fingerprint, receipt), Json));
            return new(receipt, Replayed: false);
        }
        finally { CryptographicOperations.ZeroMemory(signingKey); }
    }

    private static SortedDictionary<string, JsonElement> ReadChanges(JsonElement values, IReadOnlyList<PluginSettingDefinition> schema)
    {
        if (values.ValueKind != JsonValueKind.Object || values.GetRawText().Length > MaximumJsonLength)
            throw new PluginSettingsException(PluginSettingsContract.InvalidValues);
        var definitions = schema.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var result = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in values.EnumerateObject())
        {
            if (!definitions.TryGetValue(property.Name, out var field) || result.ContainsKey(property.Name))
                throw new PluginSettingsException(PluginSettingsContract.InvalidValues);
            PluginSettingsContract.ValidateValue(field, property.Value, allowMissing: true);
            result.Add(property.Name, property.Value.Clone());
        }
        return result;
    }

    private static State LoadState(SqliteConnection connection, string id, IReadOnlyList<PluginSettingDefinition> schema)
    {
        var json = StorageDatabase.GetMetaInTransaction(connection, StateKey(id));
        var hash = SchemaHash(schema);
        if (json is null) return new(1, 0, hash, new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        var state = Deserialize<State>(json);
        if (state.Format != 1 || state.Revision < 1 || state.Values is null || state.Values.Count > 128 || state.SchemaHash is null)
            throw new PluginSettingsException(Corrupt);
        // No implicit migration: preserve old overrides and secrets for operator review or rollback.
        if (state.PreservedLegacyState is { } legacy)
        {
            if (schema.Count != 0) throw new PluginSettingsException("plugin_settings_schema_mismatch");
            // Only authenticated original receipt replay may use this read-only historical
            // projection. The caller still supplies its real, separately approved manifest.
            return ReadPreservedEmptyState(legacy) with { IsPreservedProjection = true };
        }
        if (state.SchemaHash != hash) throw new PluginSettingsException("plugin_settings_schema_mismatch");
        var fields = schema.ToDictionary(f => f.Key, StringComparer.Ordinal);
        foreach (var entry in state.Values)
        {
            if (!fields.TryGetValue(entry.Key, out var field) || field.Type == "secret" || entry.Value.ValueKind == JsonValueKind.Null)
                throw new PluginSettingsException(Corrupt);
            PluginSettingsContract.ValidateValue(field, entry.Value, allowMissing: false, Corrupt);
        }
        return state;
    }

    private static State ReadPreservedEmptyState(string json)
    {
        var state = Deserialize<State>(json);
        if (state.Format != 1 || state.Revision < 1 || state.SchemaHash != SchemaHash([])
            || state.Values is not { Count: 0 } || state.PreservedLegacyState is not null)
            throw new PluginSettingsException(Corrupt);
        return state;
    }

    private static Dictionary<string, string?> ReadSecrets(SqliteConnection connection, string id, IReadOnlyList<PluginSettingDefinition> schema)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in schema.Where(f => f.Type == "secret"))
        {
            var secret = StorageSecrets.LoadNamedInTransaction(connection, SecretName(id, field.Key));
            if (secret is not null && (string.IsNullOrWhiteSpace(secret) || secret.Length > PluginSettingsContract.MaximumValueLength))
                throw new PluginSettingsException(Corrupt);
            result.Add(field.Key, secret);
        }
        return result;
    }

    private static Dictionary<string, JsonElement> EffectiveValues(IReadOnlyList<PluginSettingDefinition> schema,
        Dictionary<string, JsonElement> overrides, Dictionary<string, string?> secrets, bool allowMissing)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in schema)
        {
            var value = field.Type == "secret" ? JsonSerializer.SerializeToElement(secrets[field.Key]) :
                overrides.TryGetValue(field.Key, out var stored) ? stored : PluginSettingsContract.DefaultValue(field);
            PluginSettingsContract.ValidateValue(field, value, allowMissing);
            result.Add(field.Key, value);
        }
        return result;
    }

    private static T Deserialize<T>(string json) where T : class
    {
        if (json.Length > MaximumJsonLength) throw new PluginSettingsException(Corrupt);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateKeys(document.RootElement);
            return JsonSerializer.Deserialize<T>(json, Json) ?? throw new PluginSettingsException(Corrupt);
        }
        catch (JsonException) { throw new PluginSettingsException(Corrupt); }
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!keys.Add(property.Name)) throw new PluginSettingsException(Corrupt);
            RejectDuplicateKeys(property.Value);
        }
    }

    private static byte[] DecodeKey(string value)
    {
        if (value.Length != 44) throw new PluginSettingsException(Corrupt);
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 32) return bytes;
            CryptographicOperations.ZeroMemory(bytes);
        }
        catch (FormatException) { }
        throw new PluginSettingsException(Corrupt);
    }

    private static string SchemaHash(IReadOnlyList<PluginSettingDefinition> schema)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(schema.OrderBy(f => f.Key, StringComparer.Ordinal), Json))));
    private static string Authenticate(byte[] key, string text)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));
    private static string SecretName(string id, string field) => "plugin:" + id + ":" + field;
    private static string StateKey(string id) => "plugin_settings:" + id;
    private static AppliedSnapshot Stamp(PluginPackageManifest manifest, State state) => new(state.Revision, state.SchemaHash, manifest.Version);

    private static void DatabaseOperation(Action action)
    {
        try { action(); }
        catch (SqliteException) { throw new PluginSettingsException("plugin_settings_unavailable"); }
        catch (InvalidOperationException ex) when (ex.Message == "configuration_version_exhausted")
        { throw new PluginSettingsException("plugin_settings_revision_exhausted"); }
        catch (InvalidOperationException ex) when (ex.Message == "configuration_writer_required")
        { throw new PluginSettingsException("plugin_settings_unavailable"); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("database_", StringComparison.Ordinal))
        { throw new PluginSettingsException("plugin_settings_unavailable"); }
    }
}

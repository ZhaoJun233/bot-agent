extern alias StorageModule;

using System.Collections.Immutable;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;
using BotAgent.Services;
using BotAgent.Services.Resilience;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// Provider 注册表的 SQLite 适配器。
/// 只持久化路由元数据和熔断状态；实际密钥仍由环境变量/SecretsStore 提供。
/// </summary>
public sealed class ModelProviderStore
{
    internal const string PrimaryModelCredentialReference = "model:primary";
    internal const string UnconfiguredModelCredentialReference = "model:unconfigured";
    private const string FieldModesKey = "configuration_provider_field_modes";
    private const string ProviderRowsSql = """
        SELECT id, priority, name, base_url, model_name, secret_key_ref,
               is_enabled, circuit_state, consecutive_hard_failures, cooldown_until, updated_at
        FROM model_providers
        ORDER BY priority, id;
        """;
    /// <summary>
    /// 将当前配置登记为 primary Provider。
    /// 只初始化缺失登记；已有用户元数据和熔断状态不会被重置。
    /// </summary>
    public void EnsurePrimary(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var baseUrl = string.IsNullOrWhiteSpace(settings.ModelBaseUrlOverride)
            ? settings.ModelBaseUrl
            : settings.ModelBaseUrlOverride;
        // 注册表记录主模型配置；快速回复档只是请求级选择，不应改写 primary 元数据。
        var model = settings.Model;
        var secretRef = string.IsNullOrWhiteSpace(settings.ApiKeyOverride)
            ? "env:QQCHAT_API_KEY"
            : "secret:apiKey";

        var definition = new ModelProviderDefinition(
            Id: "primary",
            Priority: 0,
            Name: "Primary",
            BaseUrl: baseUrl,
            ModelName: model,
            SecretKeyRef: secretRef,
            IsEnabled: true);
        AppDatabase.Write(connection =>
        {
            var prior = LoadAllInTransaction(connection);
            if (prior.Any(provider => provider.Id == "primary")) return;
            if (prior.Any(provider => provider.Id.Trim() == "primary"))
                throw new InvalidOperationException("configuration_registration_needs_review");
            if (ConfigurationBridge.RequiresCoordinatedConfigurationWrites)
                throw new InvalidOperationException("configuration_writer_required");
            UpsertMetadataInTransaction(connection, definition);
        });
    }

    /// <summary>插入或更新非敏感 Provider 元数据，不改已有 circuit_state。</summary>
    public void UpsertMetadata(ModelProviderDefinition provider)
    {
        ValidateMetadata(provider);
        AppDatabase.Write(conn =>
        {
            if (ConfigurationBridge.RequiresCoordinatedConfigurationWrites)
                throw new InvalidOperationException("configuration_writer_required");
            var prior = LoadAllInTransaction(conn).FirstOrDefault(row => row.Id == provider.Id);
            var modes = LoadFieldModesInTransaction(conn);
            if (prior is not null && modes.TryGetValue(provider.Id, out var oldMode))
            {
                var nextMode = oldMode;
                if (prior.Name != provider.Name.Trim()) nextMode &= ~1;
                if (prior.BaseUrl != provider.BaseUrl.Trim()) nextMode &= ~2;
                if (prior.ModelName != provider.ModelName.Trim()) nextMode &= ~4;
                if (nextMode != oldMode)
                    WriteFieldModesInTransaction(conn, modes.SetItem(provider.Id, nextMode));
            }
            UpsertMetadataInTransaction(conn, provider);
        });
    }

    internal static CredentialBindingState ClassifyCredentialBinding(string reference)
        => reference switch
        {
            "secret:apiKey" or "env:QQCHAT_API_KEY" or PrimaryModelCredentialReference => CredentialBindingState.PrimaryModel,
            UnconfiguredModelCredentialReference => CredentialBindingState.Unconfigured,
            _ => CredentialBindingState.Unclassified,
        };

    internal static ImmutableDictionary<string, int> LoadFieldModesInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var json = StorageDatabase.GetMetaInTransaction(connection, FieldModesKey);
        var result = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        if (json is null) return result.ToImmutable();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireProperties(root, "version", "entries");
            if (!root.GetProperty("version").TryGetInt32(out var version) || version != 1
                || root.GetProperty("entries").ValueKind != JsonValueKind.Array) throw new JsonException();
            foreach (var entry in root.GetProperty("entries").EnumerateArray())
            {
                RequireProperties(entry, "key", "explicitFields");
                if (entry.GetProperty("key").ValueKind != JsonValueKind.String) throw new JsonException();
                var key = entry.GetProperty("key").GetString();
                if (string.IsNullOrWhiteSpace(key) || key.Trim() != key
                    || !entry.GetProperty("explicitFields").TryGetInt32(out var bits) || bits is < 0 or > 7
                    || !result.TryAdd(key, bits)) throw new JsonException();
            }
            return result.ToImmutable();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new JsonException("provider_modes_needs_review");
        }
    }

    private static void RequireProperties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var properties = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Length != allowed.Length || properties.Distinct(StringComparer.Ordinal).Count() != allowed.Length
            || properties.Any(property => !allowed.Contains(property, StringComparer.Ordinal))) throw new JsonException();
    }

    internal static void WriteFieldModesInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection,
        ImmutableDictionary<string, int> modes)
    {
        if (modes.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Trim() != entry.Key
            || entry.Value is < 0 or > 7)) throw new JsonException("provider_modes_needs_review");
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            entries = modes.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new { key = entry.Key, explicitFields = entry.Value }).ToArray(),
        });
        StorageDatabase.SetMetaInTransaction(connection, FieldModesKey, json);
    }

    /// <summary>Writes metadata on the owned configuration transaction without changing circuit state.</summary>
    internal static void UpsertMetadataInTransaction(Microsoft.Data.Sqlite.SqliteConnection conn,
        ModelProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ValidateMetadata(provider);

        AppDatabase.Exec(conn, """
            INSERT INTO model_providers
              (id, priority, name, base_url, model_name, secret_key_ref, is_enabled, updated_at)
            VALUES ($id, $priority, $name, $base, $model, $secret, $enabled, $updated)
            ON CONFLICT(id) DO UPDATE SET
              priority = excluded.priority,
              name = excluded.name,
              base_url = excluded.base_url,
              model_name = excluded.model_name,
              secret_key_ref = excluded.secret_key_ref,
              is_enabled = excluded.is_enabled,
              updated_at = excluded.updated_at;
            """,
            ("$id", provider.Id.Trim()),
            ("$priority", provider.Priority),
            ("$name", provider.Name.Trim()),
            ("$base", provider.BaseUrl.Trim()),
            ("$model", provider.ModelName.Trim()),
            ("$secret", provider.SecretKeyRef.Trim()),
            ("$enabled", provider.IsEnabled ? 1 : 0),
            ("$updated", Clock.UtcNow.ToString("O")));
    }

    private static void ValidateMetadata(ModelProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Id)) throw new ArgumentException("Provider id is required.", nameof(provider));
        if (provider.Id.Trim() != provider.Id) throw new ArgumentException("configuration_registration_needs_review", nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new ArgumentException("Provider base URL is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.ModelName)) throw new ArgumentException("Provider model name is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.SecretKeyRef)) throw new ArgumentException("Provider secret reference is required.", nameof(provider));
    }

    /// <summary>保存一次熔断状态变化。不会写入请求正文、密钥或原始响应。</summary>
    public void SaveCircuit(ProviderCircuitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var affected = 0L;
        AppDatabase.Write(conn =>
        {
            affected = AppDatabase.ExecCount(conn, """
                UPDATE model_providers
                SET circuit_state = $state,
                    consecutive_hard_failures = $failures,
                    cooldown_until = $cooldown,
                    updated_at = $updated
                WHERE id = $id;
                """,
                ("$id", snapshot.ProviderId),
                ("$state", ToStorageState(snapshot.State)),
                ("$failures", Math.Max(0, snapshot.ConsecutiveHardFailures)),
                ("$cooldown", snapshot.CooldownUntil?.ToString("O")),
                ("$updated", Clock.UtcNow.ToString("O")));
        });

        if (affected == 0)
        {
            throw new InvalidOperationException($"Provider '{snapshot.ProviderId}' is not registered.");
        }
    }

    public IReadOnlyList<PersistedModelProvider> LoadAll()
    {
        using var connection = AppDatabase.Open();
        return LoadAllInTransaction(connection);
    }

    internal static IReadOnlyList<PersistedModelProvider> LoadAllInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = ProviderRowsSql;
        using var reader = command.ExecuteReader();
        var rows = new List<PersistedModelProvider>();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    public PersistedModelProvider? Find(string providerId)
        => LoadAll().FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));

    private static PersistedModelProvider Read(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var stateText = AppDatabase.Str(reader, "circuit_state") ?? "closed";
        var state = stateText switch
        {
            "open" => ProviderCircuitState.Open,
            "half_open" => ProviderCircuitState.HalfOpen,
            _ => ProviderCircuitState.Closed
        };

        var cooldown = ParseDate(AppDatabase.Str(reader, "cooldown_until"));
        var updated = ParseDate(AppDatabase.Str(reader, "updated_at")) ?? DateTimeOffset.UnixEpoch;
        return new PersistedModelProvider(
            AppDatabase.Str(reader, "id") ?? string.Empty,
            AppDatabase.Int(reader, "priority"),
            AppDatabase.Str(reader, "name") ?? string.Empty,
            AppDatabase.Str(reader, "base_url") ?? string.Empty,
            AppDatabase.Str(reader, "model_name") ?? string.Empty,
            AppDatabase.Str(reader, "secret_key_ref") ?? string.Empty,
            AppDatabase.Bool(reader, "is_enabled", true),
            state,
            Math.Max(0, AppDatabase.Int(reader, "consecutive_hard_failures")),
            cooldown,
            updated);
    }

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var result)
            ? result
            : null;

    private static string ToStorageState(ProviderCircuitState state)
        => state switch
        {
            ProviderCircuitState.Open => "open",
            ProviderCircuitState.HalfOpen => "half_open",
            _ => "closed"
        };
}

public sealed record ModelProviderDefinition(
    string Id,
    int Priority,
    string Name,
    string BaseUrl,
    string ModelName,
    string SecretKeyRef,
    bool IsEnabled = true);

public sealed record PersistedModelProvider(
    string Id,
    int Priority,
    string Name,
    string BaseUrl,
    string ModelName,
    string SecretKeyRef,
    bool IsEnabled,
    ProviderCircuitState CircuitState,
    int ConsecutiveHardFailures,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset UpdatedAt);

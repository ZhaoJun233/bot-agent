using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Ports;
using Microsoft.Data.Sqlite;

namespace BotAgent.Storage;

/// <summary>Trusted same-connection integration only; no public JSON, environment or secret-value escape hatch.</summary>
internal static class ConfigurationStorageBridge
{
    private const string PlatformPoliciesMetaKey = "configuration_platform_policies";
    private static readonly JsonSerializerOptions PlatformPolicyCodec = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static ConfigurationPlatformPolicySnapshot LoadPlatformPolicies(SqliteConnection connection)
    {
        var json = AppDatabase.GetMetaInTransaction(connection, PlatformPoliciesMetaKey);
        if (json is null)
            return new(false, ImmutableArray<PlatformPolicyEntry>.Empty, ImmutableDictionary<PolicyTarget, int>.Empty);
        try
        {
            ValidatePolicyUnicode(json);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequirePolicyObject(root, "version", "entries");
            if (!root.GetProperty("version").TryGetInt32(out var version) || version != 1)
                throw new JsonException();
            var rows = root.GetProperty("entries");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 32) throw new JsonException();
            var entries = ImmutableArray.CreateBuilder<PlatformPolicyEntry>();
            var modes = ImmutableDictionary.CreateBuilder<PolicyTarget, int>();
            foreach (var row in rows.EnumerateArray())
            {
                RequirePolicyObject(row, "policy", "explicitFields");
                if (!row.GetProperty("explicitFields").TryGetInt32(out var bits) || bits is < 0 or > 63)
                    throw new JsonException();
                var policy = row.GetProperty("policy");
                ValidatePolicyShape(policy);
                var item = policy.Deserialize<PlatformPolicyEntry>(PlatformPolicyCodec) ?? throw new JsonException();
                var frozen = FreezeStoredPolicy(item);
                if (modes.ContainsKey(frozen.Target)) throw new JsonException();
                entries.Add(frozen);
                modes.Add(frozen.Target, bits);
            }
            return new(true, entries.OrderBy(x => ConfigurationStore.TargetOrder(x.Target), StringComparer.Ordinal)
                .ToImmutableArray(), modes.ToImmutable());
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException
            or NotSupportedException or OverflowException)
        {
            throw new JsonException("invalid_platform_policy_state");
        }
    }

    internal static void WritePlatformPolicies(SqliteConnection connection,
        ImmutableArray<PlatformPolicyEntry> entries, ImmutableDictionary<PolicyTarget, int> modes)
    {
        if (entries.IsDefault || entries.Length > 32 || modes is null || modes.Count != entries.Length)
            throw new JsonException("invalid_platform_policy_state");
        ImmutableArray<PlatformPolicyEntry> frozenEntries;
        try { frozenEntries = entries.Select(FreezeStoredPolicy).ToImmutableArray(); }
        catch (ArgumentException) { throw new JsonException("invalid_platform_policy_state"); }
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartArray("entries");
            var targets = new HashSet<PolicyTarget>();
            foreach (var entry in frozenEntries.OrderBy(x => ConfigurationStore.TargetOrder(x.Target), StringComparer.Ordinal))
            {
                if (entry is null || !targets.Add(entry.Target) || !modes.TryGetValue(entry.Target, out var bits)
                    || bits is < 0 or > 63) throw new JsonException("invalid_platform_policy_state");
                writer.WriteStartObject();
                writer.WritePropertyName("policy");
                JsonSerializer.Serialize(writer, entry, PlatformPolicyCodec);
                writer.WriteNumber("explicitFields", bits);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(output.ToArray());
        ValidatePolicyUnicode(json);
        AppDatabase.SetMetaInTransaction(connection, PlatformPoliciesMetaKey, json);
    }

    private static PlatformPolicyEntry FreezeStoredPolicy(PlatformPolicyEntry entry)
    {
        if (entry is null) throw new JsonException("invalid_platform_policy_state");
        var target = ConfigurationStore.FreezeTarget(entry.Target);
        if (target != entry.Target) throw new JsonException("invalid_platform_policy_state");
        if (target is PolicyTarget.Instance instance)
            new UTF8Encoding(false, true).GetByteCount(instance.Key.InstanceId);
        return new(target, ConfigurationStore.FreezePolicy(entry.Configuration));
    }

    private static void RequirePolicyObject(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new JsonException();
        if (seen.Count != fields.Length) throw new JsonException();
    }

    private static void ValidatePolicyShape(JsonElement entry)
    {
        RequirePolicyObject(entry, "target", "configuration");
        var target = entry.GetProperty("target");
        if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String) throw new JsonException();
        if (kind.GetString() == "accountDefault")
            RequirePolicyObject(target, "kind", "platformId", "accountScope");
        else if (kind.GetString() == "instance")
        {
            RequirePolicyObject(target, "kind", "key");
            RequirePolicyObject(target.GetProperty("key"), "platformId", "accountScope", "instanceId");
        }
        else throw new JsonException();
        var rules = entry.GetProperty("configuration");
        RequirePolicyObject(rules, "enabled", "chatEnabled", "groupWhitelist", "privateWhitelist", "allowedActions", "features");
        foreach (var name in new[] { "groupWhitelist", "privateWhitelist" })
        {
            var list = rules.GetProperty(name);
            if (list.ValueKind != JsonValueKind.Object || !list.TryGetProperty("kind", out var listKind)
                || !listKind.TryGetInt32(out var number)) throw new JsonException();
            if (number == (int)ListRuleKind.Explicit) RequirePolicyObject(list, "kind", "value");
            else RequirePolicyObject(list, "kind");
        }
        RequirePolicyObject(rules.GetProperty("allowedActions"), "kind", "actions");
        var features = rules.GetProperty("features");
        if (features.ValueKind != JsonValueKind.Array) throw new JsonException();
        foreach (var feature in features.EnumerateArray()) RequirePolicyObject(feature, "feature", "enabled");
    }

    private static void ValidatePolicyUnicode(string json)
    {
        // Validate every string token, including escaped Unicode and property names.
        var reader = new Utf8JsonReader(new UTF8Encoding(false, true).GetBytes(json));
        while (reader.Read())
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) _ = reader.GetString();
    }

    internal static bool RequiresCoordinatedConfigurationWrites
        => ConfigurationStore.RequiresCoordinatedConfigurationWrites;
    internal static ConfigurationVersion ReadConfigurationVersion(SqliteConnection connection)
        => ConfigurationStore.Version(connection);
    private const string DeviceRowsSql = """
        SELECT d.device_key,d.active_name,d.desired_name,d.desired_enabled,d.deleted,d.rename_pending,r.name
        FROM configuration_devices d LEFT JOIN configuration_revoked_names r ON r.device_key=d.device_key
        ORDER BY d.device_key,r.name
        """;
    internal static ConfigurationStore Activate(IClock clock, ConfigurationStorageParticipant participant)
        => ConfigurationStore.Activate(clock, participant);

    internal static ValueTask<BotAgent.Domain.Management.ManagedValidationResult> ValidateAsync(
        ConfigurationStore store, ConfigurationCaller caller, ConfigurationValidationRequest request,
        CancellationToken ct = default)
        => store.ValidateAsync(caller, request, ct);

    internal static BotAgent.Domain.Management.IManagedCapabilities CreateManagedCapabilities(ConfigurationStore store,
        BotAgent.Domain.Management.IManagedAuthorization authorization, Func<ManagedCatalogSnapshot> catalog,
        Func<BotAgent.Domain.Management.ManagedCommand, CancellationToken, ValueTask<ManagedDispatchResult>> dispatch)
        => store.CreateManagedCapabilities(authorization, catalog, dispatch);
    internal static SecretMutation ObserveModelApiKey(SqliteConnection connection)
    {
        var value = SecretsStore.LoadInTransaction(connection, WritableSecretSlot.ModelApiKey);
        // Match the public fixed-slot resolver: database values are trimmed before use.
        return ProjectModelApiKeyMutation(SecretEditKind.Keep, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }
    internal static SecretMutation ProjectModelApiKeyMutation(SecretEditKind kind, string? normalizedOwnedValue)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("invalid_secret_edit");
        var value = kind == SecretEditKind.Delete ? null : normalizedOwnedValue;
        var configured = !string.IsNullOrWhiteSpace(value);
        return new(WritableSecretSlot.ModelApiKey, kind)
        {
            FactsKnown = true,
            DatabaseConfigured = configured,
            DatabaseKeyUsable = configured && !value!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        };
    }
    internal static (bool CanRun, ConfigurationFindResult Result) TryStartApplyStep(ConfigurationStore store,
        ConfigurationCaller caller, ConfigurationOperationKey key, ApplyStep step)
        => store.TryStartApplyStep(caller, key, step);
    internal static ValueTask<ConfigurationSaveResult> SaveLegacyAsync(ConfigurationStore store,
        ConfigurationCaller caller, ConfigurationOperationKey key, ConfigurationPatch changes,
        ImmutableArray<SecretEdit> secrets, CancellationToken ct = default)
        => store.SaveLegacyAsync(caller, key, changes, secrets, ct);
    internal static ValueTask<LegacySaveOutcome> SaveLegacyWithMoodAsync(ConfigurationStore store,
        ConfigurationCaller caller, ConfigurationOperationKey key, ConfigurationPatch changes,
        ImmutableArray<SecretEdit> secrets, FieldEdit<string> mood, CancellationToken ct = default)
        => store.SaveLegacyWithMoodAsync(caller, key, changes, secrets, mood, ct);
    internal static ValueTask<LegacyFindOutcome> FindLegacyAsync(ConfigurationStore store,
        ConfigurationCaller caller, ConfigurationOperationKey key, CancellationToken ct = default)
        => store.FindLegacyAsync(caller, key, ct);
    internal static LegacyActionResult ApplyLegacyMood(ConfigurationStore store, ConfigurationCaller caller,
        ConfigurationOperationKey key, FieldEdit<string> mood, MoodStore instance)
        => store.ApplyLegacyMood(caller, key, mood, instance);
    internal static ReadField<T> ProjectField<T>(FieldVisibility visibility, string display,
        FieldDescriptor rule, UserValueKind userMode, ConfigurationValueSource source, ApplyState applyState, T? value = default)
        => new(visibility, display, rule, userMode, source, applyState, value);
    internal static UserValueKind ReadFieldMode(SqliteConnection connection, ConfigurationFieldId field,
        bool legacyPropertyPresent)
        => ReadFieldState(connection, field, legacyPropertyPresent).UserMode;
    internal static (UserValueKind UserMode, bool Tracked) ReadFieldState(SqliteConnection connection,
        ConfigurationFieldId field, bool legacyPropertyPresent)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_mode FROM configuration_field_modes WHERE field_id=$field";
        command.Parameters.AddWithValue("$field", (int)field);
        if (command.ExecuteScalar() is long mode) return ((UserValueKind)mode, true);
        return (legacyPropertyPresent ? UserValueKind.Explicit : UserValueKind.Absent, false);
    }

    internal static ImmutableArray<ConfigurationDeviceState> LoadDevices(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = DeviceRowsSql;
        using var reader = command.ExecuteReader();
        var result = ImmutableArray.CreateBuilder<ConfigurationDeviceState>();
        while (reader.Read())
        {
            var rawKey = reader.GetString(0);
            if (!Guid.TryParseExact(rawKey, "N", out var guid) || guid == Guid.Empty || rawKey != guid.ToString("N"))
                throw new InvalidDataException("invalid_device_state");
            var key = new DeviceKey(guid);
            if (result.Count == 0 || result[^1].Key != key)
            {
                var active = reader.GetString(1);
                var desired = reader.GetString(2);
                if (!ValidDeviceName(active) || !ValidDeviceName(desired))
                    throw new InvalidDataException("invalid_device_state");
                result.Add(new(key, active, desired, reader.GetInt64(3) == 1, reader.GetInt64(4) == 1,
                    reader.GetInt64(5) == 1, ImmutableArray<string>.Empty));
            }
            if (!reader.IsDBNull(6))
            {
                var name = reader.GetString(6);
                if (!ValidDeviceName(name)) throw new InvalidDataException("invalid_device_state");
                result[^1] = result[^1] with { RevokedNames = result[^1].RevokedNames.Add(name) };
            }
        }
        return result.ToImmutable();
    }

    private static bool SameName(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool ValidDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name != name.Trim() || name.Contains('\0')) return false;
        try { new System.Text.UTF8Encoding(false, true).GetByteCount(name); return true; }
        catch (ArgumentException) { return false; }
    }
    private static ImmutableArray<string> AddName(ImmutableArray<string> names, string name)
        => names.Any(x => SameName(x, name)) ? names : names.Add(name);
}

internal readonly record struct ConfigurationPlatformPolicySnapshot(bool Stored,
    ImmutableArray<PlatformPolicyEntry> Entries, ImmutableDictionary<PolicyTarget, int> ExplicitFields)
{
    public override string ToString() => "ConfigurationPlatformPolicySnapshot";
}

internal sealed record ConfigurationDeviceState(DeviceKey Key, string ActiveName, string DesiredName,
    bool DesiredEnabled, bool Deleted, bool RenamePending, ImmutableArray<string> RevokedNames)
{
    public override string ToString() => "ConfigurationDeviceState";
}

internal readonly record struct SecretMutation(WritableSecretSlot Slot, SecretEditKind Kind)
{
    /// <summary>非空表示这条变更指向已登记模型凭据，而不是固定槽位；固定槽位的事实观察按此过滤。</summary>
    internal ModelCredentialKey? CredentialKey { get; init; }
    /// <summary>仅当 <see cref="CredentialKey"/> 非空时携带，供参与者在提交前预演凭据事实。</summary>
    internal string? NormalizedValue { get; init; }
    internal bool FactsKnown { get; init; }
    internal bool DatabaseConfigured { get; init; }
    internal bool DatabaseKeyUsable { get; init; }
    public override string ToString() => "[redacted]";
}
internal enum ConfigurationStorageProfile { Strict, Legacy }

internal enum LegacyActionState { NotStarted = 0, Applied = 1, Failed = 2, Unknown = 3 }
internal sealed record LegacyActionResult(string Action, LegacyActionState State, string? ErrorCode)
{
    public override string ToString() => "LegacyActionResult";
}
internal sealed record LegacySaveOutcome(ConfigurationSaveResult ConfigurationResult,
    ImmutableArray<LegacyActionResult> LegacyActionResults)
{
    public override string ToString() => "LegacySaveOutcome";
}
internal sealed record LegacyFindOutcome(ConfigurationFindResult ConfigurationResult,
    ImmutableArray<LegacyActionResult> LegacyActionResults)
{
    public override string ToString() => "LegacyFindOutcome";
}

internal abstract class ConfigurationStorageParticipant
{
    // Controlled activation only; DB facts, version and audit share the existing transaction.
    internal virtual bool Initialize(SqliteConnection connection, long serverUnixSeconds) => false;
    internal virtual void AppendInitializationAudit(SqliteConnection connection,
        ConfigurationVersion before, ConfigurationVersion after) { }
    internal abstract bool Reauthorize(ConfigurationCaller caller, ConfigurationAccess required);
    internal abstract ImmutableArray<SecretMetadata> SecretSources(
        SqliteConnection connection, ImmutableArray<SecretMutation> proposed);
    internal abstract PreparedConfigurationChange Prepare(SqliteConnection connection,
        ConfigurationPatch ownedIntent, ImmutableArray<SecretMetadata> proposedSecrets,
        ImmutableArray<SecretMutation> proposedMutations,
        ConfigurationStorageProfile profile);
    internal abstract ConfigurationReadResult Read(SqliteConnection connection,
        ConfigurationCaller caller, ConfigurationReadKind kind, ConfigurationVersion version,
        ConfigurationGeneration generation, long serverUnixSeconds, ApplyState latestApplyState);
    internal abstract void AppendAudit(SqliteConnection connection, ConfigurationReceipt receipt,
        ImmutableArray<ConfigurationFieldId> fields);
}

internal sealed record PreparedConfigurationChange(
    bool Changed, ImmutableArray<FieldError> Errors,
    ImmutableArray<DeviceKeyAssignment> DeviceKeyAssignments,
    Action<SqliteConnection> Persist,
    ConfigurationResultCode FailureCode = ConfigurationResultCode.ValidationFailed)
{
    public override string ToString() => "PreparedConfigurationChange";
}

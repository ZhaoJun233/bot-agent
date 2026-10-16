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
    private const string SaveDeviceSql = """
        INSERT INTO configuration_devices(device_key,active_name,desired_name,desired_enabled,deleted,rename_pending)
        VALUES($key,$active,$desired,$enabled,$deleted,$pending)
        ON CONFLICT(device_key) DO UPDATE SET active_name=excluded.active_name,desired_name=excluded.desired_name,
          desired_enabled=excluded.desired_enabled,deleted=excluded.deleted,rename_pending=excluded.rename_pending
        """;
    private const string RevokeDeviceNameSql = "INSERT INTO configuration_revoked_names(name,device_key) VALUES($name,$key) ON CONFLICT(name) DO NOTHING";
    internal static ConfigurationStore Activate(IClock clock, ConfigurationStorageParticipant participant)
        => ConfigurationStore.Activate(clock, participant);
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

    internal static PreparedDeviceChange PrepareDevices(SqliteConnection connection,
        ImmutableArray<DeviceConfiguration> desired, ConfigurationStorageProfile profile)
    {
        var code = profile == ConfigurationStorageProfile.Legacy
            ? ConfigurationResultCode.LegacyNeedsReview : ConfigurationResultCode.ValidationFailed;
        PreparedDeviceChange Reject() => new(false, code,
            ImmutableArray.Create(new FieldError(new FieldAddress(ConfigurationFieldId.AgentDevices, null), "invalid_device_identity")),
            ImmutableArray<DeviceKeyAssignment>.Empty, ImmutableArray<DeviceConfiguration>.Empty, _ => { });
        if (desired.IsDefault || desired.Length > 128 || !Enum.IsDefined(profile)) return Reject();
        var existing = LoadDevices(connection);
        var states = existing.ToDictionary(x => x.Key);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<DeviceKey>();
        var normalized = ImmutableArray.CreateBuilder<DeviceConfiguration>();
        var assignments = ImmutableArray.CreateBuilder<DeviceKeyAssignment>();
        for (var ordinal = 0; ordinal < desired.Length; ordinal++)
        {
            var item = desired[ordinal];
            if (item is null || !ValidDeviceName(item.NameRaw) || !names.Add(item.NameRaw)) return Reject();
            ConfigurationDeviceState? previous = null;
            if (item.Key is { } provided)
            {
                if (!states.TryGetValue(provided, out previous) || previous.Deleted) return Reject();
            }
            else if (profile == ConfigurationStorageProfile.Legacy)
            {
                var matches = existing.Where(x => SameName(x.ActiveName, item.NameRaw) || SameName(x.DesiredName, item.NameRaw)).ToArray();
                if (matches.Length > 1 || matches.Any(x => x.Deleted)) return Reject();
                previous = matches.SingleOrDefault();
            }
            if (existing.Any(x => x.RevokedNames.Any(n => SameName(n, item.NameRaw)))) return Reject();
            if (existing.Any(x => x.Key != previous?.Key
                && (SameName(x.ActiveName, item.NameRaw) || SameName(x.DesiredName, item.NameRaw)))) return Reject();
            var key = previous?.Key ?? new DeviceKey(Guid.NewGuid());
            if (!retained.Add(key)) return Reject();
            if (previous is null) assignments.Add(new(ordinal, key));
            var revoked = previous?.RevokedNames ?? ImmutableArray<string>.Empty;
            if (previous is { RenamePending: true } && !SameName(previous.DesiredName, item.NameRaw)
                && !SameName(previous.DesiredName, previous.ActiveName))
                revoked = AddName(revoked, previous.DesiredName);
            var active = previous?.ActiveName ?? item.NameRaw;
            states[key] = new(key, active, item.NameRaw, item.Enable, false, !SameName(active, item.NameRaw), revoked);
            normalized.Add(item with { Key = key });
        }
        foreach (var omitted in existing.Where(x => !x.Deleted && !retained.Contains(x.Key)))
            states[omitted.Key] = omitted with { DesiredEnabled = false, Deleted = true, RenamePending = false,
                RevokedNames = AddName(AddName(omitted.RevokedNames, omitted.ActiveName), omitted.DesiredName) };
        var planned = states.Values.OrderBy(x => x.Key.Value).ToImmutableArray();
        // Cross-check names revoked by this batch too (including abandoned pending names).
        if (normalized.Any(x => planned.Any(state => state.RevokedNames.Any(n => SameName(n, x.NameRaw))))) return Reject();
        var changed = planned.Any(state => !existing.Any(old => EquivalentDeviceState(old, state)))
            || planned.Length != existing.Length;
        return new(changed, ConfigurationResultCode.Ok, ImmutableArray<FieldError>.Empty,
            assignments.ToImmutable(), normalized.ToImmutable(), conn =>
            {
                foreach (var state in planned.Where(state => !existing.Any(old => EquivalentDeviceState(old, state))))
                    PersistDevice(conn, state);
            });
    }

    /// <summary>Trusted Host calls only after existing connection-token authentication; no client key is accepted.</summary>
    internal static ConfigurationDeviceGate AuthorizeDeviceHello(string name)
    {
        var denied = new ConfigurationDeviceGate(false, false, null, false);
        try
        {
            name = name.Trim();
            if (!ValidDeviceName(name)) return denied;
            var result = denied;
            AppDatabase.Write(conn =>
            {
                var states = LoadDevices(conn);
                var matches = states.Where(x => KnownName(x, name)).ToArray();
                if (matches.Length == 0) { result = new(true, false, null, false); return; }
                if (matches.Length != 1) return;
                var state = matches[0];
                result = new(false, true, state.Key, state.RenamePending);
                if (state.Deleted || !state.DesiredEnabled || state.RevokedNames.Any(n => SameName(n, name))) return;
                if (state.RenamePending && SameName(state.DesiredName, name))
                {
                    state = state with { ActiveName = state.DesiredName, RenamePending = false,
                        RevokedNames = AddName(state.RevokedNames, state.ActiveName) };
                    PersistDevice(conn, state);
                    ConfigurationStore.AppendDeviceRuntimeAudit(conn, state.Key);
                }
                if (SameName(state.ActiveName, name)) result = new(true, true, state.Key, state.RenamePending);
            });
            return result;
        }
        catch { return denied; }
    }

    /// <summary>Read committed gate immediately before actual task/control dispatch; never trust stale SettingsBox.</summary>
    internal static bool CanDispatchDevice(string name, DeviceKey? serverBoundKey)
    {
        try
        {
            name = name.Trim();
            if (!ValidDeviceName(name) || serverBoundKey is { Value: var key } && key == Guid.Empty) return false;
            using var conn = AppDatabase.Open();
            var states = LoadDevices(conn);
            var matches = states.Where(x => KnownName(x, name)).ToArray();
            if (matches.Length == 0) return serverBoundKey is null;
            if (matches.Length != 1) return false;
            var state = matches[0];
            return !state.Deleted && state.DesiredEnabled && SameName(state.ActiveName, name)
                && !state.RevokedNames.Any(n => SameName(n, name))
                && (serverBoundKey is null || serverBoundKey == state.Key);
        }
        catch { return false; }
    }

    private static bool KnownName(ConfigurationDeviceState state, string name)
        => SameName(state.ActiveName, name) || SameName(state.DesiredName, name) || state.RevokedNames.Any(n => SameName(n, name));
    private static bool SameName(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool ValidDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name != name.Trim() || name.Contains('\0')) return false;
        try { new System.Text.UTF8Encoding(false, true).GetByteCount(name); return true; }
        catch (ArgumentException) { return false; }
    }
    private static ImmutableArray<string> AddName(ImmutableArray<string> names, string name)
        => names.Any(x => SameName(x, name)) ? names : names.Add(name);
    private static bool EquivalentDeviceState(ConfigurationDeviceState left, ConfigurationDeviceState right)
        => left.Key == right.Key && left.ActiveName == right.ActiveName && left.DesiredName == right.DesiredName
            && left.DesiredEnabled == right.DesiredEnabled && left.Deleted == right.Deleted
            && left.RenamePending == right.RenamePending
            && left.RevokedNames.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(right.RevokedNames.OrderBy(x => x, StringComparer.Ordinal));
    private static void PersistDevice(SqliteConnection connection, ConfigurationDeviceState state)
    {
        var key = state.Key.Value.ToString("N");
        AppDatabase.Exec(connection, SaveDeviceSql, ("$key", key), ("$active", state.ActiveName), ("$desired", state.DesiredName),
            ("$enabled", state.DesiredEnabled ? 1 : 0), ("$deleted", state.Deleted ? 1 : 0), ("$pending", state.RenamePending ? 1 : 0));
        foreach (var name in state.RevokedNames)
            AppDatabase.Exec(connection, RevokeDeviceNameSql, ("$name", name), ("$key", key));
    }
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
internal sealed record PreparedDeviceChange(bool Changed, ConfigurationResultCode Code, ImmutableArray<FieldError> Errors,
    ImmutableArray<DeviceKeyAssignment> Assignments, ImmutableArray<DeviceConfiguration> DesiredWithKeys, Action<SqliteConnection> Persist)
{
    public override string ToString() => "PreparedDeviceChange";
}
internal readonly record struct ConfigurationDeviceGate(bool Allowed, bool Registered, DeviceKey? Key, bool RenamePending);

internal readonly record struct SecretMutation(WritableSecretSlot Slot, SecretEditKind Kind)
{
    internal bool FactsKnown { get; init; }
    internal bool DatabaseConfigured { get; init; }
    internal bool DatabaseKeyUsable { get; init; }
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
    internal virtual void AppendDeviceRuntimeAudit(SqliteConnection connection, DeviceKey key) { }
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

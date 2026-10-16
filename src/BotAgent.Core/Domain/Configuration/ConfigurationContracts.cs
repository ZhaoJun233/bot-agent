using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotAgent.Domain.Configuration;

/// <summary>Only an authorized projection retains an editable value; Hidden retains none.</summary>
[JsonConverter(typeof(ReadFieldConverterFactory))]
public sealed class ReadField<T>
{
    private readonly T? _editable;
    public FieldVisibility Visibility { get; }
    public string Display { get; }
    public FieldDescriptor Rule { get; }
    public UserValueKind UserMode { get; }
    public ConfigurationValueSource EffectiveSource { get; }
    public ApplyState ApplyState { get; }

    internal ReadField(FieldVisibility visibility, string display, FieldDescriptor rule,
        UserValueKind userMode, ConfigurationValueSource effectiveSource, ApplyState applyState,
        T? editable = default)
    {
        if (!Enum.IsDefined(visibility) || !Enum.IsDefined(userMode)
            || !Enum.IsDefined(effectiveSource) || !Enum.IsDefined(applyState))
            throw new ArgumentException("invalid_read_projection");
        if (visibility == FieldVisibility.NameEditable && typeof(T) != typeof(string))
            throw new ArgumentException("invalid_name_projection");
        Visibility = visibility;
        Display = display ?? throw new ArgumentNullException(nameof(display));
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        UserMode = userMode; EffectiveSource = effectiveSource; ApplyState = applyState;
        _editable = visibility == FieldVisibility.Hidden ? default : editable;
    }
    public bool TryGetEditableValue(out T value)
    {
        value = Visibility == FieldVisibility.Hidden ? default! : _editable!;
        return Visibility != FieldVisibility.Hidden;
    }
    public override string ToString() => "ReadField";
}

internal sealed class ReadFieldConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ReadField<>);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(ReadFieldConverter<>).MakeGenericType(type.GetGenericArguments()))!;
}

internal sealed class ReadFieldConverter<T> : JsonConverter<ReadField<T>>
{
    public override ReadField<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => throw new JsonException("read_projection_is_output_only");
    public override void Write(Utf8JsonWriter writer, ReadField<T> field, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("visibility", (int)field.Visibility);
        writer.WriteString("display", field.Display);
        writer.WritePropertyName("rule"); JsonSerializer.Serialize(writer, field.Rule, options);
        writer.WriteNumber("userMode", (int)field.UserMode);
        writer.WriteNumber("effectiveSource", (int)field.EffectiveSource);
        writer.WriteNumber("applyState", (int)field.ApplyState);
        if (field.TryGetEditableValue(out var value))
        {
            writer.WritePropertyName(field.Visibility == FieldVisibility.NameEditable ? "nameRaw" : "value");
            JsonSerializer.Serialize(writer, value, options);
        }
        writer.WriteEndObject();
    }
}


public enum ConfigurationReadKind { Overview = 0, Editable = 1 }
[Flags]
public enum ConfigurationAccess
{
    None = 0, ReadOverview = 1, ReadEditable = 2,
    SaveConfiguration = 4, EditAllowedSecrets = 8, ValidateConfiguration = 16
}
public sealed record ConfigurationCaller(
    string AuthorityId, string SubjectId, string ResourceId,
    ConfigurationAccess Access)
{
    public override string ToString() => "ConfigurationCaller";
}
[JsonConverter(typeof(ConfigurationVersionConverter))]
public readonly record struct ConfigurationVersion(long Value);
[JsonConverter(typeof(ConfigurationKeyConverterFactory))]
public readonly record struct ConfigurationGeneration(Guid Value);
[JsonConverter(typeof(ConfigurationKeyConverterFactory))]
public readonly record struct ConfigurationOperationKey(
    ConfigurationGeneration Generation, long IssuedUnixSeconds, Guid Nonce);
public sealed record ConfigurationSaveRequest(
    string ContractRevision, ConfigurationVersion ExpectedVersion,
    ConfigurationOperationKey Key, ConfigurationPatch Changes,
    ImmutableArray<SecretEdit> Secrets)
{
    public override string ToString() => "ConfigurationSaveRequest";
}
public sealed record ConfigurationValidationRequest(
    string ContractRevision, ConfigurationVersion ExpectedVersion,
    ConfigurationPatch Changes, ImmutableArray<SecretEdit> Secrets)
{
    public override string ToString() => "ConfigurationValidationRequest";
}
public sealed record ConfigurationPatch(
    BehaviorChanges? Behavior, ModelChanges? Model, AgentChanges? Agent,
    PlatformChanges? Platform, VoiceChanges? Voice, MusicChanges? Music,
    ReportingChanges? Reporting, RegisteredConfigurationChanges? Registered)
{
    public override string ToString() => "ConfigurationPatch";
}
public enum EditKind { Keep = 0, Set = 1, Clear = 2 }
public enum ConfigurationResultCode
{
    Ok = 0, Forbidden = 1, ValidationFailed = 2, VersionConflict = 3,
    IdempotencyConflict = 4, KeyExpired = 5, KeyGenerationMismatch = 6,
    CapacityExceeded = 7, ClockRegression = 8, StorageUnavailable = 9,
    CommitFailed = 10, CancelledBeforeCommit = 11, UnknownCommit = 12,
    NoChanges = 13, NotFound = 14, LegacyNeedsReview = 15,
    UnsupportedContractRevision = 16
}
public enum ConfigurationDecision { Changed = 0, NoChanges = 1 }
public enum CommitState { NotCommitted = 0, Committed = 1, Unknown = 2 }
public enum ApplyState
{
    NotStarted = 0, Applied = 1, PartiallyApplied = 2,
    RestartRequired = 3, Failed = 4, Unknown = 5
}
public enum ApplyStep
{
    PublishSnapshot = 0, RuntimePolicy = 1, ModelProjection = 2,
    ConcurrencyGate = 3, ContextWindow = 4, Schedulers = 5,
    TtsFileProjection = 6, RegisteredConfiguration = 7
}
public sealed record ApplyStepReport(
    ApplyStep Step, ApplyState State, string? ErrorCode)
{
    public override string ToString() => "ApplyStepReport";
}
public sealed record ConfigurationReceipt(
    ConfigurationOperationKey Key, string ContractRevision,
    ConfigurationDecision Decision, ConfigurationVersion ObservedVersion,
    ConfigurationVersion? CommittedVersion, CommitState CommitState,
    ApplyState ApplyState, ImmutableArray<ApplyStepReport> Steps,
    ImmutableArray<DeviceKeyAssignment> DeviceKeyAssignments)
{
    public override string ToString() => "ConfigurationReceipt";
}
public sealed record DeviceKeyAssignment(int InputOrdinal, DeviceKey Key)
{
    public override string ToString() => "DeviceKeyAssignment";
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConfigurationObjectAddress.Device), "device")]
[JsonDerivedType(typeof(ConfigurationObjectAddress.Policy), "policy")]
[JsonDerivedType(typeof(ConfigurationObjectAddress.Plugin), "plugin")]
[JsonDerivedType(typeof(ConfigurationObjectAddress.Provider), "provider")]
[JsonDerivedType(typeof(ConfigurationObjectAddress.Secret), "secret")]
public abstract record ConfigurationObjectAddress
{
    private ConfigurationObjectAddress() { }
    public sealed record Device(DeviceKey Key) : ConfigurationObjectAddress
    {
        public override string ToString() => "Device";
    }
    public sealed record Policy(PolicyTarget Target) : ConfigurationObjectAddress
    {
        public override string ToString() => "Policy";
    }
    public sealed record Plugin(PluginKey Key) : ConfigurationObjectAddress
    {
        public override string ToString() => "Plugin";
    }
    public sealed record Provider(ProviderKey Key) : ConfigurationObjectAddress
    {
        public override string ToString() => "Provider";
    }
    public sealed record Secret(WritableSecretSlot Slot) : ConfigurationObjectAddress
    {
        public override string ToString() => "Secret";
    }
}
public sealed record FieldAddress(
    ConfigurationFieldId Field, ConfigurationObjectAddress? Object)
{
    public override string ToString() => "FieldAddress";
}
public sealed record FieldError(FieldAddress Address, string Code)
{
    public override string ToString() => "FieldError";
}
public sealed record ConfigurationSaveResult(
    ConfigurationResultCode Code, bool Replayed,
    ConfigurationReceipt? Receipt, ConfigurationVersion CurrentVersion,
    ImmutableArray<FieldError> Errors)
{
    public override string ToString() => "ConfigurationSaveResult";
}
public sealed record ConfigurationFindResult(
    ConfigurationResultCode Code, ConfigurationReceipt? Receipt)
{
    public override string ToString() => "ConfigurationFindResult";
}
public enum CredentialSource { None = 0, Database = 1, Environment = 2 }
public sealed record SecretMetadata(
    WritableSecretSlot Slot, bool Configured, CredentialSource Source)
{
    public override string ToString() => "SecretMetadata";
}
public enum FieldEffect
{
    Hot = 0, Restart = 1, ExternalProjection = 2, ReadOnly = 3
}
public sealed record FieldDescriptor(
    ConfigurationFieldId Id, FieldEffect Effect, bool CanClear,
    bool Editable, bool LegacyNeedsReview)
{
    public override string ToString() => "FieldDescriptor";
}
public enum FieldVisibility { Hidden = 0, Editable = 1, NameEditable = 2 }
public enum UserValueKind { Absent = 0, Inherited = 1, Explicit = 2 }
public enum ConfigurationValueSource
{
    Default = 0, UserConfiguration = 1, Environment = 2,
    LegacyCompatibility = 3, Unclassified = 4
}
public sealed record ConfigurationReadResult(
    ConfigurationResultCode Code, ConfigurationVersion Version,
    ConfigurationGeneration Generation, string ContractRevision,
    long ServerUnixSeconds, ConfigurationView? View,
    ImmutableArray<SecretMetadata> Secrets,
    ImmutableArray<FieldDescriptor> Fields, ApplyState LatestApplyState)
{
    public override string ToString() => "ConfigurationReadResult";
}
public sealed record ConfigurationView(
    BehaviorView Behavior, ModelView Model, AgentView Agent,
    PlatformView Platform, VoiceView Voice, MusicView Music,
    ReportingView Reporting, RegisteredConfigurationView Registered)
{
    public override string ToString() => "ConfigurationView";
}

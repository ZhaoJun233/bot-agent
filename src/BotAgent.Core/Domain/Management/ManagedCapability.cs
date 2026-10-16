using System.Collections.Immutable;
using System.Text.Json.Serialization;
using BotAgent.Domain.Configuration;

namespace BotAgent.Domain.Management;

public enum ManagedKind
{
    Driver = 0, Plugin = 1, Provider = 2, AgentDevice = 3, McpServer = 4
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ManagedObjectKey.Driver), "driver")]
[JsonDerivedType(typeof(ManagedObjectKey.Plugin), "plugin")]
[JsonDerivedType(typeof(ManagedObjectKey.Provider), "provider")]
[JsonDerivedType(typeof(ManagedObjectKey.Device), "device")]
[JsonDerivedType(typeof(ManagedObjectKey.Mcp), "mcp")]
public abstract record ManagedObjectKey
{
    private ManagedObjectKey() { }
    public sealed record Driver(DriverKey Key) : ManagedObjectKey
    {
        public override string ToString() => "Driver";
    }
    public sealed record Plugin(PluginKey Key) : ManagedObjectKey
    {
        public override string ToString() => "Plugin";
    }
    public sealed record Provider(ProviderKey Key) : ManagedObjectKey
    {
        public override string ToString() => "Provider";
    }
    public sealed record Device(DeviceKey Key) : ManagedObjectKey
    {
        public override string ToString() => "Device";
    }
    public sealed record Mcp(string RegisteredId) : ManagedObjectKey
    {
        public override string ToString() => "Mcp";
    }
}
public enum ManagedAction
{
    Inspect = 0, ValidateConfiguration = 1, Configure = 2,
    Start = 3, Stop = 4, TestConnection = 5,
    Connect = 6, Disconnect = 7, Cancel = 8
}
public enum ManagedRuntimeState
{
    Unknown = 0, NotLoaded = 1, Disabled = 2, Starting = 3,
    Running = 4, Stopping = 5, Disconnected = 6, Faulted = 7
}
public enum ManagedResultCode
{
    Ok = 0, Forbidden = 1, NotFound = 2, AmbiguousLegacyIdentity = 3,
    UnsupportedAction = 4, VersionConflict = 5, ValidationFailed = 6,
    StaleCursor = 7, InvalidCursor = 8, IdempotencyConflict = 9,
    KeyExpired = 10, KeyGenerationMismatch = 11, CapacityExceeded = 12,
    StorageUnavailable = 13, Unknown = 14,
    ClockRegression = 15, UnsupportedContractRevision = 16
}
public sealed record ActionCapability(
    ManagedAction Action, bool Supported, string ReasonCode)
{
    public override string ToString() => "ActionCapability";
}
public sealed record ManagedDescriptor(
    ManagedObjectKey Key, string DisplayName, bool? DesiredEnabled,
    ManagedRuntimeState RuntimeState, ImmutableArray<ActionCapability> Actions,
    bool CredentialConfigured, string? ErrorCode, string DescriptorRevision)
{
    public override string ToString() => "ManagedDescriptor";
}
public sealed record ManagedPageRequest(ManagedKind? Kind, int Limit, string? Cursor)
{
    public override string ToString() => "ManagedPageRequest";
}
public sealed record ManagedPage(
    ManagedResultCode Code, ImmutableArray<ManagedDescriptor> Items,
    string? NextCursor, string DescriptorRevision)
{
    public override string ToString() => "ManagedPage";
}
public sealed record ManagedInspectResult(
    ManagedResultCode Code, ManagedDescriptor? Descriptor)
{
    public override string ToString() => "ManagedInspectResult";
}
public sealed record ManagedValidationResult(
    ManagedResultCode Code, ImmutableArray<FieldError> Errors)
{
    public override string ToString() => "ManagedValidationResult";
}
public enum DriverCommand { Start = 0, Stop = 1, TestConnection = 2 }
public enum PluginCommand { Start = 0, Stop = 1 }
public enum ProviderCommand { TestConnection = 0 }
public enum McpCommand { Connect = 0, Disconnect = 1, TestConnection = 2 }
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ManagedCommand.Driver), "driver")]
[JsonDerivedType(typeof(ManagedCommand.Plugin), "plugin")]
[JsonDerivedType(typeof(ManagedCommand.Provider), "provider")]
[JsonDerivedType(typeof(ManagedCommand.Mcp), "mcp")]
public abstract record ManagedCommand
{
    private ManagedCommand() { }
    public sealed record Driver(DriverKey Key, DriverCommand Action) : ManagedCommand
    {
        public override string ToString() => "Driver";
    }
    public sealed record Plugin(PluginKey Key, PluginCommand Action) : ManagedCommand
    {
        public override string ToString() => "Plugin";
    }
    public sealed record Provider(ProviderKey Key, ProviderCommand Action) : ManagedCommand
    {
        public override string ToString() => "Provider";
    }
    public sealed record Mcp(string RegisteredId, McpCommand Action) : ManagedCommand
    {
        public override string ToString() => "Mcp";
    }
}
[JsonConverter(typeof(ConfigurationKeyConverterFactory))]
public readonly record struct ManagedOperationKey(
    ConfigurationGeneration Generation, long IssuedUnixSeconds, Guid Nonce);
public sealed record ManagedExecuteRequest(
    string ContractRevision, string ExpectedDescriptorRevision,
    ManagedOperationKey Key, ManagedCommand Command)
{
    public override string ToString() => "ManagedExecuteRequest";
}
public enum ManagedOperationState
{
    NotStarted = 0, Running = 1, Completed = 2, Failed = 3,
    CancelRequested = 4, Cancelled = 5, Unknown = 6
}
public sealed record ManagedReceipt(
    ManagedOperationKey Key, ManagedObjectKey Target,
    ManagedAction Action, string DescriptorRevision,
    ManagedOperationState State, string? ErrorCode)
{
    public override string ToString() => "ManagedReceipt";
}
public sealed record ManagedOperationResult(
    ManagedResultCode Code, bool Replayed, ManagedReceipt? Receipt)
{
    public override string ToString() => "ManagedOperationResult";
}
public sealed record ManagedCancellationResult(
    ManagedResultCode Code, ManagedOperationState State)
{
    public override string ToString() => "ManagedCancellationResult";
}
public enum ManagedPermission { Inspect = 0, Execute = 1, Query = 2, Cancel = 3 }
public sealed record ManagedAuthorizationRequest(
    ConfigurationCaller Caller, ManagedObjectKey Target,
    ManagedPermission Permission, ManagedAction? Action)
{
    public override string ToString() => "ManagedAuthorizationRequest";
}

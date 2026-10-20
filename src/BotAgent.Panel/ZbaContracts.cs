using System.Collections.Immutable;
using System.Text.Json.Serialization;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;

namespace BotAgent.Panel;

/// <summary>ZBA v1 的稳定契约版本。客户端用 meta 端点核对，不匹配时不得发起写操作。</summary>
public static class ZbaContract
{
    public const string Version = "zba-1.0";
    public const int MaxBodyBytes = 1024 * 1024;
    public const int MaxPromptChars = 32_000;
    public const int MaxPageSize = 200;
    public const int MaxCapabilityPageSize = 100;
    public const int DefaultPageSize = 50;
}

public sealed record ZbaOptions
{
    public required string? AdminToken { get; init; }
    public required string? ReadToken { get; init; }

    /// <summary>
    /// 默认关闭。仅在明确的本机开发场景打开；打开时回环地址视为管理员，
    /// 与“无凭据一律拒绝”的默认姿态分开记录。
    /// </summary>
    public bool AllowLoopbackWithoutToken { get; init; }

    public int MaxBodyBytes { get; init; } = ZbaContract.MaxBodyBytes;
}

public enum ZbaScope
{
    Read = 0,
    Admin = 1
}

public sealed record ZbaPrincipal(ZbaScope Scope, string Subject, bool Loopback)
{
    public override string ToString() => "ZbaPrincipal";
}

public sealed record ZbaRouteInfo(
    string Method,
    string Template,
    ZbaScope Scope,
    bool Public,
    string OperationId,
    string Summary,
    string? RequestSchema,
    string ResponseSchema)
{
    public override string ToString() => OperationId;
}

public sealed record ZbaRequest(
    string Method,
    string Path,
    string QueryString,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    string? RemoteAddress)
{
    public override string ToString() => Method + " " + Path;
}

public sealed record ZbaResponse(
    int Status,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers)
{
    public override string ToString() => Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record ZbaHealth(
    string Status,
    bool Ready,
    bool Configured,
    string ContractVersion,
    DateTimeOffset CheckedAt);

public sealed record ZbaComponent(
    string Id,
    string Kind,
    string DisplayName,
    string State,
    bool Available,
    string? ReasonCode,
    DateTimeOffset? UpdatedAt);

public sealed record ZbaErrorSummary(DateTimeOffset At, string Scope, string Code);

public sealed record ZbaStatus(
    string State,
    bool Ready,
    string Version,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? ConfigurationVersion,
    string ConfigurationApplyState,
    IReadOnlyList<ZbaComponent> Components,
    IReadOnlyList<ZbaErrorSummary> RecentErrors);

public sealed record ZbaMeta(
    string ContractVersion,
    string ServerVersion,
    string Authentication,
    IReadOnlyList<string> Scopes,
    bool Sse,
    bool McpManagement,
    bool DockerManagement,
    int MaxBodyBytes);

public sealed record ZbaConfigurationReadRequest([property: JsonRequired] ConfigurationReadKind Kind);

public sealed record ZbaConfigurationFindRequest([property: JsonRequired] ConfigurationOperationKey Key);

public sealed record ZbaCapabilityInspectRequest([property: JsonRequired] ManagedObjectKey Key);

public sealed record ZbaSecretEdit(
    [property: JsonRequired] WritableSecretSlot Slot,
    [property: JsonRequired] SecretEditKind Kind,
    string? Value);

/// <summary>
/// 请求侧的秘密编辑用独立闭包形状，不反序列化 Core 的 <see cref="SecretEdit" />
/// （它的转换器只允许宿主侧构造，禁止任何网络输入直接变成 SensitiveInput）。
/// </summary>
public sealed record ZbaConfigurationSaveRequest(
    [property: JsonRequired] string ContractRevision,
    [property: JsonRequired] ConfigurationVersion ExpectedVersion,
    [property: JsonRequired] ConfigurationOperationKey Key,
    [property: JsonRequired] ConfigurationPatch Changes,
    [property: JsonRequired] IReadOnlyList<ZbaSecretEdit> Secrets)
{
    public override string ToString() => "ZbaConfigurationSaveRequest";
}

public sealed record ZbaConfigurationValidationRequest(
    [property: JsonRequired] string ContractRevision,
    [property: JsonRequired] ConfigurationVersion ExpectedVersion,
    [property: JsonRequired] ConfigurationPatch Changes,
    [property: JsonRequired] IReadOnlyList<ZbaSecretEdit> Secrets)
{
    public override string ToString() => "ZbaConfigurationValidationRequest";
}

public enum ZbaTaskState
{
    Queued = 0,
    Running = 1,
    WaitingApproval = 2,
    Succeeded = 3,
    Failed = 4,
    CancelRequested = 5,
    Cancelled = 6,
    Unknown = 7
}

public sealed record ZbaTaskSubmission(
    [property: JsonRequired] string SourceKey,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] string Prompt,
    [property: JsonRequired] string Target,
    [property: JsonRequired] int TimeoutSeconds)
{
    public override string ToString() => "ZbaTaskSubmission";
}

public sealed record ZbaTaskAccepted(string Id, ZbaTaskState State, DateTimeOffset AcceptedAt);

public sealed record ZbaTaskSummary(
    string Id,
    ZbaTaskState State,
    string Target,
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long? DurationMs,
    int? ToolCalls,
    string? ErrorCode,
    bool CancellationRequested)
{
    public override string ToString() => "ZbaTaskSummary";
}

public sealed record ZbaTaskPage(IReadOnlyList<ZbaTaskSummary> Items, string? NextCursor);

public sealed record ZbaTaskCancelResult(ZbaTaskState State, bool CancellationRequested, string Outcome);

public sealed record ZbaApprovalSummary(
    string Id,
    string ToolId,
    string Summary,
    string Session,
    DateTimeOffset ExpiresAt,
    int PolicyVersion)
{
    public override string ToString() => "ZbaApprovalSummary";
}

public sealed record ZbaApprovalDecision([property: JsonRequired] bool Approve);

public sealed record ZbaApprovalDecisionResult(bool Ok, string Decision, string ReasonCode);

public sealed record ZbaEvent(
    long Sequence,
    string Type,
    DateTimeOffset At,
    string? TaskId,
    string? State,
    string? Version,
    string? ReasonCode);

public sealed record ZbaEventPage(
    IReadOnlyList<ZbaEvent> Items,
    long NextSequence,
    bool ResyncRequired,
    DateTimeOffset GeneratedAt);

public sealed record ZbaError(string Code, string Message, string? Reason = null, int? BackendCode = null);

public sealed record ZbaErrorBody(ZbaError Error);

/// <summary>宿主适配层发现请求不可满足时抛出；ZBA 只返回安全的状态码/原因码，不转发异常原文。</summary>
public sealed class ZbaBackendException : Exception
{
    public ZbaBackendException(int status, string code, string message, string? reason = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Reason = reason;
    }

    public int Status { get; }
    public string Code { get; }
    public string? Reason { get; }
}

/// <summary>
/// 宿主实现的能力面。ZBA 不直接访问数据库、配置仓储或协议端；
/// 所有调用都经过 Core 契约或这里定义的只读投影。
/// </summary>
public interface IZbaBackend
{
    ValueTask<ZbaHealth> GetHealthAsync(CancellationToken ct = default);

    ValueTask<ZbaStatus> GetStatusAsync(ConfigurationCaller caller, CancellationToken ct = default);

    ValueTask<ManagedPage> ListCapabilitiesAsync(
        ConfigurationCaller caller, ManagedPageRequest request, CancellationToken ct = default);

    ValueTask<ManagedInspectResult> InspectCapabilityAsync(
        ConfigurationCaller caller, ManagedObjectKey key, CancellationToken ct = default);

    ValueTask<ManagedValidationResult> ValidateCapabilityAsync(
        ConfigurationCaller caller, ZbaConfigurationValidationRequest request, CancellationToken ct = default);

    ValueTask<ManagedOperationResult> ExecuteCapabilityAsync(
        ConfigurationCaller caller, ManagedExecuteRequest request, CancellationToken ct = default);

    ValueTask<ManagedOperationResult> QueryCapabilityAsync(
        ConfigurationCaller caller, ManagedOperationKey key, CancellationToken ct = default);

    ValueTask<ManagedCancellationResult> CancelCapabilityAsync(
        ConfigurationCaller caller, ManagedOperationKey key, CancellationToken ct = default);

    ValueTask<ConfigurationReadResult> ReadConfigurationAsync(
        ConfigurationCaller caller, ConfigurationReadKind kind, CancellationToken ct = default);

    ValueTask<ConfigurationSaveResult> SaveConfigurationAsync(
        ConfigurationCaller caller, ZbaConfigurationSaveRequest request, CancellationToken ct = default);

    ValueTask<ConfigurationFindResult> FindConfigurationAsync(
        ConfigurationCaller caller, ConfigurationOperationKey key, CancellationToken ct = default);

    ValueTask<ZbaTaskAccepted> SubmitTaskAsync(
        ConfigurationCaller caller, ZbaTaskSubmission submission, string idempotencyKey, CancellationToken ct = default);

    ValueTask<ZbaTaskPage> ListTasksAsync(
        ConfigurationCaller caller, int limit, string? cursor, CancellationToken ct = default);

    ValueTask<ZbaTaskSummary?> GetTaskAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default);

    ValueTask<ZbaTaskCancelResult> CancelTaskAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default);

    ValueTask<IReadOnlyList<ZbaApprovalSummary>> ListApprovalsAsync(
        ConfigurationCaller caller, CancellationToken ct = default);

    ValueTask<ZbaApprovalDecisionResult> DecideApprovalAsync(
        ConfigurationCaller caller, string requestId, bool approve, CancellationToken ct = default);

    ValueTask<ZbaEventPage> ReadEventsAsync(
        ConfigurationCaller caller, long after, int limit, CancellationToken ct = default);
}

internal static class ZbaCallers
{
    internal static ConfigurationCaller Read(string subject) => new(
        "zba", subject, "bot",
        ConfigurationAccess.ReadOverview | ConfigurationAccess.ReadEditable | ConfigurationAccess.ValidateConfiguration);

    internal static ConfigurationCaller Admin(string subject) => new(
        "zba", subject, "bot",
        ConfigurationAccess.ReadOverview | ConfigurationAccess.ReadEditable |
        ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets |
        ConfigurationAccess.ValidateConfiguration);
}

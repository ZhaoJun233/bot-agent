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
    int MaxBodyBytes,
    string InstanceId,
    IReadOnlyList<string> Features);

/// <summary>
/// 服务器执行会话的安全投影：只给 id、显示名、状态与形状（轮数/历史长度），
/// 不返回历史正文、流水或工具结果。
/// </summary>
public sealed record ZbaSessionSummary(
    string Id,
    string Name,
    string Backend,
    bool Current,
    int Turns,
    int HistoryChars,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool AutoNamed)
{
    public override string ToString() => "ZbaSessionSummary";
}

public sealed record ZbaSessionPage(
    string SourceKey,
    IReadOnlyList<ZbaSessionSummary> Items,
    string? NextCursor);

public sealed record ZbaSessionEnsureRequest([property: JsonRequired] string SourceKey);

public sealed record ZbaSessionEnsureResult(ZbaSessionSummary Session, bool Created);

public sealed record ZbaConfigurationReadRequest([property: JsonRequired] ConfigurationReadKind Kind);

public sealed record ZbaConfigurationFindRequest([property: JsonRequired] ConfigurationOperationKey Key);

public sealed record ZbaCapabilityInspectRequest([property: JsonRequired] ManagedObjectKey Key);

/// <summary>
/// 秘密编辑的目标是“固定槽位”或“已登记凭据键”二者之一：两个字段都可选，但必须恰好给出一个。
/// 形状保持封闭（<see cref="ZbaJson.Wire" /> 拒绝未知字段）；真正的目标校验在
/// <see cref="ZbaRules.ValidateSecrets" />。
/// </summary>
public sealed record ZbaSecretEdit(
    [property: JsonRequired] SecretEditKind Kind,
    WritableSecretSlot? Slot,
    string? CredentialKey,
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

/// <summary>
/// 任务结果的安全投影：只对 <c>panel:</c> 源的成功任务返回有界摘要；
/// 不返回 prompt、工具载荷或模型隐藏推理。
/// </summary>
public sealed record ZbaTaskResult(
    string Id,
    ZbaTaskState State,
    DateTimeOffset UpdatedAt,
    bool Available,
    string? ReasonCode,
    string? Summary,
    bool SummaryTruncated,
    int SummaryChars,
    int? ToolCalls,
    long? DurationMs,
    string? ErrorCode)
{
    public override string ToString() => "ZbaTaskResult";
}

public sealed record ZbaTaskFindRequest([property: JsonRequired] string Key);

/// <summary>
/// 按提交幂等键核对结果。Code 为 found / expired / accepted_unknown / unknown；
/// 后两者只给受理时的任务 id 与时间，不给 prompt 或结果。
/// </summary>
public sealed record ZbaTaskFindResult(
    string Code,
    string InstanceId,
    ZbaTaskSummary? Task,
    string? TaskId,
    DateTimeOffset? AcceptedAt)
{
    public override string ToString() => "ZbaTaskFindResult";
}

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

    ValueTask<ZbaSessionPage> ListSessionsAsync(
        ConfigurationCaller caller, string sourceKey, int limit, string? cursor, CancellationToken ct = default);

    ValueTask<ZbaSessionEnsureResult> EnsureSessionAsync(
        ConfigurationCaller caller, string sourceKey, CancellationToken ct = default);

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

    ValueTask<ZbaTaskResult?> GetTaskResultAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default);

    ValueTask<ZbaTaskFindResult> FindTaskAsync(
        ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default);

    ValueTask<ZbaTaskCancelResult> CancelTaskAsync(
        ConfigurationCaller caller, string id, CancellationToken ct = default);

    ValueTask<IReadOnlyList<ZbaApprovalSummary>> ListApprovalsAsync(
        ConfigurationCaller caller, CancellationToken ct = default);

    ValueTask<ZbaApprovalDecisionResult> DecideApprovalAsync(
        ConfigurationCaller caller, string requestId, bool approve, CancellationToken ct = default);

    ValueTask<ZbaEventPage> ReadEventsAsync(
        ConfigurationCaller caller, long after, int limit, CancellationToken ct = default);

    ValueTask<ZbaPluginCatalog> ListPluginCatalogAsync(
        ConfigurationCaller caller, string? query, string? category, CancellationToken ct = default);

    ValueTask<ZbaInstalledPlugins> ListInstalledPluginsAsync(
        ConfigurationCaller caller, CancellationToken ct = default);

    ValueTask<ZbaPluginSourceResolution> ResolvePluginSourceAsync(
        ConfigurationCaller caller, string sourceUrl, CancellationToken ct = default);

    ValueTask<ZbaPluginOperationResult> InstallPluginAsync(
        ConfigurationCaller caller, ZbaPluginMutationRequest request, CancellationToken ct = default);

    ValueTask<ZbaPluginOperationResult> UpdatePluginAsync(
        ConfigurationCaller caller, ZbaPluginMutationRequest request, CancellationToken ct = default);

    ValueTask<ZbaPluginOperationResult> UninstallPluginAsync(
        ConfigurationCaller caller, string pluginId, ZbaPluginUninstallRequest request,
        CancellationToken ct = default);

    ValueTask<ZbaPluginOperationResult> FindPluginOperationAsync(
        ConfigurationCaller caller, string idempotencyKey, CancellationToken ct = default);

    ValueTask<ZbaPluginSettings> ReadPluginSettingsAsync(
        ConfigurationCaller caller, string pluginId, CancellationToken ct = default);

    ValueTask<ZbaPluginSettings> SavePluginSettingsAsync(
        ConfigurationCaller caller, string pluginId, ZbaPluginSettingsSaveRequest request,
        CancellationToken ct = default);

    ValueTask<ZbaProviderModels> ListProviderModelsAsync(
        ConfigurationCaller caller, string providerKey, CancellationToken ct = default);
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

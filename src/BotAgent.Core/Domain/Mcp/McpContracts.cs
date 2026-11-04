using System.Text.Json.Nodes;

namespace BotAgent.Domain.Mcp;

/// <summary>一个 MCP 服务器在进程内的运行状态（只用于观测/面板，不参与授权判定）。</summary>
public enum McpConnectionState
{
    Disabled = 0,
    Disconnected = 1,
    Connecting = 2,
    Ready = 3,
    Faulted = 4,
    Disposed = 5,
}

/// <summary>
/// 一个被显式授权的 MCP 服务器。
///
/// `AllowedTools` **为空表示一个工具都不授权**（不是“全部”）：MCP 工具的发现结果来自远端，
/// 不能把“发现了”当成“获得了执行权”。`Token` 只活在内存里，来源是密钥库/环境变量，
/// 不写 settings、不落审计、不回显。
/// </summary>
public sealed record McpServerOptions(
    string Id,
    string Endpoint,
    string? Token,
    IReadOnlyList<string> AllowedTools,
    TimeSpan CallTimeout,
    int MaxResultBytes)
{
    /// <summary>默认 record 的 ToString 会把 Token 打出来；这里显式遮掉，日志/调试器不再泄密。</summary>
    public override string ToString()
    {
        var endpoint = Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}{uri.AbsolutePath}"
            : "<invalid>";
        return $"McpServerOptions {{ Id = {Id}, Endpoint = {endpoint}, Token = ***, AllowedTools = {AllowedTools.Count} }}";
    }
}

/// <summary>
/// 一次发现得到的工具。`Name` 是远端原名；`QualifiedName` 是投影给模型/闸门的
/// `mcp.&lt;server&gt;.&lt;tool&gt;`，两者不可混用（执行远端的永远是 `Name`）。
/// </summary>
public sealed record McpToolInfo(
    string Name,
    string QualifiedName,
    string Description,
    string InputSchemaJson,
    string ServerId,
    bool ReadOnlyHint);

/// <summary>
/// 一次 MCP 调用的结果。`ReasonCode` 是短码；`Content` 只给模型看，`AuditDetail` 只写形状
/// （服务器/工具/状态/字节/耗时），绝不写参数或正文。`UnknownOutcome` = 远端可能已经执行，
/// 但本地没拿到确定结果 —— 调用方不得自动重放有副作用的调用。
/// </summary>
public sealed record McpCallResult(
    bool Ok,
    string ReasonCode,
    string? Content,
    string? AuditDetail = null,
    bool UnknownOutcome = false,
    int SizeBytes = 0,
    long DurationMs = 0)
{
    /// <summary>内容只给模型；record 默认 ToString 会带 Content，这里显式不打印。</summary>
    public override string ToString()
        => $"McpCallResult {{ Ok = {Ok}, ReasonCode = {ReasonCode}, Bytes = {SizeBytes}, Unknown = {UnknownOutcome} }}";
}

/// <summary>
/// MCP 工具来源（实现由宿主注入；Engine 只依赖这个端口）。
/// 发现与执行都带 <see cref="CancellationToken" />；实现必须把“调用者取消”与“本地超时”分开报告。
/// </summary>
public interface IMcpToolSource
{
    /// <summary>配置层面是否启用（false 时不会发生任何网络 I/O）。</summary>
    bool Enabled { get; }

    /// <summary>当前连接状态（观测用）。</summary>
    McpConnectionState State { get; }

    /// <summary>
    /// 发现所有已授权服务器的工具。失败按服务器隔离；永远不抛网络异常给调用方
    /// （调用者取消除外）。返回的 `QualifiedName` 必须唯一。
    /// </summary>
    Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct);

    /// <summary>
    /// 执行一个 `QualifiedName`。实现必须在入口再次核对授权清单；不在清单里的直接拒绝。
    /// 调用者取消 → 抛 <see cref="OperationCanceledException" />；本地超时 → 返回 `mcp_timeout`。
    /// </summary>
    Task<McpCallResult> CallAsync(string qualifiedName, JsonObject arguments, CancellationToken ct);

    /// <summary>停机释放：取消在途调用、关闭会话（含 DELETE）、不无限等待。</summary>
    ValueTask DisposeAsync();
}

/// <summary>MCP 首版的硬边界。数字是上限，不是目标；配置可以更小，不能更大。</summary>
public static class McpLimits
{
    public const int MaxServers = 8;
    public const int MaxTools = 64;
    public const int MaxToolNameLength = 96;
    public const int MaxDescriptionLength = 400;
    public const int MaxSchemaBytes = 16 * 1024;
    public const int MaxArgumentsBytes = 32 * 1024;
    public const int MaxResultBytes = 256 * 1024;
    public const int DefaultCallTimeoutSeconds = 20;
    public const int DefaultResultBytes = 64 * 1024;
}

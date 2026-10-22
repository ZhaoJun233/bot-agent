using System.Text.Json.Nodes;
using BotAgent.Domain.Mcp;

namespace BotAgent.Services.Mcp;

/// <summary>
/// MCP 未启用时的占位来源：不建连、不发现、不执行。
/// 有它之后 <see cref="McpToolUseCase" /> 不必到处判 null，宿主也可以无条件装配。
/// </summary>
public sealed class NullMcpToolSource : IMcpToolSource
{
    public static NullMcpToolSource Instance { get; } = new();

    public bool Enabled => false;

    public McpConnectionState State => McpConnectionState.Disabled;

    public Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<McpToolInfo>>(Array.Empty<McpToolInfo>());

    public Task<McpCallResult> CallAsync(string qualifiedName, JsonObject arguments, CancellationToken ct)
        => Task.FromResult(new McpCallResult(false, "mcp_disabled", null, "status=mcp_disabled;unknown=0"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

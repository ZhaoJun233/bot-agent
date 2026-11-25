using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Agent;

namespace BotAgent.Packages.McpTools;

/// <summary>Independent system capability package for <c>system.mcp-tools</c>.</summary>
public sealed class McpToolsPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private McpToolsEngine? _engine;

    public string Id => "system.mcp-tools";
    public string Name => "MCP 与 Python 工具来源治理";
    public string Version => typeof(McpToolsPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "统一发现与合并外部 MCP Server 及已安装 Python 插件工具来源。";
    public PluginCategory Category => PluginCategory.Management;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _engine = new McpToolsEngine();
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IMcpToolsContribution) ? _engine : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _engine = null;
        return Task.CompletedTask;
    }
}

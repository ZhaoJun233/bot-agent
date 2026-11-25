using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Agent;

namespace BotAgent.Packages.Agent;

/// <summary>Independent system capability package for <c>system.agent</c>.</summary>
public sealed class AgentPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private AgentRuntimeEngine? _engine;

    public string Id => "system.agent";
    public string Name => "内置 Agent 任务运行时";
    public string Version => typeof(AgentPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "服务器内置 Agent 任务准入隔离、原会话键恢复与双锁工具白名单治理。";
    public PluginCategory Category => PluginCategory.Management;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _engine = new AgentRuntimeEngine();
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IAgentRuntimeContribution) ? _engine : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _engine = null;
        return Task.CompletedTask;
    }
}

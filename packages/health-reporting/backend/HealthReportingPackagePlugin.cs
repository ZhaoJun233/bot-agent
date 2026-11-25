using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Ops;

namespace BotAgent.Packages.HealthReporting;

/// <summary>Independent system capability package for <c>system.health-reporting</c>.</summary>
public sealed class HealthReportingPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private HealthReportCoordinator? _coordinator;

    public string Id => "system.health-reporting";
    public string Name => "每日健康日报";
    public string Version => typeof(HealthReportingPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "北京时间每日服务器与机器人状态日报调度、模板生成与缺包降级。";
    public PluginCategory Category => PluginCategory.Management;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ports = context.Services.GetService(typeof(IHealthReportHostPorts)) as IHealthReportHostPorts;
        var clock = context.Services.GetService(typeof(BotAgent.Domain.Ports.IClock)) as BotAgent.Domain.Ports.IClock;
        _coordinator = new HealthReportCoordinator(
            new HealthReportingEngine(),
            ports,
            clock is null ? null : () => clock.UtcNow);
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IHealthReportingRuntimeContribution) || contract == typeof(IHealthReportingContribution)
            ? _coordinator
            : null;

    public async Task ShutdownAsync(CancellationToken ct)
    {
        var coordinator = Interlocked.Exchange(ref _coordinator, null);
        if (coordinator is not null)
        {
            await coordinator.ShutdownAsync(ct).ConfigureAwait(false);
        }
    }
}

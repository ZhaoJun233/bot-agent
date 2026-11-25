using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Model;

namespace BotAgent.Packages.ModelServices;

/// <summary>Independent system capability package for <c>system.model-services</c>.</summary>
public sealed class ModelServicesPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private ModelServicesEngine? _engine;

    public string Id => "system.model-services";
    public string Name => "模型路由与服务治理";
    public string Version => typeof(ModelServicesPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "Provider 路由选择、快速回复档位切换、端点安全边界与故障转移预算。";
    public PluginCategory Category => PluginCategory.Management;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _engine = new ModelServicesEngine();
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IModelServicesContribution) ? _engine : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _engine = null;
        return Task.CompletedTask;
    }
}

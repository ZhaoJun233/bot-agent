using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Profiles;

namespace BotAgent.Packages.Profiles;

/// <summary>Independent package implementation for <c>preset.feature.profiles</c>.</summary>
public sealed class ProfilesPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private ProfilesCoordinator? _coordinator;

    public string Id => "preset.feature.profiles";
    public string Name => "长效人物画像与记忆";
    public string Version => typeof(ProfilesPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "基于发送者标识构建短期会话栈与长期用户画像（支持事实证据链溯源与人工设定覆盖）。";
    public PluginCategory Category => PluginCategory.Feature;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ports = context.Services.GetService(typeof(IProfilesHostPorts)) as IProfilesHostPorts;
        _coordinator = new ProfilesCoordinator(new ProfilesPolicyEngine(), ports);
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IProfilesRuntimeContribution) || contract == typeof(IProfilesPolicyContribution)
            ? _coordinator
            : null;

    public async Task ShutdownAsync(CancellationToken ct)
    {
        var coordinator = _coordinator;
        _coordinator = null;
        if (coordinator is not null)
        {
            await coordinator.ShutdownAsync(ct).ConfigureAwait(false);
        }
    }
}

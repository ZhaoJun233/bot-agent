using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Platforms;

namespace BotAgent.Packages.PlatformQqOfficial;

/// <summary>Exclusive-connection platform driver package for QQ Official Open Platform.</summary>
public sealed class PlatformQqOfficialPlugin : IBotPlugin, IPluginContributionSource, IPluginTwoPhaseLifecycle
{
    private QqOfficialDriver? _driver;

    public string Id => "system.platform-qq-official";
    public string Name => "QQ 官方开放平台";
    public string Version => typeof(PlatformQqOfficialPlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "QQ 开放平台官方机器人通道（独占网关连接、冷采用 + 两阶段激活）。";
    public PluginCategory Category => PluginCategory.Management;
    public bool RequiresTwoPhaseActivation => true;

    public Task PrepareAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _driver = new QqOfficialDriver();
        return Task.CompletedTask;
    }

    public Task ActivateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
        => PrepareAsync(context, ct);

    public object? GetContribution(Type contract)
        => contract == typeof(IPlatformDriverContribution) ? _driver : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _driver = null;
        return Task.CompletedTask;
    }

    private sealed class QqOfficialDriver : IPlatformDriverContribution
    {
        public PlatformDriverDescriptor Descriptor { get; } = new(
            "qq.official", "QQ 官方开放平台", ExclusiveConnection: true,
            SupportsText: true, SupportsImage: true, SupportsVoice: true, SupportsQuote: true);
        public bool RequiresExclusiveConnection => true;
        public bool AllowsTarget(string nativeTargetId, IReadOnlySet<string> allowlist)
            => !string.IsNullOrWhiteSpace(nativeTargetId) && allowlist is not null && allowlist.Contains(nativeTargetId.Trim());
    }
}
